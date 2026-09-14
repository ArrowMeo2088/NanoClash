using System.Diagnostics;
using System.Security.Principal;

namespace Clash.Inbound;

/// <summary>Windows admin check + optional UAC relaunch for enhance mode.</summary>
internal static class Elevation
{
    public static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows())
            return true;
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static bool TryRelaunchElevated()
    {
        if (!OperatingSystem.IsWindows())
            return false;
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
            return false;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                Verb = "runas",
            });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
