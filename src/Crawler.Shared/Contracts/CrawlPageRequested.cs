namespace Crawler.Shared.Contracts;

// The plain JSON work contract shared by producer/consumer. JobId is public; PageId is an internal SQL key; SchemaVersion allows rejecting incompatible tasks.
public sealed record CrawlPageRequested(
    Guid JobId,
    long PageId,
    string Url,
    int Depth,
    DateTimeOffset OccurredAt,
    int SchemaVersion = 1);
