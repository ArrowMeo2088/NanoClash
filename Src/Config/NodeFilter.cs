namespace Clash.Config;

/// <summary>Central filter: only dialable trojan / compliant vless nodes.</summary>
internal static class NodeFilter
{
    public static bool Accept(ProxyNode node, out string? reason)
    {
        reason = null;
        var type = (node.Type ?? "").Trim().ToLowerInvariant();
        if (type is not ("vless" or "trojan"))
        {
            reason = $"type={type}";
            return false;
        }

        var network = (node.Network ?? "tcp").Trim().ToLowerInvariant();
        if (network is "websocket")
            network = "ws";
        if (network is "grpc" or "xhttp" or "h2")
        {
            reason = $"network={network}";
            return false;
        }

        if (network is not ("tcp" or "ws" or ""))
        {
            reason = $"network={network}";
            return false;
        }

        if (type == "vless")
        {
            var security = ResolveSecurity(node);
            if (security is not ("none" or "tls" or "reality"))
            {
                reason = $"security={security}";
                return false;
            }

            var flow = (node.Flow ?? "").Trim();
            if (flow.Length > 0 &&
                !flow.Equals("xtls-rprx-vision", StringComparison.OrdinalIgnoreCase))
            {
                reason = $"flow={flow}";
                return false;
            }

            if (string.IsNullOrWhiteSpace(node.Uuid))
            {
                reason = "missing uuid";
                return false;
            }
        }
        else if (string.IsNullOrWhiteSpace(node.Password))
        {
            reason = "missing password";
            return false;
        }

        return true;
    }

    public static string ResolveSecurity(ProxyNode node)
    {
        if (!string.IsNullOrEmpty(node.Security))
            return node.Security.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(node.RealityPublicKey))
            return "reality";
        if (node.Tls)
            return "tls";
        return "none";
    }
}
