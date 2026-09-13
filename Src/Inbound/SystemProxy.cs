namespace Clash.Inbound;

/// <summary>OS system HTTP(S) proxy pointing at local inbound.</summary>
internal interface ISystemProxy
{
    void SetEnabled(bool enabled);
    void ForceRestore();
}

/// <summary>Desktop without supported system-proxy APIs (e.g. non-GNOME Linux).</summary>
internal sealed class UnsupportedSystemProxy(string reason) : ISystemProxy
{
    public void SetEnabled(bool enabled)
    {
        if (enabled)
            throw new PlatformNotSupportedException(reason);
    }

    public void ForceRestore()
    {
        // nothing applied
    }
}

/// <summary>Factory + facade for the current OS system proxy implementation.</summary>
internal static class SystemProxy
{
    private static readonly ISystemProxy Impl = Create();

    public static ISystemProxy Current => Impl;

    public static void SetEnabled(bool enabled) => Impl.SetEnabled(enabled);

    public static void ForceRestore() => Impl.ForceRestore();

    private static ISystemProxy Create()
    {
        if (OperatingSystem.IsWindows())
            return new WindowsSystemProxy();
        if (OperatingSystem.IsMacOS())
            return new MacSystemProxy();
        if (OperatingSystem.IsLinux())
        {
            if (LinuxGnomeSystemProxy.IsAvailable())
                return new LinuxGnomeSystemProxy();
            return new UnsupportedSystemProxy(
                "System proxy requires GNOME gsettings on Linux. Set HTTP proxy to 127.0.0.1:7887 manually, or use GNOME.");
        }

        return new UnsupportedSystemProxy("System proxy is not supported on this OS.");
    }
}
