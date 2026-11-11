using System.Net;
using System.Net.Sockets;

namespace Osiris.Core;

public sealed class UnsafeUrlException(string message) : Exception(message);

/// <summary>
/// Guards against SSRF: URLs the agent reaches come from the workspace (MCP servers, http_request), and a server runs
/// next to private services. Requires https and a host that resolves only to public addresses; <c>allowPrivate</c>
/// (an agent on the user's own machine) also permits http and private addresses, for servers on localhost.
/// </summary>
// shortcut: DNS can change between this check and the connection (rebinding); pin the checked address in the HttpClient when it matters.
public static class SafeUrl
{
    /// <summary>Loopback, private, link-local (cloud metadata), CGNAT, multicast: what no workspace's request may reach.</summary>
    static readonly IPNetwork[] Blocked = [.. new[]
    {
        "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8", "169.254.0.0/16", "172.16.0.0/12", "192.168.0.0/16", "224.0.0.0/3",
        "::/128", "::1/128", "fc00::/7", "fe80::/10", "ff00::/8",
    }.Select(IPNetwork.Parse)];

    public static bool IsBlocked(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return Blocked.Any(n => n.Contains(address));
    }

    public static async Task<Uri> CheckAsync(string raw, bool allowPrivate, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var url) || url.Scheme is not ("https" or "http")) throw new UnsafeUrlException($"Invalid URL: {raw}");
        if (url.UserInfo.Length > 0) throw new UnsafeUrlException("The URL must not contain credentials.");
        if (allowPrivate) return url;
        if (url.Scheme != "https") throw new UnsafeUrlException("The URL must use https.");

        IPAddress[] addresses;
        try { addresses = IPAddress.TryParse(url.DnsSafeHost, out var ip) ? [ip] : await Dns.GetHostAddressesAsync(url.DnsSafeHost, ct); }
        catch (SocketException) { addresses = []; }
        if (addresses.Length == 0) throw new UnsafeUrlException($"The host {url.Host} doesn't resolve.");
        if (addresses.Any(IsBlocked)) throw new UnsafeUrlException($"The host {url.Host} is a private address.");
        return url;
    }
}
