using AngleSharp.Html.Parser;
using Crawler.Shared.Urls;

namespace Crawler.Worker.Http;

public sealed class HtmlLinkExtractor
{
    /// <summary>
    /// Parses HTML with AngleSharp, applies a valid base href if present, and resolves anchor links
    /// against the final response URL. Returns sorted distinct HTTP(S) URLs; malformed/non-HTTP
    /// links are skipped.
    /// </summary>
    public async Task<IReadOnlyList<string>> ExtractAsync(string html, Uri pageUrl, CancellationToken ct)
    {
        using var document = await new HtmlParser().ParseDocumentAsync(html, ct);
        var baseHref = document.QuerySelector("base[href]")?.GetAttribute("href");
        var effectiveBase = UrlNormalizer.TryNormalizeLink(pageUrl, baseHref, out var normalizedBase)
            ? new Uri(normalizedBase) : pageUrl;
        var links = new HashSet<string>(StringComparer.Ordinal);
        foreach (var anchor in document.QuerySelectorAll("a[href]"))
            if (UrlNormalizer.TryNormalizeLink(effectiveBase, anchor.GetAttribute("href"), out var normalized))
                links.Add(normalized);
        return links.Order(StringComparer.Ordinal).ToArray();
    }
}
