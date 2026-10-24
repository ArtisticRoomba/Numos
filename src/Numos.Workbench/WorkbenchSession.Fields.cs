using Numos.API;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.Maths;

namespace Numos.Workbench;

/// <summary>
///     Reads a fixed list of fields at a fixed list of voxels. Create one with
///     <see cref="WorkbenchSession.CreateSampler" /> and call <see cref="Sample" /> as often as needed.
/// </summary>
/// <remarks>
///     <para>
///         Each call copies the fields it needs from every chunk that holds a point, and nothing else. The sampler
///         allocates one snapshot per chunk per call and nothing per voxel. It never changes the simulation.
///     </para>
///     <para>
///         The sampler keeps the simulation reference it was created with. Create a new sampler after a project
///         closes or the timeline restores the world.
///     </para>
/// </remarks>
public sealed class VoxelSampler
{
    private readonly AtmosSimulation _simulation;
    private readonly ChunkGroup[] _groups;
    private readonly AtmosChunkSnapshotFields _required;

    internal VoxelSampler(AtmosSimulation simulation, IReadOnlyList<Int3> points, WorkbenchField[] fields)
    {
        _simulation = simulation;
        Fields = fields;
        PointCount = points.Count;
        var dimensions = simulation.ChunkDimensions;
        var groups = new Dictionary<Int3, List<(int Point, int Local)>>();
        for (int point = 0; point < points.Count; point++)
        {
            var chunk = VoxelBox.SplitGlobal(points[point], dimensions, out var local);
            if (!groups.TryGetValue(chunk, out var list))
            {
                list = [];
                groups.Add(chunk, list);
            }

            list.Add((point, local.X + dimensions.X * (local.Y + dimensions.Y * local.Z)));
        }

        _groups = groups
            .OrderBy(static pair => pair.Key.X)
            .ThenBy(static pair => pair.Key.Y)
            .ThenBy(static pair => pair.Key.Z)
            .Select(static pair => new ChunkGroup(new AtmosChunkHandle(pair.Key), pair.Value.ToArray()))
            .ToArray();

        foreach (var field in fields)
            _required |= field.Required;
    }

    /// <summary>
    ///     The fields in column order.
    /// </summary>
    public IReadOnlyList<WorkbenchField> Fields { get; }

    /// <summary>
    ///     The number of points.
    /// </summary>
    public int PointCount { get; }

    /// <summary>
    ///     Reads every field at every point.
    /// </summary>
    /// <param name="destination">
    ///     Receives `PointCount * Fields.Count` values in row-major order: the value of field `f` at point `p` is at
    ///     `p * Fields.Count + f`. A point outside every chunk reads as NaN.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="destination" /> is too short.</exception>
    public void Sample(Span<float> destination)
    {
        int columns = Fields.Count;
        if (destination.Length < PointCount * columns)
            throw new ArgumentException("The destination is too short.", nameof(destination));

        destination[..(PointCount * columns)].Fill(float.NaN);
        foreach (var group in _groups)
        {
            AtmosChunkSnapshot snapshot;
            try
            {
                if (!_simulation.TryGetChunkSnapshot(group.Handle, default, _required, out snapshot))
                    continue;
            }
            catch (KeyNotFoundException)
            {
                // A point outside every chunk stays NaN.
                continue;
            }

            foreach (var (point, local) in group.Voxels)
            {
                for (int column = 0; column < columns; column++)
                    destination[point * columns + column] = Fields[column].Sample(in snapshot, local);
            }
        }
    }

    private readonly record struct ChunkGroup(AtmosChunkHandle Handle, (int Point, int Local)[] Voxels);
}

public sealed partial class WorkbenchSession
{
    /// <summary>
    ///     The fields that sampling, export, and probes can read.
    /// </summary>
    /// <remarks>
    ///     Add a field here to make it available to every front end. The registry lives as long as the session.
    /// </remarks>
    public WorkbenchFieldRegistry Fields { get; } = WorkbenchFieldRegistry.CreateDefault();

    /// <summary>
    ///     Creates a sampler for a list of voxels.
    /// </summary>
    /// <param name="points">Global voxel coordinates.</param>
    /// <param name="fieldNames">Field names from <see cref="Fields" />.</param>
    /// <param name="simulation">The simulation to read, or <see langword="null" /> for the active one.</param>
    /// <returns>A reusable sampler.</returns>
    /// <exception cref="KeyNotFoundException">A field name is unknown.</exception>
    public VoxelSampler CreateSampler(
        IReadOnlyList<Int3> points,
        IReadOnlyList<string> fieldNames,
        AtmosSimulationId? simulation = null)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(fieldNames);
        var target = RequireSimulation(simulation);
        var fields = Fields.ResolveAll(fieldNames, out _);
        return new VoxelSampler(target, points, fields);
    }

    /// <summary>
    ///     Reads fields at a list of voxels once.
    /// </summary>
    /// <returns>Values in row-major order, `points.Count * fieldNames.Count` of them.</returns>
    public float[] SampleVoxels(
        IReadOnlyList<Int3> points,
        IReadOnlyList<string> fieldNames,
        AtmosSimulationId? simulation = null)
    {
        var sampler = CreateSampler(points, fieldNames, simulation);
        float[] values = new float[points.Count * fieldNames.Count];
        sampler.Sample(values);
        return values;
    }

    /// <summary>
    ///     Reads fields over a box of voxels.
    /// </summary>
    /// <param name="box">The inclusive box in global voxel coordinates.</param>
    /// <param name="fieldNames">Field names from <see cref="Fields" />.</param>
    /// <param name="simulation">The simulation to read, or <see langword="null" /> for the active one.</param>
    /// <returns>
    ///     One array per field, in the order of <paramref name="fieldNames" />. Each array holds
    ///     `sizeX * sizeY * sizeZ` values in C order with shape (z, y, x), so x changes fastest. A voxel outside every
    ///     chunk, or a solid voxel for a field that has no value there, is NaN.
    /// </returns>
    /// <remarks>
    ///     The call copies each chunk once, with only the snapshot fields the requested fields need.
    /// </remarks>
    /// <exception cref="ArgumentException">The box is invalid or holds too many voxels.</exception>
    public float[][] ReadBox(VoxelBox box, IReadOnlyList<string> fieldNames, AtmosSimulationId? simulation = null)
    {
        ArgumentNullException.ThrowIfNull(fieldNames);
        var target = RequireSimulation(simulation);
        box.ThrowIfInvalid();
        var fields = Fields.ResolveAll(fieldNames, out var required);
        var size = box.Size;
        if (box.VoxelCount > MaximumReadVoxels)
        {
            throw new ArgumentException(
                $"The box holds {box.VoxelCount} voxels. Reading more than {MaximumReadVoxels} voxels at once is not allowed.");
        }

        var results = new float[fields.Length][];
        for (int index = 0; index < results.Length; index++)
        {
            results[index] = new float[(int)box.VoxelCount];
            Array.Fill(results[index], float.NaN);
        }

        var dimensions = target.ChunkDimensions;
        var minChunk = VoxelBox.SplitGlobal(box.Min, dimensions, out _);
        var maxChunk = VoxelBox.SplitGlobal(box.Max, dimensions, out _);
        var existing = new HashSet<Int3>();
        foreach (var handle in target.GetChunkHandles())
            existing.Add(handle.Position);

        for (int cz = minChunk.Z; cz <= maxChunk.Z; cz++)
        for (int cy = minChunk.Y; cy <= maxChunk.Y; cy++)
        for (int cx = minChunk.X; cx <= maxChunk.X; cx++)
        {
            var position = new Int3(cx, cy, cz);
            if (!existing.Contains(position) ||
                !target.TryGetChunkSnapshot(new AtmosChunkHandle(position), default, required, out var snapshot))
            {
                continue;
            }

            int originX = cx * dimensions.X;
            int originY = cy * dimensions.Y;
            int originZ = cz * dimensions.Z;
            int x0 = Math.Max(box.Min.X, originX) - originX;
            int x1 = Math.Min(box.Max.X, originX + dimensions.X - 1) - originX;
            int y0 = Math.Max(box.Min.Y, originY) - originY;
            int y1 = Math.Min(box.Max.Y, originY + dimensions.Y - 1) - originY;
            int z0 = Math.Max(box.Min.Z, originZ) - originZ;
            int z1 = Math.Min(box.Max.Z, originZ + dimensions.Z - 1) - originZ;
            for (int z = z0; z <= z1; z++)
            for (int y = y0; y <= y1; y++)
            {
                int sourceRow = dimensions.X * (y + dimensions.Y * z);
                int targetRow = size.X * (originY + y - box.Min.Y + size.Y * (originZ + z - box.Min.Z)) +
                                originX - box.Min.X;

                for (int x = x0; x <= x1; x++)
                {
                    for (int field = 0; field < fields.Length; field++)
                        results[field][targetRow + x] = fields[field].Sample(in snapshot, sourceRow + x);
                }
            }
        }

        return results;
    }

    private const int MaximumReadVoxels = 1 << 28;
}
