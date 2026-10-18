using System.Runtime.CompilerServices;

namespace Numos.API.Generators.Common;

/// <summary>
///     Compares by reference. netstandard2.0 doesn't ship <c>System.Collections.Generic.ReferenceEqualityComparer</c>.
/// </summary>
internal sealed class ReferenceEqualityComparer : IEqualityComparer<object>
{
    public static readonly ReferenceEqualityComparer Instance = new();

    public new bool Equals(object? x, object? y)
    {
        return ReferenceEquals(x, y);
    }

    public int GetHashCode(object obj)
    {
        return RuntimeHelpers.GetHashCode(obj);
    }
}