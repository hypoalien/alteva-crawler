using System.Net.Sockets;
using Crawler.Shared.Entities;
using Crawler.Worker.Http;
using Crawler.Worker.Services;
using Xunit;

namespace Crawler.Tests;

public sealed class RecoveryTests
{
    /// <summary>
    /// Guards the outage-recovery bug: nested socket errors may justify DB retry, but HTTP failures
    /// must retain their bounded budget.
    /// </summary>
    [Fact]
    public void DatabaseRecoveryDoesNotMakeHttpFailuresRetryIndefinitely()
    {
        var socket = new SocketException((int)SocketError.HostNotFound);
        Assert.True(DatabaseFailure.IsTransient(socket));
        Assert.True(DatabaseFailure.IsTransient(new InvalidOperationException("EF connection failed", socket)));
        Assert.False(DatabaseFailure.IsTransient(new HttpRequestException("HTTP DNS failed", socket)));
        Assert.False(DatabaseFailure.IsTransient(new TransientFetchException("HTTP 503", new FetchMetadata(503, "text/html"))));
        Assert.False(DatabaseFailure.IsTransient(new InvalidCrawlMessageException("Invalid schema")));
    }
}
