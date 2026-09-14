using System.Buffers.Binary;
using System.Net;
using System.Text;

using Clash.Utils;

namespace Clash.Dns;

/// <summary>Minimal DNS message parse/build for Fake-IP A answers on TUN.</summary>
internal static class DnsMessage
{
    public const ushort TypeA = 1;
    public const ushort TypeAaaa = 28;
    public const ushort ClassIn = 1;

    public static bool TryParseQuery(
        ReadOnlySpan<byte> payload,
        out ushort id,
        out string qname,
        out ushort qtype)
    {
        id = 0;
        qname = "";
        qtype = 0;
        if (payload.Length < 12)
            return false;
        id = BinaryPrimitives.ReadUInt16BigEndian(payload);
        var flags = BinaryPrimitives.ReadUInt16BigEndian(payload[2..]);
        if ((flags & 0x8000) != 0)
            return false; // response
        var qd = BinaryPrimitives.ReadUInt16BigEndian(payload[4..]);
        if (qd < 1)
            return false;

        var off = 12;
        if (!TryReadName(payload, ref off, out qname))
            return false;
        if (off + 4 > payload.Length)
            return false;
        qtype = BinaryPrimitives.ReadUInt16BigEndian(payload[off..]);
        off += 2;
        var qclass = BinaryPrimitives.ReadUInt16BigEndian(payload[off..]);
        return qclass == ClassIn && qname.Length > 0;
    }

    public static byte[] BuildAResponse(ushort id, string qname, IPAddress ipv4, uint ttl = 300)
    {
        if (ipv4.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new ArgumentException("IPv4 required", nameof(ipv4));

        var nameBytes = EncodeName(qname);
        // header(12) + question + answer(name ptr + type/class/ttl/rdlen + 4)
        var qLen = nameBytes.Length + 4;
        var ansLen = 2 + 2 + 2 + 4 + 2 + 4; // compression ptr + fields + rdata
        var buf = new byte[12 + qLen + ansLen];
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(0), id);
        // QR=1 AA=1 RD=1 RA=1
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2), 0x8580);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(4), 1); // QD
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(6), 1); // AN
        var o = 12;
        nameBytes.AsSpan().CopyTo(buf.AsSpan(o));
        o += nameBytes.Length;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(o), TypeA);
        o += 2;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(o), ClassIn);
        o += 2;
        // name pointer to offset 12
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(o), 0xC00C);
        o += 2;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(o), TypeA);
        o += 2;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(o), ClassIn);
        o += 2;
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(o), ttl);
        o += 4;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(o), 4);
        o += 2;
        ipv4.TryWriteBytes(buf.AsSpan(o, 4), out _);
        return buf;
    }

    /// <summary>Empty answer (no error) for non-A queries so clients fall back cleanly.</summary>
    public static byte[] BuildEmptyResponse(ushort id, string qname, ushort qtype, ushort rcode = 0)
    {
        var nameBytes = EncodeName(qname);
        var buf = new byte[12 + nameBytes.Length + 4];
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(0), id);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2), (ushort)(0x8180 | (rcode & 0xF)));
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(4), 1);
        var o = 12;
        nameBytes.AsSpan().CopyTo(buf.AsSpan(o));
        o += nameBytes.Length;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(o), qtype);
        o += 2;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(o), ClassIn);
        return buf;
    }

    private static byte[] EncodeName(string name)
    {
        name = DomainName.ToAscii(name);
        var labels = name.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var len = 1;
        foreach (var l in labels)
            len += 1 + Encoding.ASCII.GetByteCount(l);
        var buf = new byte[len];
        var o = 0;
        foreach (var l in labels)
        {
            var b = Encoding.ASCII.GetBytes(l);
            if (b.Length > 63)
                throw new ArgumentException("DNS label too long");
            buf[o++] = (byte)b.Length;
            b.CopyTo(buf, o);
            o += b.Length;
        }

        buf[o] = 0;
        return buf;
    }

    private static bool TryReadName(ReadOnlySpan<byte> msg, ref int off, out string name)
    {
        name = "";
        var sb = new StringBuilder();
        var jumped = false;
        var end = off;
        var guard = 0;
        while (guard++ < 64)
        {
            if (off >= msg.Length)
                return false;
            var len = msg[off];
            if (len == 0)
            {
                off++;
                if (!jumped)
                    end = off;
                off = end;
                name = sb.ToString();
                return name.Length > 0;
            }

            if ((len & 0xC0) == 0xC0)
            {
                if (off + 1 >= msg.Length)
                    return false;
                var ptr = ((len & 0x3F) << 8) | msg[off + 1];
                if (!jumped)
                    end = off + 2;
                off = ptr;
                jumped = true;
                continue;
            }

            off++;
            if (off + len > msg.Length)
                return false;
            if (sb.Length > 0)
                sb.Append('.');
            sb.Append(Encoding.ASCII.GetString(msg.Slice(off, len)));
            off += len;
        }

        return false;
    }
}
