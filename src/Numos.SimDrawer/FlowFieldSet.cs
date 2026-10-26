using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.Maths;

namespace Numos.SimDrawer;

/// <summary>
///     Holds the per-chunk density and density gradient arrays for one frame build.
/// </summary>
/// <remarks>
///     <para>
///         The arrays are derived from the snapshots, so a frame build owns them and drops them when it ends. The set
///         allocates one array per chunk and one shared padded buffer. It allocates nothing per voxel.
///     </para>
///     <para>
///         The gradient of a chunk reads the six face neighbors. A neighbor snapshot that lacks the gas channels counts
///         as missing, so the voxel next to it uses a one-sided difference.
///     </para>
/// </remarks>
internal sealed class FlowFieldSet
{
    private const AtmosChunkSnapshotFields DensityFields =
        AtmosChunkSnapshotFields.Gases | AtmosChunkSnapshotFields.VoxelClassification;

    private readonly Dictionary<Int3, float[]> _density = [];
    private readonly Dictionary<Int3, float[]> _gradient = [];

    private FlowFieldSet()
    {
    }

    /// <summary>
    ///     Derives the fields for the chunks in scope.
    /// </summary>
    /// <param name="snapshots">Every snapshot of the frame. Out-of-scope snapshots supply neighbor densities.</param>
    /// <param name="scope">The chunks to map, or <see langword="null" /> for all of them.</param>
    /// <param name="config">The configuration that supplies the molar masses and the voxel volume.</param>
    /// <param name="includeGradient">Whether to derive the gradient as well as the density.</param>
    public static FlowFieldSet Build(
        IReadOnlyCollection<AtmosChunkSnapshot> snapshots,
        IReadOnlySet<Int3>? scope,
        AtmosConfig config,
        bool includeGradient)
    {
        var set = new FlowFieldSet();
        foreach (var snapshot in snapshots)
        {
            if (!includeGradient && scope != null && !scope.Contains(snapshot.GridPosition))
                continue;

            if (!HasUsableDensityData(snapshot))
                continue;

            int voxelCount = snapshot.Dimensions.X * snapshot.Dimensions.Y * snapshot.Dimensions.Z;
            float[] density = new float[voxelCount];
            FlowFieldMath.ComputeDensity(in snapshot, config, density);
            set._density[snapshot.GridPosition] = density;
        }

        if (!includeGradient)
            return set;

        float edge = FlowFieldMath.GetVoxelEdge(config);
        float[] padded = [];
        foreach (var snapshot in snapshots)
        {
            if (scope != null && !scope.Contains(snapshot.GridPosition))
                continue;

            if (!set._density.TryGetValue(snapshot.GridPosition, out var density))
                continue;

            var size = snapshot.Dimensions;
            int paddedCount = (size.X + 2) * (size.Y + 2) * (size.Z + 2);
            if (padded.Length < paddedCount)
                padded = new float[paddedCount];

            set.FillPadded(snapshot.GridPosition, size, density, padded.AsSpan(0, paddedCount));
            float[] gradient = new float[density.Length];
            FlowFieldMath.ComputeGradientMagnitude(padded, size, edge, gradient);
            set._gradient[snapshot.GridPosition] = gradient;
        }

        return set;
    }

    /// <summary>
    ///     Gets the density array of a chunk.
    /// </summary>
    public bool TryGetDensity(Int3 position, out float[] density)
    {
        return _density.TryGetValue(position, out density!);
    }

    /// <summary>
    ///     Gets the gradient magnitude array of a chunk.
    /// </summary>
    public bool TryGetGradient(Int3 position, out float[] gradient)
    {
        return _gradient.TryGetValue(position, out gradient!);
    }

    /// <summary>
    ///     Finds the finite density extremes over the chunks in scope.
    /// </summary>
    public void IncludeDensityRange(IReadOnlySet<Int3>? scope, ref float minimum, ref float maximum)
    {
        foreach (var (position, density) in _density)
        {
            if (scope == null || scope.Contains(position))
                FiniteRange.Include(density, ref minimum, ref maximum);
        }
    }

    /// <summary>
    ///     Finds the largest finite gradient magnitude over the chunks in scope.
    /// </summary>
    public float GetMaximumGradient()
    {
        float maximum = 0f;
        foreach (var gradient in _gradient.Values)
            maximum = Math.Max(maximum, FlowFieldMath.MaximumFinite(gradient));

        return maximum;
    }

    private static bool HasUsableDensityData(in AtmosChunkSnapshot snapshot)
    {
        if (snapshot.Dimensions.X <= 0 || snapshot.Dimensions.Y <= 0 || snapshot.Dimensions.Z <= 0)
            return false;

        int voxelCount = snapshot.Dimensions.X * snapshot.Dimensions.Y * snapshot.Dimensions.Z;
        if (!snapshot.HasFields(DensityFields) ||
            snapshot.VoxelRoomMap == null ||
            snapshot.VoxelRoomMap.Length != voxelCount ||
            snapshot.Gases == null)
        {
            return false;
        }

        foreach (var gas in snapshot.Gases)
        {
            if (gas.Moles == null || gas.Moles.Length != voxelCount)
                return false;
        }

        return true;
    }

    private void FillPadded(Int3 position, Int3 size, float[] center, Span<float> padded)
    {
        padded.Fill(float.NaN);
        int stepX = size.X + 2;
        int stepXY = stepX * (size.Y + 2);
        for (int z = 0; z < size.Z; z++)
        for (int y = 0; y < size.Y; y++)
        {
            center.AsSpan((z * size.Y + y) * size.X, size.X)
                .CopyTo(padded[((z + 1) * stepXY + (y + 1) * stepX + 1)..]);
        }

        foreach (var offset in Int3.CardinalOffsets)
        {
            var neighborPosition = new Int3(position.X + offset.X, position.Y + offset.Y, position.Z + offset.Z);
            if (TryGetNeighbor(neighborPosition, size, out var neighbor))
                CopyHaloFace(neighbor, offset, size, padded);
        }
    }

    /// <summary>
    ///     Copies the voxel layer of a face neighbor that touches the chunk into the padded buffer's halo.
    /// </summary>
    /// <param name="neighbor">The density of the neighbor chunk.</param>
    /// <param name="offset">The unit offset from the chunk to the neighbor.</param>
    /// <param name="size">The chunk dimensions.</param>
    /// <param name="padded">The padded buffer. Its halo layer on the neighbor's side receives the copy.</param>
    private static void CopyHaloFace(float[] neighbor, Int3 offset, Int3 size, Span<float> padded)
    {
        var x = new AxisLayout(1, 1, size.X);
        var y = new AxisLayout(size.X, size.X + 2, size.Y);
        var z = new AxisLayout(size.X * size.Y, (size.X + 2) * (size.Y + 2), size.Z);
        var (along, first, second) = offset.X != 0 ? (x, y, z) : offset.Y != 0 ? (y, z, x) : (z, x, y);
        bool negative = offset.X + offset.Y + offset.Z < 0;

        // The neighbor's layer that touches the chunk is its last one for a negative neighbor and its first one for
        // a positive neighbor. It fills the halo layer before the chunk or the layer after it. The other two axes
        // skip one halo voxel.
        int sourceStart = negative ? (along.Extent - 1) * along.SourceStride : 0;
        int paddedStart = (negative ? 0 : along.Extent + 1) * along.PaddedStride +
                          first.PaddedStride +
                          second.PaddedStride;

        for (int j = 0; j < second.Extent; j++)
        for (int i = 0; i < first.Extent; i++)
        {
            padded[paddedStart + i * first.PaddedStride + j * second.PaddedStride] =
                neighbor[sourceStart + i * first.SourceStride + j * second.SourceStride];
        }
    }

    private bool TryGetNeighbor(Int3 position, Int3 size, out float[] density)
    {
        return _density.TryGetValue(position, out density!) &&
               density.Length == size.X * size.Y * size.Z;
    }

    /// <summary>
    ///     Describes one axis of a chunk in its own layout and in the padded layout.
    /// </summary>
    private readonly record struct AxisLayout(int SourceStride, int PaddedStride, int Extent);
}
