using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;
using Polly.Timeout;
using System.Text;

namespace Crawler.Worker.Http;

public static class HttpConfiguration
{
    /// <summary>
    /// Registers validated HTTP limits, destination checking, HTML extraction, and the shared retry
    /// pipeline. The socket handler disables automatic redirects/cookies so every redirect is
    /// checked and jobs do not share sessions.
    /// </summary>
    public static IServiceCollection AddCrawlerHttp(this IServiceCollection services, IConfiguration config, bool testing)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        services.AddOptions<CrawlOptions>().Bind(config.GetSection("Crawl"))
            .Validate(o => o.MaxResponseBytes is >= 1024 and <= 10 * 1024 * 1024 &&
                o.AttemptTimeoutSeconds is >= 1 and <= 60 && o.HttpRetries is >= 1 and <= 3 &&
                o.MaxRedirects is >= 0 and <= 10, "Invalid crawl HTTP limits.")
            .Validate(o => testing || o.AllowedPrivateHosts.Length == 0, "Private hosts are allowed only in Testing.")
            .ValidateOnStart();
        services.AddSingleton<DestinationPolicy>();
        services.AddSingleton<HtmlLinkExtractor>();
        services.AddSingleton(provider => BuildResilience(provider.GetRequiredService<IOptions<CrawlOptions>>().Value,
            provider.GetRequiredService<ILogger<PageFetcher>>()));
        services.AddHttpClient<PageFetcher>(client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("AltevaAssessmentCrawler/1.0");
        // Default HttpClient logs contain full query strings. Our scoped logs deliberately omit them.
        }).RemoveAllLoggers().ConfigurePrimaryHttpMessageHandler(provider => new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseProxy = false, UseCookies = false,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = provider.GetRequiredService<DestinationPolicy>().ConnectAsync
        });
        return services;
    }

    /// <summary>
    /// Builds HTTP-level retries around a separate timeout for each complete fetch attempt.
    /// Transient network/status errors retry with jitter or bounded Retry-After; permanent
    /// content/destination failures bypass retries.
    /// </summary>
    private static ResiliencePipeline<FetchResult> BuildResilience(CrawlOptions options, ILogger logger) =>
        new ResiliencePipelineBuilder<FetchResult>().AddRetry(new RetryStrategyOptions<FetchResult>
        {
            MaxRetryAttempts = options.HttpRetries,
            Delay = TimeSpan.FromMilliseconds(250), BackoffType = DelayBackoffType.Exponential, UseJitter = true,
            ShouldHandle = new PredicateBuilder<FetchResult>().Handle<HttpRequestException>().Handle<TimeoutRejectedException>(),
            DelayGenerator = args => new ValueTask<TimeSpan?>(args.Outcome.Exception is TransientFetchException { RetryAfter: { } delay }
                ? TimeSpan.FromSeconds(Math.Clamp(delay.TotalSeconds, 0, 30)) : null),
            OnRetry = args =>
            {
                logger.LogWarning("Retrying fetch, attempt {Attempt}, delay {DelayMs}ms: {Reason}",
                    args.AttemptNumber + 1, args.RetryDelay.TotalMilliseconds, args.Outcome.Exception?.Message);
                return default;
            }
        }).AddTimeout(TimeSpan.FromSeconds(options.AttemptTimeoutSeconds)).Build();
}
