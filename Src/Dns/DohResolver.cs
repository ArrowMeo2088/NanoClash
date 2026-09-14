using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text.Json;

using Clash.Json;
using Clash.Net;
using Clash.Utils;

namespace Clash.Dns;

/// <summary>AliDNS JSON DoH primary (+ DNSPod backup). Used for node address resolution only.</summary>
internal sealed class DohResolver : IDisposable
{
    private const string PrimaryUrl = "https://223.5.5.5/resolve";
    private const string PrimaryHost = "dns.alidns.com";
    private const string PrimaryDial = "223.5.5.5:443";

    private const string BackupUrl = "https://doh.pub/dns-query";
    private const string BackupHost = "doh.pub";
    private const string BackupDial = "1.12.12.12:443";

    private static readonly TimeSpan NegTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MinPosTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxPosTtl = TimeSpan.FromHours(1);
    private static readonly TimeSpan DefaultPosTtl = TimeSpan.FromSeconds(300);
    private const int DnsTypeA = 1;

    private readonly HttpClient _primary;
    private readonly HttpClient _backup;
    private readonly TtlLru<IPAddress[]?> _cache = new();
    private readonly ConcurrentDictionary<string, Task<(IPAddress[] Ips, TimeSpan Ttl)>> _inflight = new(StringComparer.Ordinal);
    private bool _disposed;

    public DohResolver()
    {
        _primary = CreateClient(PrimaryDial, PrimaryHost);
        _backup = CreateClient(BackupDial, BackupHost);
    }

    private static HttpClient CreateClient(string dialAddr, string sni)
    {
        var handler = new SocketsHttpHandler
        {
            Proxy = null,
            // Belt-and-suspenders with DirectNetwork.Configure().
            UseProxy = false,
            ConnectCallback = async (context, ct) =>
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    SocketUtil.ConfigureNoDelay(socket);
                    Clash.Net.InterfaceBinder.Bind(socket);
                    await socket.ConnectAsync(IPEndPoint.Parse(dialAddr), ct).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
            SslOptions = new SslClientAuthenticationOptions
            {
                TargetHost = sni,
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 |
                                      System.Security.Authentication.SslProtocols.Tls13,
            },
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
    }

    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string name, CancellationToken ct)
    {
        name = name.Trim().TrimEnd('.');
        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("empty hostname", nameof(name));
        if (IPAddress.TryParse(name, out var literal))
            return [literal];

        var cacheKey = name.ToLowerInvariant();
        if (_cache.TryGet(cacheKey, out var cached))
        {
            if (cached is null || cached.Length == 0)
                throw new InvalidOperationException("DoH negative cache");
            return (IPAddress[])cached.Clone();
        }

        var task = _inflight.GetOrAdd(cacheKey, static (key, state) =>
            state.ResolveUncachedAsync(key), this);
        try
        {
            var (ips, _) = await task.WaitAsync(ct).ConfigureAwait(false);
            return (IPAddress[])ips.Clone();
        }
        finally
        {
            if (task.IsCompleted)
                _inflight.TryRemove(new KeyValuePair<string, Task<(IPAddress[] Ips, TimeSpan Ttl)>>(cacheKey, task));
        }
    }

    private async Task<(IPAddress[] Ips, TimeSpan Ttl)> ResolveUncachedAsync(string cacheKey)
    {
        if (_cache.TryGet(cacheKey, out var cached))
        {
            if (cached is null || cached.Length == 0)
                throw new InvalidOperationException("DoH negative cache");
            return (cached, DefaultPosTtl);
        }

        using var linked = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var name = cacheKey;
        Exception? err = null;
        try
        {
            var primary = await ResolveUpstreamAsync(_primary, PrimaryUrl, "primary", name, linked.Token)
                .ConfigureAwait(false);
            if (primary.Ips.Count > 0)
            {
                var arr = primary.Ips.ToArray();
                _cache.Set(cacheKey, arr, ClampPosTtl(primary.Ttl));
                return (arr, ClampPosTtl(primary.Ttl));
            }

            err = new InvalidOperationException("primary: empty answer");
        }
        catch (Exception ex)
        {
            err = ex;
        }

        try
        {
            var backup = await ResolveUpstreamAsync(_backup, BackupUrl, "backup", name, linked.Token)
                .ConfigureAwait(false);
            if (backup.Ips.Count > 0)
            {
                var arr = backup.Ips.ToArray();
                _cache.Set(cacheKey, arr, ClampPosTtl(backup.Ttl));
                return (arr, ClampPosTtl(backup.Ttl));
            }

            err = new InvalidOperationException("backup: empty answer");
        }
        catch (Exception ex)
        {
            err = ex;
        }

        _cache.Set(cacheKey, null, NegTtl);
        throw new InvalidOperationException("DoH: " + (err?.Message ?? $"no A for {name}"), err);
    }

    private static TimeSpan ClampPosTtl(TimeSpan ttl)
    {
        if (ttl < MinPosTtl)
            return MinPosTtl;
        if (ttl > MaxPosTtl)
            return MaxPosTtl;
        return ttl;
    }

    private static async Task<(IReadOnlyList<IPAddress> Ips, TimeSpan Ttl)> ResolveUpstreamAsync(
        HttpClient client, string baseUrl, string upName, string host, CancellationToken ct)
    {
        // Node dials are IPv4-only (avoid half-broken dual-stack paths).
        var (ips, ttlSec) = await LookupTypeAsync(client, baseUrl, upName, host, DnsTypeA, ct)
            .ConfigureAwait(false);
        if (ips.Count == 0)
            throw new InvalidOperationException($"{upName}: no A for {host}");

        var ttl = ttlSec > 0 ? TimeSpan.FromSeconds(ttlSec) : DefaultPosTtl;
        return (ips, ttl);
    }

    private static async Task<(List<IPAddress> Ips, int MinTtlSec)> LookupTypeAsync(
        HttpClient client, string baseUrl, string upName, string host, int qtype, CancellationToken ct)
    {
        var typeName = "A";
        var url = $"{baseUrl}?name={Uri.EscapeDataString(host)}&type={typeName}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Accept", "application/dns-json");
        using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{upName} HTTP {(int)resp.StatusCode}");

        await using var body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var limited = new LimitedReadStream(body, 256 * 1024);
        var parsed = await JsonSerializer.DeserializeAsync(limited, ClashJsonContext.Default.DohJsonResponse, ct)
            .ConfigureAwait(false);
        if (parsed is null)
            throw new InvalidOperationException($"{upName}: bad JSON");
        if (parsed.Status != 0)
            throw new InvalidOperationException($"{upName} rcode {parsed.Status}");

        var list = new List<IPAddress>();
        if (parsed.Answer is null)
            throw new InvalidOperationException($"{upName} no type {qtype} answer");
        var minTtl = int.MaxValue;
        foreach (var ans in parsed.Answer)
        {
            if (ans.Type != qtype || ans.Data is null)
                continue;
            if (ans.TTL > 0 && ans.TTL < minTtl)
                minTtl = ans.TTL;
            if (IPAddress.TryParse(ans.Data.Trim(), out var ip))
                list.Add(ip);
        }

        if (list.Count == 0)
            throw new InvalidOperationException($"{upName} no type {qtype} answer");
        return (list, minTtl == int.MaxValue ? 300 : minTtl);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _primary.Dispose();
        _backup.Dispose();
        _cache.Dispose();
    }

    private sealed class LimitedReadStream(Stream inner, long max) : Stream
    {
        private long _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_read >= max)
                return 0;
            var n = inner.Read(buffer, offset, (int)Math.Min(count, max - _read));
            _read += n;
            return n;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_read >= max)
                return 0;
            var slice = buffer[..(int)Math.Min(buffer.Length, max - _read)];
            var n = await inner.ReadAsync(slice, ct).ConfigureAwait(false);
            _read += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
