using System.Net;
using System.Net.Sockets;

using Clash.Config;
using Clash.Net;
using Clash.ProxyNet.Trojan;
using Clash.ProxyNet.Vless;

namespace Clash.ProxyNet;

/// <summary>
/// Dials a destination through a VLESS/Trojan node (REALITY + Vision supported).
/// Replaces QuickProxyNet.Proxy.ConnectAsync for NanoClash.
/// </summary>
internal static class ProxyConnect
{
    public static async Task<Stream> DialAsync(
        ProxyNode node,
        string dialHost,
        string destHost,
        int destPort,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout > TimeSpan.Zero)
            cts.CancelAfter(timeout);

        var tcp = await ConnectTcpAsync(dialHost, node.Port, cts.Token).ConfigureAwait(false);
        try
        {
            return node.Type.ToLowerInvariant() switch
            {
                "vless" => await VlessDialer
                    .ConnectAsync(tcp, FromVlessNode(node, dialHost), destHost, destPort, cts.Token)
                    .ConfigureAwait(false),
                "trojan" => await TrojanDialer
                    .ConnectAsync(tcp, FromTrojanNode(node, dialHost), destHost, destPort, cts.Token)
                    .ConfigureAwait(false),
                _ => throw new NotSupportedException("Outbound type " + node.Type),
            };
        }
        catch
        {
            await tcp.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static VlessOptions FromVlessNode(ProxyNode node, string dialHost)
    {
        var security = !string.IsNullOrEmpty(node.RealityPublicKey) ? VlessSecurity.Reality
            : node.Tls ? VlessSecurity.Tls
            : VlessSecurity.None;

        return new VlessOptions
        {
            Id = node.Uuid ?? throw new InvalidOperationException("VLESS node missing uuid"),
            Host = dialHost,
            Port = node.Port,
            Security = security,
            Transport = string.IsNullOrEmpty(node.Network) ? "tcp" : node.Network,
            Path = node.WsPath,
            HostHeader = node.WsHost,
            Sni = node.ServerName,
            Flow = node.Flow,
            Fingerprint = node.ClientFingerprint,
            RealityPublicKey = node.RealityPublicKey,
            RealityShortId = node.RealityShortId,
            AllowInsecure = node.SkipCertVerify,
        };
    }

    private static TrojanOptions FromTrojanNode(ProxyNode node, string dialHost) => new()
    {
        Password = node.Password ?? throw new InvalidOperationException("Trojan node missing password"),
        Host = dialHost,
        Port = node.Port,
        Transport = string.IsNullOrEmpty(node.Network) ? "tcp" : node.Network,
        Path = node.WsPath,
        HostHeader = node.WsHost,
        Sni = node.ServerName,
        AllowInsecure = node.SkipCertVerify,
    };

    private static async Task<NetworkStream> ConnectTcpAsync(string host, int port, CancellationToken ct)
    {
        if (port is <= 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));

        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
            LingerState = new LingerOption(true, 0),
        };

        try
        {
            InterfaceBinder.Bind(socket);
            if (IPAddress.TryParse(host, out var ip))
            {
                await socket.ConnectAsync(new IPEndPoint(ip, port), ct).ConfigureAwait(false);
            }
            else
            {
                await socket.ConnectAsync(host, port, ct).ConfigureAwait(false);
            }

            return new NetworkStream(socket, ownsSocket: true);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            socket.Dispose();
            throw new ProxyProtocolException(
                ProxyErrorCode.Timeout,
                $"Connection to proxy {host}:{port} timed out.");
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
