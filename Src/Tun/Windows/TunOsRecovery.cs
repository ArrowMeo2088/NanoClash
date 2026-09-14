using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;

using Clash.Dns;

namespace Clash.Tun.Windows;

/// <summary>
/// Stateless cleanup of orphaned TUN split routes / Fake DNS left after a crash or hard kill.
/// Safe to call on every startup; does not depend on in-process undo state.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class TunOsRecovery
{
    /// <summary>
    /// Delete split default routes and reset physical NIC DNS when still pointing at FakeDns.
    /// Failures are ignored — must not block app start.
    /// </summary>
    public static void RecoverOrphanedOsState()
    {
        try
        {
            TryDeleteSplitDefaults();
            if (TunUndoStore.TryRead(out var physical, out var nodeHost, out var ipv6Disabled))
            {
                if (nodeHost is not null)
                    TryDeleteRoute(nodeHost.ToString(), "255.255.255.255");
                if (ipv6Disabled && !string.IsNullOrEmpty(physical))
                    TryEnableIpv6(physical);
                TunUndoStore.Clear();
            }

            TryRestoreFakeDnsOnPhysicalNics();
            FirewallHelper.TryRemoveThisProcess();
        }
        catch
        {
            // ignore
        }
    }

    internal static void TryEnableIpv6(string ifName)
    {
        try
        {
            TunRouteConfigurator.RunNetshPublic(
                $"interface ipv6 set interface name=\"{ifName}\" admin=enabled",
                2000);
        }
        catch
        {
            // ignore
        }
    }

    internal static void TryDeleteSplitDefaults()
    {
        TryDeleteRoute("0.0.0.0", "128.0.0.0");
        TryDeleteRoute("128.0.0.0", "128.0.0.0");
    }

    /// <summary>
    /// After Fake-IP hijack, prefer DHCP. Restoring corporate static DNS via netsh during
    /// TUN teardown often hangs until the process timeout and freezes the UI.
    /// </summary>
    internal static void RestorePhysicalDns(
        string ifName,
        IReadOnlyList<IPAddress>? saved)
    {
        const int DnsTimeoutMs = 1500;
        try
        {
            TunRouteConfigurator.RunNetshPublic(
                $"interface ip set dns name=\"{ifName}\" dhcp",
                DnsTimeoutMs);
            return;
        }
        catch
        {
            // ignore DHCP restore failure
        }

        if (saved is null || saved.Count == 0)
            return;

        try
        {
            TunRouteConfigurator.RunNetshPublic(
                $"interface ip set dns name=\"{ifName}\" static addr={saved[0]} register=none",
                DnsTimeoutMs);
            for (var i = 1; i < saved.Count; i++)
            {
                TunRouteConfigurator.RunNetshPublic(
                    $"interface ip add dns name=\"{ifName}\" addr={saved[i]} index={i + 1}",
                    DnsTimeoutMs);
            }
        }
        catch
        {
            // ignore static restore failure
        }
    }

    private static void TryRestoreFakeDnsOnPhysicalNics()
    {
        var fakeDns = FakeIpPool.DnsAddress;
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up)
                continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;

            var props = ni.GetIPProperties();
            var hasFake = false;
            foreach (var dns in props.DnsAddresses)
            {
                if (dns.AddressFamily != AddressFamily.InterNetwork)
                    continue;
                if (dns.Equals(fakeDns) || IsInFakeIpRange(dns))
                {
                    hasFake = true;
                    break;
                }
            }

            if (!hasFake)
                continue;

            RestorePhysicalDns(ni.Name, saved: null);
        }

        try
        {
            TunRouteConfigurator.RunPublic("ipconfig", "/flushdns", timeoutMs: 2000);
        }
        catch
        {
            // ignore
        }
    }

    private static bool IsInFakeIpRange(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return b.Length == 4 && b[0] == 198 && b[1] == 18;
    }

    private static void TryDeleteRoute(string dest, string mask)
    {
        try
        {
            TunRouteConfigurator.RunPublic("route", $"delete {dest} mask {mask}", timeoutMs: 2000);
        }
        catch
        {
            // Not present — expected on clean machines.
        }
    }
}
