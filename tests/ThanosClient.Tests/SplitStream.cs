namespace ThanosClient.Tests;

/// <summary>
/// Reads from one stream and writes to another, so a single duplex cipher can be driven
/// from two threads at once the way a live socket drives it.
/// </summary>
public sealed class SplitStream : Stream
{
    private readonly Stream _readFrom;
    private readonly Stream _writeTo;

    public SplitStream(Stream readFrom, Stream writeTo)
    {
        _readFrom = readFrom;
        _writeTo = writeTo;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => _writeTo.Flush();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        lock (_readFrom) return _readFrom.Read(buffer, offset, count);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        lock (_writeTo) _writeTo.Write(buffer, offset, count);
    }
}
