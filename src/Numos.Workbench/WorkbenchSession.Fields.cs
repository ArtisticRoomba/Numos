using Numos.API;
using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.Maths;

namespace Numos.Workbench;

/// <summary>
///     Reads a fixed list of fields at a fixed list of voxels. Create one with
///     <see cref="WorkbenchSession.CreateSampler" /> and call <see cref="Sample" /> as often as needed.
/// </summary>
/// <remarks>
///     <para>
///         Each call copies the fields it needs from every chunk that holds a point or lies within one voxel of a
///         point that feeds a region field. The sampler copies each chunk once per call and allocates nothing per
///         voxel. It never changes the simulation.
///     </para>
///     <para>
///         The sampler keeps the simulation reference it was created with. Create a new sampler after a project
///         closes or the timeline restores the world.
///     </para>
/// </remarks>
public sealed class VoxelSampler
{
    private readonly WorkbenchSession _session;
    private readonly AtmosSimulation _simulation;
    private readonly ChunkGroup[] _groups;
    private readonly AtmosChunkSnapshotFields _required;
    private readonly IReadOnlyList<Int3> _points;
    private readonly WorkbenchFieldOptions _options;
    private readonly float[] _halo = new float[27];
    private readonly float[][] _haloDestinations;
    private readonly WorkbenchField[]?[] _haloSources;
    private readonly ChunkSnapshotCache _snapshots;
    private readonly HashSet<Int3> _existingChunks = [];
    private long _chunkRevision = -1;

    internal VoxelSampler(
        WorkbenchSession session,
        AtmosSimulation simulation,
        IReadOnlyList<Int3> points,
        WorkbenchField[] fields,
        WorkbenchFieldOptions options)
    {
        foreach (var field in fields)
        {
            if (field.NeedsReferenceAtPoints && options.SchlierenReference == null)
            {
                throw new ArgumentException(
                    $"Field '{field.Name}' normalizes over a region, so sampling it at single voxels needs a " +
                    "schlierenReference. Give the reference gradient in kg/m4, or export a region instead.");
            }
        }

        _session = session;
        _simulation = simulation;
        _points = points.ToArray();
        _options = options;
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

        _haloDestinations = [_halo];
        _haloSources = new WorkbenchField[fields.Length][];
        var haloRequired = AtmosChunkSnapshotFields.None;
        for (int column = 0; column < fields.Length; column++)
        {
            var field = fields[column];
            if (field.IsRegionField)
            {
                var source = session.Fields.Resolve(field.HaloSource!);
                _haloSources[column] = [source];
                haloRequired |= source.Required;
            }
            else
            {
                _required |= field.Required;
            }
        }

        // The chunk of a point is also in the halo of that point, so one snapshot serves both reads.
        _snapshots = new ChunkSnapshotCache(simulation, _required | haloRequired);
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
        _snapshots.Clear();
        try
        {
            foreach (var group in _groups)
            {
                AtmosChunkSnapshot snapshot;
                try
                {
                    if (!_snapshots.TryGet(group.Handle.Position, _required, out snapshot))
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
                    {
                        if (!Fields[column].IsRegionField)
                            destination[point * columns + column] = Fields[column].Sample(in snapshot, local);
                    }
                }
            }

            SampleRegionFields(destination);
        }
        finally
        {
            _snapshots.Clear();
        }
    }

    private void SampleRegionFields(Span<float> destination)
    {
        int columns = Fields.Count;
        HashSet<Int3>? existing = null;
        float edge = 0f;
        Span<float> value = stackalloc float[1];
        for (int column = 0; column < columns; column++)
        {
            var field = Fields[column];
            if (!field.IsRegionField)
                continue;

            if (existing == null)
            {
                existing = GetExistingChunks();
                edge = FlowFieldMath.GetVoxelEdge(_session.RequireConfig());
            }

            var source = _haloSources[column]!;
            for (int point = 0; point < PointCount; point++)
            {
                var center = _points[point];
                var box = new VoxelBox(
                    new Int3(center.X - 1, center.Y - 1, center.Z - 1),
                    new Int3(center.X + 1, center.Y + 1, center.Z + 1));

                WorkbenchSession.ReadVoxelFields(_simulation, box, source, _haloDestinations, existing, _snapshots);
                value[0] = float.NaN;
                field.ComputeRegion(
                    new WorkbenchRegionInput(_halo, new Int3(1, 1, 1), edge, _options.SchlierenReference),
                    value);

                destination[point * columns + column] = value[0];
            }
        }
    }

    /// <summary>
    ///     Returns the positions of the chunks that exist, rebuilding the set only when the simulation added or
    ///     removed a chunk.
    /// </summary>
    private HashSet<Int3> GetExistingChunks()
    {
        if (_simulation.TryGetChunkHandles(_chunkRevision, out _chunkRevision, out var handles))
        {
            _existingChunks.Clear();
            foreach (var handle in handles)
                _existingChunks.Add(handle.Position);
        }

        return _existingChunks;
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
    /// <param name="options">Field options. `schlieren` needs a reference here, because a point has no region.</param>
    /// <returns>A reusable sampler.</returns>
    /// <exception cref="KeyNotFoundException">A field name is unknown.</exception>
    /// <exception cref="ArgumentException">A schlieren field is requested without a reference.</exception>
    public VoxelSampler CreateSampler(
        IReadOnlyList<Int3> points,
        IReadOnlyList<string> fieldNames,
        AtmosSimulationId? simulation = null,
        WorkbenchFieldOptions options = default)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(fieldNames);
        var target = RequireSimulation(simulation);
        var fields = Fields.ResolveAll(fieldNames, out _);
        return new VoxelSampler(this, target, points, fields, options);
    }

    /// <summary>
    ///     Reads fields at a list of voxels once.
    /// </summary>
    /// <returns>Values in row-major order, `points.Count * fieldNames.Count` of them.</returns>
    public float[] SampleVoxels(
        IReadOnlyList<Int3> points,
        IReadOnlyList<string> fieldNames,
        AtmosSimulationId? simulation = null,
        WorkbenchFieldOptions options = default)
    {
        var sampler = CreateSampler(points, fieldNames, simulation, options);
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
    /// <param name="options">Field options, such as the schlieren reference.</param>
    /// <returns>
    ///     One array per field, in the order of <paramref name="fieldNames" />. Each array holds
    ///     `sizeX * sizeY * sizeZ` values in C order with shape (z, y, x), so x changes fastest. A voxel outside every
    ///     chunk, or a solid voxel for a field that has no value there, is NaN.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         The call copies each chunk once per source field, with only the snapshot fields that field needs.
    ///     </para>
    ///     <para>
    ///         A region field such as `densityGradient` reads its source over the box plus a one-voxel halo. The halo
    ///         can lie in a chunk outside the box, so a box edge on a chunk border gets the same gradient as the
    ///         interior. A neighbor that is solid, void, or missing gives a one-sided difference.
    ///     </para>
    ///     <para>
    ///         A schlieren field with no <see cref="WorkbenchFieldOptions.SchlierenReference" /> normalizes by the
    ///         largest gradient inside the box. Two boxes of the same simulation can therefore differ in brightness.
    ///     </para>
    /// </remarks>
    /// <exception cref="ArgumentException">The box is invalid or holds too many voxels.</exception>
    public float[][] ReadBox(
        VoxelBox box,
        IReadOnlyList<string> fieldNames,
        AtmosSimulationId? simulation = null,
        WorkbenchFieldOptions options = default)
    {
        ArgumentNullException.ThrowIfNull(fieldNames);
        var target = RequireSimulation(simulation);
        box.ThrowIfInvalid();
        var fields = Fields.ResolveAll(fieldNames, out _);
        CheckReadSize(box.VoxelCount);
        var existing = GetChunkPositions(target);
        var results = new float[fields.Length][];
        var halos = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
        float edge = fields.Any(static field => field.IsRegionField)
            ? FlowFieldMath.GetVoxelEdge(RequireConfig())
            : 0f;

        var pointFields = new List<WorkbenchField>();
        var pointResults = new List<float[]>();
        for (int index = 0; index < fields.Length; index++)
        {
            results[index] = new float[(int)box.VoxelCount];
            if (fields[index].IsRegionField)
                continue;

            pointFields.Add(fields[index]);
            pointResults.Add(results[index]);
        }

        if (pointFields.Count > 0)
            ReadVoxelFields(target, box, pointFields, pointResults, existing);

        for (int index = 0; index < fields.Length; index++)
        {
            var field = fields[index];
            if (!field.IsRegionField)
                continue;

            string sourceName = field.HaloSource!;
            if (!halos.TryGetValue(sourceName, out float[]? halo))
            {
                var haloBox = new VoxelBox(
                    new Int3(box.Min.X - 1, box.Min.Y - 1, box.Min.Z - 1),
                    new Int3(box.Max.X + 1, box.Max.Y + 1, box.Max.Z + 1));

                CheckReadSize(haloBox.VoxelCount);
                halo = new float[(int)haloBox.VoxelCount];
                ReadVoxelFields(target, haloBox, [Fields.Resolve(sourceName)], [halo], existing);
                halos.Add(sourceName, halo);
            }

            field.ComputeRegion(new WorkbenchRegionInput(halo, box.Size, edge, options.SchlierenReference), results[index]);
        }

        return results;
    }

    internal HashSet<Int3> GetChunkPositions(AtmosSimulation target)
    {
        var existing = new HashSet<Int3>();
        foreach (var handle in target.GetChunkHandles())
            existing.Add(handle.Position);

        return existing;
    }

    private static void CheckReadSize(long voxelCount)
    {
        if (voxelCount > MaximumReadVoxels)
        {
            throw new ArgumentException(
                $"The box holds {voxelCount} voxels. Reading more than {MaximumReadVoxels} voxels at once is not allowed.");
        }
    }

    /// <summary>
    ///     Reads voxel fields over a box into buffers, with one snapshot per chunk.
    /// </summary>
    /// <param name="target">The simulation to read.</param>
    /// <param name="box">The inclusive box in global voxel coordinates. It can reach outside every chunk.</param>
    /// <param name="fields">Voxel fields.</param>
    /// <param name="destinations">One buffer per field. Each receives `box.VoxelCount` values with x changing fastest. Voxels without data are NaN.</param>
    /// <param name="existing">The positions of the chunks that exist, so the read skips missing chunks without throwing.</param>
    /// <param name="cache">
    ///     Snapshots to reuse across reads, or <see langword="null" /> to copy each chunk for this read only. Without a
    ///     cache the read holds one snapshot at a time, which keeps the memory of a large box low.
    /// </param>
    internal static void ReadVoxelFields(
        AtmosSimulation target,
        VoxelBox box,
        IReadOnlyList<WorkbenchField> fields,
        IReadOnlyList<float[]> destinations,
        HashSet<Int3> existing,
        ChunkSnapshotCache? cache = null)
    {
        var required = AtmosChunkSnapshotFields.None;
        for (int index = 0; index < fields.Count; index++)
        {
            required |= fields[index].Required;
            Array.Fill(destinations[index], float.NaN);
        }

        var size = box.Size;
        var dimensions = target.ChunkDimensions;
        var minChunk = VoxelBox.SplitGlobal(box.Min, dimensions, out _);
        var maxChunk = VoxelBox.SplitGlobal(box.Max, dimensions, out _);
        for (int cz = minChunk.Z; cz <= maxChunk.Z; cz++)
        for (int cy = minChunk.Y; cy <= maxChunk.Y; cy++)
        for (int cx = minChunk.X; cx <= maxChunk.X; cx++)
        {
            var position = new Int3(cx, cy, cz);
            if (!existing.Contains(position))
                continue;

            AtmosChunkSnapshot snapshot;
            if (cache != null)
            {
                if (!cache.TryGet(position, required, out snapshot))
                    continue;
            }
            else if (!target.TryGetChunkSnapshot(new AtmosChunkHandle(position), default, required, out snapshot))
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
                    for (int field = 0; field < fields.Count; field++)
                        destinations[field][targetRow + x] = fields[field].Sample(in snapshot, sourceRow + x);
                }
            }
        }
    }

    private const int MaximumReadVoxels = 1 << 28;
}
