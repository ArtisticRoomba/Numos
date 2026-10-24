using System.Collections.ObjectModel;

namespace Numos.CoreSim.State.Advanced;

/// <summary>
///     A read-only list over an array that a snapshot owns outright.
/// </summary>
/// <typeparam name="T">The element type.</typeparam>
/// <remarks>
///     Callers see an ordinary <see cref="ReadOnlyCollection{T}" />. The state codecs and generated restore code can
///     reach the array. They copy or hash it as one span, instead of element by element through the list interface.
///     Generated snapshot members hold their per-voxel and other array data in one of these.
/// </remarks>
public sealed class StateArray<T> : ReadOnlyCollection<T>
{
    private readonly T[] _items;

    internal StateArray(T[] items)
        : base(items)
    {
        _items = items;
    }

    /// <summary>
    ///     Gets an empty instance.
    /// </summary>
    public static new StateArray<T> Empty { get; } = new([]);

    /// <summary>
    ///     Gets the elements as a span.
    /// </summary>
    public ReadOnlySpan<T> Span => _items;
}

/// <summary>
///     Builds and copies <see cref="StateArray{T}" /> values for generated snapshot code.
/// </summary>
public static class StateArray
{
    /// <summary>
    ///     Wraps values a snapshot is about to own, copying only when they aren't already a plain array.
    /// </summary>
    /// <param name="values">Values from a fresh capture or decode that nothing else references.</param>
    /// <typeparam name="T">The element type.</typeparam>
    /// <returns>A read-only list over the values.</returns>
    /// <remarks>
    ///     An array is adopted as is, so only pass one nothing else holds. Any other list is copied, which keeps a
    ///     caller from keeping a mutable alias.
    /// </remarks>
    public static StateArray<T> Wrap<T>(IReadOnlyList<T> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return values switch
        {
            StateArray<T> owned => owned,
            T[] array => array.Length == 0 ? StateArray<T>.Empty : new StateArray<T>(array),
            _ => new StateArray<T>(values.ToArray()),
        };
    }

    /// <summary>
    ///     Copies <paramref name="values" /> into a new list the snapshot owns.
    /// </summary>
    /// <param name="values">The live values.</param>
    /// <typeparam name="T">The element type.</typeparam>
    /// <returns>A detached copy.</returns>
    public static StateArray<T> Copy<T>(ReadOnlySpan<T> values)
    {
        return values.IsEmpty ? StateArray<T>.Empty : new StateArray<T>(values.ToArray());
    }

    /// <summary>
    ///     Copies captured values into the start of <paramref name="destination" />.
    /// </summary>
    /// <param name="source">The captured values.</param>
    /// <param name="destination">Live storage at least <c>source.Count</c> long.</param>
    /// <typeparam name="T">The element type.</typeparam>
    public static void CopyTo<T>(IReadOnlyList<T> source, Span<T> destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source is StateArray<T> array)
        {
            array.Span.CopyTo(destination);
            return;
        }

        for (int index = 0; index < source.Count; index++)
            destination[index] = source[index];
    }

    /// <summary>
    ///     Copies captured values into a new array the live object can own.
    /// </summary>
    /// <param name="source">The captured values.</param>
    /// <typeparam name="T">The element type.</typeparam>
    /// <returns>A new array.</returns>
    public static T[] ToArray<T>(IReadOnlyList<T> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var copy = new T[source.Count];
        CopyTo(source, copy);
        return copy;
    }
}
