using Crawler.Shared.Entities;
using Crawler.Shared.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Crawler.Api.Services;

// Read-response DTOs (typed JSON shapes). These projections are not additional database tables.
public sealed record JobProgress(int Total, int Pending, int Processing, int Completed, int Failed, int Skipped, int Canceled);
public sealed record JobSummary(Guid JobId, string InputUrl, string Status, int MaxDepth, int MaxPages,
    DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt,
    string? FailureReason, JobProgress Progress);
public sealed record JobHistoryItem(Guid JobId, string InputUrl, string Status,
    DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt);
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);

public sealed class JobQueryService(CrawlerDbContext db)
{
    // One SQL statement gives status and counts from the same snapshot during concurrent worker commits.
    /// <summary>
    /// Returns lifecycle and progress for one public UUID, or null when absent. Counts come from
    /// page rows in the same SQL snapshot as the job status, not from mutable in-memory counters.
    /// </summary>
    public Task<JobSummary?> GetAsync(Guid jobId, CancellationToken ct) =>
        db.Jobs.AsNoTracking().Where(job => job.ExtId == jobId)
            .Select(job => new JobSummary(job.ExtId, job.Url, job.Status.ToString(), job.MaxDepth, job.MaxPages,
                job.CreatedAt, job.StartedAt, job.CompletedAt, job.Error,
                new JobProgress(
                    db.Pages.Count(p => p.JobId == job.Id),
                    db.Pages.Count(p => p.JobId == job.Id && p.Status == PageStatus.Pending),
                    db.Pages.Count(p => p.JobId == job.Id && p.Status == PageStatus.Processing),
                    db.Pages.Count(p => p.JobId == job.Id && p.Status == PageStatus.Completed),
                    db.Pages.Count(p => p.JobId == job.Id && p.Status == PageStatus.Failed),
                    db.Pages.Count(p => p.JobId == job.Id && p.Status == PageStatus.Skipped),
                    db.Pages.Count(p => p.JobId == job.Id && p.Status == PageStatus.Canceled))))
            .SingleOrDefaultAsync(ct);

    /// <summary>
    /// Finds the job by public UUID, loads its pages in one query, and builds the response
    /// hierarchy in memory. Null means no such job; an empty list means it has no page rows.
    /// </summary>
    public async Task<IReadOnlyList<PageTreeNode>?> GetTreeAsync(Guid jobId, CancellationToken ct)
    {
        var id = await db.Jobs.Where(job => job.ExtId == jobId).Select(job => (long?)job.Id).SingleOrDefaultAsync(ct);
        if (id is null) return null;
        var pages = await db.Pages.AsNoTracking().Where(page => page.JobId == id).ToListAsync(ct);
        return JobTreeBuilder.Build(pages);
    }

    /// <summary>
    /// Returns a page of lightweight history rows plus the total count. The endpoint validates
    /// bounds; the internal ID breaks creation-time ties for deterministic ordering.
    /// </summary>
    public async Task<PagedResult<JobHistoryItem>> ListAsync(int page, int pageSize, CancellationToken ct)
    {
        var total = await db.Jobs.CountAsync(ct);
        var items = await db.Jobs.AsNoTracking().OrderByDescending(job => job.CreatedAt).ThenByDescending(job => job.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(job => new JobHistoryItem(job.ExtId, job.Url, job.Status.ToString(), job.CreatedAt, job.CompletedAt))
            .ToListAsync(ct);
        return new(items, page, pageSize, total);
    }
}
