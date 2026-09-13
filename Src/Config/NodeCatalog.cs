using System.Text;
using System.Text.Json;

namespace Clash.Config;

/// <summary>Multi-format subscription body → dialable <see cref="ProxyNode"/> list.</summary>
internal static class NodeCatalog
{
    public static IReadOnlyList<ProxyNode> Parse(byte[] raw)
    {
        var text = DecodeUtf8(raw);
        return ParseText(text, allowBase64Retry: true);
    }

    public static IReadOnlyList<ProxyNode> ParseFile(string path)
    {
        if (!File.Exists(path))
            return [];

        return Parse(File.ReadAllBytes(path));
    }

    private static IReadOnlyList<ProxyNode> ParseText(string text, bool allowBase64Retry)
    {
        text = text.Trim().TrimStart('\uFEFF');
        if (text.Length == 0)
            return [];

        // Prefer Clash YAML when visible.
        if (ContainsProxiesKey(text))
            return FilterAll(ProxyYamlLoader.LoadFromText(text));

        // JSON (Xray / sing-box / SIP008)
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            var fromJson = TryParseJson(trimmed);
            if (fromJson.Count > 0)
                return fromJson;
        }

        // URI lines
        if (LooksLikeUriList(text))
            return FilterAll(ParseUriList(text));

        // Base64 wrapper
        if (allowBase64Retry && TryDecodeBase64(text, out var decoded))
        {
            var inner = ParseText(decoded, allowBase64Retry: false);
            if (inner.Count > 0)
                return inner;
            if (LooksLikeUriList(decoded))
                return FilterAll(ParseUriList(decoded));
            if (ContainsProxiesKey(decoded))
                return FilterAll(ProxyYamlLoader.LoadFromText(decoded));
        }

        return [];
    }

    private static List<ProxyNode> FilterAll(IEnumerable<ProxyNode> nodes)
    {
        var list = new List<ProxyNode>();
        foreach (var n in nodes)
        {
            if (NodeFilter.Accept(n, out _))
                list.Add(NormalizeNetwork(n));
        }

        return list;
    }

    private static ProxyNode NormalizeNetwork(ProxyNode n)
    {
        var net = (n.Network ?? "tcp").Trim().ToLowerInvariant();
        if (net is "websocket")
            net = "ws";
        if (net is not "ws")
            net = "tcp";
        if (net == n.Network)
            return n;
        return new ProxyNode
        {
            Name = n.Name,
            Type = n.Type,
            Server = n.Server,
            Port = n.Port,
            Uuid = n.Uuid,
            Password = n.Password,
            Tls = n.Tls,
            ServerName = n.ServerName,
            Flow = n.Flow,
            Network = net,
            ClientFingerprint = n.ClientFingerprint,
            RealityPublicKey = n.RealityPublicKey,
            RealityShortId = n.RealityShortId,
            SkipCertVerify = n.SkipCertVerify,
            WsPath = n.WsPath,
            WsHost = n.WsHost,
            Security = n.Security,
        };
    }

    private static List<ProxyNode> ParseUriList(string text)
    {
        var nodes = new List<ProxyNode>();
        foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            // Tolerate multiple links on one line.
            var parts = line.Contains("://", StringComparison.Ordinal)
                ? SplitUriTokens(line)
                : [line];
            foreach (var part in parts)
            {
                var node = ShareLinkParser.TryParse(part);
                if (node is not null)
                    nodes.Add(node);
            }
        }

        return nodes;
    }

    private static IEnumerable<string> SplitUriTokens(string line)
    {
        if (!line.Contains(' ') && !line.Contains('\t'))
        {
            yield return line;
            yield break;
        }

        foreach (var p in line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
            yield return p;
    }

    private static List<ProxyNode> TryParseJson(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("outbounds", out var outbounds) &&
                outbounds.ValueKind == JsonValueKind.Array)
            {
                var list = new List<ProxyNode>();
                foreach (var ob in outbounds.EnumerateArray())
                {
                    ProxyNode? n = null;
                    if (ob.TryGetProperty("protocol", out _))
                        n = TryMapXrayOutbound(ob);
                    else if (ob.TryGetProperty("type", out _))
                        n = TryMapSingBoxOutbound(ob);
                    if (n is not null)
                        list.Add(n);
                }

                return FilterAll(list);
            }

            // SIP008 — SS only; skip (no dial support).
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("servers", out var servers) &&
                servers.ValueKind == JsonValueKind.Array)
                return [];
        }
        catch
        {
            // ignore JSON parse failures
        }

        return [];
    }

    private static ProxyNode? TryMapXrayOutbound(JsonElement ob)
    {
        var protocol = ob.GetProperty("protocol").GetString()?.Trim().ToLowerInvariant();
        if (protocol is not ("vless" or "trojan"))
            return null;

        var tag = ob.TryGetProperty("tag", out var tagEl) ? tagEl.GetString() : null;
        if (!ob.TryGetProperty("settings", out var settings))
            return null;

        string? server = null;
        int port = 0;
        string? uuid = null;
        string? password = null;
        string? flow = null;

        if (protocol == "vless" && settings.TryGetProperty("vnext", out var vnext) &&
            vnext.ValueKind == JsonValueKind.Array && vnext.GetArrayLength() > 0)
        {
            var v = vnext[0];
            server = v.TryGetProperty("address", out var a) ? a.GetString() : null;
            port = v.TryGetProperty("port", out var p) ? p.GetInt32() : 0;
            if (v.TryGetProperty("users", out var users) && users.GetArrayLength() > 0)
            {
                var u = users[0];
                uuid = u.TryGetProperty("id", out var id) ? id.GetString() : null;
                flow = u.TryGetProperty("flow", out var f) ? f.GetString() : null;
            }
        }
        else if (protocol == "trojan" && settings.TryGetProperty("servers", out var servers) &&
                 servers.ValueKind == JsonValueKind.Array && servers.GetArrayLength() > 0)
        {
            var s = servers[0];
            server = s.TryGetProperty("address", out var a) ? a.GetString() : null;
            port = s.TryGetProperty("port", out var p) ? p.GetInt32() : 0;
            password = s.TryGetProperty("password", out var pw) ? pw.GetString() : null;
        }

        if (string.IsNullOrWhiteSpace(server) || port <= 0)
            return null;

        string network = "tcp";
        string? sni = null;
        string? fp = null;
        string? pbk = null;
        string? sid = null;
        string security = "none";
        bool tls = false;
        string? wsPath = null;
        string? wsHost = null;
        bool skip = false;

        if (ob.TryGetProperty("streamSettings", out var stream))
        {
            network = stream.TryGetProperty("network", out var n) ? (n.GetString() ?? "tcp") : "tcp";
            var sec = stream.TryGetProperty("security", out var s) ? (s.GetString() ?? "none") : "none";
            security = sec.ToLowerInvariant();
            tls = security is "tls" or "reality";
            if (security == "reality" && stream.TryGetProperty("realitySettings", out var rs))
            {
                sni = rs.TryGetProperty("serverName", out var sn) ? sn.GetString() : null;
                fp = rs.TryGetProperty("fingerprint", out var f) ? f.GetString() : null;
                pbk = rs.TryGetProperty("publicKey", out var pk) ? pk.GetString() : null;
                sid = rs.TryGetProperty("shortId", out var si) ? si.GetString() : null;
            }
            else if (security == "tls" && stream.TryGetProperty("tlsSettings", out var ts))
            {
                sni = ts.TryGetProperty("serverName", out var sn) ? sn.GetString() : null;
                fp = ts.TryGetProperty("fingerprint", out var f) ? f.GetString() : null;
                skip = ts.TryGetProperty("allowInsecure", out var ai) && ai.ValueKind == JsonValueKind.True;
            }

            if ((network is "ws" or "websocket") && stream.TryGetProperty("wsSettings", out var ws))
            {
                network = "ws";
                wsPath = ws.TryGetProperty("path", out var path) ? path.GetString() : null;
                if (ws.TryGetProperty("headers", out var headers) &&
                    headers.TryGetProperty("Host", out var host))
                    wsHost = host.GetString();
            }
        }

        return new ProxyNode
        {
            Name = string.IsNullOrWhiteSpace(tag) ? server! : tag!,
            Type = protocol!,
            Server = server!,
            Port = port,
            Uuid = uuid,
            Password = password,
            Tls = tls,
            ServerName = sni,
            Flow = flow,
            Network = network,
            ClientFingerprint = fp,
            RealityPublicKey = pbk,
            RealityShortId = sid,
            SkipCertVerify = skip,
            WsPath = wsPath,
            WsHost = wsHost,
            Security = security,
        };
    }

    private static ProxyNode? TryMapSingBoxOutbound(JsonElement ob)
    {
        var type = ob.GetProperty("type").GetString()?.Trim().ToLowerInvariant();
        if (type is not ("vless" or "trojan"))
            return null;

        var tag = ob.TryGetProperty("tag", out var tagEl) ? tagEl.GetString() : null;
        var server = ob.TryGetProperty("server", out var s) ? s.GetString() : null;
        var port = ob.TryGetProperty("server_port", out var p) ? p.GetInt32() : 0;
        if (string.IsNullOrWhiteSpace(server) || port <= 0)
            return null;

        var uuid = ob.TryGetProperty("uuid", out var u) ? u.GetString() : null;
        var password = ob.TryGetProperty("password", out var pw) ? pw.GetString() : null;
        var flow = ob.TryGetProperty("flow", out var f) ? f.GetString() : null;

        string network = "tcp";
        string? wsPath = null;
        string? wsHost = null;
        if (ob.TryGetProperty("transport", out var transport) &&
            transport.ValueKind == JsonValueKind.Object)
        {
            var tType = transport.TryGetProperty("type", out var tt) ? tt.GetString() : "tcp";
            network = (tType ?? "tcp").ToLowerInvariant();
            if (network is "ws" or "websocket")
            {
                network = "ws";
                wsPath = transport.TryGetProperty("path", out var path) ? path.GetString() : null;
                if (transport.TryGetProperty("headers", out var headers) &&
                    headers.TryGetProperty("Host", out var host))
                    wsHost = host.GetString();
            }
        }

        string security = "none";
        bool tls = false;
        string? sni = null;
        string? fp = null;
        string? pbk = null;
        string? sid = null;
        bool skip = false;
        if (ob.TryGetProperty("tls", out var tlsEl) && tlsEl.ValueKind == JsonValueKind.Object)
        {
            var enabled = !tlsEl.TryGetProperty("enabled", out var en) || en.ValueKind != JsonValueKind.False;
            if (enabled)
            {
                tls = true;
                security = "tls";
                sni = tlsEl.TryGetProperty("server_name", out var sn) ? sn.GetString() : null;
                if (tlsEl.TryGetProperty("utls", out var utls) &&
                    utls.TryGetProperty("fingerprint", out var fgp))
                    fp = fgp.GetString();
                skip = tlsEl.TryGetProperty("insecure", out var insc) && insc.ValueKind == JsonValueKind.True;
                if (tlsEl.TryGetProperty("reality", out var reality) &&
                    reality.ValueKind == JsonValueKind.Object)
                {
                    var rEn = !reality.TryGetProperty("enabled", out var re) || re.ValueKind != JsonValueKind.False;
                    if (rEn)
                    {
                        security = "reality";
                        pbk = reality.TryGetProperty("public_key", out var pk) ? pk.GetString() : null;
                        sid = reality.TryGetProperty("short_id", out var si) ? si.GetString() : null;
                    }
                }
            }
        }

        return new ProxyNode
        {
            Name = string.IsNullOrWhiteSpace(tag) ? server! : tag!,
            Type = type!,
            Server = server!,
            Port = port,
            Uuid = uuid,
            Password = password,
            Tls = tls,
            ServerName = sni,
            Flow = flow,
            Network = network,
            ClientFingerprint = fp,
            RealityPublicKey = pbk,
            RealityShortId = sid,
            SkipCertVerify = skip,
            WsPath = wsPath,
            WsHost = wsHost,
            Security = security,
        };
    }

    private static bool ContainsProxiesKey(string text) =>
        text.Contains("proxies:", StringComparison.Ordinal);

    private static bool LooksLikeUriList(string text)
    {
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith('#'))
                continue;
            if (t.Contains("://", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool TryDecodeBase64(string text, out string decoded)
    {
        decoded = "";
        var compact = text.Replace("\r", "").Replace("\n", "").Replace(" ", "").Replace("\t", "");
        if (compact.Length < 16)
            return false;
        try
        {
            var bytes = Convert.FromBase64String(PadBase64(compact));
            decoded = Encoding.UTF8.GetString(bytes);
            return decoded.Length > 0 &&
                   (decoded.Contains("://", StringComparison.Ordinal) ||
                    decoded.Contains("proxies:", StringComparison.Ordinal) ||
                    decoded.TrimStart().StartsWith('{'));
        }
        catch
        {
            try
            {
                var bytes = Convert.FromBase64String(PadBase64(compact.Replace('-', '+').Replace('_', '/')));
                decoded = Encoding.UTF8.GetString(bytes);
                return decoded.Length > 0;
            }
            catch
            {
                return false;
            }
        }
    }

    private static string PadBase64(string s)
    {
        var mod = s.Length % 4;
        return mod == 0 ? s : s + new string('=', 4 - mod);
    }

    private static string DecodeUtf8(byte[] raw)
    {
        if (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF)
            return Encoding.UTF8.GetString(raw, 3, raw.Length - 3);
        return Encoding.UTF8.GetString(raw);
    }
}
