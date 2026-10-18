using System.Collections;
using System.Collections.Immutable;

namespace Numos.API.Generators.Common;

/// <summary>
///     An immutable array with value equality, for incremental generator models.
/// </summary>
/// <remarks>
///     <see cref="ImmutableArray{T}" /> compares by reference, so a model holding one would never compare equal to the
///     previous run's model and every downstream step would rerun on each keystroke.
/// </remarks>
internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IReadOnlyList<T>
    where T : IEquatable<T>
{
    private readonly T[]? _items;

    public EquatableArray(T[] items)
    {
        _items = items;
    }

    public EquatableArray(ImmutableArray<T> items)
    {
        _items = items.IsDefaultOrEmpty ? null : items.ToArray();
    }

    public static EquatableArray<T> Empty => default;

    public int Count => _items?.Length ?? 0;

    public T this[int index] => _items![index];

    public bool Equals(EquatableArray<T> other)
    {
        T[]? left = _items;
        T[]? right = other._items;
        int count = left?.Length ?? 0;
        if (count != (right?.Length ?? 0))
            return false;

        for (int i = 0; i < count; i++)
        {
            if (!left![i].Equals(right![i]))
                return false;
        }

        return true;
    }

    public override bool Equals(object? obj)
    {
        return obj is EquatableArray<T> other && Equals(other);
    }

    public override int GetHashCode()
    {
        if (_items is null)
            return 0;

        unchecked
        {
            int hash = 17;
            foreach (T item in _items)
                hash = hash * 31 + (item?.GetHashCode() ?? 0);

            return hash;
        }
    }

    public IEnumerator<T> GetEnumerator()
    {
        return ((IEnumerable<T>)(_items ?? [])).GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    public static bool operator ==(EquatableArray<T> left, EquatableArray<T> right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(EquatableArray<T> left, EquatableArray<T> right)
    {
        return !left.Equals(right);
    }
}

internal static class EquatableArray
{
    public static EquatableArray<T> ToEquatableArray<T>(this IEnumerable<T> items)
        where T : IEquatable<T>
    {
        return new EquatableArray<T>(items.ToArray());
    }
}