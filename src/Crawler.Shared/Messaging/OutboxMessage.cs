using System.Text.Json;
using Crawler.Shared.Contracts;

namespace Crawler.Shared.Messaging;

// Infrastructure bookkeeping: this row commits with the job/page changes that require delivery.
public sealed class OutboxMessage
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid? JobId { get; private set; }
    public string Destination { get; private set; } = "";
    public byte[] Body { get; private set; } = [];
    public string? Error { get; private set; }
    public string? SourceMessageId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    /// Serializes one crawl task as UTF-8 JSON and targets the work queue. The caller must save
    /// this row in the transaction that creates the corresponding page.
    /// </summary>
    public static OutboxMessage ForPage(CrawlPageRequested task) => new()
    {
        JobId = task.JobId, Destination = CrawlMessaging.QueueName,
        Body = JsonSerializer.SerializeToUtf8Bytes(task, Json), CreatedAt = task.OccurredAt
    };

    /// <summary>
    /// Retains an unsuccessful message body byte-for-byte, including invalid JSON/UTF-8, with
    /// bounded diagnostic headers. The dispatcher later publishes it to the dead-letter queue.
    /// </summary>
    public static OutboxMessage ForDeadLetter(byte[] body, string? sourceMessageId, Guid? jobId, string reason) => new()
    {
        JobId = jobId, Destination = CrawlMessaging.DeadLetterQueueName, Body = body,
        SourceMessageId = sourceMessageId is { Length: > 200 } ? sourceMessageId[..200] : sourceMessageId,
        Error = reason.Length > 2000 ? reason[..2000] : reason, CreatedAt = DateTimeOffset.UtcNow
    };
}
