namespace Clash.Config;

/// <summary>Parses vless:// and trojan:// share links into <see cref="ProxyNode"/>.</summary>
internal static class ShareLinkParser
{
    public static ProxyNode? TryParse(string line)
    {
        line = line.Trim();
        if (line.Length == 0)
            return null;

        try
        {
            if (line.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
                return ParseVless(line);
            if (line.StartsWith("trojan://", StringComparison.OrdinalIgnoreCase))
                return ParseTrojan(line);
        }
        catch
        {
            // skip bad share link
        }

        return null;
    }

    private static ProxyNode ParseVless(string url)
    {
        var uri = new Uri(url);
        var uuid = Uri.UnescapeDataString(uri.UserInfo);
        var qs = ParseQuery(uri.Query);
        var security = (Get(qs, "security") ?? "none").Trim().ToLowerInvariant();
        var network = (Get(qs, "type") ?? Get(qs, "network") ?? "tcp").Trim().ToLowerInvariant();
        if (network is "websocket")
            network = "ws";
        var name = string.IsNullOrEmpty(uri.Fragment)
            ? uri.Host
            : Uri.UnescapeDataString(uri.Fragment.TrimStart('#'));
        var pbk = Get(qs, "pbk");
        var sid = Get(qs, "sid");
        if (!string.IsNullOrEmpty(pbk))
            security = "reality";
        var tls = security is "tls" or "reality";

        return new ProxyNode
        {
            Name = string.IsNullOrWhiteSpace(name) ? uri.Host : name,
            Type = "vless",
            Server = uri.IdnHost,
            Port = uri.Port > 0 ? uri.Port : 443,
            Uuid = uuid,
            Tls = tls,
            ServerName = Get(qs, "sni") ?? Get(qs, "servername"),
            Flow = Get(qs, "flow"),
            Network = network,
            ClientFingerprint = Get(qs, "fp"),
            RealityPublicKey = pbk,
            RealityShortId = sid,
            SkipCertVerify = IsTruthy(Get(qs, "allowInsecure")) || IsTruthy(Get(qs, "insecure")),
            WsPath = Get(qs, "path"),
            WsHost = Get(qs, "host"),
            Security = security,
        };
    }

    private static ProxyNode ParseTrojan(string url)
    {
        var uri = new Uri(url);
        var password = Uri.UnescapeDataString(uri.UserInfo);
        var qs = ParseQuery(uri.Query);
        var security = (Get(qs, "security") ?? "tls").Trim().ToLowerInvariant();
        var network = (Get(qs, "type") ?? Get(qs, "network") ?? "tcp").Trim().ToLowerInvariant();
        if (network is "websocket")
            network = "ws";
        var name = string.IsNullOrEmpty(uri.Fragment)
            ? uri.Host
            : Uri.UnescapeDataString(uri.Fragment.TrimStart('#'));

        return new ProxyNode
        {
            Name = string.IsNullOrWhiteSpace(name) ? uri.Host : name,
            Type = "trojan",
            Server = uri.IdnHost,
            Port = uri.Port > 0 ? uri.Port : 443,
            Password = password,
            Tls = security is not "none",
            ServerName = Get(qs, "sni") ?? Get(qs, "servername"),
            Network = network,
            ClientFingerprint = Get(qs, "fp"),
            SkipCertVerify = IsTruthy(Get(qs, "allowInsecure")) || IsTruthy(Get(qs, "insecure")),
            WsPath = Get(qs, "path"),
            WsHost = Get(qs, "host"),
            Security = security,
        };
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(query))
            return map;
        var q = query.StartsWith('?') ? query[1..] : query;
        foreach (var part in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq < 0)
            {
                map[Uri.UnescapeDataString(part)] = "";
                continue;
            }

            var key = Uri.UnescapeDataString(part[..eq]);
            var val = Uri.UnescapeDataString(part[(eq + 1)..]);
            map[key] = val;
        }

        return map;
    }

    private static string? Get(Dictionary<string, string> qs, string key) =>
        qs.TryGetValue(key, out var v) ? v : null;

    private static bool IsTruthy(string? s) =>
        s is "1" or "true" or "True" or "yes";
}
