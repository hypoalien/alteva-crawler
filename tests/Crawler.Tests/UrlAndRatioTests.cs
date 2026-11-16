using Crawler.Shared.Urls;
using Xunit;

namespace Crawler.Tests;

public sealed class UrlAndRatioTests
{
    /// <summary>
    /// Verifies canonical URL keys without discarding meaningful query strings, trailing slashes,
    /// or non-default ports.
    /// </summary>
    [Theory]
    [InlineData("HTTPS://Example.COM:443/docs/../?b=2&a=1#top", "https://example.com/?b=2&a=1")]
    [InlineData("http://EXAMPLE.com:80", "http://example.com/")]
    [InlineData("https://example.com:8443/a/", "https://example.com:8443/a/")]
    public void CanonicalizesEquivalentRootUrlsWhilePreservingResources(string input, string expected) =>
        Assert.Equal(expected, UrlNormalizer.NormalizeRoot(input));

    /// <summary>
    /// Verifies directory/root/fragment href resolution, which determines both duplicate detection
    /// and the saved page tree.
    /// </summary>
    [Theory]
    [InlineData("../about#team", "https://example.com/about")]
    [InlineData("/products?q=1#top", "https://example.com/products?q=1")]
    [InlineData("#team", "https://example.com/docs/start")]
    public void ResolvesRelativeLinksAndRemovesFragments(string href, string expected)
    {
        Assert.True(UrlNormalizer.TryNormalizeLink(new Uri("https://example.com/docs/start"), href, out var normalized));
        Assert.Equal(expected, normalized);
    }

    /// <summary>
    /// Verifies unsupported schemes and embedded credentials are rejected for submitted URLs and
    /// extracted links alike.
    /// </summary>
    [Theory]
    [InlineData("mailto:a@example.com")]
    [InlineData("tel:123")]
    [InlineData("javascript:void(0)")]
    [InlineData("https://user:secret@example.com/")]
    public void RejectsUnsupportedDestinations(string value)
    {
        Assert.Throws<ArgumentException>(() => UrlNormalizer.NormalizeRoot(value));
        Assert.False(UrlNormalizer.TryNormalizeLink(new Uri("https://example.com/"), value, out _));
    }

    /// <summary>
    /// Checks a hand-computed 2/3 ratio, duplicate removal, and subdomain exclusion rather than
    /// deriving expected values from production code.
    /// </summary>
    [Fact]
    public void CountsDistinctExactHostLinksAndRoundsToSixPlaces()
    {
        string[] links = ["https://example.com/a", "https://example.com/a", "http://example.com/b", "https://sub.example.com/"];
        Assert.Equal(new LinkMetrics(3, 2, 0.666667m), DomainLinkRatio.Calculate(links, new Uri("https://example.com/")));
    }

    /// <summary>
    /// Protects the zero-link denominator and the exact all-internal/all-external boundaries,
    /// including the distinct www host.
    /// </summary>
    [Fact]
    public void HandlesEmptyAllInternalAndAllExternalLinkSets()
    {
        var root = new Uri("https://example.com/");
        Assert.Equal(new LinkMetrics(0, 0, 0), DomainLinkRatio.Calculate([], root));
        Assert.Equal(new LinkMetrics(1, 1, 1), DomainLinkRatio.Calculate(["http://example.com/a"], root));
        Assert.Equal(new LinkMetrics(1, 0, 0), DomainLinkRatio.Calculate(["https://www.example.com/"], root));
    }
}
