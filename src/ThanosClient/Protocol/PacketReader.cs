using System.Buffers.Binary;
using System.Text;

namespace ThanosClient.Protocol;

/// <summary>Reads a decoded packet payload. Throws <see cref="ProtocolException"/> on truncation.</summary>
public sealed class PacketReader
{
    private readonly byte[] _data;
    private int _pos;

    public PacketReader(byte[] data, int offset = 0)
    {
        _data = data;
        _pos = offset;
    }

    public int Remaining => _data.Length - _pos;
    public int Position => _pos;

    private void Need(int count)
    {
        if (Remaining < count)
            throw new ProtocolException($"packet truncated: wanted {count} byte(s), {Remaining} left");
    }

    public byte Byte()
    {
        Need(1);
        return _data[_pos++];
    }

    public sbyte SByte() => unchecked((sbyte)Byte());
    public bool Bool() => Byte() != 0;

    public int VarInt()
    {
        int result = 0;
        int shift = 0;
        while (true)
        {
            byte b = Byte();
            result |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
            if (shift >= 35) throw new ProtocolException("VarInt is too big");
        }
    }

    public long VarLong()
    {
        long result = 0;
        int shift = 0;
        while (true)
        {
            byte b = Byte();
            result |= (long)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
            if (shift >= 70) throw new ProtocolException("VarLong is too big");
        }
    }

    public ushort UShort()
    {
        Need(2);
        ushort v = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(_pos));
        _pos += 2;
        return v;
    }

    public short Short()
    {
        Need(2);
        short v = BinaryPrimitives.ReadInt16BigEndian(_data.AsSpan(_pos));
        _pos += 2;
        return v;
    }

    public int Int()
    {
        Need(4);
        int v = BinaryPrimitives.ReadInt32BigEndian(_data.AsSpan(_pos));
        _pos += 4;
        return v;
    }

    public long Long()
    {
        Need(8);
        long v = BinaryPrimitives.ReadInt64BigEndian(_data.AsSpan(_pos));
        _pos += 8;
        return v;
    }

    public float Float()
    {
        Need(4);
        float v = BinaryPrimitives.ReadSingleBigEndian(_data.AsSpan(_pos));
        _pos += 4;
        return v;
    }

    public double Double()
    {
        Need(8);
        double v = BinaryPrimitives.ReadDoubleBigEndian(_data.AsSpan(_pos));
        _pos += 8;
        return v;
    }

    public byte[] Bytes(int count)
    {
        Need(count);
        byte[] result = new byte[count];
        System.Array.Copy(_data, _pos, result, 0, count);
        _pos += count;
        return result;
    }

    public string String(int maxLength = 32767)
    {
        int len = VarInt();
        if (len < 0 || len > maxLength * 4)
            throw new ProtocolException($"string length {len} out of range");
        return Encoding.UTF8.GetString(Bytes(len));
    }

    /// <summary>VarInt-prefixed byte array.</summary>
    public byte[] Array() => Bytes(VarInt());

    /// <summary>Everything not yet consumed.</summary>
    public byte[] RestOfPacket() => Bytes(Remaining);

    /// <summary>1.8 UUID-as-16-bytes (used by player list / entity packets).</summary>
    public Guid Uuid()
    {
        long msb = Long();
        long lsb = Long();
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteInt64BigEndian(bytes, msb);
        BinaryPrimitives.WriteInt64BigEndian(bytes[8..], lsb);
        return new Guid(bytes, bigEndian: true);
    }
}

public class ProtocolException : Exception
{
    public ProtocolException(string message) : base(message) { }
    public ProtocolException(string message, Exception inner) : base(message, inner) { }
}
