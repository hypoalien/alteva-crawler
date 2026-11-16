using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Crawler.Shared.Persistence;

public static class PersistenceConfiguration
{
    /// <summary>
    /// Registers PostgreSQL Entity Framework contexts, a factory for independent short-lived
    /// contexts, and database readiness checking. Missing connection configuration fails setup
    /// early.
    /// </summary>
    public static IServiceCollection AddCrawlerPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        var connection = configuration.GetConnectionString("Crawler")
            ?? throw new InvalidOperationException("ConnectionStrings:Crawler is required.");
        services.AddDbContextFactory<CrawlerDbContext>(options =>
            options.UseNpgsql(connection));
        services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("postgres");
        return services;
    }
}
