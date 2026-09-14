using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

using Clash;
using Clash.Config;
using Clash.Dns;
using Clash.Gui;
using Clash.Inbound;
using Clash.Net;
using Clash.Outbound;
using Clash.Rules;
using Clash.Tun;

if (OperatingSystem.IsWindows())
{
    Thread.CurrentThread.SetApartmentState(ApartmentState.Unknown);
    Thread.CurrentThread.SetApartmentState(ApartmentState.STA);
}

DirectNetwork.Configure();
AppPaths.EnsureUserData();

if (OperatingSystem.IsWindows())
{
    Clash.Tun.Windows.WintunBootstrap.EnsureExtracted();
    Clash.Tun.Windows.TunOsRecovery.RecoverOrphanedOsState();
    WindowsSystemProxy.RecoverOrphanedProxy();
}
else if (OperatingSystem.IsMacOS())
{
    MacSystemProxy.RecoverOrphanedProxy();
}
else if (OperatingSystem.IsLinux())
{
    LinuxGnomeSystemProxy.RecoverOrphanedProxy();
}

TunService? tun = null;

AppDomain.CurrentDomain.ProcessExit += (_, _) =>
{
    try
    {
        tun?.StopAsync().GetAwaiter().GetResult();
    }
    catch
    {
        // ignore
    }

    try
    {
        SystemProxy.ForceRestore();
    }
    catch
    {
        // ignore
    }
};

RuleDb? rules = null;
OutboundDialer? outbound = null;
ProxyService? proxy = null;
SubscriptionClient? subClient = null;

try
{
    rules = RuleDb.LoadDefault();

    var profiles = new ProfileStore();
    profiles.LoadOrMigrate();
    subClient = new SubscriptionClient();

    var doh = new DohResolver();
    outbound = new OutboundDialer(doh);

    proxy = new ProxyService();
    await proxy.StartAsync(rules, outbound).ConfigureAwait(false);

    tun = new TunService();
    tun.Configure(rules, outbound);

    var view = new MainView(proxy, outbound, tun, profiles, subClient);
    var window = view.CreateWindow();

    window.OnClosed(() =>
    {
        try
        {
            proxy.SetSystemProxy(false);
            SystemProxy.ForceRestore();
        }
        catch
        {
            // ignore
        }

        try
        {
            Task.Run(async () =>
            {
                try { await tun.StopAsync().ConfigureAwait(false); } catch { }
                try { await proxy.StopAsync().ConfigureAwait(false); } catch { }
            }).Wait(TimeSpan.FromSeconds(10));
        }
        catch
        {
            // ignore
        }
    });

    Application.DispatcherUnhandledException += e =>
    {
        e.Handled = true;
    };

    RegisterPlatform();
    Application.Run(window);
}
finally
{
    try
    {
        if (tun is not null)
            await tun.DisposeAsync().ConfigureAwait(false);
        SystemProxy.ForceRestore();
        if (proxy is not null)
            await proxy.DisposeAsync().ConfigureAwait(false);
    }
    catch
    {
        // ignore
    }

    subClient?.Dispose();
    outbound?.Dispose();
    rules?.Dispose();
}

static void RegisterPlatform()
{
    if (OperatingSystem.IsWindows())
    {
        Win32Platform.Register();
        Direct2DBackend.Register();
    }
    else if (OperatingSystem.IsMacOS())
    {
        MacOSPlatform.Register();
        MewVGMacOSBackend.Register();
    }
    else if (OperatingSystem.IsLinux())
    {
        X11Platform.Register();
        MewVGX11Backend.Register();
    }
    else
    {
        throw new PlatformNotSupportedException("Unsupported OS for NanoClash GUI.");
    }
}
