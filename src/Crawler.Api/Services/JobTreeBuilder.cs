using Crawler.Shared.Entities;

namespace Crawler.Api.Services;

// Response-only shape: Children comes from first-discovery parent rows, not another HTTP crawl.
public sealed record PageTreeNode(string Url, string? FinalUrl, int Depth, string Status, int? HttpStatus, string? ContentType,
    int TotalLinkCount, int InternalLinkCount, decimal? LinkRatio, string? Error,
    DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, IReadOnlyList<PageTreeNode> Children);

public static class JobTreeBuilder
{
    /// <summary>
    /// Turns flat database pages into nested response objects using ParentPageId. A lookup avoids
    /// querying the database again for every child; no page HTML is included.
    /// </summary>
    public static IReadOnlyList<PageTreeNode> Build(IReadOnlyCollection<Page> pages)
    {
        var children = pages.ToLookup(page => page.ParentPageId);
        // Recursively copies one page and its children into a response node. Stops at the supported
        // depth limit and sorts siblings by URL for a predictable display.
        PageTreeNode BuildNode(Page page, int level) => new(page.Url, page.FinalUrl, page.Depth, page.Status.ToString(),
            page.HttpStatus, page.ContentType, page.TotalLinkCount, page.InternalLinkCount,
            page.LinkRatio, page.Error, page.StartedAt, page.CompletedAt,
            level >= 5 ? [] : children[page.Id].OrderBy(child => child.Url, StringComparer.Ordinal)
                .Select(child => BuildNode(child, level + 1)).ToArray());
        return children[null].Select(page => BuildNode(page, 0)).ToArray();
    }
}
