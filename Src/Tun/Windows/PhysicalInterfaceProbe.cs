using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;

using Clash.Net;

namespace Clash.Tun.Windows;

/// <summary>
/// Resolve the IPv4 egress NIC used for Internet before TUN hijacks the default route.
/// Prefer UDP connect local-endpoint (real default), then fall back to gateway scan.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class PhysicalInterfaceProbe
{
    public static PhysicalEndpoint Probe()
    {
        IPAddress? preferredLocal = null;
        try
        {
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            // No packets need to arrive; OS picks the interface for this destination.
            probe.Connect(IPAddress.Parse("1.1.1.1"), 53);
            if (probe.LocalEndPoint is IPEndPoint lep)
                preferredLocal = lep.Address;
        }
        catch
        {
            // fall back to gateway scan
        }

        PhysicalEndpoint? fallback = null;
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up)
                continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;
            var name = ni.Name;
            if (IsAdapterName(name))
                continue;

            var props = ni.GetIPProperties();
            IPAddress? local = null;
            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(ua.Address))
                {
                    local = ua.Address;
                    break;
                }
            }

            if (local is null)
                continue;

            IPAddress? gw = null;
            foreach (var g in props.GatewayAddresses)
            {
                if (g.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !g.Address.Equals(IPAddress.Any))
                {
                    gw = g.Address;
                    break;
                }
            }

            if (gw is null)
                continue;

            var idx = props.GetIPv4Properties()?.Index ?? 0;
            if (idx <= 0)
                continue;

            var dns = CollectDns(props);
            var ep = new PhysicalEndpoint(idx, local, gw, name, dns);
            if (preferredLocal is not null && local.Equals(preferredLocal))
                return ep;

            fallback ??= ep;
        }

        if (fallback is not null)
            return fallback;

        throw new InvalidOperationException("No usable physical IPv4 default interface found");
    }

    private static IReadOnlyList<IPAddress> CollectDns(IPInterfaceProperties props)
    {
        var list = new List<IPAddress>();
        foreach (var d in props.DnsAddresses)
        {
            if (d.AddressFamily != AddressFamily.InterNetwork)
                continue;
            if (IPAddress.IsLoopback(d) || d.Equals(IPAddress.Any))
                continue;
            if (list.Any(x => x.Equals(d)))
                continue;
            list.Add(d);
        }

        return list;
    }

    private static bool IsAdapterName(string name) =>
        name.Contains("Wintun", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("NanoClash", StringComparison.OrdinalIgnoreCase);
}
