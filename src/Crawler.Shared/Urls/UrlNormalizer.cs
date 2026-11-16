namespace Crawler.Shared.Urls;

public static class UrlNormalizer
{
    // Leave space for the bigint key in PostgreSQL's composite btree entries.
    public const int MaxUrlLength = 2000;

    /// <summary>
    /// Resolves an HTML href against the page/base URL, then applies the root normalization rules.
    /// Returns false for empty/unsupported links so extraction can skip them without throwing.
    /// </summary>
    public static bool TryNormalizeLink(Uri pageUrl, string? href, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(href) || !Uri.TryCreate(pageUrl, href.Trim(), out var uri)) return false;
        try { normalized = NormalizeRoot(uri.AbsoluteUri); return true; }
        catch (ArgumentException) { return false; }
    }

    /// <summary>
    /// Validates an absolute HTTP(S) URL and produces the database/deduplication key: normalized
    /// host/port, no fragment, but preserved path/query identity. Throws ArgumentException for
    /// invalid or oversized input.
    /// </summary>
    public static string NormalizeRoot(string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            string.IsNullOrEmpty(uri.Host) || !uri.IsWellFormedOriginalString() ||
            uri.UserInfo.Length > 0)
            throw new ArgumentException("Enter an absolute HTTP(S) URL without credentials.", nameof(url));

        var normalized = new UriBuilder(uri)
        {
            Scheme = uri.Scheme.ToLowerInvariant(),
            Host = uri.IdnHost.ToLowerInvariant(),
            Port = uri.IsDefaultPort ? -1 : uri.Port,
            Fragment = ""
        }.Uri.AbsoluteUri;

        if (normalized.Length > MaxUrlLength)
            throw new ArgumentException($"Normalized URL must be at most {MaxUrlLength} characters.", nameof(url));
        return normalized;
    }
}
