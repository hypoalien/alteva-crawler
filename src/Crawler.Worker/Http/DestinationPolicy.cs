using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace Crawler.Worker.Http;

public sealed class DestinationPolicy(IOptions<CrawlOptions> options)
{
    /// <summary>
    /// Resolves a hostname and applies the blocked-address policy unless the explicit Testing
    /// fixture allowance applies. DNS errors are wrapped as HTTP failures for bounded fetch retries.
    /// </summary>
    public async Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
    {
        IPAddress[] addresses;
        try { addresses = await Dns.GetHostAddressesAsync(host, ct); }
        catch (SocketException error) { throw new HttpRequestException("DNS lookup failed.", error); }
        var allowPrivate = options.Value.AllowedPrivateHosts.Contains(host, StringComparer.OrdinalIgnoreCase);
        if (addresses.Length == 0 || !allowPrivate && addresses.Any(IsPrivate))
            throw new PermanentFetchException("Destination must resolve to public IP addresses.");
        return addresses;
    }

    /// <summary>
    /// Opens the HTTP socket using only addresses that passed the destination policy, preventing
    /// an unchecked second DNS resolution. The returned stream owns and closes its socket.
    /// </summary>
    public async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var addresses = await ResolveAsync(context.DnsEndPoint.Host, ct);
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        try
        {
            // Use exactly the checked addresses rather than performing another DNS lookup.
            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch { socket.Dispose(); throw; }
    }

    /// <summary>
    /// Blocks the listed private/reserved IPv4 ranges and IPv6 outside global-unicast 2000::/3.
    /// IPv4-mapped IPv6 is checked as IPv4. This is the crawler's address policy, not an exhaustive
    /// classification of every special-purpose network range.
    /// </summary>
    public static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return (bytes[0] & 0xe0) != 0x20; // Only global unicast IPv6 (2000::/3).
        return bytes[0] is 0 or 10 or 127 || bytes[0] >= 224 ||
            bytes[0] == 169 && bytes[1] == 254 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
            bytes[0] == 192 && (bytes[1] == 168 || bytes[1] == 0 && bytes[2] is 0 or 2) ||
            bytes[0] == 100 && bytes[1] is >= 64 and <= 127 || bytes[0] == 198 && bytes[1] is 18 or 19 ||
            bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100 ||
            bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113;
    }
}
