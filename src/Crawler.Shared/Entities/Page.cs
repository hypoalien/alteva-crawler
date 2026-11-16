using Crawler.Shared.Urls;

namespace Crawler.Shared.Entities;

public enum PageStatus { Pending, Processing, Completed, Failed, Skipped, Canceled }
// Response headers/final address retained for diagnostics even if body reading or retries fail.
public sealed record FetchMetadata(int HttpStatus, string? ContentType, string? FinalUrl = null);

// One discovered URL within one job: initially frontier work, later its result. ParentPageId preserves first discovery.
public sealed class Page
{
    public long Id { get; private set; }
    public long JobId { get; private set; }
    public long? ParentPageId { get; private set; }
    public string Url { get; private set; } = "";
    // Url remains the discovery/deduplication key; redirects must not change that identity.
    public string? FinalUrl { get; private set; }
    public int Depth { get; private set; }
    public PageStatus Status { get; private set; }
    public int? HttpStatus { get; private set; }
    public string? ContentType { get; private set; }
    public int TotalLinkCount { get; private set; }
    public int InternalLinkCount { get; private set; }
    public decimal? LinkRatio { get; private set; }
    public string? Error { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>
    /// Builds a Pending frontier page with its first-discovery parent and depth. The worker
    /// reserves/saves this object and its task while holding the job lock.
    /// </summary>
    public static Page CreateChild(long jobId, long parentId, string normalizedUrl, int depth) => new()
    {
        JobId = jobId, ParentPageId = parentId, Url = normalizedUrl, Depth = depth, Status = PageStatus.Pending
    };

    /// <summary>
    /// Stores the fetched page metrics and response metadata, then marks the page Completed.
    /// Requested Url stays unchanged so redirects cannot break duplicate detection.
    /// </summary>
    public void Complete(FetchMetadata metadata, LinkMetrics metrics, DateTimeOffset now)
    {
        TotalLinkCount = metrics.TotalCount;
        InternalLinkCount = metrics.InternalCount;
        LinkRatio = metrics.Ratio;
        Finish(PageStatus.Completed, null, metadata, now);
    }

    /// <summary>
    /// Records a terminal page failure and any response metadata already received. The result
    /// writer decides whether this also fails the overall job.
    /// </summary>
    public void Fail(string reason, FetchMetadata? metadata, DateTimeOffset now) =>
        Finish(PageStatus.Failed, reason, metadata, now);

    /// <summary>
    /// Records a terminal unsupported-content result, such as a PDF or a missing HTML content
    /// type. Skipped pages have response metadata but no computed HTML-link ratio.
    /// </summary>
    public void Skip(string reason, FetchMetadata? metadata, DateTimeOffset now) =>
        Finish(PageStatus.Skipped, reason, metadata, now);

    /// <summary>
    /// Applies the shared terminal fields used by Complete, Fail, and Skip. A nullable metadata
    /// value means the request failed before a response was available.
    /// </summary>
    private void Finish(PageStatus status, string? error, FetchMetadata? metadata, DateTimeOffset now)
    {
        Status = status;
        Error = error;
        HttpStatus = metadata?.HttpStatus;
        ContentType = metadata?.ContentType;
        FinalUrl = metadata?.FinalUrl;
        CompletedAt = now;
    }

    /// <summary>
    /// Builds the first Pending page at depth zero with no parent. The API saves it atomically
    /// with the new job and root task.
    /// </summary>
    public static Page CreateRoot(long jobId, string normalizedUrl) => new()
    {
        JobId = jobId,
        Url = normalizedUrl,
        Depth = 0,
        Status = PageStatus.Pending
    };
}
