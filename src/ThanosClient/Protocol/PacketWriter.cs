using System.Buffers.Binary;
using System.Text;

namespace ThanosClient.Protocol;

/// <summary>Builds a packet payload in Minecraft's big-endian wire format.</summary>
public sealed class PacketWriter
{
    private readonly List<byte> _buf = new(64);

    public int Length => _buf.Count;

    public PacketWriter Byte(byte value) { _buf.Add(value); return this; }
    public PacketWriter SByte(sbyte value) { _buf.Add(unchecked((byte)value)); return this; }
    public PacketWriter Bool(bool value) { _buf.Add(value ? (byte)1 : (byte)0); return this; }
    public PacketWriter Raw(ReadOnlySpan<byte> value) { _buf.AddRange(value); return this; }

    public PacketWriter VarInt(int value)
    {
        uint v = unchecked((uint)value);
        do
        {
            byte temp = (byte)(v & 0x7F);
            v >>= 7;
            if (v != 0) temp |= 0x80;
            _buf.Add(temp);
        } while (v != 0);
        return this;
    }

    public PacketWriter VarLong(long value)
    {
        ulong v = unchecked((ulong)value);
        do
        {
            byte temp = (byte)(v & 0x7F);
            v >>= 7;
            if (v != 0) temp |= 0x80;
            _buf.Add(temp);
        } while (v != 0);
        return this;
    }

    public PacketWriter UShort(ushort value)
    {
        Span<byte> s = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(s, value);
        return Raw(s);
    }

    public PacketWriter Short(short value)
    {
        Span<byte> s = stackalloc byte[2];
        BinaryPrimitives.WriteInt16BigEndian(s, value);
        return Raw(s);
    }

    public PacketWriter Int(int value)
    {
        Span<byte> s = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(s, value);
        return Raw(s);
    }

    public PacketWriter Long(long value)
    {
        Span<byte> s = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(s, value);
        return Raw(s);
    }

    public PacketWriter Float(float value)
    {
        Span<byte> s = stackalloc byte[4];
        BinaryPrimitives.WriteSingleBigEndian(s, value);
        return Raw(s);
    }

    public PacketWriter Double(double value)
    {
        Span<byte> s = stackalloc byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(s, value);
        return Raw(s);
    }

    /// <summary>VarInt-prefixed UTF-8 string.</summary>
    public PacketWriter String(string value)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(value);
        VarInt(utf8.Length);
        return Raw(utf8);
    }

    /// <summary>VarInt-prefixed byte array (protocol 47 uses VarInt lengths for these).</summary>
    public PacketWriter Array(ReadOnlySpan<byte> value)
    {
        VarInt(value.Length);
        return Raw(value);
    }

    public byte[] ToArray() => _buf.ToArray();
}
