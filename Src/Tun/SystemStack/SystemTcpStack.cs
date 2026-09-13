using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;

using Clash.Dns;
using Clash.IO;
using Clash.Outbound;
using Clash.Rules;
using Clash.Tun.Ip;
using Clash.Tun.Windows;

namespace Clash.Tun.SystemStack;

/// <summary>
/// sing-tun System stack: Listen on TUN IP + packet NAT + DNS Fake-IP hijack + Accept → Proxy/Direct.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class SystemTcpStack : IDisposable
{
    private readonly WintunDevice _device;
    private readonly RuleDb _rules;
    private readonly OutboundDialer _outbound;
    private readonly FakeIpPool _fakeIp;
    private readonly TcpNat _nat = new(TimeSpan.FromMinutes(5));
    private readonly byte[] _rxBuf = new byte[WintunNative.MaxIpPacketSize];
    private readonly byte[] _dnsOutBuf = new byte[WintunNative.MaxIpPacketSize];
    private readonly object _sendGate = new();
    private readonly object _relayGate = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _relayCts;
    private Task? _packetLoop;
    private Task? _acceptLoop;
    private int _generation;
    private bool _disposed;

    public IPAddress ServerAddress { get; } = TunRouteConfigurator.TunServer;
    public IPAddress ClientAddress { get; } = TunRouteConfigurator.TunClient;
    public int ListenPort { get; private set; }

    public SystemTcpStack(
        WintunDevice device,
        RuleDb rules,
        OutboundDialer outbound,
        FakeIpPool fakeIp)
    {
        _device = device;
        _rules = rules;
        _outbound = outbound;
        _fakeIp = fakeIp;
    }

    public void Start()
    {
        FirewallHelper.AllowThisProcess();
        Exception? last = null;
        for (var i = 0; i < 10; i++)
        {
            try
            {
                _listener = new TcpListener(ServerAddress, 0);
                _listener.ExclusiveAddressUse = true;
                _listener.Start();
                last = null;
                break;
            }
            catch (Exception ex)
            {
                last = ex;
                try
                {
                    _listener?.Stop();
                }
                catch
                {
                    // ignore
                }

                _listener = null;
                Thread.Sleep(300);
            }
        }

        if (_listener is null)
            throw new InvalidOperationException(
                $"Failed to listen on {ServerAddress} (is TUN IP configured?)", last);

        ListenPort = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _cts = new CancellationTokenSource();
        _relayCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var ct = _cts.Token;
        _packetLoop = Task.Run(() => PacketLoop(ct), CancellationToken.None);
        _acceptLoop = Task.Run(() => AcceptLoop(ct), CancellationToken.None);
    }

    public void BumpGeneration()
    {
        Interlocked.Increment(ref _generation);
        lock (_relayGate)
        {
            var parent = _cts;
            if (parent is null)
                return;
            var old = _relayCts;
            _relayCts = CancellationTokenSource.CreateLinkedTokenSource(parent.Token);
            try
            {
                old?.Cancel();
            }
            catch
            {
                // ignore
            }

            old?.Dispose();
        }
    }

    private CancellationToken RelayToken
    {
        get
        {
            lock (_relayGate)
                return _relayCts?.Token ?? CancellationToken.None;
        }
    }

    private void PacketLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_disposed)
        {
            try
            {
                var n = _device.TryReceive(_rxBuf, out var closed);
                if (closed)
                    break;
                if (n == 0)
                {
                    _device.WaitForPacket(250);
                    continue;
                }

                ProcessPacket(_rxBuf.AsSpan(0, n));
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                if (ct.IsCancellationRequested || _disposed)
                    break;
                _device.WaitForPacket(250);
            }
        }
    }

    private void ProcessPacket(Span<byte> packet)
    {
        if (!Ipv4.TryParse(packet, out var ipHdrLen, out var proto, out var src, out var dst, out var totalLen))
            return;

        if (proto == Ipv4.ProtoUdp)
        {
            var udpSpan = packet.Slice(ipHdrLen, totalLen - ipHdrLen);
            if (!UdpHeader.TryParse(udpSpan, out var srcPort, out var dstPort, out var payloadLen))
                return;
            if (dstPort == 53)
                HandleDnsQuery(src, dst, srcPort, udpSpan.Slice(UdpHeader.HeaderLen, payloadLen));
            return;
        }

        if (proto != Ipv4.ProtoTcp)
            return;

        var tcpSpan = packet.Slice(ipHdrLen, totalLen - ipHdrLen);
        if (!TcpHeader.TryParse(tcpSpan, out var tcpSrcPort, out var tcpDstPort, out _, out _))
            return;

        if (src.Equals(ServerAddress) && tcpSrcPort == ListenPort)
        {
            var session = _nat.LookupBack(tcpDstPort);
            if (session is null)
                return;
            Ipv4.SetAddresses(packet[..totalLen], ipHdrLen, session.Destination.Address, session.Source.Address);
            TcpHeader.SetPorts(tcpSpan, (ushort)session.Destination.Port, (ushort)session.Source.Port);
            TcpHeader.UpdateChecksum(
                packet[..totalLen],
                ipHdrLen,
                session.Destination.Address,
                session.Source.Address);
            lock (_sendGate)
                _device.Send(packet[..totalLen]);
            return;
        }

        if (dst.Equals(ServerAddress) && tcpDstPort == ListenPort)
            return;

        if (!IsGlobalUnicast(dst) || IsTunPrefix(dst))
            return;

        var sourceEp = new IPEndPoint(src, tcpSrcPort);
        var destEp = new IPEndPoint(dst, tcpDstPort);
        ushort natPort;
        try
        {
            natPort = _nat.Lookup(sourceEp, destEp);
        }
        catch
        {
            return;
        }

        Ipv4.SetAddresses(packet[..totalLen], ipHdrLen, ClientAddress, ServerAddress);
        TcpHeader.SetPorts(tcpSpan, natPort, (ushort)ListenPort);
        TcpHeader.UpdateChecksum(packet[..totalLen], ipHdrLen, ClientAddress, ServerAddress);
        lock (_sendGate)
            _device.Send(packet[..totalLen]);
    }

    private void HandleDnsQuery(
        IPAddress src,
        IPAddress dst,
        ushort srcPort,
        ReadOnlySpan<byte> dnsPayload)
    {
        if (!DnsMessage.TryParseQuery(dnsPayload, out var id, out var qname, out var qtype))
            return;

        var answer = BuildFakeDnsAnswer(id, qname, qtype, out var fake);

        // Build a clean IPv4 header (avoid copying options / stale TTL).
        const int outIpHdr = 20;
        var ipTotal = outIpHdr + UdpHeader.HeaderLen + answer.Length;
        if (ipTotal > _dnsOutBuf.Length)
            return;

        var ip = _dnsOutBuf.AsSpan(0, outIpHdr);
        ip.Clear();
        ip[0] = 0x45;
        ip[8] = 64; // TTL
        ip[9] = Ipv4.ProtoUdp;
        Ipv4.SetAddresses(ip, outIpHdr, dst, src);
        Ipv4.SetTotalLength(_dnsOutBuf.AsSpan(0, ipTotal), (ushort)ipTotal);
        UdpHeader.Write(
            _dnsOutBuf.AsSpan(outIpHdr, UdpHeader.HeaderLen + answer.Length),
            53,
            srcPort,
            answer,
            dst,
            src);

        lock (_sendGate)
            _device.Send(_dnsOutBuf.AsSpan(0, ipTotal));
    }

    private byte[] BuildFakeDnsAnswer(ushort id, string qname, ushort qtype, out IPAddress? fake)
    {
        fake = null;
        if (qtype == DnsMessage.TypeA)
        {
            fake = _fakeIp.Lookup(qname);
            return DnsMessage.BuildAResponse(id, qname, fake);
        }

        // No AAAA / other records — empty success so clients use A / Fake-IP path.
        return DnsMessage.BuildEmptyResponse(id, qname, qtype);
    }

    private static bool IsTunPrefix(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork)
            return false;
        var b = ip.GetAddressBytes();
        return b[0] == 172 && b[1] == 19 && b[2] == 0 && b[3] <= 3;
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is not null)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch
            {
                if (ct.IsCancellationRequested)
                    break;
                continue;
            }

            var gen = Volatile.Read(ref _generation);
            var relayCt = RelayToken;
            _ = Task.Run(() => HandleAcceptedAsync(client, gen, relayCt), CancellationToken.None);
        }
    }

    private async Task HandleAcceptedAsync(TcpClient client, int gen, CancellationToken ct)
    {
        try
        {
            using (client)
            {
                if (client.Client.RemoteEndPoint is not IPEndPoint remote)
                    return;
                var session = _nat.LookupBack((ushort)remote.Port);
                if (session is null)
                {
                    return;
                }

                if (gen != Volatile.Read(ref _generation) || ct.IsCancellationRequested)
                    return;

                var dstIp = session.Destination.Address;
                var dstPort = session.Destination.Port;
                await using var local = client.GetStream();

                // DNS over TCP → Fake-IP (do not Direct to corporate resolvers).
                if (dstPort == 53)
                {
                    await HandleDnsOverTcpAsync(local, ct).ConfigureAwait(false);
                    return;
                }

                // Block public DoH/DoT so browsers fall back to system DNS → Fake-IP.
                if ((dstPort is 443 or 853) && DohBlocklist.IsBlocked(dstIp))
                    return;

                var host = _fakeIp.LookBack(dstIp);
                // mihomo: Fake-IP without mapping → drop (never fall through to ActionForIp).
                if (host is null && _fakeIp.IsFakeIp(dstIp))
                {
                    return;
                }

                string routeKey;
                RuleAction action;
                if (host is not null)
                {
                    routeKey = host;
                    action = _rules.Match(host);
                }
                else
                {
                    routeKey = dstIp.ToString();
                    action = RuleDb.ActionForIp(dstIp);
                }

                Stream dest;
                switch (action)
                {
                    case RuleAction.Reject:
                        return;
                    case RuleAction.Direct:
                        dest = await DialDirectAsync(host, dstIp, dstPort, ct).ConfigureAwait(false);
                        break;
                    default:
                        // Prefer domain so VLESS metadata / SNI-side dial matches HTTP inbound.
                        var proxyTarget = host is not null
                            ? $"{host}:{dstPort}"
                            : $"{dstIp}:{dstPort}";
                        dest = await _outbound.DialAsync(proxyTarget, ct).ConfigureAwait(false);
                        break;
                }

                await using (dest)
                {
                    // Do not TlsHelloCoalesce here: Dial already completed REALITY, and System TCP
                    // often delivers a partial first segment that coalesce mis-reads as a full
                    // record (e.g. 517B). Relay's 64KiB read feeds Vision a full ClientHello.
                    await Relay.CopyBidirectionalAsync(local, dest, ct)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // stop / generation
        }
        catch
        {
            // ignore relay failures
        }
    }

    private async Task HandleDnsOverTcpAsync(Stream stream, CancellationToken ct)
    {
        var lenBuf = new byte[2];
        while (!ct.IsCancellationRequested)
        {
            if (!await ReadExactAsync(stream, lenBuf, ct).ConfigureAwait(false))
                return;
            var msgLen = (lenBuf[0] << 8) | lenBuf[1];
            if (msgLen is < 12 or > 4096)
                return;

            var msg = new byte[msgLen];
            if (!await ReadExactAsync(stream, msg, ct).ConfigureAwait(false))
                return;
            if (!DnsMessage.TryParseQuery(msg, out var id, out var qname, out var qtype))
                return;

            var answer = BuildFakeDnsAnswer(id, qname, qtype, out var fake);

            var outLen = answer.Length;
            var frame = new byte[2 + outLen];
            frame[0] = (byte)(outLen >> 8);
            frame[1] = (byte)outLen;
            answer.CopyTo(frame.AsSpan(2));
            await stream.WriteAsync(frame, ct).ConfigureAwait(false);
        }
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buf, CancellationToken ct)
    {
        var off = 0;
        while (off < buf.Length)
        {
            var n = await stream.ReadAsync(buf.AsMemory(off, buf.Length - off), ct).ConfigureAwait(false);
            if (n == 0)
                return false;
            off += n;
        }

        return true;
    }

    private async Task<Stream> DialDirectAsync(
        string? host, IPAddress dstIp, int dstPort, CancellationToken ct)
    {
        if (host is not null && _fakeIp.IsFakeIp(dstIp))
        {
            var real = await _outbound.ResolveHostIpv4Async(host, ct).ConfigureAwait(false);
            if (real is null)
                throw new InvalidOperationException("Direct DoH failed for " + host);
            return await DirectDial.ConnectAsync($"{real}:{dstPort}", ct).ConfigureAwait(false);
        }

        return await DirectDial.ConnectAsync($"{dstIp}:{dstPort}", ct).ConfigureAwait(false);
    }

    private static bool IsGlobalUnicast(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork)
            return false;
        var b = ip.GetAddressBytes();
        if (b[0] == 0)
            return false;
        if (b[0] == 127)
            return false;
        if (b[0] == 169 && b[1] == 254)
            return false;
        if (b[0] >= 224)
            return false;
        return true;
    }

    /// <summary>Cancel accept/relays and stop the listener (does not wait for loops).</summary>
    public void RequestStop()
    {
        if (_disposed)
            return;

        try
        {
            _cts?.Cancel();
        }
        catch
        {
            // ignore
        }

        lock (_relayGate)
        {
            try
            {
                _relayCts?.Cancel();
            }
            catch
            {
                // ignore
            }
        }

        try
        {
            _listener?.Stop();
        }
        catch
        {
            // ignore
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        RequestStop();

        // After WinTUN EndSession (preferred) or Cancel, loops exit within one WaitForPacket slice.
        try
        {
            Task.WhenAll(
                    _packetLoop ?? Task.CompletedTask,
                    _acceptLoop ?? Task.CompletedTask)
                .Wait(TimeSpan.FromMilliseconds(400));
        }
        catch
        {
            // ignore
        }

        _nat.Dispose();
        lock (_relayGate)
        {
            _relayCts?.Dispose();
            _relayCts = null;
        }

        _cts?.Dispose();
        _cts = null;
        _listener = null;
    }
}
