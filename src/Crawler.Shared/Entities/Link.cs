namespace Crawler.Shared.Entities;

// One normalized outgoing HTTP(S) link from a source page. Its composite primary key prevents repeated anchors from creating duplicate rows; the target need not be crawled.
public sealed class Link
{
    public long SourcePageId { get; private set; }
    public string Url { get; private set; } = "";
}
