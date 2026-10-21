using System.Numerics;

namespace Numos.Viewer.Rendering;

/// <summary>
///     Geometry shared by the 3D and slice velocity overlays: how long an arrow is for a given velocity, which
///     velocities are too small to bother drawing, and where the two head segments go.
/// </summary>
/// <remarks>
///     Lengths are in voxels. An arrow is <c>velocity × scale</c>, so the scale is a time: at the default 0.05 s a
///     cell moving at 20 m/s draws a one-voxel arrow. Scales are clamped to [<see cref="MinScaleSeconds" />,
///     <see cref="MaxScaleSeconds" />] and arrow lengths to <see cref="MaxLength" />, so a runaway velocity cannot
///     smear a line across the whole scene; a clamped arrow still points the right way, it just stops reporting
///     magnitude. Arrows shorter than <see cref="MinLength" /> are skipped rather than drawn as dots.
/// </remarks>
public static class VelocityArrows
{
    /// <summary>
    ///     Default seconds of travel per arrow.
    /// </summary>
    public const float DefaultScaleSeconds = 0.05f;

    /// <summary>
    ///     Smallest accepted scale.
    /// </summary>
    public const float MinScaleSeconds = 0.001f;

    /// <summary>
    ///     Largest accepted scale.
    /// </summary>
    public const float MaxScaleSeconds = 1f;

    /// <summary>
    ///     Arrows shorter than this many voxels are not drawn.
    /// </summary>
    public const float MinLength = 0.05f;

    /// <summary>
    ///     Longest arrow drawn, in voxels.
    /// </summary>
    public const float MaxLength = 4f;

    /// <summary>
    ///     Head segment length as a fraction of the shaft, capped at <see cref="MaxHeadLength" />.
    /// </summary>
    private const float HeadFraction = 0.3f;

    private const float MaxHeadLength = 0.3f;

    /// <summary>
    ///     Half-width of the head relative to its length.
    /// </summary>
    private const float HeadSpread = 0.5f;

    /// <summary>
    ///     Picks how many cells to step over between drawn arrows so that at most <paramref name="maximumArrows" />
    ///     arrows are drawn from <paramref name="candidateCount" /> cells.
    /// </summary>
    /// <param name="candidateCount">Number of cells that could carry an arrow.</param>
    /// <param name="maximumArrows">Most arrows to draw. Values below one count as one.</param>
    /// <param name="rowLength">
    ///     Number of cells in a row of the cell array. The stride avoids sharing a factor with it, because such a stride
    ///     would line the arrows up in a few columns.
    /// </param>
    /// <returns>The step in cells. It is 1 when every cell fits.</returns>
    public static int GetSampleStride(int candidateCount, int maximumArrows, int rowLength)
    {
        maximumArrows = Math.Max(maximumArrows, 1);
        if (candidateCount <= maximumArrows)
            return 1;

        int stride = (int)(((long)candidateCount + maximumArrows - 1) / maximumArrows);
        if (rowLength > 1)
        {
            while (GreatestCommonDivisor(stride, rowLength) != 1)
                stride++;
        }

        return stride;
    }

    private static int GreatestCommonDivisor(int left, int right)
    {
        while (right != 0)
            (left, right) = (right, left % right);

        return left;
    }

    /// <summary>
    ///     Clamps a user-entered scale into the accepted range; NaN falls back to the default.
    /// </summary>
    /// <param name="scaleSeconds">Seconds of travel per arrow.</param>
    public static float ClampScale(float scaleSeconds)
    {
        if (float.IsNaN(scaleSeconds))
            return DefaultScaleSeconds;

        return Math.Clamp(scaleSeconds, MinScaleSeconds, MaxScaleSeconds);
    }

    /// <summary>
    ///     Computes the shaft of a 3D arrow for one velocity.
    /// </summary>
    /// <param name="velocity">Bulk velocity in metres per second.</param>
    /// <param name="scaleSeconds">Seconds of travel per arrow; clamped with <see cref="ClampScale" />.</param>
    /// <param name="shaft">Vector from the cell centre to the arrow tip, in voxels.</param>
    /// <returns>
    ///     <see langword="false" /> when any component is not finite or the arrow would be shorter than
    ///     <see cref="MinLength" />.
    /// </returns>
    public static bool TryGetShaft(Vector3 velocity, float scaleSeconds, out Vector3 shaft)
    {
        shaft = default;
        if (!float.IsFinite(velocity.X) || !float.IsFinite(velocity.Y) || !float.IsFinite(velocity.Z))
            return false;

        float length = velocity.Length() * ClampScale(scaleSeconds);
        if (!(length >= MinLength))
            return false;

        shaft = velocity * (Math.Min(length, MaxLength) / velocity.Length());
        return true;
    }

    /// <summary>
    ///     Computes the shaft of a 2D arrow for one in-plane velocity.
    /// </summary>
    /// <param name="velocity">In-plane velocity in metres per second, U along X and V along Y.</param>
    /// <param name="scaleSeconds">Seconds of travel per arrow; clamped with <see cref="ClampScale" />.</param>
    /// <param name="shaft">Vector from the cell centre to the arrow tip, in cells.</param>
    /// <returns>
    ///     <see langword="false" /> when either component is not finite or the arrow would be shorter than
    ///     <see cref="MinLength" />.
    /// </returns>
    public static bool TryGetShaft(Vector2 velocity, float scaleSeconds, out Vector2 shaft)
    {
        shaft = default;
        if (!float.IsFinite(velocity.X) || !float.IsFinite(velocity.Y))
            return false;

        float length = velocity.Length() * ClampScale(scaleSeconds);
        if (!(length >= MinLength))
            return false;

        shaft = velocity * (Math.Min(length, MaxLength) / velocity.Length());
        return true;
    }

    /// <summary>
    ///     Returns the two points the head segments run to from the tip, swept back along the shaft and spread to
    ///     either side of it.
    /// </summary>
    /// <param name="tip">Arrow tip.</param>
    /// <param name="shaft">Non-zero shaft vector from <see cref="TryGetShaft(Vector3, float, out Vector3)" />.</param>
    /// <remarks>
    ///     The spread direction is the shaft crossed with whichever axis it is least aligned with, so a head never
    ///     collapses to a line even for arrows along a coordinate axis.
    /// </remarks>
    public static (Vector3 Left, Vector3 Right) GetHead(Vector3 tip, Vector3 shaft)
    {
        float length = shaft.Length();
        var direction = shaft / length;
        var reference = Math.Abs(direction.X) <= Math.Abs(direction.Y) && Math.Abs(direction.X) <= Math.Abs(direction.Z)
            ? Vector3.UnitX
            : Math.Abs(direction.Y) <= Math.Abs(direction.Z)
                ? Vector3.UnitY
                : Vector3.UnitZ;

        var side = Vector3.Normalize(Vector3.Cross(direction, reference));
        float headLength = GetHeadLength(length);
        var back = tip - direction * headLength;
        var spread = side * (headLength * HeadSpread);
        return (back + spread, back - spread);
    }

    /// <inheritdoc cref="GetHead(Vector3, Vector3)" />
    public static (Vector2 Left, Vector2 Right) GetHead(Vector2 tip, Vector2 shaft)
    {
        float length = shaft.Length();
        var direction = shaft / length;
        var side = new Vector2(-direction.Y, direction.X);
        float headLength = GetHeadLength(length);
        var back = tip - direction * headLength;
        var spread = side * (headLength * HeadSpread);
        return (back + spread, back - spread);
    }

    private static float GetHeadLength(float shaftLength)
    {
        return Math.Min(shaftLength * HeadFraction, MaxHeadLength);
    }
}
