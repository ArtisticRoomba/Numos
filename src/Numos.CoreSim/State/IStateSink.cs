using Numos.CoreSim.Replay;

namespace Numos.CoreSim.State;

/// <summary>
///     Receives a <see cref="NumosStateAttribute" /> value as a stream of framed fields and primitives.
/// </summary>
/// <remarks>
///     <para>
///         Generated <see cref="INumosState{TSelf}.WriteState{TSink}" /> walkers drive a sink, and one walk serves
///         every consumer: <see cref="StateHashSink" /> hashes it, and a binary sink writes it. Because hash and bytes
///         come from the same calls, a field can't reach one and miss the other.
///     </para>
///     <para>
///         The call shape is fixed. Each value is bracketed by <see cref="BeginObject" /> and <see cref="EndObject" />;
///         inside it, each field is <see cref="BeginField" />, its payload, then <see cref="EndField" />, in ascending
///         id order. Collections write <see cref="WriteCount" /> followed by their elements, and a nullable value
///         writes a <see cref="WriteBoolean" /> presence flag before the value. A binary sink can therefore
///         length-prefix each field and object, and a reader can skip ids it doesn't know.
///     </para>
///     <para>
///         Implement this on a struct and pass it by <see langword="ref" />; walkers take
///         <c>where TSink : struct, IStateSink</c>, so calls are constrained and never box.
///     </para>
/// </remarks>
public interface IStateSink
{
    /// <summary>
    ///     Starts a <see cref="NumosStateAttribute" /> value, top-level or nested.
    /// </summary>
    void BeginObject();

    /// <summary>
    ///     Ends the value started by the matching <see cref="BeginObject" />.
    /// </summary>
    void EndObject();

    /// <summary>
    ///     Starts the payload of the field with <paramref name="id" />.
    /// </summary>
    /// <param name="id">The field's <see cref="StateFieldAttribute.Id" />.</param>
    void BeginField(int id);

    /// <summary>
    ///     Ends the payload started by the matching <see cref="BeginField" />.
    /// </summary>
    void EndField();

    /// <summary>
    ///     Writes the element or entry count that precedes a collection's contents.
    /// </summary>
    /// <param name="count">The number of elements or entries that follow.</param>
    void WriteCount(int count);

    /// <summary>
    ///     Writes a <see langword="bool" />.
    /// </summary>
    /// <param name="value">The value.</param>
    void WriteBoolean(bool value);

    /// <summary>
    ///     Writes a <see langword="byte" />.
    /// </summary>
    /// <param name="value">The value.</param>
    void WriteByte(byte value);

    /// <summary>
    ///     Writes an <see langword="sbyte" />.
    /// </summary>
    /// <param name="value">The value.</param>
    void WriteSByte(sbyte value);

    /// <summary>
    ///     Writes a <see langword="short" />.
    /// </summary>
    /// <param name="value">The value.</param>
    void WriteInt16(short value);

    /// <summary>
    ///     Writes a <see langword="ushort" />.
    /// </summary>
    /// <param name="value">The value.</param>
    void WriteUInt16(ushort value);

    /// <summary>
    ///     Writes an <see langword="int" />.
    /// </summary>
    /// <param name="value">The value.</param>
    void WriteInt32(int value);

    /// <summary>
    ///     Writes a <see langword="uint" />.
    /// </summary>
    /// <param name="value">The value.</param>
    void WriteUInt32(uint value);

    /// <summary>
    ///     Writes a <see langword="long" />.
    /// </summary>
    /// <param name="value">The value.</param>
    void WriteInt64(long value);

    /// <summary>
    ///     Writes a <see langword="ulong" />.
    /// </summary>
    /// <param name="value">The value.</param>
    void WriteUInt64(ulong value);

    /// <summary>
    ///     Writes a <see langword="float" /> by its exact bits.
    /// </summary>
    /// <param name="value">The value.</param>
    void WriteSingle(float value);

    /// <summary>
    ///     Writes a <see langword="double" /> by its exact bits.
    /// </summary>
    /// <param name="value">The value.</param>
    void WriteDouble(double value);

    /// <summary>
    ///     Writes a non-null string.
    /// </summary>
    /// <param name="value">The value. Nullable strings are written as a presence flag followed by this call.</param>
    void WriteString(string value);

    /// <summary>
    ///     Writes a run of fixed-size elements in one call, each little-endian, with no count.
    /// </summary>
    /// <param name="elements">The elements' bytes in native byte order.</param>
    /// <param name="elementSize">The size of one element in bytes; <paramref name="elements" /> is a whole number of them.</param>
    /// <remarks>
    ///     The bulk path for primitive arrays such as per-voxel chunk data. It produces the same bytes as writing each
    ///     element on its own, so it's only a speedup. Sinks swap each element's bytes on a big-endian host, which is
    ///     only correct for primitives; that's all generated code passes here. Callers that write a collection still
    ///     write its count first.
    /// </remarks>
    void WriteElements(ReadOnlySpan<byte> elements, int elementSize);
}

/// <summary>
///     An <see cref="IStateSink" /> that folds everything it receives into a 64-bit FNV-1a state hash.
/// </summary>
/// <remarks>
///     Field ids are hashed, so moving a value to a different id changes the hash. Object boundaries are not; a
///     type's field set is fixed, so they carry no extra information. Primitives are hashed little-endian at their
///     own width, floats by their raw bits.
/// </remarks>
/// <example>
///     <code>
///     var sink = new StateHashSink();
///     sink.WriteString(settings.Key);
///     NumosState.Write(ref sink, settings);
///     ulong hash = sink.Value;
///     </code>
/// </example>
public struct StateHashSink : IStateSink
{
    private AtmosStateHasher _hasher;

    /// <summary>
    ///     Creates a sink at the FNV-1a offset basis.
    /// </summary>
    public StateHashSink()
    {
        _hasher = new AtmosStateHasher();
    }

    internal StateHashSink(AtmosStateHasher hasher)
    {
        _hasher = hasher;
    }

    internal readonly AtmosStateHasher Hasher => _hasher;

    /// <summary>
    ///     Gets the hash of everything written so far.
    /// </summary>
    public readonly ulong Value => _hasher.Value;

    /// <inheritdoc />
    public readonly void BeginObject()
    {
    }

    /// <inheritdoc />
    public readonly void EndObject()
    {
    }

    /// <inheritdoc />
    public void BeginField(int id) => _hasher.Add(id);

    /// <inheritdoc />
    public readonly void EndField()
    {
    }

    /// <inheritdoc />
    public void WriteCount(int count) => _hasher.Add(count);

    /// <inheritdoc />
    public void WriteBoolean(bool value) => _hasher.Add(value);

    /// <inheritdoc />
    public void WriteByte(byte value) => _hasher.AddByte(value);

    /// <inheritdoc />
    public void WriteSByte(sbyte value) => _hasher.AddByte(unchecked((byte)value));

    /// <inheritdoc />
    public void WriteInt16(short value) => WriteUInt16(unchecked((ushort)value));

    /// <inheritdoc />
    public void WriteUInt16(ushort value)
    {
        _hasher.AddByte((byte)value);
        _hasher.AddByte((byte)(value >> 8));
    }

    /// <inheritdoc />
    public void WriteInt32(int value) => _hasher.Add(value);

    /// <inheritdoc />
    public void WriteUInt32(uint value) => _hasher.Add(unchecked((int)value));

    /// <inheritdoc />
    public void WriteInt64(long value) => _hasher.Add(unchecked((ulong)value));

    /// <inheritdoc />
    public void WriteUInt64(ulong value) => _hasher.Add(value);

    /// <inheritdoc />
    public void WriteSingle(float value) => _hasher.Add(value);

    /// <inheritdoc />
    public void WriteDouble(double value) => _hasher.Add(BitConverter.DoubleToUInt64Bits(value));

    /// <inheritdoc />
    public void WriteString(string value) => _hasher.Add(value);

    /// <inheritdoc />
    public void WriteElements(ReadOnlySpan<byte> elements, int elementSize)
    {
        if (BitConverter.IsLittleEndian)
        {
            _hasher.AddBytes(elements);
            return;
        }

        Span<byte> swapped = stackalloc byte[elementSize];
        for (int offset = 0; offset < elements.Length; offset += elementSize)
        {
            elements.Slice(offset, elementSize).CopyTo(swapped);
            swapped.Reverse();
            _hasher.AddBytes(swapped);
        }
    }
}
