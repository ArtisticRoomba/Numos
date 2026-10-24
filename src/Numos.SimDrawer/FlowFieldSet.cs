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
                IncludeFinite(density, ref minimum, ref maximum);
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

    private static void IncludeFinite(float[] values, ref float minimum, ref float maximum)
    {
        foreach (float value in values)
        {
            if (!float.IsFinite(value))
                continue;

            minimum = Math.Min(minimum, value);
            maximum = Math.Max(maximum, value);
        }
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

        if (TryGetNeighbor(position, -1, 0, 0, size, out var negativeX))
        {
            for (int z = 0; z < size.Z; z++)
            for (int y = 0; y < size.Y; y++)
                padded[(z + 1) * stepXY + (y + 1) * stepX] = negativeX[(z * size.Y + y) * size.X + size.X - 1];
        }

        if (TryGetNeighbor(position, 1, 0, 0, size, out var positiveX))
        {
            for (int z = 0; z < size.Z; z++)
            for (int y = 0; y < size.Y; y++)
                padded[(z + 1) * stepXY + (y + 1) * stepX + size.X + 1] = positiveX[(z * size.Y + y) * size.X];
        }

        if (TryGetNeighbor(position, 0, -1, 0, size, out var negativeY))
        {
            for (int z = 0; z < size.Z; z++)
            for (int x = 0; x < size.X; x++)
                padded[(z + 1) * stepXY + x + 1] = negativeY[(z * size.Y + size.Y - 1) * size.X + x];
        }

        if (TryGetNeighbor(position, 0, 1, 0, size, out var positiveY))
        {
            for (int z = 0; z < size.Z; z++)
            for (int x = 0; x < size.X; x++)
                padded[(z + 1) * stepXY + (size.Y + 1) * stepX + x + 1] = positiveY[z * size.Y * size.X + x];
        }

        if (TryGetNeighbor(position, 0, 0, -1, size, out var negativeZ))
        {
            for (int y = 0; y < size.Y; y++)
            for (int x = 0; x < size.X; x++)
                padded[(y + 1) * stepX + x + 1] = negativeZ[((size.Z - 1) * size.Y + y) * size.X + x];
        }

        if (TryGetNeighbor(position, 0, 0, 1, size, out var positiveZ))
        {
            for (int y = 0; y < size.Y; y++)
            for (int x = 0; x < size.X; x++)
                padded[(size.Z + 1) * stepXY + (y + 1) * stepX + x + 1] = positiveZ[y * size.X + x];
        }
    }

    private bool TryGetNeighbor(Int3 position, int dx, int dy, int dz, Int3 size, out float[] density)
    {
        var neighbor = new Int3(position.X + dx, position.Y + dy, position.Z + dz);
        return _density.TryGetValue(neighbor, out density!) &&
               density.Length == size.X * size.Y * size.Z;
    }
}
