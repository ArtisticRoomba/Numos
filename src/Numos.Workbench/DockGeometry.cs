using Numos.Maths;

namespace Numos.Workbench;

/// <summary>
///     A face of a chunk, named by the axis it is perpendicular to.
/// </summary>
public enum DockFace
{
    NegativeX,
    PositiveX,
    NegativeY,
    PositiveY,
    NegativeZ,
    PositiveZ
}

/// <summary>
///     Width and height of a chunk face in voxels.
/// </summary>
/// <param name="Width">Voxels along the face's first axis (U).</param>
/// <param name="Height">Voxels along the face's second axis (V).</param>
public readonly record struct FaceSize(int Width, int Height);

/// <summary>
///     Geometry shared by every rectangular face dock.
/// </summary>
public static class DockGeometry
{
    /// <summary>
    ///     Gets the size of <paramref name="face" /> for a chunk with the given dimensions.
    /// </summary>
    public static FaceSize GetFaceSize(Int3 dimensions, DockFace face)
    {
        return face switch
        {
            DockFace.NegativeX or DockFace.PositiveX => new FaceSize(dimensions.Y, dimensions.Z),
            DockFace.NegativeY or DockFace.PositiveY => new FaceSize(dimensions.X, dimensions.Z),
            _ => new FaceSize(dimensions.X, dimensions.Y)
        };
    }

    /// <summary>
    ///     Maps face coordinates onto a destination face rotated by quarter turns.
    /// </summary>
    /// <param name="u">U coordinate on the source face.</param>
    /// <param name="v">V coordinate on the source face.</param>
    /// <param name="destination">Size of the destination face.</param>
    /// <param name="rotation">Quarter turns. Only the two low bits count.</param>
    public static (int U, int V) RotateFace(int u, int v, FaceSize destination, int rotation)
    {
        return (rotation & 3) switch
        {
            0 => (u, v),
            1 => (destination.Width - 1 - v, u),
            2 => (destination.Width - 1 - u, destination.Height - 1 - v),
            _ => (v, destination.Height - 1 - u)
        };
    }

    /// <summary>
    ///     Gets the local voxel index of face coordinates (<paramref name="u" />, <paramref name="v" />).
    /// </summary>
    public static ushort GetFaceVoxelIndex(Int3 dimensions, DockFace face, int u, int v)
    {
        (int x, int y, int z) = face switch
        {
            DockFace.NegativeX => (0, u, v),
            DockFace.PositiveX => (dimensions.X - 1, u, v),
            DockFace.NegativeY => (u, 0, v),
            DockFace.PositiveY => (u, dimensions.Y - 1, v),
            DockFace.NegativeZ => (u, v, 0),
            _ => (u, v, dimensions.Z - 1)
        };

        return checked((ushort)(x + dimensions.X * (y + dimensions.Y * z)));
    }
}
