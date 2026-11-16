using Crawler.Api.Endpoints;
using Crawler.Api.Services;
using Crawler.Api.Middleware;
using Crawler.Shared.Messaging;
using Crawler.Shared.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;

// API entry point (like server.ts): build the service container, optionally migrate, then start HTTP routes.
var builder = WebApplication.CreateBuilder(args);
// The contract carries one bounded URL and a depth, never an uploaded page or other large payload.
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 16 * 1024);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.UseUtcTimestamp = true;
    options.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
});
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict);
// Registered services are constructor-injected by .NET; each request/task gets its own scoped database context.
builder.Services.AddCrawlerPersistence(builder.Configuration);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddOptions<CrawlJobOptions>()
    .Bind(builder.Configuration.GetSection("Crawl"))
    .Validate(options => options.MaxPages is >= 1 and <= 1000, "Crawl:MaxPages must be between 1 and 1000.")
    .ValidateOnStart();
builder.Services.AddScoped<JobCommandService>();
builder.Services.AddScoped<JobQueryService>();
builder.Services.AddCrawlMessaging();
// Building finishes setup; Run below starts the web host and registered background services.
var app = builder.Build();

// Compose runs this one-shot mode before the API/worker. It applies schema changes and exits without serving HTTP.
if (args.Contains("--migrate"))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<CrawlerDbContext>().Database.MigrateAsync();
    return;
}

// Place logging outside exception handling so failed requests are logged with their final HTTP status.
app.UseMiddleware<RequestLoggingMiddleware>();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.MapJobEndpoints();
// Liveness asks only whether this process responds; readiness also checks PostgreSQL/RabbitMQ.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready");
app.Run();
