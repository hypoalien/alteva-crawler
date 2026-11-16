namespace Crawler.Shared.Urls;

public sealed record LinkMetrics(int TotalCount, int InternalCount, decimal Ratio);

public static class LinkClassifier
{
    /// <summary>
    /// Compares the target host with the INITIAL job host, ignoring case. Protocol changes do not
    /// make a link external; subdomains and www variants do.
    /// </summary>
    public static bool IsInternal(Uri root, Uri target) =>
        StringComparer.OrdinalIgnoreCase.Equals(root.IdnHost, target.IdnHost);
}

public static class DomainLinkRatio
{
    /// <summary>
    /// Takes already-normalized HTTP(S) links, counts distinct URLs, and divides internal count by
    /// total. Empty pages get zero; persisted ratios are rounded to six decimal places.
    /// </summary>
    public static LinkMetrics Calculate(IEnumerable<string> links, Uri root)
    {
        var distinct = links.Distinct(StringComparer.Ordinal).ToArray();
        var internalCount = distinct.Count(url => LinkClassifier.IsInternal(root, new Uri(url)));
        return new(distinct.Length, internalCount,
            distinct.Length == 0 ? 0 : decimal.Round((decimal)internalCount / distinct.Length, 6));
    }
}
