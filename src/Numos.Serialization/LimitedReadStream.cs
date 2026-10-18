namespace Numos.Serialization;

internal sealed class LimitedReadStream : Stream
{
    private readonly Stream _source;
    private long _length;

    public LimitedReadStream(Stream source, long length)
    {
        _source = source;
        Reset(length);
    }

    public long Remaining { get; private set; }
    public override bool CanRead => _source.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position
    {
        get => Length - Remaining;
        set => throw new NotSupportedException();
    }

    /// <summary>
    ///     Starts a new window of <paramref name="length" /> bytes at the source's current position.
    /// </summary>
    public void Reset(long length)
    {
        _length = length;
        Remaining = length;
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int requested = (int)Math.Min(count, Remaining);
        int read = _source.Read(buffer, offset, requested);
        Remaining -= read;
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        int read = _source.Read(buffer[..(int)Math.Min(buffer.Length, Remaining)]);
        Remaining -= read;
        return read;
    }

    public override int ReadByte()
    {
        if (Remaining == 0) return -1;

        int value = _source.ReadByte();
        if (value >= 0) Remaining--;
        return value;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }

    protected override void Dispose(bool disposing)
    {
    }
}