using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Crawler.Shared.Persistence;

public sealed class DatabaseHealthCheck(IDbContextFactory<CrawlerDbContext> factory) : IHealthCheck
{
    /// <summary>
    /// Checks whether a fresh context can connect to PostgreSQL. This reports readiness only;
    /// liveness deliberately does not depend on database availability.
    /// </summary>
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Database.CanConnectAsync(ct)
            ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("PostgreSQL unavailable.");
    }
}
