namespace Numos.CoreSim.State.Advanced;

/// <summary>
///     What one call to an <see cref="IStateSink" /> wrote.
/// </summary>
internal enum StateTraceKind : byte
{
    Object,
    Count,
    Boolean,
    Byte,
    SByte,
    Int16,
    UInt16,
    Int32,
    UInt32,
    Int64,
    UInt64,
    Single,
    Double,
    String,
    Elements
}

/// <summary>
///     The primitive behind a block of <see cref="IStateSink.WriteElements" /> bytes, when the writer named one.
/// </summary>
internal enum StateTraceElementType : byte
{
    Unknown,
    Byte,
    SByte,
    Int16,
    UInt16,
    Int32,
    UInt32,
    Int64,
    UInt64,
    Single,
    Double,
    Char
}

/// <summary>
///     One recorded sink call. Scalars keep their bits, strings their text, objects their fields, and element blocks a
///     copy of their bytes.
/// </summary>
internal readonly struct StateTraceValue
{
    public StateTraceValue(StateTraceKind kind, ulong bits)
    {
        Kind = kind;
        Bits = bits;
    }

    public StateTraceValue(string text)
    {
        Kind = StateTraceKind.String;
        Text = text;
    }

    public StateTraceValue(StateTraceObject value)
    {
        Kind = StateTraceKind.Object;
        Object = value;
    }

    public StateTraceValue(byte[] elements, int elementSize, StateTraceElementType elementType)
    {
        Kind = StateTraceKind.Elements;
        Elements = elements;
        ElementSize = elementSize;
        ElementType = elementType;
    }

    public StateTraceKind Kind { get; }

    /// <summary>
    ///     Gets a scalar's bits, zero-extended, or a count.
    /// </summary>
    public ulong Bits { get; }

    public string? Text { get; }

    public StateTraceObject? Object { get; }

    /// <summary>
    ///     Gets an element block in native byte order.
    /// </summary>
    public byte[]? Elements { get; }

    public int ElementSize { get; }

    public StateTraceElementType ElementType { get; }

    /// <summary>
    ///     Compares two recorded values by everything the hash would see.
    /// </summary>
    public static bool AreEqual(in StateTraceValue left, in StateTraceValue right)
    {
        if (left.Kind != right.Kind)
            return false;

        return left.Kind switch
        {
            StateTraceKind.Object => StateTraceObject.AreEqual(left.Object!, right.Object!),
            StateTraceKind.String => string.Equals(left.Text, right.Text, StringComparison.Ordinal),
            StateTraceKind.Elements => left.ElementSize == right.ElementSize &&
                                       left.Elements.AsSpan().SequenceEqual(right.Elements),
            _ => left.Bits == right.Bits
        };
    }

    /// <summary>
    ///     Compares two value runs element by element.
    /// </summary>
    public static bool AreEqual(IReadOnlyList<StateTraceValue> left, IReadOnlyList<StateTraceValue> right)
    {
        if (left.Count != right.Count)
            return false;

        for (int index = 0; index < left.Count; index++)
        {
            if (!AreEqual(left[index], right[index]))
                return false;
        }

        return true;
    }
}

/// <summary>
///     One recorded field: its id and the values written between <see cref="IStateSink.BeginField" /> and
///     <see cref="IStateSink.EndField" />.
/// </summary>
internal sealed class StateTraceField(int id)
{
    public int Id { get; } = id;

    public List<StateTraceValue> Values { get; } = [];
}

/// <summary>
///     One recorded object: its fields in the order the walker wrote them.
/// </summary>
internal sealed class StateTraceObject
{
    public List<StateTraceField> Fields { get; } = [];

    /// <summary>
    ///     Finds a field by id.
    /// </summary>
    public StateTraceField? Find(int id)
    {
        foreach (var field in Fields)
        {
            if (field.Id == id)
                return field;
        }

        return null;
    }

    public static bool AreEqual(StateTraceObject left, StateTraceObject right)
    {
        if (left.Fields.Count != right.Fields.Count)
            return false;

        for (int index = 0; index < left.Fields.Count; index++)
        {
            if (left.Fields[index].Id != right.Fields[index].Id ||
                !StateTraceValue.AreEqual(left.Fields[index].Values, right.Fields[index].Values))
                return false;
        }

        return true;
    }
}

/// <summary>
///     An <see cref="IStateSink" /> that records the structure of a walk (objects, field ids, counts, and leaf values) as
///     a tree, so two walks can be compared field by field.
/// </summary>
/// <remarks>
///     <para>
///         It sees exactly the calls the hash sees, so two walks with equal hashes give equal trees, barring a hash
///         collision. Codecs that write something only a hash can take, such as a hand-written solver configuration's
///         own hash, write the same thing here.
///     </para>
///     <para>
///         A <see cref="StateCodecs.UnmanagedList{TList, T, TCodec}" /> names its element type through
///         <see cref="NextElementType" /> before it writes the block, because the sink interface only passes an element
///         size. The check is on the sink's type, so other sinks compile it away.
///     </para>
///     <para>
///         This copies every array it sees. It is for diagnostics, never for a tick.
///     </para>
/// </remarks>
internal struct StateTraceSink : IStateSink
{
    private readonly List<StateTraceValue> _values;
    private readonly Stack<object> _open;

    public StateTraceSink()
    {
        _values = [];
        _open = new Stack<object>();
    }

    /// <summary>
    ///     Gets the top-level values, normally a single object.
    /// </summary>
    public readonly IReadOnlyList<StateTraceValue> Values => _values;

    /// <summary>
    ///     Gets or sets the element type of the next <see cref="WriteElements" /> call. The call resets it.
    /// </summary>
    public StateTraceElementType NextElementType { get; set; }

    /// <summary>
    ///     Gets the element type for <typeparamref name="T" />, or <see cref="StateTraceElementType.Unknown" /> for one
    ///     that isn't a primitive.
    /// </summary>
    public static StateTraceElementType ElementTypeOf<T>()
    {
        if (typeof(T) == typeof(float))
            return StateTraceElementType.Single;
        if (typeof(T) == typeof(double))
            return StateTraceElementType.Double;
        if (typeof(T) == typeof(int))
            return StateTraceElementType.Int32;
        if (typeof(T) == typeof(uint))
            return StateTraceElementType.UInt32;
        if (typeof(T) == typeof(long))
            return StateTraceElementType.Int64;
        if (typeof(T) == typeof(ulong))
            return StateTraceElementType.UInt64;
        if (typeof(T) == typeof(short))
            return StateTraceElementType.Int16;
        if (typeof(T) == typeof(ushort))
            return StateTraceElementType.UInt16;
        if (typeof(T) == typeof(byte))
            return StateTraceElementType.Byte;
        if (typeof(T) == typeof(sbyte))
            return StateTraceElementType.SByte;
        if (typeof(T) == typeof(char))
            return StateTraceElementType.Char;

        return StateTraceElementType.Unknown;
    }

    /// <summary>
    ///     Gets the element type for a full type name such as <c>System.Single</c>, as rollback chunk fields save it.
    /// </summary>
    public static StateTraceElementType ElementTypeOf(string? fullName)
    {
        return fullName switch
        {
            "System.Single" => StateTraceElementType.Single,
            "System.Double" => StateTraceElementType.Double,
            "System.Int32" => StateTraceElementType.Int32,
            "System.UInt32" => StateTraceElementType.UInt32,
            "System.Int64" => StateTraceElementType.Int64,
            "System.UInt64" => StateTraceElementType.UInt64,
            "System.Int16" => StateTraceElementType.Int16,
            "System.UInt16" => StateTraceElementType.UInt16,
            "System.Byte" => StateTraceElementType.Byte,
            "System.SByte" => StateTraceElementType.SByte,
            "System.Char" => StateTraceElementType.Char,
            _ => StateTraceElementType.Unknown
        };
    }

    /// <summary>
    ///     Walks <paramref name="value" /> into a fresh trace.
    /// </summary>
    /// <returns>The top-level values.</returns>
    public static IReadOnlyList<StateTraceValue> Trace<T>(T value) where T : INumosState<T>
    {
        var sink = new StateTraceSink();
        T.WriteState(ref sink, value);
        return sink.Values;
    }

    /// <inheritdoc />
    public readonly void BeginObject()
    {
        var value = new StateTraceObject();
        Current.Add(new StateTraceValue(value));
        _open.Push(value);
    }

    /// <inheritdoc />
    public readonly void EndObject()
    {
        if (!_open.TryPop(out object? top) || top is not StateTraceObject)
            throw new InvalidOperationException("EndObject has no matching BeginObject.");
    }

    /// <inheritdoc />
    public readonly void BeginField(int id)
    {
        if (!_open.TryPeek(out object? top) || top is not StateTraceObject owner)
            throw new InvalidOperationException("BeginField must be inside an object.");

        var field = new StateTraceField(id);
        owner.Fields.Add(field);
        _open.Push(field);
    }

    /// <inheritdoc />
    public readonly void EndField()
    {
        if (!_open.TryPop(out object? top) || top is not StateTraceField)
            throw new InvalidOperationException("EndField has no matching BeginField.");
    }

    /// <inheritdoc />
    public readonly void WriteCount(int count) => Add(StateTraceKind.Count, unchecked((uint)count));

    /// <inheritdoc />
    public readonly void WriteBoolean(bool value) => Add(StateTraceKind.Boolean, value ? 1UL : 0UL);

    /// <inheritdoc />
    public readonly void WriteByte(byte value) => Add(StateTraceKind.Byte, value);

    /// <inheritdoc />
    public readonly void WriteSByte(sbyte value) => Add(StateTraceKind.SByte, unchecked((byte)value));

    /// <inheritdoc />
    public readonly void WriteInt16(short value) => Add(StateTraceKind.Int16, unchecked((ushort)value));

    /// <inheritdoc />
    public readonly void WriteUInt16(ushort value) => Add(StateTraceKind.UInt16, value);

    /// <inheritdoc />
    public readonly void WriteInt32(int value) => Add(StateTraceKind.Int32, unchecked((uint)value));

    /// <inheritdoc />
    public readonly void WriteUInt32(uint value) => Add(StateTraceKind.UInt32, value);

    /// <inheritdoc />
    public readonly void WriteInt64(long value) => Add(StateTraceKind.Int64, unchecked((ulong)value));

    /// <inheritdoc />
    public readonly void WriteUInt64(ulong value) => Add(StateTraceKind.UInt64, value);

    /// <inheritdoc />
    public readonly void WriteSingle(float value) => Add(StateTraceKind.Single, BitConverter.SingleToUInt32Bits(value));

    /// <inheritdoc />
    public readonly void WriteDouble(double value) => Add(StateTraceKind.Double, BitConverter.DoubleToUInt64Bits(value));

    /// <inheritdoc />
    public readonly void WriteString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Current.Add(new StateTraceValue(value));
    }

    /// <inheritdoc />
    public void WriteElements(ReadOnlySpan<byte> elements, int elementSize)
    {
        Current.Add(new StateTraceValue(elements.ToArray(), elementSize, NextElementType));
        NextElementType = StateTraceElementType.Unknown;
    }

    private readonly List<StateTraceValue> Current
    {
        get
        {
            if (!_open.TryPeek(out object? top))
                return _values;

            return top is StateTraceField open
                ? open.Values
                : throw new InvalidOperationException("A value must be written inside a field.");
        }
    }

    private readonly void Add(StateTraceKind kind, ulong bits) => Current.Add(new StateTraceValue(kind, bits));
}
