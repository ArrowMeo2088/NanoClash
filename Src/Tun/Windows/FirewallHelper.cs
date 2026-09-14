using System.Runtime.Versioning;

namespace Clash.Tun.Windows;

/// <summary>Allow inbound TCP to this process (required for System stack Listen on TUN IP).</summary>
[SupportedOSPlatform("windows")]
internal static class FirewallHelper
{
    public static void AllowThisProcess()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("ProcessPath unavailable");
        var abs = Path.GetFullPath(exe);
        var name = RuleName(abs);

        // Remove prior rule with same name then add.
        TryNetsh($"advfirewall firewall delete rule name=\"{Escape(name)}\"");
        RunNetsh(
            $"advfirewall firewall add rule name=\"{Escape(name)}\" dir=in action=allow program=\"{Escape(abs)}\" enable=yes profile=any");
    }

    public static void TryRemoveThisProcess()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
                return;
            var name = RuleName(Path.GetFullPath(exe));
            TryNetsh("advfirewall firewall delete rule name=\"" + Escape(name) + "\"");
        }
        catch
        {
            // ignore
        }
    }

    private static string RuleName(string abs) => "NanoClash (" + abs + ")";

    private static string Escape(string s) => s.Replace("\"", "\\\"");

    private static void TryNetsh(string args)
    {
        try
        {
            RunNetsh(args);
        }
        catch
        {
            // ignore
        }
    }

    private static void RunNetsh(string args)
    {
        using var p = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        p.Start();
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(5_000))
        {
            try
            {
                p.Kill(entireProcessTree: true);
            }
            catch
            {
                // ignore
            }

            throw new TimeoutException("netsh timed out: " + args);
        }

        string stdout;
        string stderr;
        try
        {
            stdout = stdoutTask.GetAwaiter().GetResult();
            stderr = stderrTask.GetAwaiter().GetResult();
        }
        catch
        {
            stdout = "";
            stderr = "";
        }

        if (p.ExitCode != 0)
            throw new InvalidOperationException("netsh failed: " + stdout + stderr);
    }
}
