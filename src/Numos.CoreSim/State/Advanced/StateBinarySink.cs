using System.Buffers;
using System.Buffers.Binary;
using Numos.CoreSim.Replay;

namespace Numos.CoreSim.State.Advanced;

/// <summary>
///     An <see cref="IStateSink" /> that writes the state wire format into a growable buffer.
/// </summary>
/// <remarks>
///     <para>
///         Everything is little-endian. A field is its id (<see langword="int" />), its payload length
///         (<see langword="int" />), then the payload, so a reader can skip ids it doesn't know. A nested object is its
///         length followed by its fields. The outermost object has no length of its own: whatever contains it, an
///         archive section or a state file, already records the payload length.
///     </para>
///     <para>
///         Lengths are written as placeholders and patched when the field or object ends, so the value is walked
///         once. Counts are <see langword="int" />, booleans one byte, floats their raw bits, and strings a byte length
///         followed by UTF-8.
///     </para>
///     <para>
///         <see cref="StateReader" /> reads this format back. Call <see cref="Reset" /> before reusing a sink for another value.
///     </para>
/// </remarks>
/// <example>
///     <code>
///     var sink = new StateBinarySink();
///     NumosState.Write(ref sink, settings);
///     ReadOnlySpan&lt;byte&gt; payload = sink.WrittenSpan;
///     </code>
/// </example>
public struct StateBinarySink : IStateSink
{
    private byte[] _buffer;
    private int _length;

    // Offsets of the length placeholders still open, innermost last. The outermost object pushes -1 because it has no
    // placeholder to patch.
    private int[] _open;
    private int _depth;

    // Whether _buffer came from the shared pool, so growing and Return hand it back.
    private bool _pooled;

    /// <summary>
    ///     Creates an empty sink.
    /// </summary>
    public StateBinarySink()
    {
        _buffer = new byte[256];
        _open = new int[16];
    }

    /// <summary>
    ///     Creates an empty sink with room for <paramref name="capacity" /> bytes before it has to grow.
    /// </summary>
    /// <param name="capacity">The expected payload size.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity" /> is negative.</exception>
    public StateBinarySink(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        _buffer = new byte[Math.Max(capacity, 16)];
        _open = new int[16];
    }

    private StateBinarySink(byte[] buffer, bool pooled)
    {
        _buffer = buffer;
        _open = new int[16];
        _pooled = pooled;
    }

    /// <summary>
    ///     Creates a sink whose buffer comes from <see cref="ArrayPool{T}.Shared" />, for a large payload that's copied
    ///     out once written. Call <see cref="Return" /> when done with <see cref="WrittenSpan" />.
    /// </summary>
    internal static StateBinarySink Rent(int capacity)
    {
        return new StateBinarySink(ArrayPool<byte>.Shared.Rent(Math.Max(capacity, 256)), true);
    }

    /// <summary>
    ///     Gives a rented buffer back to the pool. The sink is empty afterwards.
    /// </summary>
    internal void Return()
    {
        if (_pooled)
            ArrayPool<byte>.Shared.Return(_buffer);

        _buffer = [];
        _pooled = false;
        Reset();
    }

    /// <summary>
    ///     Gets the bytes written so far.
    /// </summary>
    /// <remarks>Only complete once the outermost <see cref="EndObject" /> has run.</remarks>
    public readonly ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _length);

    /// <summary>
    ///     Empties the sink so it can write another value, keeping its buffer.
    /// </summary>
    public void Reset()
    {
        _length = 0;
        _depth = 0;
    }

    /// <inheritdoc />
    public void BeginObject()
    {
        if (_depth == 0)
        {
            Push(-1);
            return;
        }

        Push(_length);
        WriteInt32(0);
    }

    /// <inheritdoc />
    public void EndObject() => Pop();

    /// <inheritdoc />
    public void BeginField(int id)
    {
        WriteInt32(id);
        Push(_length);
        WriteInt32(0);
    }

    /// <inheritdoc />
    public void EndField() => Pop();

    /// <inheritdoc />
    public void WriteCount(int count) => WriteInt32(count);

    /// <inheritdoc />
    public void WriteBoolean(bool value) => WriteByte(value ? (byte)1 : (byte)0);

    /// <inheritdoc />
    public void WriteByte(byte value) => Reserve(1)[0] = value;

    /// <inheritdoc />
    public void WriteSByte(sbyte value) => WriteByte(unchecked((byte)value));

    /// <inheritdoc />
    public void WriteInt16(short value) => BinaryPrimitives.WriteInt16LittleEndian(Reserve(2), value);

    /// <inheritdoc />
    public void WriteUInt16(ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(Reserve(2), value);

    /// <inheritdoc />
    public void WriteInt32(int value) => BinaryPrimitives.WriteInt32LittleEndian(Reserve(4), value);

    /// <inheritdoc />
    public void WriteUInt32(uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Reserve(4), value);

    /// <inheritdoc />
    public void WriteInt64(long value) => BinaryPrimitives.WriteInt64LittleEndian(Reserve(8), value);

    /// <inheritdoc />
    public void WriteUInt64(ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(Reserve(8), value);

    /// <inheritdoc />
    public void WriteSingle(float value) => BinaryPrimitives.WriteSingleLittleEndian(Reserve(4), value);

    /// <inheritdoc />
    public void WriteDouble(double value) => BinaryPrimitives.WriteDoubleLittleEndian(Reserve(8), value);

    /// <inheritdoc />
    public void WriteString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        int byteCount = AtmosReplayCodec.Utf8.GetByteCount(value);
        WriteInt32(byteCount);
        AtmosReplayCodec.Utf8.GetBytes(value, Reserve(byteCount));
    }

    /// <inheritdoc />
    public void WriteElements(ReadOnlySpan<byte> elements, int elementSize)
    {
        Span<byte> destination = Reserve(elements.Length);
        elements.CopyTo(destination);
        ReverseElements(destination, elementSize);
    }

    /// <summary>
    ///     Reverses the bytes of each element on a big-endian host. It does nothing on a little-endian host.
    /// </summary>
    /// <param name="elements">The elements' bytes, a whole number of elements.</param>
    /// <param name="elementSize">The size of one element in bytes.</param>
    /// <remarks>
    ///     The swap is its own inverse, so the same call converts native bytes to wire bytes and back. The reader uses
    ///     it too. Only primitives are valid, because a struct's fields would need swapping one by one.
    /// </remarks>
    internal static void ReverseElements(Span<byte> elements, int elementSize)
    {
        if (BitConverter.IsLittleEndian)
            return;

        for (int offset = 0; offset < elements.Length; offset += elementSize)
            elements.Slice(offset, elementSize).Reverse();
    }

    private Span<byte> Reserve(int count)
    {
        int required = checked(_length + count);
        if (required > _buffer.Length)
            Grow(Math.Max(required, (int)Math.Min(Array.MaxLength, (long)_buffer.Length * 2)));

        Span<byte> span = _buffer.AsSpan(_length, count);
        _length = required;
        return span;
    }

    private void Grow(int length)
    {
        if (!_pooled)
        {
            Array.Resize(ref _buffer, length);
            return;
        }

        byte[] grown = ArrayPool<byte>.Shared.Rent(length);
        _buffer.AsSpan(0, _length).CopyTo(grown);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = grown;
    }

    private void Push(int placeholder)
    {
        if (_depth == _open.Length)
            Array.Resize(ref _open, _open.Length * 2);

        _open[_depth++] = placeholder;
    }

    private void Pop()
    {
        if (_depth == 0)
            throw new InvalidOperationException("EndField or EndObject has no matching Begin call.");

        int placeholder = _open[--_depth];
        if (placeholder >= 0)
        {
            int payloadLength = _length - placeholder - sizeof(int);
            BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(placeholder), payloadLength);
        }
    }
}
