using System.IO.Compression;

namespace ThanosClient.Protocol;

/// <summary>
/// Minecraft packet framing over a TCP stream: length-prefixed frames, optional zlib
/// compression, and optional AES-CFB8 encryption. Both layers can be switched on
/// mid-connection, which is exactly what the login sequence does.
/// </summary>
public sealed class PacketStream : IDisposable
{
    private Stream _stream;
    private readonly object _writeLock = new();
    private int _compressionThreshold = -1;
    private bool _disposed;

    /// <summary>Hard cap on a single frame, so a hostile server cannot exhaust memory.</summary>
    private const int MaxPacketSize = 8 * 1024 * 1024;

    public PacketStream(Stream stream) => _stream = stream;

    public bool EncryptionEnabled { get; private set; }
    public bool CompressionEnabled => _compressionThreshold >= 0;

    /// <summary>Wraps the transport in AES-CFB8, straight after the encryption response is flushed.</summary>
    public void EnableEncryption(byte[] sharedSecret)
    {
        lock (_writeLock)
        {
            _stream = new AesCfb8Stream(_stream, sharedSecret);
            EncryptionEnabled = true;
        }
    }

    /// <summary>Threshold below which packets stay uncompressed. Negative disables compression.</summary>
    public void SetCompressionThreshold(int threshold)
    {
        lock (_writeLock) _compressionThreshold = threshold;
    }

    /// <summary>Reads one full packet and returns its decoded body (packet id VarInt included).</summary>
    public byte[] ReadPacket()
    {
        int frameLength = ReadVarIntFromStream();
        if (frameLength < 0 || frameLength > MaxPacketSize)
            throw new ProtocolException($"frame length {frameLength} out of range");

        byte[] frame = ReadExactly(frameLength);

        if (!CompressionEnabled)
            return frame;

        var reader = new PacketReader(frame);
        int uncompressedLength = reader.VarInt();
        byte[] body = frame.AsSpan(reader.Position).ToArray();

        if (uncompressedLength == 0)
            return body;

        if (uncompressedLength > MaxPacketSize)
            throw new ProtocolException($"declared uncompressed size {uncompressedLength} out of range");

        return Inflate(body, uncompressedLength);
    }

    /// <summary>Serialises and sends a packet. Safe to call from multiple threads.</summary>
    public void SendPacket(int packetId, byte[] payload)
    {
        byte[] body = new PacketWriter().VarInt(packetId).Raw(payload).ToArray();

        lock (_writeLock)
        {
            byte[] frame;

            if (CompressionEnabled)
            {
                var framed = new PacketWriter();
                if (body.Length >= _compressionThreshold)
                {
                    framed.VarInt(body.Length);
                    framed.Raw(Deflate(body));
                }
                else
                {
                    framed.VarInt(0);
                    framed.Raw(body);
                }
                frame = framed.ToArray();
            }
            else
            {
                frame = body;
            }

            byte[] packet = new PacketWriter().VarInt(frame.Length).Raw(frame).ToArray();
            _stream.Write(packet, 0, packet.Length);
            _stream.Flush();
        }
    }

    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true))
            zlib.Write(data, 0, data.Length);
        return output.ToArray();
    }

    private static byte[] Inflate(byte[] data, int expectedLength)
    {
        byte[] result = new byte[expectedLength];
        using var input = new MemoryStream(data);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);

        int total = 0;
        while (total < expectedLength)
        {
            int read = zlib.Read(result, total, expectedLength - total);
            if (read <= 0) throw new ProtocolException("compressed packet was shorter than declared");
            total += read;
        }

        return result;
    }

    private int ReadVarIntFromStream()
    {
        int result = 0;
        int shift = 0;
        while (true)
        {
            int b = _stream.ReadByte();
            if (b < 0) throw new EndOfStreamException("connection closed while reading packet length");
            result |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
            if (shift >= 35) throw new ProtocolException("VarInt is too big");
        }
    }

    private byte[] ReadExactly(int count)
    {
        byte[] buffer = new byte[count];
        int total = 0;
        while (total < count)
        {
            int read = _stream.Read(buffer, total, count - total);
            if (read <= 0) throw new EndOfStreamException("connection closed mid-packet");
            total += read;
        }
        return buffer;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _stream.Dispose(); } catch { /* socket already gone */ }
    }
}
