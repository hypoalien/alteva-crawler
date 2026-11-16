using Crawler.Api.Services;
using Crawler.Shared.Entities;

namespace Crawler.Api.Endpoints;

// ASP.NET binds JSON into this request record; the response exposes only the public UUID.
public sealed record CreateJobRequest(string? Url, int? MaxDepth = null);
public sealed record CreateJobResponse(Guid JobId);

public static class JobEndpoints
{
    /// <summary>
    /// Connects URLs and HTTP methods to handlers, like registering routes on an Express router.
    /// Services are supplied by ASP.NET dependency injection.
    /// </summary>
    public static IEndpointRouteBuilder MapJobEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/jobs", CreateAsync);
        app.MapPost("/api/jobs/{jobId:guid}/cancel", CancelAsync);
        app.MapGet("/api/jobs/{jobId:guid}", async (Guid jobId, JobQueryService service, CancellationToken ct) =>
            await service.GetAsync(jobId, ct) is { } job ? Results.Ok(job) : JobNotFound());
        app.MapGet("/api/jobs/{jobId:guid}/tree", async (Guid jobId, JobQueryService service, CancellationToken ct) =>
            await service.GetTreeAsync(jobId, ct) is { } tree ? Results.Ok(tree) : JobNotFound());
        app.MapGet("/api/jobs", ListAsync);
        return app;
    }

    /// <summary>
    /// Handles POST cancel: returns the updated summary on success/repeat, 404 for an unknown
    /// UUID, or 409 if completion/failure already won the race.
    /// </summary>
    private static async Task<IResult> CancelAsync(Guid jobId, JobCommandService commands,
        JobQueryService queries, CancellationToken ct)
    {
        var status = await commands.CancelAsync(jobId, ct);
        if (status is null) return JobNotFound();
        if (status != JobStatus.Canceled)
            return Results.Problem(statusCode: 409, title: "Crawl already finished.",
                detail: $"A {status.ToString()!.ToLowerInvariant()} crawl cannot be canceled.");
        return Results.Ok(await queries.GetAsync(jobId, ct));
    }

    /// <summary>
    /// Validates the one-based page number and page size before asking the query service for
    /// newest-first history. Invalid bounds return HTTP 400.
    /// </summary>
    private static async Task<IResult> ListAsync(int? page, int? pageSize, JobQueryService service, CancellationToken ct)
    {
        var number = page ?? 1;
        var size = pageSize ?? 20;
        if (number is < 1 or > 1_000_000 || size is < 1 or > 100)
            return Results.Problem(statusCode: 400, title: "Invalid pagination.",
                detail: "page must be between 1 and 1000000; pageSize must be between 1 and 100.");
        return Results.Ok(await service.ListAsync(number, size, ct));
    }

    /// <summary>
    /// Builds the same JSON Problem Details response for every unknown job, so clients can handle
    /// 404 consistently.
    /// </summary>
    private static IResult JobNotFound() => Results.Problem(statusCode: 404, title: "Job not found.");

    /// <summary>
    /// Handles POST create: applies depth 2 when omitted, delegates the transaction, and returns
    /// HTTP 201 plus the job location. URL/depth validation errors become HTTP 400.
    /// </summary>
    private static async Task<IResult> CreateAsync(
        CreateJobRequest request, JobCommandService service, HttpContext context, CancellationToken ct)
    {
        try
        {
            var jobId = await service.CreateAsync(request.Url, request.MaxDepth ?? 2, ct);
            context.Items["jobId"] = jobId;
            return Results.Created($"/api/jobs/{jobId}", new CreateJobResponse(jobId));
        }
        catch (ArgumentException error) when (error.ParamName is "url" or "maxDepth")
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [error.ParamName] = [error.Message]
            });
        }
    }
}
