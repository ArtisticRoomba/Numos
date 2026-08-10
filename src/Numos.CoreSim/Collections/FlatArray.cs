using Numos.Maths;

namespace Numos.CoreSim.Collections;

/// <summary>
///     2-3D to 1D mapping for an array.
/// </summary>
/// <typeparam name="T">The type of data to store in the array.</typeparam>
/// <para>
///     Super basic helper struct that wraps an array and allows for 2D/3D indexing in sim ctx.
///     Struct so that we don't double-lookup on the reference, as this is basically just an array extension.
/// </para>
/// <para>
///     Because this is wrapped in a struct with a stable surface exposed,
///     we can technically swap out the underlying impl whenever we want to microopt.
/// </para>
internal readonly struct FlatArray<T>
{
    /// <summary>
    ///     Internal backing array.
    /// </summary>
    /// <remarks>
    ///     Marked as private to prevent sim from relying on whatever mapping is in the array.
    ///     Sim should access this via an API.
    /// </remarks>
    private readonly T[] _data;

    /// <summary>
    ///     Number of elements along each axis.
    /// </summary>
    private readonly Int3 _dimensions;

    /// <summary>
    ///     Whether this instance wraps a backing array.
    /// </summary>
    public bool IsInitialized => _data != null;

    /// <summary>
    ///     The number of elements in the array.
    /// </summary>
    public int Length => _data?.Length ?? 0;

    /// <summary>
    ///     The number of elements along each axis.
    /// </summary>
    public Int3 Dimensions => _dimensions;

    /// <summary>
    ///     Wraps an array as a one-dimensional flat array.
    /// </summary>
    /// <param name="data">The backing array.</param>
    /// <exception cref="ArgumentNullException"><paramref name="data" /> is <see langword="null" />.</exception>
    /// <remarks>
    ///     Why are you using this man.
    /// </remarks>
    public FlatArray(T[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        _data = data;
        _dimensions = new Int3(data.Length, 1, 1);
    }

    /// <summary>
    ///     Wraps an array using the supplied dimensions for coordinate indexing.
    /// </summary>
    /// <param name="data">The backing array.</param>
    /// <param name="dimensions">The number of elements along each axis.</param>
    /// <exception cref="ArgumentNullException"><paramref name="data" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Any dimension is zero or negative.</exception>
    /// <exception cref="ArgumentException">
    ///     The backing array's length does not match the product of <paramref name="dimensions" />.
    /// </exception>
    public FlatArray(T[] data, Int3 dimensions)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimensions.X);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimensions.Y);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimensions.Z);

        long expectedLength = (long)dimensions.X * dimensions.Y * dimensions.Z;
        if (expectedLength != data.LongLength)
        {
            throw new ArgumentException(
                $"The array length must equal the product of its dimensions ({expectedLength}).", nameof(data));
        }

        _data = data;
        _dimensions = dimensions;
    }

    /// <summary>
    ///     Gets or sets an element by its flat array index.
    /// </summary>
    /// <param name="index">The zero-based flat array index.</param>
    public T this[int index]
    {
        get => _data[index];
        set => _data[index] = value;
    }

    /// <summary>
    ///     Gets or sets an element by an <see cref="Int3" />.
    /// </summary>
    /// <param name="position">The zero-based coordinate of the element.</param>
    public T this[Int3 position]
    {
        get => _data[GetIndex(position)];
        set => _data[GetIndex(position)] = value;
    }

    /// <summary>
    ///     Converts a coordinate to its flat array index.
    /// </summary>
    public int GetIndex(Int3 position)
    {
        if (!position.IsWithin(default, _dimensions))
            throw new IndexOutOfRangeException();

        return position.X + position.Y * _dimensions.X + position.Z * _dimensions.X * _dimensions.Y;
    }

    /// <summary>
    ///     Converts a flat array index to its coordinate.
    /// </summary>
    public Int3 GetPosition(int index)
    {
        if ((uint)index >= (uint)Length)
            throw new IndexOutOfRangeException();

        return new Int3(index % _dimensions.X,
            index / _dimensions.X % _dimensions.Y,
            index / (_dimensions.X * _dimensions.Y));
    }

    /// <summary>
    ///     Clears every element in the array.
    /// </summary>
    public void Clear()
    {
        _data.AsSpan().Clear();
    }

    /// <summary>
    ///     Sets every element in the array to the supplied value.
    /// </summary>
    public void Fill(T value)
    {
        _data.AsSpan().Fill(value);
    }

    /// <summary>
    ///     Copies the array into the supplied destination.
    /// </summary>
    public void CopyTo(Span<T> destination)
    {
        _data.AsSpan().CopyTo(destination);
    }

    /// <summary>
    ///     Copies the supplied values into the beginning of this array.
    /// </summary>
    public void CopyFrom(ReadOnlySpan<T> source)
    {
        source.CopyTo(_data);
    }

    /// <summary>
    ///     Returns a wrapper over the same storage using new dimensions.
    /// </summary>
    public FlatArray<T> Reshape(Int3 dimensions)
    {
        return new FlatArray<T>(_data, dimensions);
    }

    /// <summary>
    ///     Copies the contents to a new one-dimensional array.
    /// </summary>
    public T[] ToArray()
    {
        return [.. _data];
    }
}