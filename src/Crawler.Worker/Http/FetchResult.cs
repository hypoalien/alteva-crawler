using Crawler.Shared.Entities;

namespace Crawler.Worker.Http;

// FetchResult carries temporary HTML plus response metadata. The exception types distinguish permanent failure, unsupported-content skip, and retryable failure without losing received headers.
public sealed record FetchResult(Uri FinalUrl, FetchMetadata Metadata, string Html);
public class PermanentFetchException(string message, FetchMetadata? metadata = null) : Exception(message)
{
    public FetchMetadata? Metadata { get; } = metadata;
}
public sealed class UnsupportedContentException(FetchMetadata metadata)
    : PermanentFetchException($"Unsupported content type: {metadata.ContentType ?? "missing"}.", metadata);
public sealed class TransientFetchException(string message, FetchMetadata? metadata = null,
    TimeSpan? retryAfter = null, Exception? inner = null) : HttpRequestException(message, inner)
{
    // The handler retains this typed exception through retries; no separate fault envelope is necessary.
    public FetchMetadata? Metadata { get; } = metadata;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
