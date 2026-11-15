using Numos.Maths;

namespace Numos.Collections;

/// <summary>
///     A read-only view over a flat backing array, indexed by 3D coordinate with X fastest:
///     <c>index = x + y * X + z * X * Y</c>.
/// </summary>
/// <typeparam name="T">The type of data in the array.</typeparam>
/// <remarks>
///     <para>
///         This is the read half of <see cref="FlatArray{T}" />. Copies of the struct share one backing array, and
///         <see langword="default" /> has no storage at all (<see cref="IsInitialized" /> is
///         <see langword="false" />).
///     </para>
///     <para>
///         The view stops writes through itself only. The owner of the backing array can still change it, and the
///         view shows those changes.
///     </para>
/// </remarks>
public readonly struct ReadOnlyFlatArray<T>
{
    private readonly T[] _data;

    private readonly Int3 _dimensions;

    internal ReadOnlyFlatArray(T[] data, Int3 dimensions)
    {
        _data = data;
        _dimensions = dimensions;
    }

    /// <summary>
    ///     Whether this instance wraps a backing array.
    /// </summary>
    public bool IsInitialized => _data != null;

    /// <summary>
    ///     The total number of elements in the array.
    /// </summary>
    public int Length => _data?.Length ?? 0;

    /// <summary>
    ///     Maximum number of elements along each axis.
    /// </summary>
    public Int3 Dimensions => _dimensions;

    /// <summary>
    ///     Returns a read-only reference to an element by its flat array index.
    /// </summary>
    /// <param name="index">The zero-based flat array index.</param>
    /// <exception cref="IndexOutOfRangeException"><paramref name="index" /> is outside the backing array.</exception>
    public ref readonly T this[int index] => ref _data[index];

    /// <summary>
    ///     Returns a read-only reference to an element by its <see cref="Int3" /> coordinate.
    /// </summary>
    /// <param name="position">The zero-based coordinate of the element.</param>
    /// <exception cref="IndexOutOfRangeException">
    ///     <paramref name="position" /> is outside <see cref="Dimensions" />.
    /// </exception>
    public ref readonly T this[Int3 position] => ref _data[GetIndex(position)];

    /// <summary>
    ///     Converts a coordinate to its flat array index.
    /// </summary>
    /// <param name="position">The zero-based coordinate.</param>
    /// <returns>The flat index of <paramref name="position" />.</returns>
    /// <exception cref="IndexOutOfRangeException">
    ///     <paramref name="position" /> is outside <see cref="Dimensions" />.
    /// </exception>
    public int GetIndex(Int3 position)
    {
        return FlatArrayIndexing.GetIndex(position, _dimensions);
    }

    /// <summary>
    ///     Converts a coordinate to its flat array index without a bounds check.
    /// </summary>
    /// <param name="position">The zero-based coordinate.</param>
    /// <returns>The flat index of <paramref name="position" />.</returns>
    /// <remarks>
    ///     This method does not catch an out-of-range component. The result is either an index outside the array or
    ///     a silent alias of a different element (for example <c>x == Dimensions.X</c> lands on <c>(0, y + 1, z)</c>).
    /// </remarks>
    public int GetIndexUnsafe(Int3 position)
    {
        return FlatArrayIndexing.GetIndexUnsafe(position, _dimensions);
    }

    /// <summary>
    ///     Converts a flat array index to its coordinate.
    /// </summary>
    /// <param name="index">The zero-based flat array index.</param>
    /// <returns>The coordinate stored at <paramref name="index" />.</returns>
    /// <exception cref="IndexOutOfRangeException"><paramref name="index" /> is outside the backing array.</exception>
    public Int3 GetPosition(int index)
    {
        return FlatArrayIndexing.GetPosition(index, Length, _dimensions);
    }

    /// <summary>
    ///     Copies the array into the supplied destination.
    /// </summary>
    /// <param name="destination">The span to copy into. It must be at least <see cref="Length" /> long.</param>
    /// <exception cref="ArgumentException"><paramref name="destination" /> is shorter than this array.</exception>
    public void CopyTo(Span<T> destination)
    {
        _data.AsSpan().CopyTo(destination);
    }

    /// <summary>
    ///     Returns a read-only span over the backing storage.
    /// </summary>
    /// <remarks>
    ///     The span aliases the backing array, so it shows later writes that the array's owner makes.
    /// </remarks>
    public ReadOnlySpan<T> AsSpan()
    {
        return _data.AsSpan();
    }

    /// <summary>
    ///     Copies the contents to a new one-dimensional array.
    /// </summary>
    public T[] ToArray()
    {
        return [.. _data];
    }
}