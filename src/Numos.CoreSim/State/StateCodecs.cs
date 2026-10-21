using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Numos.CoreSim.State;

/// <summary>
///     Writes, compares, and orders one kind of state value. Generated <see cref="NumosStateAttribute" /> code picks a
///     codec per field type from <see cref="StateCodecs" />.
/// </summary>
/// <typeparam name="T">The value type the codec handles.</typeparam>
/// <remarks>
///     Codecs are type-level only (never instantiated) and implemented on structs, so generic code over them is
///     specialized per codec and the static calls inline.
/// </remarks>
public interface IStateCodec<T>
{
    /// <summary>
    ///     Writes <paramref name="value" /> into <paramref name="sink" />.
    /// </summary>
    /// <param name="sink">The sink to write to.</param>
    /// <param name="value">The value to write.</param>
    /// <typeparam name="TSink">The sink type.</typeparam>
    static abstract void Write<TSink>(ref TSink sink, T value) where TSink : struct, IStateSink;

    /// <summary>
    ///     Compares two values the way <see cref="Write{TSink}" /> would distinguish them.
    /// </summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    /// <returns>True when both write identically.</returns>
    static abstract bool StateEquals(T left, T right);

    /// <summary>
    ///     Orders two values for canonical collection order.
    /// </summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    /// <returns>Negative, zero, or positive; zero exactly when <see cref="StateEquals" /> is true.</returns>
    static abstract int Compare(T left, T right);

    /// <summary>
    ///     Reads a value written by <see cref="Write{TSink}" /> through a <see cref="StateBinarySink" />.
    /// </summary>
    /// <param name="reader">The reader, positioned at the value.</param>
    /// <param name="gases">Resolves gas references by name; may be <see langword="null" /> when none are expected.</param>
    /// <returns>The value.</returns>
    static abstract T Read(ref StateReader reader, IGasRegistry? gases);

    /// <summary>
    ///     Gets the value a field of this type takes when the data doesn't contain it.
    /// </summary>
    /// <remarks><c>default</c>, except that strings are empty and collections are empty rather than null.</remarks>
    static virtual T Default => default!;
}

/// <summary>
///     The codecs generated <see cref="NumosStateAttribute" /> code uses for each supported field type.
/// </summary>
/// <remarks>
///     Public because generated code in downstream assemblies refers to them. They are plumbing for the generator;
///     host code normally goes through <see cref="NumosState" /> instead.
/// </remarks>
public static class StateCodecs
{
    /// <summary>
    ///     Gets <typeparamref name="TCodec" />'s <see cref="IStateCodec{T}.Default" />, which generated code can't name
    ///     directly when the codec keeps the interface's default implementation.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <typeparam name="TCodec">The codec.</typeparam>
    /// <returns>The value a missing field takes.</returns>
    public static T DefaultValue<T, TCodec>() where TCodec : IStateCodec<T> => TCodec.Default;

    /// <summary>
    ///     Codec for <see langword="bool" />.
    /// </summary>
    public readonly struct Boolean : IStateCodec<bool>
    {
        /// <inheritdoc />
        public static void Write<TSink>(ref TSink sink, bool value) where TSink : struct, IStateSink => sink.WriteBoolean(value);

        /// <inheritdoc />
        public static bool StateEquals(bool left, bool right) => left == right;

        /// <inheritdoc />
        public static int Compare(bool left, bool right) => left.CompareTo(right);

        /// <inheritdoc />
        public static bool Read(ref StateReader reader, IGasRegistry? gases) => reader.ReadBoolean();
    }

    /// <summary>
    ///     Codec for <see langword="byte" />.
    /// </summary>
    public readonly struct Byte : IStateCodec<byte>
    {
        /// <inheritdoc />
        public static void Write<TSink>(ref TSink sink, byte value) where TSink : struct, IStateSink => sink.WriteByte(value);

        /// <inheritdoc />
        public static bool StateEquals(byte left, byte right) => left == right;

        /// <inheritdoc />
        public static int Compare(byte left, byte right) => left.CompareTo(right);

        /// <inheritdoc />
        public static byte Read(ref StateReader reader, IGasRegistry? gases) => reader.ReadByte();
    }

    /// <summary>
    ///     Codec for <see langword="sbyte" />.
    /// </summary>
    public readonly struct SByte : IStateCodec<sbyte>
    {
        /// <inheritdoc />
        public static void Write<TSink>(ref TSink sink, sbyte value) where TSink : struct, IStateSink => sink.WriteSByte(value);

        /// <inheritdoc />
        public static bool StateEquals(sbyte left, sbyte right) => left == right;

        /// <inheritdoc />
        public static int Compare(sbyte left, sbyte right) => left.CompareTo(right);

        /// <inheritdoc />
        public static sbyte Read(ref StateReader reader, IGasRegistry? gases) => reader.ReadSByte();
    }

    /// <summary>
    ///     Codec for <see langword="short" />.
    /// </summary>
    public readonly struct Int16 : IStateCodec<short>
    {
        /// <inheritdoc />
        public static void Write<TSink>(ref TSink sink, short value) where TSink : struct, IStateSink => sink.WriteInt16(value);

        /// <inheritdoc />
        public static bool StateEquals(short left, short right) => left == right;

        /// <inheritdoc />
        public static int Compare(short left, short right) => left.CompareTo(right);

        /// <inheritdoc />
        public static short Read(ref StateReader reader, IGasRegistry? gases) => reader.ReadInt16();
    }

    /// <summary>
    ///     Codec for <see langword="ushort" />.
    /// </summary>
    public readonly struct UInt16 : IStateCodec<ushort>
    {
        /// <inheritdoc />
        public static void Write<TSink>(ref TSink sink, ushort value) where TSink : struct, IStateSink => sink.WriteUInt16(value);

        /// <inheritdoc />
        public static bool StateEquals(ushort left, ushort right) => left == right;

        /// <inheritdoc />
        public static int Compare(ushort left, ushort right) => left.CompareTo(right);

        /// <inheritdoc />
        public static ushort Read(ref StateReader reader, IGasRegistry? gases) => reader.ReadUInt16();
    }

    /// <summary>
    ///     Codec for <see langword="int" />.
    /// </summary>
    public readonly struct Int32 : IStateCodec<int>
    {
        /// <inheritdoc />
        public static void Write<TSink>(ref TSink sink, int value) where TSink : struct, IStateSink => sink.WriteInt32(value);

        /// <inheritdoc />
        public static bool StateEquals(int left, int right) => left == right;

        /// <inheritdoc />
        public static int Compare(int left, int right) => left.CompareTo(right);

        /// <inheritdoc />
        public static int Read(ref StateReader reader, IGasRegistry? gases) => reader.ReadInt32();
    }

    /// <summary>
    ///     Codec for <see langword="uint" />.
    /// </summary>
    public readonly struct UInt32 : IStateCodec<uint>
    {
        /// <inheritdoc />
        public static void Write<TSink>(ref TSink sink, uint value) where TSink : struct, IStateSink => sink.WriteUInt32(value);

        /// <inheritdoc />
        public static bool StateEquals(uint left, uint right) => left == right;

        /// <inheritdoc />
        public static int Compare(uint left, uint right) => left.CompareTo(right);

        /// <inheritdoc />
        public static uint Read(ref StateReader reader, IGasRegistry? gases) => reader.ReadUInt32();
    }

    /// <summary>
    ///     Codec for <see langword="long" />.
    /// </summary>
    public readonly struct Int64 : IStateCodec<long>
    {
        /// <inheritdoc />
        public static void Write<TSink>(ref TSink sink, long value) where TSink : struct, IStateSink => sink.WriteInt64(value);

        /// <inheritdoc />
        public static bool StateEquals(long left, long right) => left == right;

        /// <inheritdoc />
        public static int Compare(long left, long right) => left.CompareTo(right);

        /// <inheritdoc />
        public static long Read(ref StateReader reader, IGasRegistry? gases) => reader.ReadInt64();
    }

    /// <summary>
    ///     Codec for <see langword="ulong" />.
    /// </summary>
    public readonly struct UInt64 : IStateCodec<ulong>
    {
        /// <inheritdoc />
        public static void Write<TSink>(ref TSink sink, ulong value) where TSink : struct, IStateSink => sink.WriteUInt64(value);

        /// <inheritdoc />
        public static bool StateEquals(ulong left, ulong right) => left == right;

        /// <inheritdoc />
        public static int Compare(ulong left, ulong right) => left.CompareTo(right);

        /// <inheritdoc />
        public static ulong Read(ref StateReader reader, IGasRegistry? gases) => reader.ReadUInt64();
    }

    /// <summary>
    ///     Codec for <see langword="float" /> and the quantity aliases stored as one. Compares by bits, so <c>0f</c>
    ///     and <c>-0f</c> differ and NaN equals an identical NaN.
    /// </summary>
    public readonly struct Single : IStateCodec<float>
    {
        /// <inheritdoc />
        public static void Write<TSink>(ref TSink sink, float value) where TSink : struct, IStateSink => sink.WriteSingle(value);

        /// <inheritdoc />
        public static bool StateEquals(float left, float right) =>
            BitConverter.SingleToInt32Bits(left) == BitConverter.SingleToInt32Bits(right);

        /// <inheritdoc />
        public static int Compare(float left, float right) =>
            BitConverter.SingleToInt32Bits(left).CompareTo(BitConverter.SingleToInt32Bits(right));

        /// <inheritdoc />
        public static float Read(ref StateReader reader, IGasRegistry? gases) => reader.ReadSingle();
    }

    /// <summary>
    ///     Codec for <see langword="double" /> and the quantity aliases stored as one. Compares by bits like
    ///     <see cref="Single" />.
    /// </summary>
    public readonly struct Double : IStateCodec<double>
    {
        /// <inheritdoc />
        public static void Write<TSink>(ref TSink sink, double value) where TSink : struct, IStateSink => sink.WriteDouble(value);

        /// <inheritdoc />
        public static bool StateEquals(double left, double right) =>
            BitConverter.DoubleToInt64Bits(left) == BitConverter.DoubleToInt64Bits(right);

        /// <inheritdoc />
        public static int Compare(double left, double right) =>
            BitConverter.DoubleToInt64Bits(left).CompareTo(BitConverter.DoubleToInt64Bits(right));

        /// <inheritdoc />
        public static double Read(ref StateReader reader, IGasRegistry? gases) => reader.ReadDouble();
    }

    /// <summary>
    ///     Codec for non-null strings, compared ordinally.
    /// </summary>
    public readonly struct String : IStateCodec<string>
    {
        /// <inheritdoc />
        public static void Write<TSink>(ref TSink sink, string value) where TSink : struct, IStateSink => sink.WriteString(value);

        /// <inheritdoc />
        public static bool StateEquals(string left, string right) => string.Equals(left, right, StringComparison.Ordinal);

        /// <inheritdoc />
        public static int Compare(string left, string right) => string.CompareOrdinal(left, right);

        /// <inheritdoc />
        public static string Read(ref StateReader reader, IGasRegistry? gases) => reader.ReadString();

        /// <inheritdoc />
        public static string Default => "";
    }

    /// <summary>
    ///     Codec for <see cref="Numos.Maths.Int3" />, written as X, Y, Z.
    /// </summary>
    public readonly struct Int3 : IStateCodec<Numos.Maths.Int3>
    {
        /// <inheritdoc />
        public static void Write<TSink>(ref TSink sink, Numos.Maths.Int3 value) where TSink : struct, IStateSink
        {
            sink.WriteInt32(value.X);
            sink.WriteInt32(value.Y);
            sink.WriteInt32(value.Z);
        }

        /// <inheritdoc />
        public static bool StateEquals(Numos.Maths.Int3 left, Numos.Maths.Int3 right) => left.Equals(right);

        /// <inheritdoc />
        public static int Compare(Numos.Maths.Int3 left, Numos.Maths.Int3 right)
        {
            int result = left.X.CompareTo(right.X);
            if (result != 0)
                return result;

            result = left.Y.CompareTo(right.Y);
            return result != 0 ? result : left.Z.CompareTo(right.Z);
        }

        /// <inheritdoc />
        public static Numos.Maths.Int3 Read(ref StateReader reader, IGasRegistry? gases)
        {
            int x = reader.ReadInt32();
            int y = reader.ReadInt32();
            return new Numos.Maths.Int3(x, y, reader.ReadInt32());
        }
    }

    /// <summary>
    ///     Codec for a <see cref="GasProperties" /> reference, identified by <see cref="GasProperties.Name" /> alone.
    /// </summary>
    /// <remarks>
    ///     The gas's physical properties belong to the registry, which is hashed and saved on its own. Writing the
    ///     name keeps a reference stable when the registry is reordered, and lets a reader resolve it against the
    ///     registry it loads.
    /// </remarks>
    public readonly struct Gas : IStateCodec<GasProperties>
    {
        /// <inheritdoc />
        public static void Write<TSink>(ref TSink sink, GasProperties value) where TSink : struct, IStateSink =>
            sink.WriteString(value.Name);

        /// <inheritdoc />
        public static bool StateEquals(GasProperties left, GasProperties right) =>
            string.Equals(left.Name, right.Name, StringComparison.Ordinal);

        /// <inheritdoc />
        public static int Compare(GasProperties left, GasProperties right) => string.CompareOrdinal(left.Name, right.Name);

        /// <inheritdoc />
        /// <exception cref="InvalidOperationException"><paramref name="gases" /> is <see langword="null" />.</exception>
        /// <exception cref="InvalidDataException">No gas in <paramref name="gases" /> has the stored name.</exception>
        public static GasProperties Read(ref StateReader reader, IGasRegistry? gases)
        {
            string name = reader.ReadString();
            if (gases is null)
            {
                throw new InvalidOperationException(
                    $"State data references gas '{name}', so reading it needs a gas registry to resolve the name.");
            }

            foreach (GasProperties gas in gases)
            {
                if (string.Equals(gas.Name, name, StringComparison.Ordinal))
                    return gas;
            }

            throw new InvalidDataException($"State data references gas '{name}', which the gas registry doesn't contain.");
        }
    }

    /// <summary>
    ///     Codec for an enum, written as its underlying integer.
    /// </summary>
    /// <typeparam name="TEnum">The enum type.</typeparam>
    /// <typeparam name="TUnderlying">The enum's underlying integer type.</typeparam>
    /// <typeparam name="TUnderlyingCodec">The codec for <typeparamref name="TUnderlying" />.</typeparam>
    public readonly struct Enum<TEnum, TUnderlying, TUnderlyingCodec> : IStateCodec<TEnum>
        where TEnum : struct, Enum
        where TUnderlying : unmanaged
        where TUnderlyingCodec : IStateCodec<TUnderlying>
    {
        /// <inheritdoc />
        public static void Write<TSink>(ref TSink sink, TEnum value) where TSink : struct, IStateSink =>
            TUnderlyingCodec.Write(ref sink, Unsafe.As<TEnum, TUnderlying>(ref value));

        /// <inheritdoc />
        public static bool StateEquals(TEnum left, TEnum right) =>
            TUnderlyingCodec.StateEquals(Unsafe.As<TEnum, TUnderlying>(ref left), Unsafe.As<TEnum, TUnderlying>(ref right));

        /// <inheritdoc />
        public static int Compare(TEnum left, TEnum right) =>
            TUnderlyingCodec.Compare(Unsafe.As<TEnum, TUnderlying>(ref left), Unsafe.As<TEnum, TUnderlying>(ref right));

        /// <inheritdoc />
        public static TEnum Read(ref StateReader reader, IGasRegistry? gases)
        {
            TUnderlying value = TUnderlyingCodec.Read(ref reader, gases);
            return Unsafe.As<TUnderlying, TEnum>(ref value);
        }
    }

    /// <summary>
    ///     Codec for a nested <see cref="NumosStateAttribute" /> value.
    /// </summary>
    /// <typeparam name="T">The nested type.</typeparam>
    public readonly struct State<T> : IStateCodec<T> where T : INumosState<T>
    {
        /// <inheritdoc />
        public static void Write<TSink>(ref TSink sink, T value) where TSink : struct, IStateSink => T.WriteState(ref sink, value);

        /// <inheritdoc />
        public static bool StateEquals(T left, T right) => T.StateEquals(left, right);

        /// <inheritdoc />
        public static int Compare(T left, T right) => T.CompareState(left, right);

        /// <inheritdoc />
        public static T Read(ref StateReader reader, IGasRegistry? gases) => T.ReadState(ref reader, gases);
    }

    /// <summary>
    ///     Codec for a nullable reference, written as a presence flag followed by the value when present. Null sorts
    ///     first.
    /// </summary>
    /// <typeparam name="T">The reference type.</typeparam>
    /// <typeparam name="TCodec">The codec for non-null values.</typeparam>
    public readonly struct Nullable<T, TCodec> : IStateCodec<T?>
        where T : class
        where TCodec : IStateCodec<T>
    {
        /// <inheritdoc />
        public static void Write<TSink>(ref TSink sink, T? value) where TSink : struct, IStateSink
        {
            sink.WriteBoolean(value is not null);
            if (value is not null)
                TCodec.Write(ref sink, value);
        }

        /// <inheritdoc />
        public static bool StateEquals(T? left, T? right)
        {
            if (left is null || right is null)
                return left is null && right is null;

            return TCodec.StateEquals(left, right);
        }

        /// <inheritdoc />
        public static int Compare(T? left, T? right)
        {
            if (left is null || right is null)
                return (left is not null).CompareTo(right is not null);

            return TCodec.Compare(left, right);
        }

        /// <inheritdoc />
        public static T? Read(ref StateReader reader, IGasRegistry? gases) =>
            reader.ReadBoolean() ? TCodec.Read(ref reader, gases) : null;
    }

    /// <summary>
    ///     Codec for a <see cref="System.Nullable{T}" /> value, written like <see cref="Nullable{T, TCodec}" />.
    /// </summary>
    /// <typeparam name="T">The underlying value type.</typeparam>
    /// <typeparam name="TCodec">The codec for <typeparamref name="T" />.</typeparam>
    public readonly struct NullableValue<T, TCodec> : IStateCodec<T?>
        where T : struct
        where TCodec : IStateCodec<T>
    {
        /// <inheritdoc />
        public static void Write<TSink>(ref TSink sink, T? value) where TSink : struct, IStateSink
        {
            sink.WriteBoolean(value.HasValue);
            if (value.HasValue)
                TCodec.Write(ref sink, value.GetValueOrDefault());
        }

        /// <inheritdoc />
        public static bool StateEquals(T? left, T? right)
        {
            if (!left.HasValue || !right.HasValue)
                return left.HasValue == right.HasValue;

            return TCodec.StateEquals(left.GetValueOrDefault(), right.GetValueOrDefault());
        }

        /// <inheritdoc />
        public static int Compare(T? left, T? right)
        {
            if (!left.HasValue || !right.HasValue)
                return left.HasValue.CompareTo(right.HasValue);

            return TCodec.Compare(left.GetValueOrDefault(), right.GetValueOrDefault());
        }

        /// <inheritdoc />
        public static T? Read(ref StateReader reader, IGasRegistry? gases) =>
            reader.ReadBoolean() ? TCodec.Read(ref reader, gases) : null;
    }

    /// <summary>
    ///     Codec for an ordered sequence (an array, <see cref="IReadOnlyList{T}" />, or <c>ImmutableArray&lt;T&gt;</c>),
    ///     written as a count followed by the elements in order.
    /// </summary>
    /// <typeparam name="TList">The declared collection type.</typeparam>
    /// <typeparam name="T">The element type.</typeparam>
    /// <typeparam name="TCodec">The element codec.</typeparam>
    public readonly struct List<TList, T, TCodec> : IStateCodec<TList>
        where TList : IReadOnlyList<T>
        where TCodec : IStateCodec<T>
    {
        /// <inheritdoc />
        public static void Write<TSink>(ref TSink sink, TList value) where TSink : struct, IStateSink
        {
            int count = value.Count;
            sink.WriteCount(count);
            for (int i = 0; i < count; i++)
                TCodec.Write(ref sink, value[i]);
        }

        /// <inheritdoc />
        public static bool StateEquals(TList left, TList right)
        {
            int count = left.Count;
            if (count != right.Count)
                return false;

            for (int i = 0; i < count; i++)
            {
                if (!TCodec.StateEquals(left[i], right[i]))
                    return false;
            }

            return true;
        }

        /// <inheritdoc />
        public static int Compare(TList left, TList right)
        {
            int result = left.Count.CompareTo(right.Count);
            for (int i = 0; result == 0 && i < left.Count; i++)
                result = TCodec.Compare(left[i], right[i]);

            return result;
        }

        /// <inheritdoc />
        public static TList Read(ref StateReader reader, IGasRegistry? gases)
        {
            var elements = new T[reader.ReadCount()];
            for (int i = 0; i < elements.Length; i++)
                elements[i] = TCodec.Read(ref reader, gases);

            return FromArray(elements);
        }

        /// <inheritdoc />
        public static TList Default => FromArray([]);

        internal static TList FromArray(T[] elements)
        {
            if (typeof(TList) == typeof(ImmutableArray<T>))
            {
                ImmutableArray<T> immutable = ImmutableCollectionsMarshal.AsImmutableArray(elements);
                return Unsafe.As<ImmutableArray<T>, TList>(ref immutable);
            }

            // T[] and IReadOnlyList<T>, the other declared shapes the generator allows.
            return (TList)(object)elements;
        }
    }

    /// <summary>
    ///     Codec for an ordered sequence of primitives (other than <see langword="bool" />), written like
    ///     <see cref="List{TList, T, TCodec}" /> but as one block through <see cref="IStateSink.WriteElements" />.
    /// </summary>
    /// <typeparam name="TList">The declared collection type.</typeparam>
    /// <typeparam name="T">The primitive element type.</typeparam>
    /// <typeparam name="TCodec">The element codec, which orders elements the same way it would in a plain list.</typeparam>
    /// <remarks>
    ///     The bytes and the hash are identical to <see cref="List{TList, T, TCodec}" />; the generator picks this codec
    ///     for primitive elements because per-voxel chunk arrays go through it on every checkpoint hash. Booleans stay
    ///     on the per-element path so the stored byte is always 0 or 1.
    /// </remarks>
    public readonly struct UnmanagedList<TList, T, TCodec> : IStateCodec<TList>
        where TList : IReadOnlyList<T>
        where T : unmanaged
        where TCodec : IStateCodec<T>
    {
        /// <inheritdoc />
        public static void Write<TSink>(ref TSink sink, TList value) where TSink : struct, IStateSink
        {
            int count = value.Count;
            sink.WriteCount(count);
            if (TryGetSpan(value, out ReadOnlySpan<T> span))
            {
                sink.WriteElements(MemoryMarshal.AsBytes(span), Unsafe.SizeOf<T>());
                return;
            }

            T[] buffer = System.Buffers.ArrayPool<T>.Shared.Rent(count);
            try
            {
                for (int i = 0; i < count; i++)
                    buffer[i] = value[i];

                sink.WriteElements(MemoryMarshal.AsBytes(buffer.AsSpan(0, count)), Unsafe.SizeOf<T>());
            }
            finally
            {
                System.Buffers.ArrayPool<T>.Shared.Return(buffer);
            }
        }

        /// <inheritdoc />
        public static bool StateEquals(TList left, TList right)
        {
            if (left.Count != right.Count)
                return false;

            if (TryGetSpan(left, out ReadOnlySpan<T> first) && TryGetSpan(right, out ReadOnlySpan<T> second))
                return MemoryMarshal.AsBytes(first).SequenceEqual(MemoryMarshal.AsBytes(second));

            return List<TList, T, TCodec>.StateEquals(left, right);
        }

        /// <inheritdoc />
        public static int Compare(TList left, TList right) => List<TList, T, TCodec>.Compare(left, right);

        /// <inheritdoc />
        public static TList Read(ref StateReader reader, IGasRegistry? gases) =>
            List<TList, T, TCodec>.FromArray(reader.ReadArray<T>(reader.ReadCount()));

        /// <inheritdoc />
        public static TList Default => List<TList, T, TCodec>.FromArray([]);

        private static bool TryGetSpan(TList value, out ReadOnlySpan<T> span)
        {
            if (typeof(TList) == typeof(ImmutableArray<T>))
            {
                span = Unsafe.As<TList, ImmutableArray<T>>(ref value).AsSpan();
                return true;
            }

            switch (value)
            {
                case T[] array:
                    span = array;
                    return true;
                case Replay.CheckpointArray<T> owned:
                    span = owned.Span;
                    return true;
                default:
                    span = default;
                    return false;
            }
        }
    }

    /// <summary>
    ///     Codec for an unordered set, written as a count followed by the elements sorted by
    ///     <typeparamref name="TCodec" />.
    /// </summary>
    /// <typeparam name="TSet">The declared set type.</typeparam>
    /// <typeparam name="T">The element type.</typeparam>
    /// <typeparam name="TCodec">The element codec.</typeparam>
    /// <remarks>Sorting allocates, which is fine for configuration but keeps this off per-tick paths.</remarks>
    public readonly struct Set<TSet, T, TCodec> : IStateCodec<TSet>
        where TSet : IReadOnlyCollection<T>
        where TCodec : IStateCodec<T>
    {
        /// <inheritdoc />
        public static void Write<TSink>(ref TSink sink, TSet value) where TSink : struct, IStateSink
        {
            T[] sorted = Sort(value);
            sink.WriteCount(sorted.Length);
            foreach (T element in sorted)
                TCodec.Write(ref sink, element);
        }

        /// <inheritdoc />
        public static bool StateEquals(TSet left, TSet right) => Compare(left, right) == 0;

        /// <inheritdoc />
        public static int Compare(TSet left, TSet right)
        {
            int result = left.Count.CompareTo(right.Count);
            if (result != 0)
                return result;

            T[] first = Sort(left);
            T[] second = Sort(right);
            for (int i = 0; result == 0 && i < first.Length; i++)
                result = TCodec.Compare(first[i], second[i]);

            return result;
        }

        private static T[] Sort(TSet set)
        {
            var sorted = new T[set.Count];
            int index = 0;
            foreach (T element in set)
                sorted[index++] = element;

            Array.Sort(sorted, static (left, right) => TCodec.Compare(left, right));
            return sorted;
        }

        /// <inheritdoc />
        /// <exception cref="InvalidDataException">The data repeats an element.</exception>
        public static TSet Read(ref StateReader reader, IGasRegistry? gases)
        {
            int count = reader.ReadCount();
            var elements = new HashSet<T>(count);
            for (int i = 0; i < count; i++)
            {
                if (!elements.Add(TCodec.Read(ref reader, gases)))
                    throw new InvalidDataException("A state set repeats an element.");
            }

            return FromSet(elements);
        }

        /// <inheritdoc />
        public static TSet Default => FromSet(new HashSet<T>());

        private static TSet FromSet(HashSet<T> elements)
        {
            if (typeof(TSet) == typeof(FrozenSet<T>))
                return (TSet)(object)elements.ToFrozenSet();

            return (TSet)(object)elements;
        }
    }

    /// <summary>
    ///     Codec for a dictionary, written as a count followed by key/value pairs sorted by
    ///     <typeparamref name="TKeyCodec" />.
    /// </summary>
    /// <typeparam name="TDictionary">The declared dictionary type.</typeparam>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TKeyCodec">The key codec, which also sets the canonical order.</typeparam>
    /// <typeparam name="TValue">The value type.</typeparam>
    /// <typeparam name="TValueCodec">The value codec.</typeparam>
    /// <remarks>
    ///     Two keys the dictionary treats as distinct but <typeparamref name="TKeyCodec" /> treats as equal (two gases
    ///     with the same name) have no canonical order between them. Validate that away before hashing.
    /// </remarks>
    public readonly struct Dictionary<TDictionary, TKey, TKeyCodec, TValue, TValueCodec> : IStateCodec<TDictionary>
        where TDictionary : IReadOnlyDictionary<TKey, TValue>
        where TKey : notnull
        where TKeyCodec : IStateCodec<TKey>
        where TValueCodec : IStateCodec<TValue>
    {
        /// <inheritdoc />
        public static void Write<TSink>(ref TSink sink, TDictionary value) where TSink : struct, IStateSink
        {
            KeyValuePair<TKey, TValue>[] sorted = Sort(value);
            sink.WriteCount(sorted.Length);
            foreach (KeyValuePair<TKey, TValue> entry in sorted)
            {
                TKeyCodec.Write(ref sink, entry.Key);
                TValueCodec.Write(ref sink, entry.Value);
            }
        }

        /// <inheritdoc />
        public static bool StateEquals(TDictionary left, TDictionary right) => Compare(left, right) == 0;

        /// <inheritdoc />
        public static int Compare(TDictionary left, TDictionary right)
        {
            int result = left.Count.CompareTo(right.Count);
            if (result != 0)
                return result;

            KeyValuePair<TKey, TValue>[] first = Sort(left);
            KeyValuePair<TKey, TValue>[] second = Sort(right);
            for (int i = 0; result == 0 && i < first.Length; i++)
            {
                result = TKeyCodec.Compare(first[i].Key, second[i].Key);
                if (result == 0)
                    result = TValueCodec.Compare(first[i].Value, second[i].Value);
            }

            return result;
        }

        private static KeyValuePair<TKey, TValue>[] Sort(TDictionary dictionary)
        {
            var sorted = new KeyValuePair<TKey, TValue>[dictionary.Count];
            int index = 0;
            foreach (KeyValuePair<TKey, TValue> entry in dictionary)
                sorted[index++] = entry;

            Array.Sort(sorted, static (left, right) => TKeyCodec.Compare(left.Key, right.Key));
            return sorted;
        }

        /// <inheritdoc />
        /// <exception cref="InvalidDataException">The data repeats a key.</exception>
        public static TDictionary Read(ref StateReader reader, IGasRegistry? gases)
        {
            int count = reader.ReadCount();
            var entries = new System.Collections.Generic.Dictionary<TKey, TValue>(count);
            for (int i = 0; i < count; i++)
            {
                TKey key = TKeyCodec.Read(ref reader, gases);
                if (!entries.TryAdd(key, TValueCodec.Read(ref reader, gases)))
                    throw new InvalidDataException("A state dictionary repeats a key.");
            }

            return FromDictionary(entries);
        }

        /// <inheritdoc />
        public static TDictionary Default => FromDictionary(new System.Collections.Generic.Dictionary<TKey, TValue>());

        private static TDictionary FromDictionary(System.Collections.Generic.Dictionary<TKey, TValue> entries)
        {
            if (typeof(TDictionary) == typeof(FrozenDictionary<TKey, TValue>))
                return (TDictionary)(object)entries.ToFrozenDictionary();

            // Dictionary<TKey, TValue> and IReadOnlyDictionary<TKey, TValue>.
            return (TDictionary)(object)entries;
        }
    }
}
