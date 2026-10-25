using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Numos.CoreSim.Replay;

namespace Numos.CoreSim.State.Advanced;

/// <summary>
///     Reads the wire format <see cref="StateBinarySink" /> writes, for generated
///     <see cref="INumosState{TSelf}.ReadState" /> code and the <see cref="StateCodecs" />.
/// </summary>
/// <remarks>
///     <para>
///         Assume the bytes are hostile. The reader checks every length against the field or object that contains it
///         before it allocates anything. Field ids must strictly increase within an object (which also rules out
///         duplicates). Nesting is capped, and a field must be consumed exactly. Anything that does not fit is an
///         <see cref="InvalidDataException" />, the same exception replay archives use for corrupt data.
///     </para>
///     <para>
///         Generated readers follow one pattern. They call <see cref="BeginObject" />, then loop on
///         <see cref="TryReadField" />. For a known field the loop reads the payload and calls
///         <see cref="EndField" />. For an unknown id it calls <see cref="SkipField" />. After the loop the reader calls
///         <see cref="EndObject" />.
///     </para>
/// </remarks>
public ref struct StateReader
{
    /// <summary>
    ///     The default limit on a string's UTF-8 length, matching the replay reader's default.
    /// </summary>
    public const int DefaultMaxStringBytes = 1024 * 1024;

    /// <summary>
    ///     The default limit on how deeply objects may nest.
    /// </summary>
    public const int DefaultMaxDepth = 64;

    private readonly ReadOnlySpan<byte> _data;
    private readonly int _maxStringBytes;
    private readonly int _maxDepth;
    private int _position;

    // One entry per open object or field: where it ends, and for objects the last field id read (-1 for a field).
    private int[] _ends;
    private int[] _lastIds;
    private int _frames;
    private int _objects;

    /// <summary>
    ///     Creates a reader over one outermost value's payload.
    /// </summary>
    /// <param name="payload">The bytes, with no length prefix for the outermost object.</param>
    /// <param name="maxStringBytes">The largest string to accept, in UTF-8 bytes.</param>
    /// <param name="maxDepth">How deeply objects may nest, counting the outermost one.</param>
    /// <exception cref="ArgumentOutOfRangeException">A limit is negative, or <paramref name="maxDepth" /> is zero.</exception>
    public StateReader(ReadOnlySpan<byte> payload, int maxStringBytes = DefaultMaxStringBytes, int maxDepth = DefaultMaxDepth)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxStringBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDepth);
        _data = payload;
        _maxStringBytes = maxStringBytes;
        _maxDepth = maxDepth;
        _ends = new int[8];
        _lastIds = new int[8];
    }

    /// <summary>
    ///     Gets the registry that resolves solver configuration keys when the data holds a configuration snapshot, or
    ///     <see langword="null" /> for the built-in registrations.
    /// </summary>
    public SolverConfigurationTypes? SolverConfigurations { get; init; }

    /// <summary>
    ///     Gets whether every byte has been consumed.
    /// </summary>
    public readonly bool IsAtEnd => _position == _data.Length;

    // Field and nested-object reads are bounded by the innermost open frame, not the whole payload.
    private readonly int Limit => _frames == 0 ? _data.Length : _ends[_frames - 1];

    /// <summary>
    ///     Starts an object: the outermost one spans the whole payload, a nested one reads its length.
    /// </summary>
    /// <exception cref="InvalidDataException">The length doesn't fit, or objects nest too deeply.</exception>
    public void BeginObject()
    {
        if (_objects == _maxDepth)
            throw new InvalidDataException("State data nests objects too deeply.");

        int end = _data.Length;
        if (_objects > 0)
        {
            int length = ReadLength();
            end = _position + length;
        }

        _objects++;
        Push(end, 0);
    }

    /// <summary>
    ///     Reads the next field's id and enters its payload.
    /// </summary>
    /// <param name="id">The field id.</param>
    /// <returns><see langword="false" /> once the current object has no more fields.</returns>
    /// <exception cref="InvalidDataException">The id is out of order or the length doesn't fit.</exception>
    public bool TryReadField(out int id)
    {
        if (_frames == 0 || _lastIds[_frames - 1] < 0)
            throw new InvalidOperationException("TryReadField must be called inside an object, not a field.");

        if (_position == Limit)
        {
            id = 0;
            return false;
        }

        id = ReadInt32();
        if (id <= _lastIds[_frames - 1])
            throw new InvalidDataException($"State field {id} is out of order or repeated.");

        _lastIds[_frames - 1] = id;
        int length = ReadLength();
        Push(_position + length, -1);
        return true;
    }

    /// <summary>
    ///     Skips the rest of the current field.
    /// </summary>
    public void SkipField()
    {
        _position = PopField();
    }

    /// <summary>
    ///     Leaves the current field, which must have been read exactly.
    /// </summary>
    /// <exception cref="InvalidDataException">Bytes remain in the field.</exception>
    public void EndField()
    {
        if (PopField() != _position)
            throw new InvalidDataException("A state field has trailing data.");
    }

    /// <summary>
    ///     Leaves the current object once <see cref="TryReadField" /> has returned <see langword="false" />.
    /// </summary>
    public void EndObject()
    {
        if (_frames == 0 || _lastIds[_frames - 1] < 0 || _position != _ends[_frames - 1])
            throw new InvalidOperationException("EndObject must follow the last field of an object.");

        _frames--;
        _objects--;
    }

    /// <summary>
    ///     Reads a collection's element or entry count.
    /// </summary>
    /// <returns>The count, which is at most the bytes left in the field, since every element takes at least one.</returns>
    /// <exception cref="InvalidDataException">The count is negative or can't fit.</exception>
    public int ReadCount()
    {
        int count = ReadInt32();
        if (count < 0 || count > Limit - _position)
            throw new InvalidDataException("A state collection count is invalid.");

        return count;
    }

    /// <summary>
    ///     Reads a <see langword="bool" />, which must be stored as 0 or 1.
    /// </summary>
    public bool ReadBoolean()
    {
        return ReadByte() switch
        {
            0 => false,
            1 => true,
            _ => throw new InvalidDataException("A state boolean is neither 0 nor 1."),
        };
    }

    /// <summary>
    ///     Reads a <see langword="byte" />.
    /// </summary>
    public byte ReadByte() => Take(1)[0];

    /// <summary>
    ///     Reads an <see langword="sbyte" />.
    /// </summary>
    public sbyte ReadSByte() => unchecked((sbyte)ReadByte());

    /// <summary>
    ///     Reads a <see langword="short" />.
    /// </summary>
    public short ReadInt16() => BinaryPrimitives.ReadInt16LittleEndian(Take(2));

    /// <summary>
    ///     Reads a <see langword="ushort" />.
    /// </summary>
    public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

    /// <summary>
    ///     Reads an <see langword="int" />.
    /// </summary>
    public int ReadInt32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

    /// <summary>
    ///     Reads a <see langword="uint" />.
    /// </summary>
    public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

    /// <summary>
    ///     Reads a <see langword="long" />.
    /// </summary>
    public long ReadInt64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));

    /// <summary>
    ///     Reads a <see langword="ulong" />.
    /// </summary>
    public ulong ReadUInt64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));

    /// <summary>
    ///     Reads a <see langword="float" /> from its exact bits.
    /// </summary>
    public float ReadSingle() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));

    /// <summary>
    ///     Reads a <see langword="double" /> from its exact bits.
    /// </summary>
    public double ReadDouble() => BinaryPrimitives.ReadDoubleLittleEndian(Take(8));

    /// <summary>
    ///     Reads a non-null string.
    /// </summary>
    /// <exception cref="InvalidDataException">The length exceeds the limit or the bytes aren't valid UTF-8.</exception>
    public string ReadString()
    {
        int length = ReadInt32();
        if (length < 0 || length > _maxStringBytes)
            throw new InvalidDataException("A state string length is invalid.");

        ReadOnlySpan<byte> bytes = Take(length);
        try
        {
            return AtmosReplayCodec.Utf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("A state string is not valid UTF-8.", exception);
        }
    }

    /// <summary>
    ///     Reads <paramref name="count" /> little-endian primitives written by <see cref="IStateSink.WriteElements" />.
    /// </summary>
    /// <param name="count">The element count, usually just read with <see cref="ReadCount" />.</param>
    /// <typeparam name="T">A primitive type.</typeparam>
    /// <returns>A new array holding the elements.</returns>
    /// <exception cref="InvalidDataException">Fewer than <paramref name="count" /> elements remain in the field.</exception>
    public T[] ReadArray<T>(int count) where T : unmanaged
    {
        int size = Unsafe.SizeOf<T>();
        if (count < 0 || (long)count * size > Limit - _position)
            throw new InvalidDataException("State data is truncated.");

        var values = new T[count];
        ReadElements(MemoryMarshal.AsBytes(values.AsSpan()), size);
        return values;
    }

    /// <summary>
    ///     Gets the bytes left in the current field, or in the object when no field is open.
    /// </summary>
    /// <remarks>Lets a reader reject a length before allocating for it.</remarks>
    public readonly int Remaining => Limit - _position;

    /// <summary>
    ///     Fills <paramref name="destination" /> with elements written by <see cref="IStateSink.WriteElements" />.
    /// </summary>
    /// <param name="destination">The elements' bytes, in native layout once read.</param>
    /// <param name="elementSize">The size of one element, for swapping on a big-endian host.</param>
    /// <exception cref="InvalidDataException">Fewer bytes than <paramref name="destination" /> holds remain.</exception>
    public void ReadElements(Span<byte> destination, int elementSize)
    {
        Take(destination.Length).CopyTo(destination);
        StateBinarySink.ReverseElements(destination, elementSize);
    }

    private int ReadLength()
    {
        int length = ReadInt32();
        if (length < 0 || length > Limit - _position)
            throw new InvalidDataException("A state field or object length runs past its container.");

        return length;
    }

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count > Limit - _position)
            throw new InvalidDataException("State data is truncated.");

        ReadOnlySpan<byte> span = _data.Slice(_position, count);
        _position += count;
        return span;
    }

    private void Push(int end, int lastId)
    {
        if (_frames == _ends.Length)
        {
            // BeginObject already enforced the depth limit, so this only grows to about twice that.
            Array.Resize(ref _ends, _ends.Length * 2);
            Array.Resize(ref _lastIds, _lastIds.Length * 2);
        }

        _ends[_frames] = end;
        _lastIds[_frames] = lastId;
        _frames++;
    }

    private int PopField()
    {
        if (_frames == 0 || _lastIds[_frames - 1] >= 0)
            throw new InvalidOperationException("There is no open field to leave.");

        return _ends[--_frames];
    }
}
