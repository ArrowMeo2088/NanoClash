using System.Diagnostics;
using System.Runtime.Versioning;

namespace Clash.Inbound;

/// <summary>macOS system Web/Secure Web proxy via networksetup → 127.0.0.1:7887.</summary>
[SupportedOSPlatform("macos")]
internal sealed class MacSystemProxy : ISystemProxy
{
    private const string Host = "127.0.0.1";
    private const string Port = "7887";

    private bool _applied;
    private List<ServiceSnapshot>? _snapshots;

    public void SetEnabled(bool enabled)
    {
        if (enabled)
            Apply();
        else
            Restore();
    }

    public void ForceRestore()
    {
        if (_applied)
        {
            Restore();
            return;
        }

        RecoverOrphanedProxy();
    }

    public static void RecoverOrphanedProxy()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        new MacSystemProxy().TryRecoverFromDisk();
    }

    private void TryRecoverFromDisk()
    {
        if (!File.Exists(UndoPath))
            return;
        try
        {
            var snaps = LoadUndo();
            if (snaps is null || snaps.Count == 0)
            {
                ClearUndo();
                return;
            }

            var stillOurs = false;
            foreach (var svc in ListNetworkServices())
            {
                var web = CaptureProxy(svc, "getwebproxy");
                if (web.Enabled && web.Server == Host && web.Port.ToString() == Port)
                {
                    stillOurs = true;
                    break;
                }
            }

            if (!stillOurs)
            {
                ClearUndo();
                return;
            }

            foreach (var s in snaps)
                TryRestoreService(s);
        }
        catch
        {
            // ignore
        }

        ClearUndo();
    }

    private void Apply()
    {
        if (_applied)
            return;

        var services = ListNetworkServices();
        if (services.Count == 0)
            throw new InvalidOperationException("No network services found for networksetup");

        var snaps = new List<ServiceSnapshot>(services.Count);
        foreach (var svc in services)
        {
            snaps.Add(new ServiceSnapshot(
                svc,
                CaptureProxy(svc, "getwebproxy"),
                CaptureProxy(svc, "getsecurewebproxy")));
        }

        try
        {
            foreach (var svc in services)
            {
                RunNetworkSetup("setwebproxy", svc, Host, Port);
                RunNetworkSetup("setsecurewebproxy", svc, Host, Port);
                RunNetworkSetup("setwebproxystate", svc, "on");
                RunNetworkSetup("setsecurewebproxystate", svc, "on");
            }

            _snapshots = snaps;
            WriteUndo(snaps);
            _applied = true;
        }
        catch
        {
            foreach (var s in snaps)
                TryRestoreService(s);
            _snapshots = null;
            _applied = false;
            throw;
        }
    }

    private void Restore()
    {
        if (!_applied || _snapshots is null)
            return;

        foreach (var s in _snapshots)
            TryRestoreService(s);

        _snapshots = null;
        _applied = false;
        ClearUndo();
    }

    private static void TryRestoreService(ServiceSnapshot s)
    {
        try
        {
            RestoreOne(s.Name, "setwebproxy", "setwebproxystate", s.Web);
            RestoreOne(s.Name, "setsecurewebproxy", "setsecurewebproxystate", s.SecureWeb);
        }
        catch
        {
            // best-effort
        }
    }

    private static void RestoreOne(string svc, string setCmd, string stateCmd, ProxyState state)
    {
        if (state.Enabled && !string.IsNullOrEmpty(state.Server) && state.Port > 0)
        {
            RunNetworkSetup(setCmd, svc, state.Server, state.Port.ToString());
            RunNetworkSetup(stateCmd, svc, "on");
        }
        else
        {
            RunNetworkSetup(stateCmd, svc, "off");
        }
    }

    private static List<string> ListNetworkServices()
    {
        var output = RunNetworkSetupCapture("listallnetworkservices");
        var list = new List<string>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("An asterisk", StringComparison.OrdinalIgnoreCase))
                continue;
            // Disabled services are prefixed with "* "
            var name = line.StartsWith("* ", StringComparison.Ordinal) ? line[2..] : line;
            if (string.IsNullOrWhiteSpace(name))
                continue;
            // Skip disabled
            if (line.StartsWith("* ", StringComparison.Ordinal))
                continue;
            list.Add(name);
        }

        return list;
    }

    private static ProxyState CaptureProxy(string service, string getCmd)
    {
        var output = RunNetworkSetupCapture(getCmd, service);
        var enabled = false;
        string? server = null;
        var port = 0;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("Enabled:", StringComparison.OrdinalIgnoreCase))
                enabled = line.Contains("Yes", StringComparison.OrdinalIgnoreCase);
            else if (line.StartsWith("Server:", StringComparison.OrdinalIgnoreCase))
                server = line["Server:".Length..].Trim();
            else if (line.StartsWith("Port:", StringComparison.OrdinalIgnoreCase))
                int.TryParse(line["Port:".Length..].Trim(), out port);
        }

        return new ProxyState(enabled, server, port);
    }

    private static void RunNetworkSetup(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "/usr/sbin/networksetup",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start networksetup");
        var err = p.StandardError.ReadToEnd();
        var stdout = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(15_000))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("networksetup timed out");
        }
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"networksetup {string.Join(' ', args)} failed ({p.ExitCode}): {err}{stdout}");
    }

    private static string RunNetworkSetupCapture(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "/usr/sbin/networksetup",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start networksetup");
        var stdout = p.StandardOutput.ReadToEnd();
        var err = p.StandardError.ReadToEnd();
        if (!p.WaitForExit(15_000))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("networksetup timed out");
        }
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"networksetup {string.Join(' ', args)} failed ({p.ExitCode}): {err}");
        return stdout;
    }

    private static string UndoPath => Path.Combine(AppPaths.UserDataDir, "proxy-undo-mac.txt");

    private static void WriteUndo(List<ServiceSnapshot> snaps)
    {
        try
        {
            using var sw = new StringWriter();
            foreach (var s in snaps)
            {
                sw.Write(s.Name); sw.Write('\t');
                sw.Write(s.Web.Enabled); sw.Write('\t');
                sw.Write(s.Web.Server ?? ""); sw.Write('\t');
                sw.Write(s.Web.Port); sw.Write('\t');
                sw.Write(s.SecureWeb.Enabled); sw.Write('\t');
                sw.Write(s.SecureWeb.Server ?? ""); sw.Write('\t');
                sw.Write(s.SecureWeb.Port);
                sw.Write('\n');
            }

            File.WriteAllText(UndoPath, sw.ToString());
        }
        catch
        {
            // ignore
        }
    }

    private static List<ServiceSnapshot>? LoadUndo()
    {
        var list = new List<ServiceSnapshot>();
        foreach (var line in File.ReadAllLines(UndoPath))
        {
            var p = line.Split('\t');
            if (p.Length < 7)
                continue;
            list.Add(new ServiceSnapshot(
                p[0],
                new ProxyState(bool.TryParse(p[1], out var we) && we, p[2], int.TryParse(p[3], out var wp) ? wp : 0),
                new ProxyState(bool.TryParse(p[4], out var se) && se, p[5], int.TryParse(p[6], out var sp) ? sp : 0)));
        }

        return list;
    }

    private static void ClearUndo()
    {
        try
        {
            if (File.Exists(UndoPath))
                File.Delete(UndoPath);
        }
        catch
        {
            // ignore
        }
    }

    private readonly record struct ProxyState(bool Enabled, string? Server, int Port);
    private sealed record ServiceSnapshot(string Name, ProxyState Web, ProxyState SecureWeb);
}
