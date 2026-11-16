using Crawler.Shared.Contracts;
using Crawler.Shared.Entities;
using Crawler.Shared.Persistence;
using Crawler.Shared.Urls;
using Crawler.Worker.Http;
using Crawler.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Crawler.Worker.Services;

// The processor owns the transaction; results and child publish intents commit together.
public sealed class CrawlResultWriter(CrawlerDbContext db, TimeProvider clock,
    ILogger<CrawlResultWriter> logger)
{
    /// <summary>
    /// While the processor transaction is open, saves outgoing links/metrics, reserves new internal
    /// children, and stores their task intents. Returns false if cancellation won the job lock
    /// before this result could commit.
    /// </summary>
    public async Task<bool> PersistAsync(Page page, FetchResult fetch, IReadOnlyList<string> links, CancellationToken ct)
    {
        var job = await LockJobAsync(page.JobId, ct);
        // Always check after acquiring the same lock as cancellation, before writing results or child tasks.
        if (job.Status == JobStatus.Canceled)
        {
            logger.LogInformation("Discarded late result for canceled job {JobId}", job.ExtId);
            return false;
        }
        var now = clock.GetUtcNow();
        var metrics = DomainLinkRatio.Calculate(links, new Uri(job.Url));
        var urls = links.ToArray();
        // Batch all outgoing edges in one SQL round trip; duplicate edge keys are harmless.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO links (source_page_id, url) SELECT {page.Id}, unnest({urls}::text[]) ON CONFLICT DO NOTHING", ct);
        var children = await ReserveChildrenAsync(job, page, links, ct);
        page.Complete(fetch.Metadata, metrics, now);
        await db.SaveChangesAsync(ct);
        foreach (var child in children)
            db.Outbox.Add(OutboxMessage.ForPage(new CrawlPageRequested(job.ExtId, child.Id, child.Url, child.Depth, now)));
        await TryCompleteAsync(job, ct);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Completed page; {Links} links, ratio {Ratio}, {Children} children scheduled",
            metrics.TotalCount, metrics.Ratio, children.Count);
        return true;
    }

    /// <summary>
    /// Persists a failed or skipped page and checks whether the job is finished. Returns false for
    /// a canceled job, letting failure handling avoid creating a misleading dead letter.
    /// </summary>
    public async Task<bool> FinishAsync(Page page, string reason, FetchMetadata? metadata, bool skipped, CancellationToken ct)
    {
        var job = await LockJobAsync(page.JobId, ct);
        // Always check after acquiring the same lock as cancellation, before writing results or child tasks.
        if (job.Status == JobStatus.Canceled)
        {
            logger.LogInformation("Discarded late result for canceled job {JobId}", job.ExtId);
            return false;
        }
        var now = clock.GetUtcNow();
        if (skipped) page.Skip(reason, metadata, now);
        else page.Fail(reason, metadata, now);
        if (!skipped && page.ParentPageId is null) job.Fail(reason, now);
        await db.SaveChangesAsync(ct);
        await TryCompleteAsync(job, ct);
        await db.SaveChangesAsync(ct);
        logger.LogWarning("Page finished with {PageStatus}: {Reason}", page.Status, reason);
        return true;
    }

    /// <summary>
    /// Chooses unseen same-host links within the depth/page budget and creates Pending child
    /// objects. The caller already holds the job lock, so concurrent discoveries cannot exceed the
    /// page cap.
    /// </summary>
    private async Task<List<Page>> ReserveChildrenAsync(Job job, Page source, IReadOnlyList<string> links, CancellationToken ct)
    {
        // Every discovery holds the same job-row lock, so the read/count/insert sequence cannot overshoot the cap.
        if (source.Depth >= job.MaxDepth) return [];
        var existing = await db.Pages.Where(page => page.JobId == job.Id).Select(page => page.Url).ToListAsync(ct);
        var known = existing.ToHashSet(StringComparer.Ordinal);
        var root = new Uri(job.Url);
        var children = links.Where(url => !known.Contains(url) && LinkClassifier.IsInternal(root, new Uri(url)))
            .Take(Math.Max(0, job.MaxPages - existing.Count))
            .Select(url => Page.CreateChild(job.Id, source.Id, url, source.Depth + 1)).ToList();
        db.Pages.AddRange(children);
        return children;
    }

    /// <summary>
    /// Loads the job with a SQL row lock held until the processor transaction ends. Result writers
    /// and cancellation use this same lock to agree on the winning state change.
    /// </summary>
    private Task<Job> LockJobAsync(long jobId, CancellationToken ct) =>
        db.Jobs.FromSqlInterpolated($"SELECT * FROM jobs WHERE id = {jobId} FOR UPDATE").SingleAsync(ct);

    /// <summary>
    /// Completes an active job only when no Pending/Processing pages remain. The caller saves the
    /// current page and new children first so completion cannot overlook newly scheduled work.
    /// </summary>
    private async Task TryCompleteAsync(Job job, CancellationToken ct)
    {
        if (job.Status is not (JobStatus.Pending or JobStatus.Running)) return;
        // The source result and new frontier rows must be saved before deciding that the crawl has drained.
        var active = await db.Pages.AnyAsync(page => page.JobId == job.Id &&
            (page.Status == PageStatus.Pending || page.Status == PageStatus.Processing), ct);
        if (active) return;
        job.Complete(clock.GetUtcNow());
        logger.LogInformation("Completed job {JobId}", job.ExtId);
    }
}
