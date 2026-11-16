using System.Diagnostics;
using Crawler.Shared.Contracts;
using Crawler.Shared.Entities;
using Crawler.Shared.Persistence;
using Crawler.Shared.Messaging;
using Crawler.Worker.Http;
using Microsoft.EntityFrameworkCore;

namespace Crawler.Worker.Services;

// Permanent task-validation failure: keep the original body in the DLQ without changing a legitimate page.
public sealed class InvalidCrawlMessageException(string reason) : Exception(reason);

public sealed class CrawlPageProcessor(CrawlerDbContext db, IDbContextFactory<CrawlerDbContext> factory,
    PageFetcher fetcher, HtmlLinkExtractor extractor, CrawlResultWriter writer, JobCancellationMonitor cancellation, TimeProvider clock,
    ILogger<CrawlPageProcessor> logger)
{
    /// <summary>
    /// Validates a task against stored data, exposes Running progress, and serializes work for this
    /// page. Fetching, result writing, and child scheduling share one result transaction; committed
    /// terminal pages are no-ops.
    /// </summary>
    public async Task ProcessAsync(CrawlPageRequested message, CancellationToken ct)
    {
        var snapshot = await db.Pages.AsNoTracking().SingleOrDefaultAsync(page => page.Id == message.PageId, ct);
        var job = snapshot is null ? null : await db.Jobs.AsNoTracking().SingleAsync(job => job.Id == snapshot.JobId, ct);
        if (message.SchemaVersion != 1 || snapshot is null || job?.ExtId != message.JobId ||
            snapshot.Url != message.Url || snapshot.Depth != message.Depth)
            throw new InvalidCrawlMessageException("Crawl message does not match a stored page or supported schema.");
        if (IsTerminal(snapshot.Status))
        {
            logger.LogInformation("Ignored duplicate task for terminal page");
            return;
        }
        if (job.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Canceled) return;

        await MarkStartedAsync(snapshot, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Serialize duplicate workers without locking the page row during slow HTTP. Cancellation can then
        // update pages immediately. PostgreSQL releases this transaction-scoped advisory lock on crash/rollback.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({message.PageId})", ct);
        var page = await db.Pages.SingleAsync(page => page.Id == message.PageId, ct);
        if (IsTerminal(page.Status))
        {
            logger.LogInformation("Ignored duplicate task after waiting for page lock");
            return;
        }
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["url"] = new Uri(page.Url).GetLeftPart(UriPartial.Path), ["depth"] = page.Depth
        });
        var timer = Stopwatch.StartNew();
        logger.LogInformation("Processing page");
        // ct stops work when the consumer/process disconnects. The linked token additionally responds to a user cancel.
        using var work = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var cancellationWatch = cancellation.WatchAsync(page.JobId, work);
        try
        {
            var fetch = await fetcher.FetchAsync(page.Url, work.Token);
            var links = await extractor.ExtractAsync(fetch.Html, fetch.FinalUrl, work.Token);
            await writer.PersistAsync(page, fetch, links, ct);
        }
        catch (OperationCanceledException) when (work.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // The API already committed Canceled for the job and unfinished pages; acknowledge without retries/DLQ.
            logger.LogInformation("Stopped HTTP processing for canceled crawl");
        }
        catch (UnsupportedContentException error)
        {
            await writer.FinishAsync(page, error.Message, error.Metadata, skipped: true, ct);
        }
        catch (PermanentFetchException error)
        {
            await writer.FinishAsync(page, error.Message, error.Metadata, skipped: false, ct);
        }
        finally
        {
            work.Cancel();
            // Join the watcher before its token/context scope ends; no background task leaks after this page returns.
            await cancellationWatch;
        }
        await transaction.CommitAsync(ct);
        logger.LogInformation("Page processing finished in {DurationMs}ms", timer.ElapsedMilliseconds);
    }

    /// <summary>
    /// After task retries are exhausted, atomically records page/job failure and the original
    /// message for dead-letter delivery. Canceled or already terminal pages are left untouched.
    /// </summary>
    public async Task FailAsync(CrawlPageRequested message, Exception error, byte[] body, string? messageId, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({message.PageId})", ct);
        var page = await db.Pages.SingleOrDefaultAsync(page => page.Id == message.PageId, ct);
        // Never let malformed/replayed input overwrite another job or an already committed result.
        if (page is null || IsTerminal(page.Status)) return;
        if (page.Url != message.Url || page.Depth != message.Depth ||
            !await db.Jobs.AnyAsync(job => job.Id == page.JobId && job.ExtId == message.JobId, ct))
            throw new InvalidCrawlMessageException("Failure task does not match a stored page.");
        var reason = $"Message processing exhausted retries: {error.Message}";
        if (reason.Length > 2000) reason = reason[..2000];
        var metadata = (error as TransientFetchException)?.Metadata;
        if (!await writer.FinishAsync(page, reason, metadata, skipped: false, ct)) return;
        // Persist the failure and original dead-letter body atomically, before acknowledging the task.
        db.Outbox.Add(OutboxMessage.ForDeadLetter(body, messageId, message.JobId, reason));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        logger.LogError("Crawl task exhausted retries; original task scheduled for the dead-letter queue");
    }

    /// <summary>
    /// Commits conditional Pending-to-Running/Processing updates through a separate context so the
    /// UI can see work during HTTP. Conditional updates cannot revive a canceled page or job.
    /// </summary>
    private async Task MarkStartedAsync(Page page, CancellationToken ct)
    {
        // Progress is committed separately so polling can see Running/Processing during the fetch.
        // Results and child tasks still commit atomically in the processing transaction.
        await using var progress = await factory.CreateDbContextAsync(ct);
        var now = clock.GetUtcNow();
        await progress.Jobs.Where(job => job.Id == page.JobId && job.Status == JobStatus.Pending)
            .ExecuteUpdateAsync(update => update.SetProperty(job => job.Status, JobStatus.Running)
                .SetProperty(job => job.StartedAt, now), ct);
        await progress.Pages.Where(candidate => candidate.Id == page.Id && candidate.Status == PageStatus.Pending)
            .ExecuteUpdateAsync(update => update.SetProperty(candidate => candidate.Status, PageStatus.Processing)
                .SetProperty(candidate => candidate.StartedAt, now), ct);
    }

    /// <summary>
    /// Identifies page states that require no more processing. This includes Canceled so replayed
    /// canceled tasks acknowledge without fetching again.
    /// </summary>
    public static bool IsTerminal(PageStatus status) => status is PageStatus.Completed or PageStatus.Failed or PageStatus.Skipped or PageStatus.Canceled;
}
