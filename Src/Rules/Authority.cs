namespace Clash.Rules;

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
