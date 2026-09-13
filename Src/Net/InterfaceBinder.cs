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

    public static bool IsBound => _physical is not null;

    public static PhysicalEndpoint? Current => Volatile.Read(ref _physical);

    public static void SetPhysical(PhysicalEndpoint? endpoint) =>
        Volatile.Write(ref _physical, endpoint);

    public static void Clear() => Volatile.Write(ref _physical, null);

    public static void Bind(Socket socket)
    {
        var phy = Current;
        if (phy is null)
            return;
        if (socket.AddressFamily != AddressFamily.InterNetwork)
            return;

        socket.Bind(new IPEndPoint(phy.LocalAddress, 0));

        // Force egress interface even when the routing table prefers TUN (/1 defaults).
        Span<byte> idxBe = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(idxBe, phy.InterfaceIndex);
        socket.SetSocketOption(SocketOptionLevel.IP, IpUnicastIf, idxBe.ToArray());
    }
}

internal sealed record PhysicalEndpoint(
    int InterfaceIndex,
    IPAddress LocalAddress,
    IPAddress Gateway,
    string Name,
    IReadOnlyList<IPAddress> DnsServers);
