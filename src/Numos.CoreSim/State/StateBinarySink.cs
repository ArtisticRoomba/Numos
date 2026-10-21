using System.Buffers.Binary;
using System.Text;

namespace Numos.CoreSim.State;

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
///         <see cref="StateReader" /> reads this format back. A sink is single-use; create a new one per value.
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
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    private byte[] _buffer;
    private int _length;

    // Offsets of the length placeholders still open, innermost last. The outermost object pushes -1 because it has no
    // placeholder to patch.
    private int[] _open;
    private int _depth;

    /// <summary>
    ///     Creates an empty sink.
    /// </summary>
    public StateBinarySink()
    {
        _buffer = new byte[256];
        _open = new int[16];
    }

    /// <summary>
    ///     Gets the bytes written so far.
    /// </summary>
    /// <remarks>Only complete once the outermost <see cref="EndObject" /> has run.</remarks>
    public readonly ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _length);

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
        int byteCount = Utf8.GetByteCount(value);
        WriteInt32(byteCount);
        Utf8.GetBytes(value, Reserve(byteCount));
    }

    private Span<byte> Reserve(int count)
    {
        int required = checked(_length + count);
        if (required > _buffer.Length)
            Array.Resize(ref _buffer, Math.Max(required, (int)Math.Min(Array.MaxLength, (long)_buffer.Length * 2)));

        Span<byte> span = _buffer.AsSpan(_length, count);
        _length = required;
        return span;
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
