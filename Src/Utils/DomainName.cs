using System.Globalization;

namespace Clash.Utils;

/// <summary>ASCII / Punycode host normalization (InvariantGlobalization-safe).</summary>
internal static class DomainName
{
    private static readonly IdnMapping Idn = new() { UseStd3AsciiRules = false };

    public static string ToAscii(string host)
    {
        host = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (host.Length == 0)
            return host;
        if (IsAscii(host))
            return host;

        try
        {
            return Idn.GetAscii(host);
        }
        catch
        {
            return host;
        }
    }

    private static bool IsAscii(string s)
    {
        foreach (var c in s)
        {
            if (c > 127)
                return false;
        }

        return true;
    }
}
