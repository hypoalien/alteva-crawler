using Crawler.Shared.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace Crawler.Shared.Messaging;

public sealed class OutboxDispatcher(IDbContextFactory<CrawlerDbContext> factory, RabbitMqConnection broker,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    /// <summary>
    /// Runs for the host lifetime: opens a publishing channel, declares queues, and drains stored
    /// intents. Idle periods poll briefly; dependency failures back off five seconds instead of
    /// spinning.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var connection = await broker.GetAsync(stoppingToken);
                var options = new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true);
                await using var channel = await connection.CreateChannelAsync(options, stoppingToken);
                await CrawlMessaging.DeclareQueuesAsync(channel, stoppingToken);
                // Reconnect even when the outbox is empty; otherwise idle API readiness could stay unhealthy.
                while (channel.IsOpen && !stoppingToken.IsCancellationRequested)
                    if (!await PublishNextAsync(channel, stoppingToken))
                        await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                logger.LogWarning(error, "Outbox delivery unavailable; retrying in five seconds");
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }

    /// <summary>
    /// Locks one available outbox row, publishes with routing/confirmation checks, then deletes it
    /// only after confirmation. Returns false when there is no work; failures roll back and leave
    /// the row for retry.
    /// </summary>
    private async Task<bool> PublishNextAsync(IChannel channel, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // API and worker dispatchers can run together; SKIP LOCKED gives each a different pending row.
        var pending = await db.Outbox.FromSqlRaw(
            "SELECT * FROM crawl_outbox ORDER BY created_at, id LIMIT 1 FOR UPDATE SKIP LOCKED").ToListAsync(ct);
        if (pending.Count == 0) return false;
        var message = pending[0];
        var properties = new BasicProperties
        {
            Persistent = true, ContentType = "application/json", MessageId = message.Id.ToString(),
            CorrelationId = message.JobId?.ToString(),
            Headers = new Dictionary<string, object?>()
        };
        if (message.Error is not null) properties.Headers["x-failure-reason"] = message.Error;
        if (message.SourceMessageId is not null) properties.Headers["x-original-message-id"] = message.SourceMessageId;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        // Await broker confirmation AND require routing. Never delete a row after an unconfirmed send.
        await channel.BasicPublishAsync(exchange: "", routingKey: message.Destination, mandatory: true,
            basicProperties: properties, body: message.Body, cancellationToken: timeout.Token);
        db.Outbox.Remove(message);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        // A crash after confirmation but before commit can resend; the page lock makes that harmless.
        logger.LogInformation("Delivered outbox message {MessageId} for job {JobId} to {Queue}",
            message.Id, message.JobId, message.Destination);
        return true;
    }
}
