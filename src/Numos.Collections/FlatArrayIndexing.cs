using System.Runtime.CompilerServices;
using Numos.Maths;

namespace Numos.Collections;

/// <summary>
///     The coordinate-to-index math that <see cref="FlatArray{T}" /> and <see cref="ReadOnlyFlatArray{T}" /> share.
/// </summary>
internal static class FlatArrayIndexing
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int GetIndex(Int3 position, Int3 dimensions)
    {
        if (!position.IsWithin(dimensions))
            throw new IndexOutOfRangeException();

        return GetIndexUnsafe(position, dimensions);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int GetIndexUnsafe(Int3 position, Int3 dimensions)
    {
        return position.X + position.Y * dimensions.X + position.Z * dimensions.X * dimensions.Y;
    }

    internal static Int3 GetPosition(int index, int length, Int3 dimensions)
    {
        if ((uint)index >= (uint)length)
            throw new IndexOutOfRangeException();

        return new Int3(
            index % dimensions.X,
            index / dimensions.X % dimensions.Y,
            index / (dimensions.X * dimensions.Y));
    }
}