using System.Text;

namespace Clash.Config;

/// <summary>
/// Minimal Clash YAML reader for the <c>proxies:</c> list only.
/// Hand-rolled for NativeAOT / full trim (no YAML library).
/// Filtering is applied by <see cref="NodeCatalog"/> / <see cref="NodeFilter"/>.
/// </summary>
internal static class ProxyYamlLoader
{
    public static IReadOnlyList<ProxyNode> LoadFromText(string text)
    {
        try
        {
            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var entries = ParseProxiesSection(lines);
            var nodes = new List<ProxyNode>(entries.Count);
            foreach (var p in entries)
            {
                var type = (p.Get("type") ?? "").Trim().ToLowerInvariant();
                if (type is not ("vless" or "trojan"))
                    continue;

                var network = (p.Get("network") ?? "tcp").Trim().ToLowerInvariant();
                var name = p.Get("name");
                var server = p.Get("server");
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(server) ||
                    !int.TryParse(p.Get("port"), out var port) || port <= 0)
                    continue;

                if (type == "vless" && string.IsNullOrWhiteSpace(p.Get("uuid")))
                    continue;

                if (type == "trojan" && string.IsNullOrWhiteSpace(p.Get("password")))
                    continue;

                var net = network is "ws" or "websocket" ? "ws" : network;
                var hasReality = !string.IsNullOrEmpty(p.Get("reality-opts.public-key"));
                var tls = ParseBool(p.Get("tls")) || type == "trojan" || hasReality;
                var security = hasReality ? "reality" : tls ? "tls" : "none";

                nodes.Add(new ProxyNode
                {
                    Name = name!,
                    Type = type,
                    Server = server!,
                    Port = port,
                    Uuid = p.Get("uuid"),
                    Password = p.Get("password"),
                    Tls = tls,
                    ServerName = p.Get("servername") ?? p.Get("sni"),
                    Flow = p.Get("flow"),
                    Network = net,
                    ClientFingerprint = p.Get("client-fingerprint"),
                    RealityPublicKey = p.Get("reality-opts.public-key"),
                    RealityShortId = p.Get("reality-opts.short-id"),
                    SkipCertVerify = ParseBool(p.Get("skip-cert-verify")),
                    WsPath = p.Get("ws-opts.path"),
                    WsHost = p.Get("ws-opts.headers.Host") ?? p.Get("ws-opts.headers.host"),
                    Security = security,
                });
            }

            return nodes;
        }
        catch
        {
            return [];
        }
    }

    private static bool ParseBool(string? s) =>
        s is not null &&
        (s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1" ||
         s.Equals("yes", StringComparison.OrdinalIgnoreCase));

    private static List<YamlMap> ParseProxiesSection(string[] lines)
    {
        var result = new List<YamlMap>();
        var i = 0;
        while (i < lines.Length && lines[i].TrimEnd() != "proxies:")
            i++;
        if (i >= lines.Length)
            return result;
        i++;

        YamlMap? current = null;
        var nest = new List<(int Indent, string Prefix)>();

        while (i < lines.Length)
        {
            var raw = lines[i++];
            if (string.IsNullOrWhiteSpace(raw) || raw.TrimStart().StartsWith('#'))
                continue;

            if (raw.Length > 0 && raw[0] is not (' ' or '\t') &&
                raw.TrimEnd().EndsWith(':') && !raw.TrimStart().StartsWith('-'))
                break;

            var indent = CountIndent(raw);
            var content = raw.Trim();

            if (content.StartsWith("- "))
            {
                if (current is not null)
                    result.Add(current);
                current = new YamlMap();
                nest.Clear();
                var rest = content[2..].Trim();
                if (rest.Length > 0)
                {
                    // Clash flow-style: - { name: 'x', type: trojan, server: host, port: 1, ... }
                    if (rest[0] == '{')
                        ApplyFlowMap(current, rest);
                    else
                        ApplyPair(current, nest, indent + 2, rest);
                }

                continue;
            }

            if (current is null)
                continue;

            ApplyPair(current, nest, indent, content);
        }

        if (current is not null)
            result.Add(current);
        return result;
    }

    private static void ApplyPair(
        YamlMap map, List<(int Indent, string Prefix)> nest, int indent, string content)
    {
        var colon = content.IndexOf(':');
        if (colon < 0)
            return;

        var key = content[..colon].Trim();
        var val = Unquote(content[(colon + 1)..].Trim());

        while (nest.Count > 0 && indent <= nest[^1].Indent)
            nest.RemoveAt(nest.Count - 1);

        var prefix = nest.Count == 0 ? "" : nest[^1].Prefix + ".";
        var fullKey = prefix + key;

        if (string.IsNullOrEmpty(val))
            nest.Add((indent, fullKey));
        else
            map.Set(fullKey, val);
    }

    /// <summary>Parse a single-line flow map <c>{ k: v, k2: 'v2' }</c> into flat keys.</summary>
    private static void ApplyFlowMap(YamlMap map, string flow)
    {
        var s = flow.Trim();
        if (s.Length >= 2 && s[0] == '{')
            s = s[1..];
        if (s.Length >= 1 && s[^1] == '}')
            s = s[..^1];

        foreach (var part in SplitFlowEntries(s))
        {
            var colon = IndexOfTopLevelColon(part);
            if (colon <= 0)
                continue;
            var key = part[..colon].Trim();
            var val = Unquote(part[(colon + 1)..].Trim());
            if (key.Length == 0)
                continue;
            // Nested flow values (reality-opts: { ... }) — flatten one level of simple k:v.
            if (val.Length >= 2 && val[0] == '{' && val[^1] == '}')
            {
                var inner = new YamlMap();
                ApplyFlowMap(inner, val);
                foreach (var (ik, iv) in inner.Entries)
                    map.Set(key + "." + ik, iv);
            }
            else
            {
                map.Set(key, val);
            }
        }
    }

    private static List<string> SplitFlowEntries(string s)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        var depth = 0;
        char? quote = null;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (quote is char q)
            {
                sb.Append(c);
                if (c == q && (i == 0 || s[i - 1] != '\\'))
                    quote = null;
                continue;
            }

            if (c is '\'' or '"')
            {
                quote = c;
                sb.Append(c);
                continue;
            }

            if (c == '{')
            {
                depth++;
                sb.Append(c);
                continue;
            }

            if (c == '}')
            {
                if (depth > 0)
                    depth--;
                sb.Append(c);
                continue;
            }

            if (c == ',' && depth == 0)
            {
                var piece = sb.ToString().Trim();
                if (piece.Length > 0)
                    parts.Add(piece);
                sb.Clear();
                continue;
            }

            sb.Append(c);
        }

        var last = sb.ToString().Trim();
        if (last.Length > 0)
            parts.Add(last);
        return parts;
    }

    private static int IndexOfTopLevelColon(string s)
    {
        var depth = 0;
        char? quote = null;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (quote is char q)
            {
                if (c == q && (i == 0 || s[i - 1] != '\\'))
                    quote = null;
                continue;
            }

            if (c is '\'' or '"')
            {
                quote = c;
                continue;
            }

            if (c == '{')
            {
                depth++;
                continue;
            }

            if (c == '}')
            {
                if (depth > 0)
                    depth--;
                continue;
            }

            if (c == ':' && depth == 0)
                return i;
        }

        return -1;
    }

    private static int CountIndent(string s)
    {
        var n = 0;
        while (n < s.Length && s[n] == ' ')
            n++;
        return n;
    }

    private static string Unquote(string s)
    {
        if (s.Length >= 2 &&
            ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\'')))
            return s[1..^1];
        return s;
    }

    private sealed class YamlMap
    {
        private readonly Dictionary<string, string> _map = new(StringComparer.Ordinal);

        public void Set(string key, string value) => _map[key] = value;
        public string? Get(string key) => _map.TryGetValue(key, out var v) ? v : null;
        public IEnumerable<(string Key, string Value)> Entries =>
            _map.Select(kv => (kv.Key, kv.Value));
    }
}
