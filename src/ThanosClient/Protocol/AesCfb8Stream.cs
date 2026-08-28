using System.Security.Cryptography;

namespace ThanosClient.Protocol;

/// <summary>
/// AES-128/CFB8 stream cipher, as used by the Minecraft protocol once encryption is
/// enabled. Key and IV are both the 16-byte shared secret.
/// CFB8 is built by hand on top of an ECB block transform because .NET's CFB support
/// with a feedback size of 8 is not available on every platform.
/// </summary>
public sealed class AesCfb8Stream : Stream
{
    private readonly Stream _inner;
    private readonly Aes _aes;

    // Each direction gets its own transform, feedback register and keystream buffer.
    // Reads and writes run on different threads simultaneously - the receive loop is
    // decrypting while the tick thread sends position updates - so sharing any of this
    // state between the two directions silently desynchronises the cipher.
    private readonly ICryptoTransform _encBlock;
    private readonly ICryptoTransform _decBlock;
    private readonly byte[] _encRegister = new byte[16];
    private readonly byte[] _decRegister = new byte[16];
    private readonly byte[] _encKeyStream = new byte[16];
    private readonly byte[] _decKeyStream = new byte[16];
    private readonly object _encLock = new();
    private readonly object _decLock = new();

    public AesCfb8Stream(Stream inner, byte[] sharedSecret)
    {
        if (sharedSecret.Length != 16)
            throw new ArgumentException("shared secret must be 16 bytes", nameof(sharedSecret));

        _inner = inner;
        _aes = Aes.Create();
        _aes.Mode = CipherMode.ECB;
        _aes.Padding = PaddingMode.None;
        _aes.Key = sharedSecret;
        _encBlock = _aes.CreateEncryptor();
        _decBlock = _aes.CreateEncryptor();   // CFB8 decryption also uses the forward transform

        Buffer.BlockCopy(sharedSecret, 0, _encRegister, 0, 16);
        Buffer.BlockCopy(sharedSecret, 0, _decRegister, 0, 16);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => _inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = _inner.Read(buffer, offset, count);
        if (read <= 0) return read;

        lock (_decLock)
        {
            for (int i = 0; i < read; i++)
            {
                _decBlock.TransformBlock(_decRegister, 0, 16, _decKeyStream, 0);
                byte cipher = buffer[offset + i];
                buffer[offset + i] = (byte)(cipher ^ _decKeyStream[0]);
                ShiftIn(_decRegister, cipher);
            }
        }

        return read;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        byte[] output = new byte[count];

        lock (_encLock)
        {
            for (int i = 0; i < count; i++)
            {
                _encBlock.TransformBlock(_encRegister, 0, 16, _encKeyStream, 0);
                byte cipher = (byte)(buffer[offset + i] ^ _encKeyStream[0]);
                output[i] = cipher;
                ShiftIn(_encRegister, cipher);
            }
        }

        _inner.Write(output, 0, count);
    }

    /// <summary>Slide the feedback register one byte left and append the ciphertext byte.</summary>
    private static void ShiftIn(byte[] register, byte cipherByte)
    {
        Buffer.BlockCopy(register, 1, register, 0, 15);
        register[15] = cipherByte;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _encBlock.Dispose();
            _decBlock.Dispose();
            _aes.Dispose();
            _inner.Dispose();
        }
        base.Dispose(disposing);
    }
}
