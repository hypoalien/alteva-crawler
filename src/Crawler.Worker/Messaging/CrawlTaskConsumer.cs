using Crawler.Shared.Messaging;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Crawler.Worker.Messaging;

public sealed class CrawlTaskConsumer(RabbitMqConnection broker, CrawlTaskHandler handler,
    ILogger<CrawlTaskConsumer> logger) : BackgroundService, IHealthCheck
{
    private const int Parallelism = 4;
    private int activeConsumers;

    /// <summary>
    /// Starts four independent consumer loops and keeps the hosted service running until all stop.
    /// Each loop owns its channel and processes one unacknowledged delivery at a time.
    /// </summary>
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Enumerable.Range(0, Parallelism).Select(_ => ConsumeAsync(stoppingToken)));

    /// <summary>
    /// Subscribes with manual acknowledgement, passes copied message bytes to the handler, and
    /// acknowledges only after it succeeds. Closing a failed channel returns unfinished deliveries
    /// to RabbitMQ for recovery.
    /// </summary>
    private async Task ConsumeAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var connection = await broker.GetAsync(stoppingToken);
                using var disconnected = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
                await CrawlMessaging.DeclareQueuesAsync(channel, stoppingToken);
                // One delivery per channel keeps acknowledgement ownership and concurrency easy to follow.
                await channel.BasicQosAsync(0, prefetchCount: 1, global: false, stoppingToken);
                channel.ChannelShutdownAsync += (_, _) =>
                {
                    disconnected.Cancel();
                    return Task.CompletedTask;
                };
                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.UnregisteredAsync += (_, _) =>
                {
                    // A broker-side cancellation needs a new subscription even if the TCP connection stays open.
                    disconnected.Cancel();
                    return Task.CompletedTask;
                };
                consumer.ReceivedAsync += async (_, delivery) =>
                {
                    try
                    {
                        // RabbitMQ owns the receive buffer; copy it before the callback can return.
                        await handler.HandleAsync(delivery.Body.ToArray(), delivery.BasicProperties.MessageId, disconnected.Token);
                        await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, disconnected.Token);
                    }
                    catch (OperationCanceledException) when (disconnected.IsCancellationRequested) { }
                    catch (Exception error)
                    {
                        logger.LogWarning(error, "Delivery interrupted; closing channel so RabbitMQ can redeliver");
                        disconnected.Cancel();
                    }
                };
                await channel.BasicConsumeAsync(CrawlMessaging.QueueName, autoAck: false, consumer, stoppingToken);
                Interlocked.Increment(ref activeConsumers);
                try { await Task.Delay(Timeout.Infinite, disconnected.Token); }
                catch (OperationCanceledException) when (disconnected.IsCancellationRequested) { }
                finally { Interlocked.Decrement(ref activeConsumers); }
                // Disposal closes the channel; unacknowledged messages return to the durable queue.
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogWarning(error, "Consumer disconnected; reconnecting in five seconds"); }
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    /// <summary>
    /// Reports ready only when all four subscriptions are active. A live TCP connection alone
    /// would miss broker-side consumer cancellation or a reconnecting subscription.
    /// </summary>
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default) =>
        Task.FromResult(Volatile.Read(ref activeConsumers) == Parallelism ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Crawl consumers are reconnecting."));
}
