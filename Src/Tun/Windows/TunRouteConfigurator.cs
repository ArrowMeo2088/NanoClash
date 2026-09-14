using System.Diagnostics;
using System.Net;
using System.Runtime.Versioning;
using System.Text;

using Clash.Net;

namespace Clash.Tun.Windows;

/// <summary>Configure WinTUN IPv4 address, anti-loop host routes, and split default routes.</summary>
[SupportedOSPlatform("windows")]
internal sealed class TunRouteConfigurator
{
    public static readonly IPAddress TunServer = IPAddress.Parse("172.19.0.1");
    public static readonly IPAddress TunClient = IPAddress.Parse("172.19.0.2");
    /// <summary>Force Windows DNS here so UDP/TCP 53 enters TUN for Fake-IP (mihomo: gateway+1).</summary>
    public static readonly IPAddress FakeDns = Clash.Dns.FakeIpPool.DnsAddress;
    public const string TunPrefix = "172.19.0.1/30";
    public const int Mtu = 1400;

    private readonly List<string[]> _undo = [];
    private IPAddress? _nodeHostRoute;
    private PhysicalEndpoint? _physical;
    private string? _dnsOverrideIfName;
    private string? _tunDnsIfName;
    private string? _ipv6DisabledIfName;
    private IReadOnlyList<IPAddress>? _savedPhysicalDns;

    public TunRouteConfigurator()
    {
    }

    public PhysicalEndpoint? Physical => _physical;

    public void ConfigureInterface(string adapterName, PhysicalEndpoint physical)
    {
        _physical = physical;
        Exception? last = null;
        var configured = false;
        for (var i = 0; i < 20; i++)
        {
            try
            {
                RunNetsh(
                    $"interface ip set address name=\"{adapterName}\" source=static addr={TunServer} mask=255.255.255.252");
                RunNetsh($"interface ipv4 set subinterface \"{adapterName}\" mtu={Mtu} store=active");
                // Prefer TUN for DNS/default like sing-tun (Metric=0).
                try
                {
                    RunNetsh($"interface ip set interface name=\"{adapterName}\" metric=1");
                }
                catch
                {
                    // ignore metric set failure
                }

                configured = true;
                break;
            }
            catch (Exception ex)
            {
                last = ex;
                Thread.Sleep(200);
            }
        }

        if (!configured)
        {
            throw new InvalidOperationException(
                $"Failed to configure TUN IP on '{adapterName}'", last);
        }

        WaitUntilAddressBindable(adapterName, TimeSpan.FromSeconds(15));
        // DNS hijack must run after split default routes (see ConfigureDnsHijack).
    }

    /// <summary>
    /// Point TUN + physical NIC DNS at FakeDns so queries hit TUN Fake-IP instead of
    /// corporate/DoH resolvers (which return real IPs and break domain rules).
    /// Both overrides must succeed — soft-fail would leave enhance "up" without domain rules.
    /// </summary>
    public void ConfigureDnsHijack(string tunAdapterName, PhysicalEndpoint physical)
    {
        _tunDnsIfName = tunAdapterName;
        RunNetsh(
            $"interface ip set dns name=\"{tunAdapterName}\" static addr={FakeDns} register=none");

        _dnsOverrideIfName = physical.Name;
        _savedPhysicalDns = physical.DnsServers;
        try
        {
            RunNetsh(
                $"interface ip set dns name=\"{physical.Name}\" static addr={FakeDns} register=none");
        }
        catch
        {
            RestoreTunDns();
            _dnsOverrideIfName = null;
            _savedPhysicalDns = null;
            throw;
        }

        try
        {
            Run("ipconfig", "/flushdns");
        }
        catch
        {
            // ignore flushdns failure
        }
    }

    private void RestoreDns()
    {
        RestoreTunDns();
        var ifName = _dnsOverrideIfName;
        var saved = _savedPhysicalDns;
        _dnsOverrideIfName = null;
        _savedPhysicalDns = null;
        if (ifName is null)
            return;

        TunOsRecovery.RestorePhysicalDns(ifName, saved);
    }

    private void RestoreTunDns()
    {
        var tun = _tunDnsIfName;
        _tunDnsIfName = null;
        if (tun is null)
            return;
        try
        {
            RunNetsh($"interface ip set dns name=\"{tun}\" dhcp");
        }
        catch
        {
            // adapter may already be gone
        }
    }

    public void DisablePhysicalIpv6(PhysicalEndpoint physical)
    {
        RunNetsh($"interface ipv6 set interface name=\"{physical.Name}\" admin=disabled");
        _ipv6DisabledIfName = physical.Name;
    }

    public void RestorePhysicalIpv6()
    {
        var name = _ipv6DisabledIfName;
        _ipv6DisabledIfName = null;
        if (name is null)
            return;
        TunOsRecovery.TryEnableIpv6(name);
    }

    public void ClearNodeHostRoute()
    {
        if (_nodeHostRoute is null || _physical is null)
            return;
        RemoveTrackedHostRoute(_nodeHostRoute, _physical);
        _nodeHostRoute = null;
    }

    /// <summary>
    /// netsh can report success before Winsock accepts Bind (WSAEADDRNOTAVAIL 10049).
    /// Poll until a probe socket can bind to the TUN server address.
    /// </summary>
    public void WaitUntilAddressBindable(string adapterName, TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        Exception? last = null;
        while (Environment.TickCount64 < deadline)
        {
            if (HasLocalAddress(TunServer))
            {
                try
                {
                    using var probe = new System.Net.Sockets.Socket(
                        System.Net.Sockets.AddressFamily.InterNetwork,
                        System.Net.Sockets.SocketType.Stream,
                        System.Net.Sockets.ProtocolType.Tcp);
                    probe.Bind(new IPEndPoint(TunServer, 0));
                    return;
                }
                catch (Exception ex)
                {
                    last = ex;
                }
            }

            Thread.Sleep(100);
        }

        throw new TimeoutException(
            $"TUN address {TunServer} not bindable within {timeout.TotalSeconds:0}s on '{adapterName}'",
            last);
    }

    private static bool HasLocalAddress(IPAddress want)
    {
        foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.Equals(want))
                    return true;
            }
        }

        return false;
    }

    /// <summary>Node / DoH / DNS host routes only (no default hijack yet).</summary>
    public void InstallAntiLoopRoutes(PhysicalEndpoint physical, IPAddress nodeIpv4)
    {
        _physical = physical;
        SetNodeHostRoute(nodeIpv4, physical);

        // Do not /32-exclude DoH hosts: app DoH would bypass Fake-IP via physical path.
        // Outbound DoH uses InterfaceBinder instead.
    }

    public void InstallSplitDefault()
    {
        AddRoute("0.0.0.0", "128.0.0.0", TunServer.ToString());
        AddRoute("128.0.0.0", "128.0.0.0", TunServer.ToString());
    }

    public void InstallRoutes(PhysicalEndpoint physical, IPAddress nodeIpv4)
    {
        InstallAntiLoopRoutes(physical, nodeIpv4);
        InstallSplitDefault();
    }

    public void SetNodeHostRoute(IPAddress nodeIpv4, PhysicalEndpoint physical)
    {
        if (_nodeHostRoute is not null && _nodeHostRoute.Equals(nodeIpv4))
            return;

        var previous = _nodeHostRoute;

        // Install the new /32 first so old and new both work during the switch window.
        try
        {
            AddRoute(nodeIpv4.ToString(), "255.255.255.255", physical.Gateway.ToString(), physical.InterfaceIndex);
        }
        catch (Exception ex) when (LooksLikeExists(ex))
        {
            TryDeleteRoute(nodeIpv4.ToString(), "255.255.255.255");
            AddRoute(nodeIpv4.ToString(), "255.255.255.255", physical.Gateway.ToString(), physical.InterfaceIndex);
        }

        _nodeHostRoute = nodeIpv4;

        if (previous is not null)
            RemoveTrackedHostRoute(previous, physical);
    }

    private void RemoveTrackedHostRoute(IPAddress host, PhysicalEndpoint physical)
    {
        var hostStr = host.ToString();
        var gw = physical.Gateway.ToString();
        for (var i = _undo.Count - 1; i >= 0; i--)
        {
            var u = _undo[i];
            if (u.Length >= 5 &&
                u[0] == "delete" &&
                u[1] == hostStr &&
                u[2] == "mask" &&
                u[3] == "255.255.255.255")
            {
                _undo.RemoveAt(i);
            }
        }

        TryDeleteRoute(hostStr, "255.255.255.255", gw);
    }

    public void UninstallAll()
    {
        // Drop split defaults first so the machine regains a normal default route quickly.
        TryDeleteRoute("0.0.0.0", "128.0.0.0");
        TryDeleteRoute("128.0.0.0", "128.0.0.0");

        for (var i = _undo.Count - 1; i >= 0; i--)
        {
            try
            {
                RunRoute(_undo[i]);
            }
            catch
            {
                // ignore route undo failure
            }
        }

        _undo.Clear();
        _nodeHostRoute = null;

        RestorePhysicalIpv6();
        RestoreDns();
    }

    private void AddRoute(string dest, string mask, string gateway, int? ifIndex = null)
    {
        var args = new List<string> { "add", dest, "mask", mask, gateway };
        if (ifIndex is int idx and > 0)
        {
            args.Add("if");
            args.Add(idx.ToString());
        }

        RunRoute(args.ToArray());
        _undo.Add(["delete", dest, "mask", mask, gateway]);
    }

    private void TryDeleteRoute(string dest, string mask, string? gateway = null)
    {
        try
        {
            if (gateway is null)
                RunRoute("delete", dest, "mask", mask);
            else
                RunRoute("delete", dest, "mask", mask, gateway);
        }
        catch
        {
            // ignore
        }
    }

    private static bool LooksLikeExists(Exception ex)
    {
        var m = ex.Message;
        return m.Contains("exists", StringComparison.OrdinalIgnoreCase) ||
               m.Contains("已存在", StringComparison.Ordinal) ||
               m.Contains("对象已存在", StringComparison.Ordinal);
    }

    private static void RunNetsh(string args) => Run("netsh", args);

    /// <summary>Shared with <see cref="TunOsRecovery"/> (same process helpers).</summary>
    internal static void RunNetshPublic(string args, int timeoutMs = 5_000) =>
        Run("netsh", args, timeoutMs);

    /// <summary>Shared with <see cref="TunOsRecovery"/>.</summary>
    internal static void RunPublic(string file, string args, int timeoutMs = 5_000) =>
        Run(file, args, timeoutMs);

    private static void RunRoute(params string[] args)
    {
        var sb = new StringBuilder();
        foreach (var a in args)
        {
            if (sb.Length > 0)
                sb.Append(' ');
            sb.Append(a);
        }

        Run("route", sb.ToString());
    }

    private static void Run(string file, string args, int timeoutMs = 5_000)
    {
        using var p = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        p.Start();
        // Drain stdout/stderr concurrently — sequential ReadToEnd can deadlock on full pipes.
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs))
        {
            try
            {
                p.Kill(entireProcessTree: true);
            }
            catch
            {
                // ignore
            }

            throw new TimeoutException($"{file} {args} timed out");
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
            throw new InvalidOperationException($"{file} {args} => {p.ExitCode}: {stdout}{stderr}");
    }
}
