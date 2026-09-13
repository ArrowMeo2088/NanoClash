using System.Net;
using System.Net.Sockets;

using Clash.Net;
using Clash.Rules;
using Clash.Utils;

namespace Clash.IO;

/// <summary>Direct TCP dial via system DNS (Happy Eyeballs); binds physical NIC when set.</summary>
internal static class DirectDial
{
    private static readonly TimeSpan DialTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan HappyEyeballsDelay = TimeSpan.FromMilliseconds(250);
    private const int MaxParallelDials = 2;

    public static async Task<NetworkStream> ConnectAsync(string hostPort, CancellationToken ct)
    {
        if (!RuleDb.TrySplitHostPort(hostPort, out var host, out var portStr) &&
            !TrySplitLoose(hostPort, out host, out portStr))
            throw new ArgumentException("invalid host:port", nameof(hostPort));
        if (!int.TryParse(portStr, out var port))
            throw new ArgumentException("invalid port", nameof(hostPort));

        host = host.Trim('[', ']');
        if (IPAddress.TryParse(host, out var literal))
        {
            var sock = await DialSocketAsync(literal, port, TimeSpan.Zero, ct).ConfigureAwait(false);
            return new NetworkStream(sock, ownsSocket: true);
        }

        // System DNS (OS resolver), not DoH.
        var ips = await System.Net.Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        if (ips.Length == 0)
            throw new SocketException((int)SocketError.HostNotFound);
        return await HappyEyeballsAsync(ips, port, ct).ConfigureAwait(false);
    }

    private static bool TrySplitLoose(string hostPort, out string host, out string port)
    {
        host = "";
        port = "";
        var idx = hostPort.LastIndexOf(':');
        if (idx <= 0)
            return false;
        host = hostPort[..idx];
        port = hostPort[(idx + 1)..];
        return port.Length > 0;
    }

    private static async Task<NetworkStream> HappyEyeballsAsync(
        IPAddress[] ips, int port, CancellationToken ct)
    {
        if (ips.Length == 1)
        {
            var sock = await DialSocketAsync(ips[0], port, TimeSpan.Zero, ct).ConfigureAwait(false);
            return new NetworkStream(sock, ownsSocket: true);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var gate = new SemaphoreSlim(MaxParallelDials, MaxParallelDials);
        var tasks = new List<Task<Socket>>(ips.Length);
        for (var i = 0; i < ips.Length; i++)
        {
            var ip = ips[i];
            var delay = HappyEyeballsDelay * i;
            tasks.Add(DialWithGateAsync(ip, port, delay, gate, linked.Token));
        }

        Exception? last = null;
        while (tasks.Count > 0)
        {
            var done = await Task.WhenAny(tasks).ConfigureAwait(false);
            tasks.Remove(done);
            try
            {
                var sock = await done.ConfigureAwait(false);
                linked.Cancel();
                _ = Task.Run(async () =>
                {
                    foreach (var t in tasks)
                    {
                        try
                        {
                            (await t.ConfigureAwait(false)).Dispose();
                        }
                        catch
                        {
                            // ignore
                        }
                    }
                });
                return new NetworkStream(sock, ownsSocket: true);
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        throw last ?? new SocketException((int)SocketError.HostUnreachable);
    }

    private static async Task<Socket> DialWithGateAsync(
        IPAddress ip, int port, TimeSpan delay, SemaphoreSlim gate, CancellationToken ct)
    {
        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, ct).ConfigureAwait(false);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await DialSocketAsync(ip, port, TimeSpan.Zero, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<Socket> DialSocketAsync(
        IPAddress ip, int port, TimeSpan delay, CancellationToken ct)
    {
        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, ct).ConfigureAwait(false);
        var sock = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            SocketUtil.ConfigureNoDelay(sock);
            InterfaceBinder.Bind(sock);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(DialTimeout);
            await sock.ConnectAsync(new IPEndPoint(ip, port), timeout.Token).ConfigureAwait(false);
            return sock;
        }
        catch
        {
            sock.Dispose();
            throw;
        }
    }
}
