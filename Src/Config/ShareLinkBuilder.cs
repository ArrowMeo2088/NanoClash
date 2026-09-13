using System.Text;

namespace Clash.Config;

internal static class ShareLinkBuilder
{
    public static string Build(ProxyNode node, string dialHost)
    {
        var host = FormatHost(dialHost);
        return node.Type switch
        {
            "vless" => BuildVless(node, host),
            "trojan" => BuildTrojan(node, host),
            _ => throw new NotSupportedException("type " + node.Type),
        };
    }

    private static string FormatHost(string dialHost) =>
        dialHost.Contains(':') && !dialHost.StartsWith('[') ? $"[{dialHost}]" : dialHost;

    private static string BuildVless(ProxyNode node, string host)
    {
        var user = Uri.EscapeDataString(node.Uuid ?? "");
        var q = new List<string> { "encryption=none" };

        var security = !string.IsNullOrEmpty(node.RealityPublicKey) ? "reality"
            : node.Tls ? "tls" : "none";
        q.Add("security=" + Uri.EscapeDataString(security));

        if (!string.IsNullOrEmpty(node.ServerName))
            q.Add("sni=" + Uri.EscapeDataString(node.ServerName));
        if (!string.IsNullOrEmpty(node.ClientFingerprint))
            q.Add("fp=" + Uri.EscapeDataString(node.ClientFingerprint));

        if (security == "reality")
        {
            if (!string.IsNullOrEmpty(node.RealityPublicKey))
                q.Add("pbk=" + Uri.EscapeDataString(node.RealityPublicKey));
            if (!string.IsNullOrEmpty(node.RealityShortId))
                q.Add("sid=" + Uri.EscapeDataString(node.RealityShortId));
        }

        if (node.SkipCertVerify)
            q.Add("allowInsecure=1");

        var type = node.Network == "ws" ? "ws" : "tcp";
        q.Add("type=" + type);
        if (type == "ws")
        {
            if (!string.IsNullOrEmpty(node.WsPath))
                q.Add("path=" + Uri.EscapeDataString(node.WsPath));
            if (!string.IsNullOrEmpty(node.WsHost))
                q.Add("host=" + Uri.EscapeDataString(node.WsHost));
        }

        if (!string.IsNullOrEmpty(node.Flow))
            q.Add("flow=" + Uri.EscapeDataString(node.Flow));

        var frag = string.IsNullOrEmpty(node.Name) ? "" : "#" + Uri.EscapeDataString(node.Name);
        return $"vless://{user}@{host}:{node.Port}?{string.Join("&", q)}{frag}";
    }

    private static string BuildTrojan(ProxyNode node, string host)
    {
        var user = Uri.EscapeDataString(node.Password ?? "");
        var q = new List<string>();
        var security = node.Tls || !string.IsNullOrEmpty(node.ServerName) ? "tls" : "none";
        q.Add("security=" + Uri.EscapeDataString(security));
        if (!string.IsNullOrEmpty(node.ServerName))
            q.Add("sni=" + Uri.EscapeDataString(node.ServerName));
        if (!string.IsNullOrEmpty(node.ClientFingerprint))
            q.Add("fp=" + Uri.EscapeDataString(node.ClientFingerprint));
        if (node.SkipCertVerify)
            q.Add("allowInsecure=1");

        var type = node.Network == "ws" ? "ws" : "tcp";
        q.Add("type=" + type);
        if (type == "ws")
        {
            if (!string.IsNullOrEmpty(node.WsPath))
                q.Add("path=" + Uri.EscapeDataString(node.WsPath));
            if (!string.IsNullOrEmpty(node.WsHost))
                q.Add("host=" + Uri.EscapeDataString(node.WsHost));
        }

        var frag = string.IsNullOrEmpty(node.Name) ? "" : "#" + Uri.EscapeDataString(node.Name);
        return $"trojan://{user}@{host}:{node.Port}?{string.Join("&", q)}{frag}";
    }
}
