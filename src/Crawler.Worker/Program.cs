using Crawler.Shared.Persistence;
using Crawler.Shared.Messaging;
using Crawler.Worker.Messaging;
using Crawler.Worker.Http;
using Crawler.Worker.Services;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

// Worker entry point: register crawl dependencies and start background consumers plus health-only HTTP routes.
var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.UseUtcTimestamp = true;
    options.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
});
// Registered services are constructor-injected by .NET; each request/task gets its own scoped database context.
builder.Services.AddCrawlerPersistence(builder.Configuration);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddCrawlerHttp(builder.Configuration, builder.Environment.IsEnvironment("Testing"));
builder.Services.AddScoped<CrawlPageProcessor>();
builder.Services.AddSingleton<JobCancellationMonitor>();
builder.Services.AddScoped<CrawlResultWriter>();
builder.Services.AddCrawlMessaging();
// A BackgroundService is .NET's managed long-running loop; the host starts/stops it with the process.
builder.Services.AddSingleton<CrawlTaskHandler>();
builder.Services.AddSingleton<CrawlTaskConsumer>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<CrawlTaskConsumer>());
builder.Services.AddHealthChecks().AddCheck<CrawlTaskConsumer>("consumers");
// Building finishes setup; Run below starts the web host and registered background services.
var app = builder.Build();
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready");
app.Run();
