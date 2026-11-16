namespace Crawler.Worker.Http;

// Worker HTTP safety settings loaded from Crawl configuration. HTTP setup validates these values; private-host exceptions are permitted only in Testing.
public sealed class CrawlOptions
{
    public int MaxResponseBytes { get; set; } = 2 * 1024 * 1024;
    public int AttemptTimeoutSeconds { get; set; } = 10;
    public int HttpRetries { get; set; } = 2;
    public int MaxRedirects { get; set; } = 5;
    public string[] AllowedPrivateHosts { get; set; } = [];
}
