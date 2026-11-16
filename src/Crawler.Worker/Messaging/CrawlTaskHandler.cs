using System.Text.Json;
using Crawler.Shared.Contracts;
using Crawler.Shared.Messaging;
using Crawler.Shared.Persistence;
using Crawler.Worker.Services;

namespace Crawler.Worker.Messaging;

// This class owns retry policy; the processor owns database/crawl logic, and the consumer owns AMQP.
public sealed class CrawlTaskHandler(IServiceScopeFactory scopes, ILogger<CrawlTaskHandler> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Parses one received body and owns task-level retry policy using a fresh service scope each
    /// attempt. Returns only after durable handling; database outages retain delivery, while
    /// invalid/exhausted tasks are recorded safely.
    /// </summary>
    public async Task HandleAsync(byte[] body, string? messageId, CancellationToken ct)
    {
        CrawlPageRequested? message = null;
        try { message = JsonSerializer.Deserialize<CrawlPageRequested>(body, Json); }
        catch (JsonException) { /* Invalid input is retained in the dead-letter queue below. */ }

        using var correlation = logger.BeginScope(new Dictionary<string, object?>
        {
            ["jobId"] = message?.JobId, ["pageId"] = message?.PageId,
            ["correlationId"] = message?.JobId, ["messageId"] = messageId
        });
        Exception? failure = null;
        var attempt = 1;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            using var attemptScope = logger.BeginScope(new Dictionary<string, object?> { ["attempt"] = attempt });
            try
            {
                // A failed transaction/context is discarded before every retry.
                await using var scope = scopes.CreateAsyncScope();
                if (message is null || failure is InvalidCrawlMessageException)
                    await SaveDeadLetterAsync(scope.ServiceProvider, body, messageId, message?.JobId,
                        "Invalid JSON, unsupported schema, or message does not match a stored page.", ct);
                else if (failure is not null && attempt > 3)
                    await scope.ServiceProvider.GetRequiredService<CrawlPageProcessor>()
                        .FailAsync(message, failure, body, messageId, ct);
                else
                    await scope.ServiceProvider.GetRequiredService<CrawlPageProcessor>().ProcessAsync(message, ct);
                return; // The consumer may acknowledge only after this durable database work succeeds.
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error) when (DatabaseFailure.IsTransient(error))
            {
                // Infrastructure downtime is not a poison page. Keep the delivery unacknowledged until recovery.
                logger.LogWarning("Database unavailable; retaining delivery and retrying in five seconds");
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
            catch (InvalidCrawlMessageException error)
            {
                failure = error;
                logger.LogWarning("Rejected task that does not match a stored page or supported schema");
            }
            catch (Exception error)
            {
                // If recording the terminal failure fails, retain that failure and retry persistence safely.
                if (failure is not null && attempt > 3)
                {
                    logger.LogError(error, "Cannot persist task failure; retaining delivery for recovery");
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                    continue;
                }
                failure = error;
                logger.LogWarning("Crawl attempt {Attempt} failed ({FailureType})", attempt, error.GetType().Name);
                if (attempt < 3) await Task.Delay(TimeSpan.FromSeconds(attempt == 1 ? 1 : 3), ct);
                attempt++;
            }
        }
    }

    /// <summary>
    /// Saves an invalid task as an outbox row before its original delivery is acknowledged. This
    /// protects poison-message diagnostics even if the broker or worker stops immediately
    /// afterward.
    /// </summary>
    private static async Task SaveDeadLetterAsync(IServiceProvider services, byte[] body, string? messageId,
        Guid? jobId, string reason, CancellationToken ct)
    {
        var db = services.GetRequiredService<CrawlerDbContext>();
        db.Outbox.Add(OutboxMessage.ForDeadLetter(body, messageId, jobId, reason));
        await db.SaveChangesAsync(ct);
    }
}
