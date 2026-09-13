using System.Net;
using System.Runtime.Versioning;

using Clash.Config;
using Clash.Net;
using Clash.Outbound;
using Clash.Rules;
using Clash.Tun.SystemStack;
using Clash.Tun.Windows;

namespace Clash.Tun;

/// <summary>Windows System-TCP TUN enhance mode facade.</summary>
internal sealed class TunService : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _nodeSwitch = new(1, 1);
    private WintunNative? _api;
    private WintunDevice? _device;
    private TunRouteConfigurator? _routes;
    private SystemTcpStack? _stack;
    private RuleDb? _rules;
    private OutboundDialer? _outbound;
    private bool _running;

    public TunService()
    {
    }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
                return _running;
        }
    }

    public void Configure(RuleDb rules, OutboundDialer outbound)
    {
        _rules = rules;
        _outbound = outbound;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Enhance mode (TUN) is only supported on Windows.");

        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_running)
                    return;
            }

            if (_rules is null || _outbound is null)
                throw new InvalidOperationException("TunService not configured");

            var outbound = _outbound;
            var rules = _rules;
            if (outbound.Current is null || outbound.Current.IsSubscriptionInfo)
                throw new InvalidOperationException("Select a proxy node before enabling enhance mode");

            // Resolve node IP before hijacking the default route (DoH uses normal routing here).
            IPAddress nodeIp;
            try
            {
                nodeIp = await outbound.ResolveNodeIpv4Async(outbound.Current, ct).ConfigureAwait(false)
                         ?? throw new InvalidOperationException("Could not resolve node IPv4 for anti-loop route");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidOperationException(
                    "Node IPv4 required for anti-loop host route before TUN start: " + ex.Message, ex);
            }

            await Task.Run(() =>
            {
                if (OperatingSystem.IsWindows())
                    StartCoreWindows(rules, outbound, nodeIp);
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    [SupportedOSPlatform("windows")]
    private void StartCoreWindows(RuleDb rules, OutboundDialer outbound, IPAddress nodeIp)
    {
        lock (_gate)
        {
            if (_running)
                return;

            WintunNative? api = null;
            WintunDevice? device = null;
            TunRouteConfigurator? routes = null;
            SystemTcpStack? stack = null;
            var binderSet = false;

            try
            {
                var physical = PhysicalInterfaceProbe.Probe();
                InterfaceBinder.SetPhysical(physical);
                binderSet = true;

                api = WintunNative.LoadFrom(AppPaths.WintunDll);
                device = WintunDevice.Create(api);
                routes = new TunRouteConfigurator();
                routes.ConfigureInterface(WintunDevice.AdapterName, physical);
                // Anti-loop host routes first; listen before hijacking the default route.
                routes.InstallAntiLoopRoutes(physical, nodeIp);

                stack = new SystemTcpStack(device, rules, outbound, new Clash.Dns.FakeIpPool());
                stack.Start();
                routes.InstallSplitDefault();
                // After split routes: FakeDns is reachable via TUN (mihomo/sing-tun order).
                routes.ConfigureDnsHijack(WintunDevice.AdapterName, physical);

                _api = api;
                _device = device;
                _routes = routes;
                _stack = stack;
                _running = true;
                api = null;
                device = null;
                routes = null;
                stack = null;
                binderSet = false;
            }
            catch
            {
                try
                {
                    stack?.Dispose();
                }
                catch
                {
                    // ignore
                }

                try
                {
                    routes?.UninstallAll();
                }
                catch
                {
                    // ignore
                }

                try
                {
                    device?.Dispose();
                }
                catch
                {
                    // ignore
                }

                try
                {
                    api?.Dispose();
                }
                catch
                {
                    // ignore
                }

                if (binderSet)
                    InterfaceBinder.Clear();

                _api = null;
                _device = null;
                _routes = null;
                _stack = null;
                _running = false;
                throw;
            }
        }
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                if (OperatingSystem.IsWindows())
                    StopCoreWindows();
                else
                    StopCoreNonWindows();
            }).ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    [SupportedOSPlatform("windows")]
    private void StopCoreWindows()
    {
        SystemTcpStack? stack;
        WintunDevice? device;
        TunRouteConfigurator? routes;
        WintunNative? api;

        lock (_gate)
        {
            if (!_running && _device is null && _routes is null && _api is null)
                return;

            stack = _stack;
            device = _device;
            routes = _routes;
            api = _api;
            _stack = null;
            _routes = null;
            _device = null;
            _api = null;
            _running = false;
        }

        // Slow work outside _gate so node-switch / IsRunning stay responsive.
        // Tear down routes/DNS first so the machine regains connectivity even if stack join is slow.
        try
        {
            stack?.RequestStop();
        }
        catch
        {
            // ignore
        }

        try
        {
            device?.EndSession();
        }
        catch
        {
            // ignore
        }

        try
        {
            routes?.UninstallAll();
        }
        catch
        {
            // ignore
        }

        InterfaceBinder.Clear();

        try
        {
            stack?.Dispose();
        }
        catch
        {
            // ignore
        }

        try
        {
            device?.Dispose();
        }
        catch
        {
            // ignore
        }

        try
        {
            api?.Dispose();
        }
        catch
        {
            // ignore
        }

    }

    private void StopCoreNonWindows()
    {
        lock (_gate)
        {
            InterfaceBinder.Clear();
            _running = false;
            _api = null;
            _device = null;
            _routes = null;
            _stack = null;
        }
    }

    /// <summary>
    /// Install/replace the node /32 host route, then drop TUN relays.
    /// Returns false if the route could not be prepared (caller must not SetCurrent yet).
    /// Serialized so concurrent UI clicks cannot interleave route updates.
    /// </summary>
    public async Task<bool> OnNodeChangedAsync(ProxyNode? node, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return true;

        await _nodeSwitch.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            OutboundDialer? outbound;
            lock (_gate)
            {
                if (!_running || _routes is null || _outbound is null)
                    return true;
                outbound = _outbound;
            }

            if (node is null || node.IsSubscriptionInfo)
            {
                if (OperatingSystem.IsWindows())
                    ApplyNodeRouteWindows(null);
                return true;
            }

            IPAddress? ip;
            try
            {
                ip = await outbound.ResolveNodeIpv4Async(node, ct).ConfigureAwait(false);
            }
            catch
            {
                return false;
            }

            if (ip is null)
            {
                return false;
            }

            if (OperatingSystem.IsWindows())
                return ApplyNodeRouteWindows(ip);

            return false;
        }
        finally
        {
            _nodeSwitch.Release();
        }
    }

    [SupportedOSPlatform("windows")]
    private bool ApplyNodeRouteWindows(IPAddress? ip)
    {
        lock (_gate)
        {
            if (!_running || _routes is null)
                return false;
            var physical = _routes.Physical ?? InterfaceBinder.Current;
            if (physical is null)
                return false;

            if (ip is not null)
            {
                try
                {
                    _routes.SetNodeHostRoute(ip, physical);
                }
                catch
                {
                    return false;
                }
            }

            _stack?.BumpGeneration();
            return true;
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
