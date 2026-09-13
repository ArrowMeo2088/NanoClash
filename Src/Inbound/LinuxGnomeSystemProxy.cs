using System.Diagnostics;
using System.Runtime.Versioning;

namespace Clash.Inbound;

/// <summary>GNOME system proxy via gsettings → 127.0.0.1:7887.</summary>
[SupportedOSPlatform("linux")]
internal sealed class LinuxGnomeSystemProxy : ISystemProxy
{
    private const string Host = "127.0.0.1";
    private const int Port = 7887;

    private bool _applied;
    private Snapshot? _prev;

    public static bool IsAvailable()
    {
        try
        {
            return GSettingsGet("org.gnome.system.proxy", "mode") is not null;
        }
        catch
        {
            return false;
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled)
            Apply();
        else
            Restore();
    }

    public void ForceRestore() => Restore();

    private void Apply()
    {
        if (_applied)
            return;

        var snap = new Snapshot(
            GSettingsGet("org.gnome.system.proxy", "mode") ?? "none",
            GSettingsGet("org.gnome.system.proxy", "use-same-proxy") ?? "true",
            GSettingsGet("org.gnome.system.proxy.http", "host") ?? "",
            ParseInt(GSettingsGet("org.gnome.system.proxy.http", "port"), 0),
            GSettingsGet("org.gnome.system.proxy.http", "enabled") ?? "false",
            GSettingsGet("org.gnome.system.proxy.https", "host") ?? "",
            ParseInt(GSettingsGet("org.gnome.system.proxy.https", "port"), 0));

        try
        {
            GSettingsSet("org.gnome.system.proxy", "mode", "manual");
            GSettingsSet("org.gnome.system.proxy", "use-same-proxy", "true");
            GSettingsSet("org.gnome.system.proxy.http", "enabled", "true");
            GSettingsSet("org.gnome.system.proxy.http", "host", Host);
            GSettingsSet("org.gnome.system.proxy.http", "port", Port.ToString());
            GSettingsSet("org.gnome.system.proxy.https", "host", Host);
            GSettingsSet("org.gnome.system.proxy.https", "port", Port.ToString());
            _prev = snap;
            _applied = true;
        }
        catch
        {
            TryWriteSnapshot(snap);
            _prev = null;
            _applied = false;
            throw;
        }
    }

    private void Restore()
    {
        if (!_applied || _prev is null)
            return;

        TryWriteSnapshot(_prev);
        _prev = null;
        _applied = false;
    }

    private static void TryWriteSnapshot(Snapshot s)
    {
        try
        {
            GSettingsSet("org.gnome.system.proxy", "mode", s.Mode);
            GSettingsSet("org.gnome.system.proxy", "use-same-proxy", s.UseSame);
            GSettingsSet("org.gnome.system.proxy.http", "enabled", s.HttpEnabled);
            GSettingsSet("org.gnome.system.proxy.http", "host", s.HttpHost);
            GSettingsSet("org.gnome.system.proxy.http", "port", s.HttpPort.ToString());
            GSettingsSet("org.gnome.system.proxy.https", "host", s.HttpsHost);
            GSettingsSet("org.gnome.system.proxy.https", "port", s.HttpsPort.ToString());
        }
        catch
        {
            // best-effort
        }
    }

    private static int ParseInt(string? s, int fallback) =>
        int.TryParse(s, out var n) ? n : fallback;

    private static string? GSettingsGet(string schema, string key)
    {
        var (code, stdout, _) = Run("gsettings", "get", schema, key);
        if (code != 0)
            return null;
        var t = stdout.Trim();
        if (t.StartsWith('\'') && t.EndsWith('\'') && t.Length >= 2)
            return t[1..^1];
        return t;
    }

    private static void GSettingsSet(string schema, string key, string value)
    {
        string formatted;
        if (value is "true" or "false")
            formatted = value;
        else if (int.TryParse(value, out _))
            formatted = value;
        else
            formatted = "'" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("'", "\\'", StringComparison.Ordinal) + "'";

        var (code, _, err) = Run("gsettings", "set", schema, key, formatted);
        if (code != 0)
            throw new InvalidOperationException($"gsettings set {schema} {key} failed: {err}");
    }

    private static (int Code, string Stdout, string Stderr) Run(string file, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {file}");
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(10_000);
        return (p.ExitCode, stdout, stderr);
    }

    private sealed record Snapshot(
        string Mode,
        string UseSame,
        string HttpHost,
        int HttpPort,
        string HttpEnabled,
        string HttpsHost,
        int HttpsPort);
}
