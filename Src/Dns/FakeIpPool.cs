using System.Net;
using System.Net.Sockets;

namespace Clash.Dns;

/// <summary>
/// Clash/mihomo-style Fake-IP pool (198.18.0.0/16).
/// Reserves .0–.3 (network / gateway / DNS / spare); allocates from .4 like mihomo.
/// Evicts least-recently-used mappings when the pool is full.
/// </summary>
internal sealed class FakeIpPool
{
    // 198.18.0.0/16 — benchmarking range used by Clash fake-ip
    private const byte Net0 = 198;
    private const byte Net1 = 18;

    /// <summary>DNS hijack address (mihomo: gateway+1 when TUN is 198.18.0.1/30).</summary>
    public static readonly IPAddress DnsAddress = IPAddress.Parse("198.18.0.2");

    private const uint FirstHost = 4;
    private const uint LastHost = 0xFFFE;
    /// <summary>Soft cap before LRU eviction (leave headroom under /16).</summary>
    private const int MaxMappings = 8192;

    private readonly object _gate = new();
    private readonly Dictionary<string, IPAddress> _hostToIp = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<IPAddress, string> _ipToHost = new();
    private readonly LinkedList<string> _lru = new();
    private readonly Dictionary<string, LinkedListNode<string>> _lruNodes =
        new(StringComparer.OrdinalIgnoreCase);
    private uint _next = FirstHost;

    public bool IsFakeIp(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork)
            return false;
        var b = ip.GetAddressBytes();
        if (b[0] != Net0 || b[1] != Net1)
            return false;
        var host = (uint)(b[2] << 8 | b[3]);
        return host >= FirstHost;
    }

    public bool IsReservedDns(IPAddress ip) => ip.Equals(DnsAddress);

    public IPAddress Lookup(string host)
    {
        host = Normalize(host);
        lock (_gate)
        {
            if (_hostToIp.TryGetValue(host, out var existing))
            {
                TouchLru(host);
                return existing;
            }

            if (_hostToIp.Count >= MaxMappings)
                EvictOldest();

            for (var attempt = 0; attempt < 65530; attempt++)
            {
                var ip = HostToIp(_next);
                _next++;
                if (_next > LastHost)
                    _next = FirstHost;

                var b = ip.GetAddressBytes();
                if (b[3] is 0 or 255)
                    continue;
                if (_ipToHost.ContainsKey(ip))
                    continue;

                _hostToIp[host] = ip;
                _ipToHost[ip] = host;
                var node = _lru.AddFirst(host);
                _lruNodes[host] = node;
                return ip;
            }

            // Still exhausted after wrap — force eviction and retry once.
            EvictOldest();
            for (var attempt = 0; attempt < 1024; attempt++)
            {
                var ip = HostToIp(_next);
                _next++;
                if (_next > LastHost)
                    _next = FirstHost;
                var b = ip.GetAddressBytes();
                if (b[3] is 0 or 255)
                    continue;
                if (_ipToHost.ContainsKey(ip))
                    continue;

                _hostToIp[host] = ip;
                _ipToHost[ip] = host;
                var node = _lru.AddFirst(host);
                _lruNodes[host] = node;
                return ip;
            }

            throw new InvalidOperationException("Fake-IP pool exhausted");
        }
    }

    public string? LookBack(IPAddress ip)
    {
        lock (_gate)
        {
            if (!_ipToHost.TryGetValue(ip, out var host))
                return null;
            TouchLru(host);
            return host;
        }
    }

    private void TouchLru(string host)
    {
        if (!_lruNodes.TryGetValue(host, out var node))
            return;
        _lru.Remove(node);
        _lru.AddFirst(node);
    }

    private void EvictOldest()
    {
        var last = _lru.Last;
        if (last is null)
            return;
        var host = last.Value;
        _lru.RemoveLast();
        _lruNodes.Remove(host);
        if (_hostToIp.Remove(host, out var ip))
            _ipToHost.Remove(ip);
    }

    private static string Normalize(string host) =>
        host.Trim().TrimEnd('.').ToLowerInvariant();

    private static IPAddress HostToIp(uint hostBits) =>
        new([Net0, Net1, (byte)(hostBits >> 8), (byte)hostBits]);
}
