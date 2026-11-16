namespace Crawler.Shared.Entities;

public enum JobStatus { Pending, Running, Completed, Failed, Canceled }

// A persisted crawl request/lifecycle. Its methods change this object; a service must save the surrounding transaction.
public sealed class Job
{
    // Id is the internal SQL key; ExtId is the UUID exposed to the browser and message correlation.
    public long Id { get; private set; }
    public Guid ExtId { get; private set; }
    public string Url { get; private set; } = "";
    public JobStatus Status { get; private set; }
    public int MaxDepth { get; private set; }
    public int MaxPages { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public string? Error { get; private set; }

    /// <summary>
    /// Moves a Pending job to Running and records the supplied UTC time. Terminal jobs stay
    /// terminal; the worker uses an equivalent conditional SQL update for visible progress.
    /// </summary>
    public void Start(DateTimeOffset now)
    {
        if (Status != JobStatus.Pending) return;
        Status = JobStatus.Running;
        StartedAt = now;
    }

    /// <summary>
    /// Marks an active job Completed once its frontier has drained. Canceled, failed, or already
    /// completed jobs cannot be changed by a late completion.
    /// </summary>
    public void Complete(DateTimeOffset now)
    {
        if (Status is not (JobStatus.Pending or JobStatus.Running)) return;
        Status = JobStatus.Completed;
        CompletedAt = now;
    }

    /// <summary>
    /// Marks an active job Failed with a reason and end time. The worker uses this for root-page
    /// failure; a failed child does not automatically fail the whole job.
    /// </summary>
    public void Fail(string reason, DateTimeOffset now)
    {
        if (Status is not (JobStatus.Pending or JobStatus.Running)) return;
        Status = JobStatus.Failed;
        Error = reason;
        CompletedAt = now;
    }

    /// <summary>
    /// Marks an active job Canceled and records its end time, returning true only when it changed
    /// state. The command service separately updates unfinished pages in the same transaction.
    /// </summary>
    public bool Cancel(DateTimeOffset now)
    {
        if (Status is not (JobStatus.Pending or JobStatus.Running)) return false;
        Status = JobStatus.Canceled;
        CompletedAt = now;
        return true;
    }

    /// <summary>
    /// Constructs a Pending job with a new public UUID after validating depth/page limits. This
    /// only creates the C# object; the command service is responsible for saving it.
    /// </summary>
    public static Job Create(string normalizedUrl, int maxDepth, int maxPages, DateTimeOffset now)
    {
        if (maxDepth is < 0 or > 5)
            throw new ArgumentOutOfRangeException(nameof(maxDepth), "Maximum depth must be between 0 and 5.");
        if (maxPages is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(maxPages), "Maximum pages must be between 1 and 1000.");

        return new Job
        {
            ExtId = Guid.NewGuid(),
            Url = normalizedUrl,
            Status = JobStatus.Pending,
            MaxDepth = maxDepth,
            MaxPages = maxPages,
            CreatedAt = now
        };
    }
}
