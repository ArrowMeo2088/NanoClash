using System.Net;
using System.Runtime.Versioning;
using System.Text;

namespace Clash.Tun.Windows;

/// <summary>User-data snapshot so crash recovery can drop node /32 and re-enable IPv6.</summary>
[SupportedOSPlatform("windows")]
internal static class TunUndoStore
{
    private static string Path => System.IO.Path.Combine(AppPaths.UserDataDir, "tun-undo.json");

    public static void Write(string physicalName, IPAddress? nodeHost, bool ipv6Disabled)
    {
        try
        {
            var node = nodeHost is null ? "null" : "\"" + nodeHost + "\"";
            var json =
                $"{{\"physical\":\"{Esc(physicalName)}\",\"nodeHost\":{node},\"ipv6Disabled\":{(ipv6Disabled ? "true" : "false")}}}";
            File.WriteAllText(Path, json, Encoding.UTF8);
        }
        catch
        {
            // ignore
        }
    }

    public static bool TryRead(out string? physical, out IPAddress? nodeHost, out bool ipv6Disabled)
    {
        physical = null;
        nodeHost = null;
        ipv6Disabled = false;
        try
        {
            if (!File.Exists(Path))
                return false;
            var text = File.ReadAllText(Path);
            if (!TryField(text, "physical", out var phyRaw) || phyRaw == "null")
                return false;
            physical = Unquote(phyRaw);
            if (TryField(text, "nodeHost", out var nodeRaw) && nodeRaw != "null")
            {
                var s = Unquote(nodeRaw);
                if (IPAddress.TryParse(s, out var ip))
                    nodeHost = ip;
            }

            if (TryField(text, "ipv6Disabled", out var v6))
                ipv6Disabled = v6.Equals("true", StringComparison.OrdinalIgnoreCase);
            return !string.IsNullOrEmpty(physical);
        }
        catch
        {
            return false;
        }
    }

    public static void Clear()
    {
        try
        {
            if (File.Exists(Path))
                File.Delete(Path);
        }
        catch
        {
            // ignore
        }
    }

    private static bool TryField(string text, string name, out string raw)
    {
        raw = "";
        var key = "\"" + name + "\"";
        var i = text.IndexOf(key, StringComparison.Ordinal);
        if (i < 0)
            return false;
        i = text.IndexOf(':', i + key.Length);
        if (i < 0)
            return false;
        i++;
        while (i < text.Length && char.IsWhiteSpace(text[i]))
            i++;
        if (i >= text.Length)
            return false;
        if (text[i] == '"')
        {
            var end = text.IndexOf('"', i + 1);
            if (end < 0)
                return false;
            raw = text[i..(end + 1)];
            return true;
        }

        var start = i;
        while (i < text.Length && text[i] is not (',' or '}' or ' '))
            i++;
        raw = text[start..i].Trim();
        return raw.Length > 0;
    }

    private static string Unquote(string s)
    {
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
            return s[1..^1];
        return s;
    }

    private static string Esc(string s) =>
        s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}
