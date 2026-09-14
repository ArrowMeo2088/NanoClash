using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Clash.Net;

/// <summary>
/// When enhance/TUN is on, force outbound sockets onto the physical NIC so traffic
/// does not re-enter the WinTUN default route (loop prevention).
/// </summary>
internal static class InterfaceBinder
{
    // WinSock2 IPPROTO_IP / IP_UNICAST_IF
    private const SocketOptionName IpUnicastIf = (SocketOptionName)31;

    private static PhysicalEndpoint? _physical;
    private static byte[]? _unicastIf;

    public static bool IsBound => _physical is not null;

    public static PhysicalEndpoint? Current => Volatile.Read(ref _physical);

    public static void SetPhysical(PhysicalEndpoint? endpoint)
    {
        Volatile.Write(ref _physical, endpoint);
        if (endpoint is null)
        {
            Volatile.Write(ref _unicastIf, null);
            return;
        }

        var buf = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buf, endpoint.InterfaceIndex);
        Volatile.Write(ref _unicastIf, buf);
    }

    public static void Clear() => SetPhysical(null);

    public static void Bind(Socket socket)
    {
        var phy = Current;
        if (phy is null)
            return;
        if (socket.AddressFamily != AddressFamily.InterNetwork)
            return;

        socket.Bind(new IPEndPoint(phy.LocalAddress, 0));

        // Force egress interface even when the routing table prefers TUN (/1 defaults).
        var idx = Volatile.Read(ref _unicastIf);
        if (idx is not null)
            socket.SetSocketOption(SocketOptionLevel.IP, IpUnicastIf, idx);
    }
}

internal sealed record PhysicalEndpoint(
    int InterfaceIndex,
    IPAddress LocalAddress,
    IPAddress Gateway,
    string Name,
    IReadOnlyList<IPAddress> DnsServers);
