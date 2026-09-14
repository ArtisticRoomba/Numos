namespace Numos.Serialization;

internal sealed class LimitedReadStream : Stream
{
    private readonly Stream _source;

    public LimitedReadStream(Stream source, long length)
    {
        _source = source;
        Length = length;
        Remaining = length;
    }

    public long Remaining { get; private set; }
    public override bool CanRead => _source.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length { get; }
    public override long Position
    {
        get => Length - Remaining;
        set => throw new NotSupportedException();
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

internal sealed class CountingWriteStream : Stream
{
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => Position;
    public override long Position { get; set; }

    public override void Flush()
    {
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        Position = checked(Position + count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Position = checked(Position + buffer.Length);
    }

    public override void WriteByte(byte value)
    {
        Position = checked(Position + 1);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }
}