using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

using Clash.Utils;

namespace Clash.Rules;

internal enum RuleAction : byte
{
    Reject = 1,
    Direct = 2,
    Proxy = 3,
}

/// <summary>Domain rules database (CFWR rules.bin).</summary>
internal sealed class RuleDb : IDisposable
{
    private const byte FlagSuffix = 0x80;
    private const byte Sep = 0x01;

    private readonly Dictionary<string, byte> _records;
    private readonly TtlLru<RuleAction> _cache = new();

    private RuleDb(Dictionary<string, byte> records) => _records = records;

    public int Count => _records.Count;

    /// <summary>Logical name of the embedded CFWR database (see NanoClash.csproj).</summary>
    public const string EmbeddedResourceName = "NanoClash.Rules.bin";

    /// <summary>
    /// Load rules: optional sidecar <c>Rules.bin</c> beside the exe overrides the embed;
    /// otherwise read the embedded resource stream in-process (never extracted to disk).
    /// </summary>
    public static RuleDb LoadDefault()
    {
        var sidecar = AppPaths.RulesBin;
        if (File.Exists(sidecar))
        {
            using var file = File.OpenRead(sidecar);
            return Load(file);
        }

        return LoadEmbedded();
    }

    public static RuleDb LoadEmbedded()
    {
        var asm = typeof(RuleDb).Assembly;
        using var stream = asm.GetManifestResourceStream(EmbeddedResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded rules database missing ({EmbeddedResourceName}). Rebuild with Res/Rules.bin present.");
        return Load(stream);
    }

    public static RuleDb LoadFile(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("rules database not found", path);
        using var stream = File.OpenRead(path);
        return Load(stream);
    }

    public static RuleDb Load(Stream stream)
    {
        // One buffer in memory — no temp file / no "extract then read".
        byte[] raw;
        if (stream is MemoryStream ms)
        {
            raw = ms.ToArray();
        }
        else
        {
            using var buf = new MemoryStream();
            stream.CopyTo(buf);
            raw = buf.ToArray();
        }

        return LoadBytes(raw);
    }

    public static RuleDb LoadBytes(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < 9 || raw[0] != (byte)'C' || raw[1] != (byte)'F' || raw[2] != (byte)'W' ||
            raw[3] != (byte)'R' || raw[4] != 1)
            throw new InvalidOperationException("rules.bin: bad magic");

        var count = BinaryPrimitives.ReadUInt32LittleEndian(raw[5..]);
        var records = new Dictionary<string, byte>((int)count, StringComparer.Ordinal);
        var off = 9;
        for (uint i = 0; i < count; i++)
        {
            if (off + 2 > raw.Length)
                throw new InvalidOperationException("rules.bin truncated");
            var keyLen = BinaryPrimitives.ReadUInt16LittleEndian(raw[off..]);
            off += 2;
            if (off + keyLen + 1 > raw.Length)
                throw new InvalidOperationException("rules.bin truncated key");
            var key = Encoding.UTF8.GetString(raw.Slice(off, keyLen));
            off += keyLen;
            records[key] = raw[off++];
        }

        var db = new RuleDb(records);
        db.SelfCheck();
        return db;
    }

    private void SelfCheck()
    {
        Check("www.baidu.com", RuleAction.Direct);
        Check("www.google.com", RuleAction.Proxy);
        Check("www.gstatic.com", RuleAction.Proxy);
        Check("fonts.gstatic.com", RuleAction.Proxy);
    }

    private void Check(string host, RuleAction want)
    {
        var got = MatchDomainUncached(host);
        if (got != want)
            throw new InvalidOperationException($"rules self-check: {host} got {got} want {want}");
    }

    public RuleAction DecideRoute(string hostPort)
    {
        var host = hostPort;
        if (TrySplitHostPort(hostPort, out var h, out _))
            host = h;
        host = host.Trim().Trim('[', ']');
        if (IPAddress.TryParse(host, out var ip))
            return ActionForIp(ip);
        return Match(host);
    }

    public RuleAction Match(string domain)
    {
        domain = domain.Trim().TrimEnd('.').ToLowerInvariant();
        if (domain.Length == 0)
            return RuleAction.Proxy;
        if (IPAddress.TryParse(domain, out var ip))
            return ActionForIp(ip);
        if (_cache.TryGet(domain, out var cached))
            return cached;
        var a = MatchDomainUncached(domain);
        _cache.Set(domain, a);
        return a;
    }

    private RuleAction MatchDomainUncached(string domain)
    {
        var parts = domain.Split('.');
        var labels = new List<string>(parts.Length);
        foreach (var p in parts)
        {
            if (p.Length > 0)
                labels.Add(p);
        }

        if (labels.Count == 0)
            return RuleAction.Proxy;

        RuleAction best = 0;
        var found = false;
        var sb = new StringBuilder();
        var accLen = 0;
        for (var i = labels.Count - 1; i >= 0; i--)
        {
            if (sb.Length > 0)
                sb.Append((char)Sep);
            sb.Append(labels[i]);
            accLen++;
            if (!_records.TryGetValue(sb.ToString(), out var packed))
                continue;
            var action = (RuleAction)(packed & 0x7F);
            var isSuffix = (packed & FlagSuffix) != 0;
            if (isSuffix || accLen == labels.Count)
            {
                best = action;
                found = true;
            }
        }

        if (!found)
            return RuleAction.Proxy;
        return best is RuleAction.Reject or RuleAction.Direct or RuleAction.Proxy
            ? best
            : RuleAction.Proxy;
    }

    internal static RuleAction ActionForIp(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || IsPrivate(ip) || IsLinkLocal(ip) || IsCarrierGradeNat(ip) ||
            ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))
            return RuleAction.Direct;
        return RuleAction.Proxy;
    }

    private static bool IsCarrierGradeNat(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork)
            return false;
        var b = ip.GetAddressBytes();
        return b[0] == 100 && b[1] >= 64 && b[1] <= 127;
    }

    private static bool IsLinkLocal(IPAddress ip)
    {
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 169 && b[1] == 254;
        }

        return ip.IsIPv6LinkLocal || ip.IsIPv6Multicast;
    }

    private static bool IsPrivate(IPAddress ip)
    {
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10 ||
                   (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
                   (b[0] == 192 && b[1] == 168);
        }

        var bytes = ip.GetAddressBytes();
        return bytes.Length == 16 && (bytes[0] & 0xfe) == 0xfc;
    }

    internal static bool TrySplitHostPort(string authority, out string host, out string port)
    {
        host = "";
        port = "";
        if (authority.StartsWith('[') || CountChar(authority, ':') == 1)
        {
            var idx = authority.LastIndexOf(':');
            if (authority.StartsWith('['))
            {
                var end = authority.IndexOf(']');
                if (end > 0 && end + 1 < authority.Length && authority[end + 1] == ':')
                {
                    host = authority[1..end];
                    port = authority[(end + 2)..];
                    return port.Length > 0;
                }
            }
            else if (idx > 0)
            {
                host = authority[..idx];
                port = authority[(idx + 1)..];
                return port.Length > 0;
            }
        }

        return false;
    }

    private static int CountChar(string s, char c)
    {
        var n = 0;
        foreach (var ch in s)
        {
            if (ch == c)
                n++;
        }

        return n;
    }

    public void Dispose() => _cache.Dispose();
}

internal static class Authority
{
    public static string WithPort(string authority, string defaultPort)
    {
        if (string.IsNullOrWhiteSpace(authority))
            throw new ArgumentException("empty authority");

        if (authority.StartsWith('[') || CountColon(authority) == 1)
        {
            if (RuleDb.TrySplitHostPort(authority, out var h, out var p))
                return JoinHostPort(h, p);
        }

        var host = authority;
        if (authority.StartsWith('[') && authority.EndsWith(']'))
            host = authority[1..^1];
        return JoinHostPort(host, defaultPort);
    }

    public static string JoinHostPort(string host, string port) =>
        host.Contains(':') && !host.StartsWith('[') ? $"[{host}]:{port}" : $"{host}:{port}";

    private static int CountColon(string s)
    {
        var n = 0;
        foreach (var ch in s)
        {
            if (ch == ':')
                n++;
        }

        return n;
    }
}
