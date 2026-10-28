using Numos.Maths;

namespace Numos.Workbench;

/// <summary>
///     An inclusive box of voxels in global voxel coordinates.
/// </summary>
/// <remarks>
///     A global coordinate is `chunkPosition * chunkDimensions + localCoordinate`, so it stays valid for negative
///     chunk positions. Both corners belong to the box.
/// </remarks>
/// <param name="Min">The corner with the lowest coordinates.</param>
/// <param name="Max">The corner with the highest coordinates. Each component must be at least that of <paramref name="Min" />.</param>
public readonly record struct VoxelBox(Int3 Min, Int3 Max)
{
    /// <summary>
    ///     The number of voxels along each axis.
    /// </summary>
    public Int3 Size => new(Max.X - Min.X + 1, Max.Y - Min.Y + 1, Max.Z - Min.Z + 1);

    /// <summary>
    ///     The number of voxels in the box.
    /// </summary>
    public long VoxelCount => (long)Size.X * Size.Y * Size.Z;

    /// <summary>
    ///     Returns whether every component of <see cref="Max" /> is at least that of <see cref="Min" />.
    /// </summary>
    public bool IsValid => Max.X >= Min.X && Max.Y >= Min.Y && Max.Z >= Min.Z;

    /// <summary>
    ///     Returns whether a global voxel lies in the box.
    /// </summary>
    public bool Contains(Int3 point)
    {
        return point.X >= Min.X && point.X <= Max.X &&
               point.Y >= Min.Y && point.Y <= Max.Y &&
               point.Z >= Min.Z && point.Z <= Max.Z;
    }

    /// <summary>
    ///     Throws when <see cref="IsValid" /> is false.
    /// </summary>
    /// <exception cref="ArgumentException">A component of <see cref="Max" /> is below <see cref="Min" />.</exception>
    public void ThrowIfInvalid()
    {
        if (!IsValid)
        {
            throw new ArgumentException(
                $"Region max ({Max.X}, {Max.Y}, {Max.Z}) must be at least min ({Min.X}, {Min.Y}, {Min.Z}) on every axis.");
        }
    }

    /// <summary>
    ///     Splits a global coordinate into a chunk position and a local coordinate.
    /// </summary>
    /// <param name="global">The global voxel coordinate.</param>
    /// <param name="chunkDimensions">The chunk dimensions of the simulation.</param>
    /// <param name="local">The coordinate inside the chunk.</param>
    /// <returns>The chunk position.</returns>
    public static Int3 SplitGlobal(Int3 global, Int3 chunkDimensions, out Int3 local)
    {
        var chunk = new Int3(
            FloorDivide(global.X, chunkDimensions.X),
            FloorDivide(global.Y, chunkDimensions.Y),
            FloorDivide(global.Z, chunkDimensions.Z));

        local = new Int3(
            global.X - chunk.X * chunkDimensions.X,
            global.Y - chunk.Y * chunkDimensions.Y,
            global.Z - chunk.Z * chunkDimensions.Z);

        return chunk;
    }

    /// <summary>
    ///     Divides and rounds toward negative infinity.
    /// </summary>
    internal static int FloorDivide(int value, int divisor)
    {
        int quotient = value / divisor;
        return value % divisor != 0 && (value < 0 != divisor < 0) ? quotient - 1 : quotient;
    }
}
