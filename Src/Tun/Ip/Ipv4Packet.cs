using System.Buffers.Binary;
using System.Net;

namespace Clash.Tun.Ip;

internal static class Ipv4
{
    public const byte ProtoTcp = 6;
    public const byte ProtoUdp = 17;
    public const int HeaderMin = 20;

    public static bool TryParse(
        Span<byte> packet,
        out int headerLen,
        out byte protocol,
        out IPAddress src,
        out IPAddress dst,
        out int totalLen)
    {
        headerLen = 0;
        protocol = 0;
        src = IPAddress.None;
        dst = IPAddress.None;
        totalLen = 0;
        if (packet.Length < HeaderMin)
            return false;
        var verIhl = packet[0];
        if ((verIhl >> 4) != 4)
            return false;
        headerLen = (verIhl & 0x0F) * 4;
        if (headerLen < HeaderMin || packet.Length < headerLen)
            return false;
        totalLen = BinaryPrimitives.ReadUInt16BigEndian(packet[2..4]);
        if (totalLen < headerLen || totalLen > packet.Length)
            totalLen = packet.Length;
        protocol = packet[9];
        src = new IPAddress(packet.Slice(12, 4));
        dst = new IPAddress(packet.Slice(16, 4));
        return true;
    }

    public static void SetAddresses(Span<byte> packet, int headerLen, IPAddress src, IPAddress dst)
    {
        if (!src.TryWriteBytes(packet.Slice(12, 4), out var sn) || sn != 4)
            return;
        if (!dst.TryWriteBytes(packet.Slice(16, 4), out var dn) || dn != 4)
            return;
        UpdateChecksum(packet, headerLen);
    }

    public static void UpdateChecksum(Span<byte> packet, int headerLen)
    {
        packet[10] = 0;
        packet[11] = 0;
        var sum = Checksum.Compute(packet[..headerLen]);
        BinaryPrimitives.WriteUInt16BigEndian(packet[10..12], sum);
    }
    public static void SetTotalLength(Span<byte> packet, ushort totalLen)
    {
        BinaryPrimitives.WriteUInt16BigEndian(packet[2..4], totalLen);
        var headerLen = (packet[0] & 0x0F) * 4;
        UpdateChecksum(packet, headerLen);
    }
}

internal static class UdpHeader
{
    public const int HeaderLen = 8;

    public static bool TryParse(
        ReadOnlySpan<byte> segment,
        out ushort srcPort,
        out ushort dstPort,
        out int payloadLen)
    {
        srcPort = 0;
        dstPort = 0;
        payloadLen = 0;
        if (segment.Length < HeaderLen)
            return false;
        srcPort = BinaryPrimitives.ReadUInt16BigEndian(segment);
        dstPort = BinaryPrimitives.ReadUInt16BigEndian(segment[2..]);
        var udpLen = BinaryPrimitives.ReadUInt16BigEndian(segment[4..]);
        if (udpLen < HeaderLen || udpLen > segment.Length)
            udpLen = (ushort)segment.Length;
        payloadLen = udpLen - HeaderLen;
        return true;
    }

    public static void Write(
        Span<byte> segment,
        ushort srcPort,
        ushort dstPort,
        ReadOnlySpan<byte> payload,
        IPAddress src,
        IPAddress dst)
    {
        var udpLen = HeaderLen + payload.Length;
        BinaryPrimitives.WriteUInt16BigEndian(segment, srcPort);
        BinaryPrimitives.WriteUInt16BigEndian(segment[2..], dstPort);
        BinaryPrimitives.WriteUInt16BigEndian(segment[4..], (ushort)udpLen);
        segment[6] = 0;
        segment[7] = 0;
        payload.CopyTo(segment[HeaderLen..]);

        // UDP checksum optional for IPv4; compute properly for picky stacks.
        Span<byte> srcBytes = stackalloc byte[4];
        Span<byte> dstBytes = stackalloc byte[4];
        src.TryWriteBytes(srcBytes, out _);
        dst.TryWriteBytes(dstBytes, out _);
        uint sum = 0;
        sum += (uint)(srcBytes[0] << 8 | srcBytes[1]);
        sum += (uint)(srcBytes[2] << 8 | srcBytes[3]);
        sum += (uint)(dstBytes[0] << 8 | dstBytes[1]);
        sum += (uint)(dstBytes[2] << 8 | dstBytes[3]);
        sum += Ipv4.ProtoUdp;
        sum += (uint)udpLen;
        sum += Checksum.SumWords(segment[..udpLen]);
        var csum = Checksum.Fold(sum);
        if (csum == 0)
            csum = 0xFFFF;
        BinaryPrimitives.WriteUInt16BigEndian(segment[6..8], csum);
    }
}

internal static class TcpHeader
{
    public const int HeaderMin = 20;
    public const byte FlagFin = 0x01;
    public const byte FlagSyn = 0x02;
    public const byte FlagRst = 0x04;
    public const byte FlagPsh = 0x08;
    public const byte FlagAck = 0x10;

    public static bool TryParse(
        ReadOnlySpan<byte> segment,
        out ushort srcPort,
        out ushort dstPort,
        out byte flags,
        out int dataOffset)
    {
        srcPort = 0;
        dstPort = 0;
        flags = 0;
        dataOffset = 0;
        if (segment.Length < HeaderMin)
            return false;
        srcPort = BinaryPrimitives.ReadUInt16BigEndian(segment);
        dstPort = BinaryPrimitives.ReadUInt16BigEndian(segment[2..]);
        dataOffset = (segment[12] >> 4) * 4;
        if (dataOffset < HeaderMin || dataOffset > segment.Length)
            return false;
        flags = segment[13];
        return true;
    }

    public static void SetPorts(Span<byte> segment, ushort srcPort, ushort dstPort)
    {
        BinaryPrimitives.WriteUInt16BigEndian(segment, srcPort);
        BinaryPrimitives.WriteUInt16BigEndian(segment[2..], dstPort);
    }

    public static void UpdateChecksum(
        Span<byte> packet,
        int ipHeaderLen,
        IPAddress src,
        IPAddress dst)
    {
        // Prefer the slice length we are actually rewriting (header TotalLength may be stale/wrong).
        var totalLen = packet.Length;
        var hdrTotal = BinaryPrimitives.ReadUInt16BigEndian(packet[2..4]);
        if (hdrTotal >= ipHeaderLen && hdrTotal <= packet.Length)
            totalLen = hdrTotal;

        var tcpLen = totalLen - ipHeaderLen;
        if (tcpLen < HeaderMin)
            return;
        var tcp = packet.Slice(ipHeaderLen, tcpLen);
        tcp[16] = 0;
        tcp[17] = 0;

        Span<byte> srcBytes = stackalloc byte[4];
        Span<byte> dstBytes = stackalloc byte[4];
        if (!src.TryWriteBytes(srcBytes, out var sn) || sn != 4)
            return;
        if (!dst.TryWriteBytes(dstBytes, out var dn) || dn != 4)
            return;

        uint sum = 0;
        sum += (uint)(srcBytes[0] << 8 | srcBytes[1]);
        sum += (uint)(srcBytes[2] << 8 | srcBytes[3]);
        sum += (uint)(dstBytes[0] << 8 | dstBytes[1]);
        sum += (uint)(dstBytes[2] << 8 | dstBytes[3]);
        sum += Ipv4.ProtoTcp;
        sum += (uint)tcpLen;
        sum += Checksum.SumWords(tcp);
        var csum = Checksum.Fold(sum);
        BinaryPrimitives.WriteUInt16BigEndian(tcp[16..18], csum);
    }
}

internal static class Checksum
{
    public static ushort Compute(ReadOnlySpan<byte> data) => Fold(SumWords(data));

    public static uint SumWords(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        var i = 0;
        for (; i + 1 < data.Length; i += 2)
            sum += (uint)(data[i] << 8 | data[i + 1]);
        if (i < data.Length)
            sum += (uint)(data[i] << 8);
        return sum;
    }

    public static ushort Fold(uint sum)
    {
        while (sum > 0xFFFF)
            sum = (sum & 0xFFFF) + (sum >> 16);
        return (ushort)~sum;
    }
}
