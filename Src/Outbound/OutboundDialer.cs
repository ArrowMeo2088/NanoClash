using System.Net;
using System.Net.Sockets;

using Clash.Config;
using Clash.Dns;
using Clash.ProxyNet;
using Clash.Rules;

namespace Clash.Outbound;

internal sealed class OutboundDialer : IDisposable
{
    private static readonly TimeSpan DialTimeout = TimeSpan.FromSeconds(15);

    private readonly DohResolver _doh;
    private ProxyNode? _current;

    public OutboundDialer(DohResolver doh) => _doh = doh;

    public ProxyNode? Current => Volatile.Read(ref _current);

    public void SetCurrent(ProxyNode? node) => Volatile.Write(ref _current, node);

    public Task<Stream> DialAsync(string hostPort, CancellationToken ct)
    {
        var node = Current ?? throw new InvalidOperationException("No proxy node selected");
        if (!RuleDb.TrySplitHostPort(hostPort, out var destHost, out var portStr))
        {
            var idx = hostPort.LastIndexOf(':');
            if (idx <= 0)
                throw new ArgumentException("invalid host:port", nameof(hostPort));
            destHost = hostPort[..idx].Trim('[', ']');
            portStr = hostPort[(idx + 1)..];
        }

        destHost = destHost.Trim('[', ']');
        if (!int.TryParse(portStr, out var destPort))
            throw new ArgumentException("invalid port", nameof(hostPort));

        return DialViaAsync(node, destHost, destPort, ct);
    }

    public async Task<Stream> DialViaAsync(ProxyNode node, string host, int port, CancellationToken ct)
    {
        var dialHost = await ResolveNodeHostAsync(node.Server, ct).ConfigureAwait(false);
        return await ProxyConnect
            .DialAsync(node, dialHost, host, port, DialTimeout, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Resolve hostname to IPv4 via DoH (TUN Direct out of Fake-IP).</summary>
    public Task<IPAddress?> ResolveHostIpv4Async(string host, CancellationToken ct = default) =>
        ResolveHostToIpv4Async(host, ct);

    private async Task<IPAddress?> ResolveHostToIpv4Async(string host, CancellationToken ct)
    {
        host = host.Trim().TrimEnd('.');
        if (IPAddress.TryParse(host, out var lit))
        {
            if (lit.AddressFamily != AddressFamily.InterNetwork)
                return null;
            return lit;
        }

        try
        {
            var ips = await _doh.ResolveAsync(host, ct).ConfigureAwait(false);
            return ips.FirstOrDefault(i => i.AddressFamily == AddressFamily.InterNetwork);
        }
        catch
        {
            return null;
        }
    }

    private async Task<string> ResolveNodeHostAsync(string server, CancellationToken ct)
    {
        var ip = await ResolveHostToIpv4Async(server, ct).ConfigureAwait(false);
        if (ip is null)
            throw new InvalidOperationException("DoH no A record for node " + server);
        return ip.ToString();
    }

    /// <summary>Resolve current/selected node server to IPv4 for TUN anti-loop host routes.</summary>
    public async Task<IPAddress?> ResolveNodeIpv4Async(ProxyNode? node, CancellationToken ct = default)
    {
        if (node is null || node.IsSubscriptionInfo)
            return null;
        var host = await ResolveNodeHostAsync(node.Server, ct).ConfigureAwait(false);
        return IPAddress.Parse(host);
    }

    public void Dispose() => _doh.Dispose();
}
