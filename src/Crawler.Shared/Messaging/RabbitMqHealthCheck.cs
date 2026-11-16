using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Crawler.Shared.Messaging;

public sealed class RabbitMqHealthCheck(RabbitMqConnection broker) : IHealthCheck
{
    /// <summary>
    /// Reports whether this process currently has an open broker connection. A failed check does
    /// not reject job creation; committed publish intents can wait in PostgreSQL.
    /// </summary>
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default) =>
        Task.FromResult(broker.IsConnected ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("RabbitMQ unavailable; committed tasks remain in the outbox."));
}
