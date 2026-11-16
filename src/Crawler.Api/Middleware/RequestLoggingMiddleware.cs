using System.Diagnostics;

namespace Crawler.Api.Middleware;

public sealed class RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
{
    /// <summary>
    /// Wraps one HTTP request, adds its request ID, and logs final status/duration even when
    /// downstream code throws. next(context) passes control to the rest of the ASP.NET request
    /// pipeline.
    /// </summary>
    public async Task InvokeAsync(HttpContext context)
    {
        var timer = Stopwatch.StartNew();
        context.Response.Headers["X-Request-Id"] = context.TraceIdentifier;
        var routeJobId = context.Request.RouteValues["jobId"];
        using var requestScope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["requestId"] = context.TraceIdentifier, ["jobId"] = routeJobId,
            ["correlationId"] = routeJobId ?? context.TraceIdentifier
        });
        try { await next(context); }
        finally
        {
            var jobId = context.Items["jobId"] ?? context.Request.RouteValues["jobId"];
            using var scope = logger.BeginScope(new Dictionary<string, object?>
            {
                ["requestId"] = context.TraceIdentifier, ["jobId"] = jobId,
                ["correlationId"] = jobId ?? context.TraceIdentifier
            });
            // Log the path, never query strings or bodies; either can contain credentials or sensitive filters.
            logger.LogInformation("HTTP {Method} {Path} returned {StatusCode} in {DurationMs}ms",
                context.Request.Method, context.Request.Path.Value,
                context.RequestAborted.IsCancellationRequested ? 499 : context.Response.StatusCode, timer.ElapsedMilliseconds);
        }
    }
}
