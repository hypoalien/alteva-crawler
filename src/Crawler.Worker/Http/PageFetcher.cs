using System.Net;
using System.Text;
using Crawler.Shared.Entities;
using Crawler.Shared.Urls;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Timeout;

namespace Crawler.Worker.Http;

public sealed class PageFetcher(HttpClient client, DestinationPolicy destinations,
    ResiliencePipeline<FetchResult> resilience, IOptions<CrawlOptions> options)
{
    /// <summary>
    /// Runs the complete fetch through its HTTP retry/timeout budget and preserves the last
    /// response metadata on exhaustion. Successful output contains final URL, metadata, and
    /// temporary HTML for parsing.
    /// </summary>
    public async Task<FetchResult> FetchAsync(string url, CancellationToken ct)
    {
        FetchMetadata? lastResponse = null;
        try
        {
            return await resilience.ExecuteAsync(token =>
                FetchAttemptAsync(url, metadata => lastResponse = metadata, token), ct);
        }
        catch (Exception error) when (error is HttpRequestException or TimeoutRejectedException)
        {
            // Keep the last response even when its body stalls or the next retry fails before receiving headers.
            throw new TransientFetchException(error.Message, lastResponse, inner: error);
        }
    }

    /// <summary>
    /// Performs one attempt with manually checked redirects and a finite redirect limit. Relative
    /// Location values use standard URL resolution, and every destination is revalidated before
    /// HTTP.
    /// </summary>
    private async ValueTask<FetchResult> FetchAttemptAsync(string url, Action<FetchMetadata> observe, CancellationToken ct)
    {
        var current = new Uri(UrlNormalizer.NormalizeRoot(url));
        FetchMetadata? lastResponse = null;
        for (var redirects = 0; redirects <= options.Value.MaxRedirects; redirects++)
        {
            await destinations.ResolveAsync(current.IdnHost, ct);
            using var response = await client.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, ct);
            lastResponse = new((int)response.StatusCode,
                response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant(), current.AbsoluteUri);
            observe(lastResponse);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                if (!UrlNormalizer.TryNormalizeLink(current, response.Headers.Location?.ToString(), out var next))
                    throw new PermanentFetchException("Invalid redirect destination.", lastResponse);
                current = new Uri(next);
                continue;
            }
            return await ReadResponseAsync(response, current, lastResponse, ct);
        }
        throw new PermanentFetchException("Redirect limit exceeded.", lastResponse);
    }

    /// <summary>
    /// Classifies status/content type before reading the body: transient errors retry, permanent
    /// errors fail, and non-HTML is skipped. Successful bounded bytes are decoded using the
    /// declared charset with a UTF-8 fallback.
    /// </summary>
    private async Task<FetchResult> ReadResponseAsync(HttpResponseMessage response, Uri url, FetchMetadata metadata, CancellationToken ct)
    {
        if (response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
        {
            var retry = response.Headers.RetryAfter;
            var delay = retry?.Delta ?? (retry?.Date - DateTimeOffset.UtcNow);
            throw new TransientFetchException($"Transient HTTP {metadata.HttpStatus}.", metadata, delay);
        }
        if (!response.IsSuccessStatusCode) throw new PermanentFetchException($"HTTP {metadata.HttpStatus}.", metadata);
        if (metadata.ContentType is not ("text/html" or "application/xhtml+xml"))
            throw new UnsupportedContentException(metadata);
        var bytes = await ReadBoundedAsync(response, metadata, ct);
        var charset = response.Content.Headers.ContentType?.CharSet?.Trim('"');
        Encoding encoding;
        try { encoding = charset is null ? Encoding.UTF8 : Encoding.GetEncoding(charset); }
        catch (ArgumentException) { encoding = Encoding.UTF8; }
        return new(url, metadata, encoding.GetString(bytes));
    }

    /// <summary>
    /// Streams the decompressed body with cancellation and a byte cap, even when Content-Length is
    /// absent or misleading. Oversized bodies fail permanently; broken streams become retryable
    /// HTTP errors.
    /// </summary>
    private async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, FetchMetadata metadata, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > options.Value.MaxResponseBytes)
            throw new PermanentFetchException("Response exceeds the body size limit.", metadata);
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        try
        {
            while ((count = await input.ReadAsync(buffer, ct)) > 0)
            {
                if (output.Length + count > options.Value.MaxResponseBytes)
                    throw new PermanentFetchException("Response exceeds the body size limit.", metadata);
                output.Write(buffer, 0, count);
            }
        }
        catch (IOException error) { throw new HttpRequestException("Response stream failed.", error); }
        return output.ToArray();
    }
}
