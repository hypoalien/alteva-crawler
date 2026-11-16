using Crawler.Shared.Entities;
using Crawler.Shared.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Crawler.Worker.Services;

// PostgreSQL is the durable cancellation source, so this also works across multiple worker processes.
public sealed class JobCancellationMonitor(IDbContextFactory<CrawlerDbContext> factory,
    ILogger<JobCancellationMonitor> logger)
{
    /// <summary>
    /// Checks durable job cancellation every 500 ms and cancels this page's linked work token when
    /// requested. The processor stops and awaits this watcher in finally; DB outages back off
    /// without pretending the user canceled.
    /// </summary>
    public async Task WatchAsync(long jobId, CancellationTokenSource work)
    {
        while (!work.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), work.Token);
                await using var db = await factory.CreateDbContextAsync(work.Token);
                if (!await db.Jobs.AnyAsync(job => job.Id == jobId && job.Status == JobStatus.Canceled, work.Token))
                    continue;
                work.Cancel(); // Interrupt DNS, response streaming, HTTP retry delays, and HTML parsing.
                return;
            }
            catch (OperationCanceledException) when (work.IsCancellationRequested) { return; }
            catch (Exception error) when (DatabaseFailure.IsTransient(error))
            {
                // A failed cancellation lookup is not evidence that the user canceled the crawl.
                logger.LogWarning("Cancellation lookup unavailable; checking again after database recovery");
                try { await Task.Delay(TimeSpan.FromSeconds(5), work.Token); }
                catch (OperationCanceledException) when (work.IsCancellationRequested) { return; }
            }
        }
    }
}
