using System.Net;
using System.Net.Sockets;

namespace Clash.Dns;

/// <summary>
/// Well-known public DoH/DoT resolver IPs. Under Fake-IP TUN these must not return real A records
/// to apps (would bypass domain rules). Outbound DoH still works via InterfaceBinder.
/// </summary>
internal static class DohBlocklist
{
    private static readonly HashSet<uint> Blocked = Build();

    public static bool IsBlocked(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork)
            return false;
        var b = ip.GetAddressBytes();
        var v = (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
        return Blocked.Contains(v);
    }

    private static HashSet<uint> Build()
    {
        string[] hosts =
        [
            "1.1.1.1", "1.0.0.1",
            "8.8.8.8", "8.8.4.4",
            "9.9.9.9", "149.112.112.112",
            "208.67.222.222", "208.67.220.220",
            "94.140.14.14", "94.140.15.15",
            "1.12.12.12", "120.53.53.53",
            "223.5.5.5", "223.6.6.6",
            "76.76.21.21",
            "185.222.222.222",
            "45.90.28.167", "45.90.30.167",
        ];
        var set = new HashSet<uint>();
        foreach (var h in hosts)
        {
            if (!IPAddress.TryParse(h, out var ip))
                continue;
            var b = ip.GetAddressBytes();
            if (b.Length != 4)
                continue;
            set.Add((uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]));
        }

        return set;
    }
}
