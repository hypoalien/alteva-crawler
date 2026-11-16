namespace Crawler.Api.Services;

// API-owned per-job settings. The API validates this page cap at startup and copies it onto each new job so workers use the same persisted budget.
public sealed class CrawlJobOptions
{
    public int MaxPages { get; set; } = 200;
}
