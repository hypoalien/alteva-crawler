using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;

namespace Crawler.Shared.Messaging;

public static class CrawlMessaging
{
    // Versioned queue names keep incompatible message formats from sharing a queue.
    public const string QueueName = "crawl-page-v2";
    public const string DeadLetterQueueName = "crawl-page-v2-dead";

    /// <summary>
    /// Registers one broker connection per process, the background outbox dispatcher, and RabbitMQ
    /// readiness. Both API and worker reuse this setup.
    /// </summary>
    public static IServiceCollection AddCrawlMessaging(this IServiceCollection services)
    {
        services.AddSingleton<RabbitMqConnection>();
        services.AddHostedService<OutboxDispatcher>();
        services.AddHealthChecks().AddCheck<RabbitMqHealthCheck>("rabbitmq");
        return services;
    }

    /// <summary>
    /// Idempotently declares the durable work and dead-letter queues on a channel. Publishing
    /// through the default exchange uses each queue name as its routing key.
    /// </summary>
    public static async Task DeclareQueuesAsync(IChannel channel, CancellationToken ct)
    {
        await channel.QueueDeclareAsync(QueueName, durable: true, exclusive: false, autoDelete: false,
            cancellationToken: ct);
        await channel.QueueDeclareAsync(DeadLetterQueueName, durable: true, exclusive: false, autoDelete: false,
            cancellationToken: ct);
    }
}
