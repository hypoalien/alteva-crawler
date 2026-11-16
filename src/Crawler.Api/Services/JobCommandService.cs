using Crawler.Shared.Contracts;
using Crawler.Shared.Entities;
using Crawler.Shared.Persistence;
using Crawler.Shared.Urls;
using Crawler.Shared.Messaging;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;

namespace Crawler.Api.Services;

public sealed class JobCommandService(
    CrawlerDbContext db,
    IOptions<CrawlJobOptions> options,
    TimeProvider clock,
    ILogger<JobCommandService> logger)
{
    /// <summary>
    /// Uses the public UUID to cancel a job and its unfinished pages in one transaction. Returns
    /// null if missing; otherwise returns the resulting/existing status without overwriting
    /// terminal jobs.
    /// </summary>
    public async Task<JobStatus?> CancelAsync(Guid jobId, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Completion and cancellation take the same job lock; whichever commits first defines the outcome.
        var job = await db.Jobs.FromSqlInterpolated($"SELECT * FROM jobs WHERE ext_id = {jobId} FOR UPDATE")
            .SingleOrDefaultAsync(ct);
        if (job is null) return null;
        var now = clock.GetUtcNow();
        if (!job.Cancel(now)) return job.Status; // Repeated cancellation preserves the original timestamp.
        await db.SaveChangesAsync(ct);
        await db.Pages.Where(page => page.JobId == job.Id &&
                (page.Status == PageStatus.Pending || page.Status == PageStatus.Processing))
            .ExecuteUpdateAsync(update => update.SetProperty(page => page.Status, PageStatus.Canceled)
                .SetProperty(page => page.CompletedAt, now).SetProperty(page => page.Error, "Canceled by user."), ct);
        await transaction.CommitAsync(ct);
        // Queued deliveries are allowed to drain as no-ops; purging the shared queue could delete another job's tasks.
        logger.LogInformation("Canceled job {JobId}; preserved completed results and canceled unfinished pages", jobId);
        return job.Status;
    }

    /// <summary>
    /// Normalizes input, saves the new job/root page/publish intent together, and returns the
    /// public UUID after commit. RabbitMQ delivery happens later, so a broker outage does not lose
    /// an accepted job.
    /// </summary>
    public async Task<Guid> CreateAsync(string? url, int maxDepth, CancellationToken ct)
    {
        var normalizedUrl = UrlNormalizer.NormalizeRoot(url);
        var now = clock.GetUtcNow();
        var job = Job.Create(normalizedUrl, maxDepth, options.Value.MaxPages, now);
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["jobId"] = job.ExtId, ["correlationId"] = job.ExtId
        });

        // Identity-generating saves stay inside this transaction with the publish intent: no orphan Pending jobs.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        db.Jobs.Add(job);
        await db.SaveChangesAsync(ct);

        var root = Page.CreateRoot(job.Id, job.Url);
        db.Pages.Add(root);
        await db.SaveChangesAsync(ct);

        db.Outbox.Add(OutboxMessage.ForPage(new CrawlPageRequested(job.ExtId, root.Id, root.Url, root.Depth, now)));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        logger.LogInformation(
            "Created job {JobId} with root page {PageId}, correlation {CorrelationId}, URL {Url}, depth {MaxDepth}",
            job.ExtId, root.Id, job.ExtId, new Uri(job.Url).GetLeftPart(UriPartial.Path), job.MaxDepth);
        return job.ExtId;
    }
}
