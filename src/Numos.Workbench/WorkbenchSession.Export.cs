using System.Globalization;
using System.Text;
using Numos.API;
using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.Maths;
using Numos.SimDrawer;

namespace Numos.Workbench;

/// <summary>
///     Shared file helpers for exports.
/// </summary>
internal static class ExportFiles
{
    internal static void EnsureParentDirectory(string path)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
    }
}

/// <summary>
///     One field written by <see cref="WorkbenchSession.ExportRegion" /> or <see cref="WorkbenchSession.ExportSlice" />.
/// </summary>
/// <param name="Field">The field name.</param>
/// <param name="Unit">The unit label.</param>
/// <param name="Path">The file written, or <see langword="null" /> for an in-memory export.</param>
/// <param name="Shape">The array shape, slowest axis first.</param>
/// <param name="Minimum">The smallest finite value, or <see langword="null" /> when none is finite.</param>
/// <param name="Maximum">The largest finite value, or <see langword="null" /> when none is finite.</param>
/// <param name="Values">The values in C order.</param>
public sealed record FieldExport(
    string Field,
    string Unit,
    string? Path,
    int[] Shape,
    float? Minimum,
    float? Maximum,
    float[] Values);

/// <summary>
///     A voxel that a <see cref="ProbeRecorder" /> samples.
/// </summary>
/// <param name="Name">The column prefix. It must not contain a comma, a quote, or a line break.</param>
/// <param name="Voxel">The global voxel coordinate.</param>
public readonly record struct ProbePoint(string Name, Int3 Voxel);

/// <summary>
///     An RGBA image of a slice.
/// </summary>
/// <param name="Width">The width in pixels.</param>
/// <param name="Height">The height in pixels.</param>
/// <param name="Rgba">`Width * Height * 4` bytes from the top row down.</param>
/// <param name="RangeMinimum">The low end of the color range the render used.</param>
/// <param name="RangeMaximum">The high end of the color range the render used.</param>
public sealed record SliceImage(int Width, int Height, byte[] Rgba, float RangeMinimum, float RangeMaximum);

/// <summary>
///     Writes probe samples to a CSV file while the simulation ticks.
/// </summary>
/// <remarks>
///     <para>
///         The first two columns are `tick` and `timeSeconds`. After that comes one column per probe and field, named
///         `name.field`, with the probes in the order given.
///     </para>
///     <para>
///         Sampling reads detached snapshots of the chunks that hold probes. It never changes the simulation.
///     </para>
/// </remarks>
public sealed class ProbeRecorder : IDisposable
{
    private readonly WorkbenchSession _session;
    private readonly VoxelSampler _sampler;
    private readonly StreamWriter _writer;
    private readonly float[] _values;
    private readonly StringBuilder _line = new();

    internal ProbeRecorder(
        WorkbenchSession session,
        IReadOnlyList<ProbePoint> points,
        IReadOnlyList<string> fields,
        string path,
        AtmosSimulationId? simulation,
        WorkbenchFieldOptions options)
    {
        _session = session;
        foreach (var point in points)
        {
            if (string.IsNullOrWhiteSpace(point.Name) || point.Name.IndexOfAny([',', '"', '\n', '\r']) >= 0)
                throw new ArgumentException($"Probe name '{point.Name}' must be nonblank and free of commas and quotes.");
        }

        _sampler = session.CreateSampler(points.Select(static point => point.Voxel).ToArray(), fields, simulation, options);
        _values = new float[points.Count * fields.Count];
        Path = System.IO.Path.GetFullPath(path);
        ExportFiles.EnsureParentDirectory(Path);
        _writer = new StreamWriter(Path, false, new UTF8Encoding(false)) { NewLine = "\n" };
        var header = new StringBuilder("tick,timeSeconds");
        foreach (var point in points)
        {
            foreach (var field in _sampler.Fields)
                header.Append(',').Append(point.Name).Append('.').Append(field.Name);
        }

        _writer.WriteLine(header.ToString());
    }

    /// <summary>
    ///     The full path of the CSV file.
    /// </summary>
    public string Path { get; }

    /// <summary>
    ///     The number of data rows written so far.
    /// </summary>
    public int Rows { get; private set; }

    /// <summary>
    ///     Samples the probes at the current world tick and appends one row.
    /// </summary>
    public void Record()
    {
        int tick = _session.World!.TickCount;
        _sampler.Sample(_values);
        _line.Clear();
        _line.Append(tick.ToString(CultureInfo.InvariantCulture))
            .Append(',')
            .Append((tick / (double)AtmosSimulation.SimulationRate).ToString("R", CultureInfo.InvariantCulture));

        foreach (float value in _values)
            _line.Append(',').Append(value.ToString("R", CultureInfo.InvariantCulture));

        _writer.WriteLine(_line.ToString());
        Rows++;
    }

    /// <summary>
    ///     Flushes and closes the file.
    /// </summary>
    public void Dispose()
    {
        _writer.Dispose();
    }
}

public sealed partial class WorkbenchSession
{
    private const string ExportSource = "Export";
    private const int MaximumRenderPixels = 1 << 27;

    /// <summary>
    ///     Opens a probe file for a list of voxels.
    /// </summary>
    /// <param name="points">The probes, in column order.</param>
    /// <param name="fieldNames">Field names from <see cref="Fields" />.</param>
    /// <param name="path">The CSV file to create or replace.</param>
    /// <param name="simulation">The simulation to read, or <see langword="null" /> for the active one.</param>
    /// <param name="options">
    ///     Field options. A probe has no region, so a `schlieren` column needs
    ///     <see cref="WorkbenchFieldOptions.SchlierenReference" /> here.
    /// </param>
    /// <returns>A recorder. Dispose it to close the file.</returns>
    public ProbeRecorder CreateProbeRecorder(
        IReadOnlyList<ProbePoint> points,
        IReadOnlyList<string> fieldNames,
        string path,
        AtmosSimulationId? simulation = null,
        WorkbenchFieldOptions options = default)
    {
        return Guard(
            "Could not open the probe file",
            () =>
            {
                RequireWorld();
                ArgumentNullException.ThrowIfNull(points);
                ArgumentException.ThrowIfNullOrWhiteSpace(path);
                var recorder = new ProbeRecorder(this, points, fieldNames, path, simulation, options);
                Log.Write(
                    WorkbenchLogLevel.Info,
                    ExportSource,
                    $"Probing {points.Count} voxels into '{recorder.Path}'.",
                    null,
                    World!.TickCount);

                return recorder;
            });
    }

    /// <summary>
    ///     Gets the global box of one slice across all chunks of a simulation.
    /// </summary>
    /// <param name="axis">The axis the slice is perpendicular to.</param>
    /// <param name="index">The global voxel index along <paramref name="axis" />.</param>
    /// <param name="simulation">The simulation, or <see langword="null" /> for the active one.</param>
    /// <exception cref="InvalidOperationException">The simulation has no chunks.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The index is outside the chunk bounds.</exception>
    public VoxelBox GetSliceBox(SliceAxis axis, int index, AtmosSimulationId? simulation = null)
    {
        var bounds = GetChunkBounds(simulation) ??
                     throw new InvalidOperationException("The simulation has no chunks to slice.");

        var (min, max) = (bounds.Min, bounds.Max);
        switch (axis)
        {
            case SliceAxis.X:
                CheckSliceIndex(index, min.X, max.X);
                return new VoxelBox(new Int3(index, min.Y, min.Z), new Int3(index, max.Y, max.Z));
            case SliceAxis.Y:
                CheckSliceIndex(index, min.Y, max.Y);
                return new VoxelBox(new Int3(min.X, index, min.Z), new Int3(max.X, index, max.Z));
            case SliceAxis.Z:
                CheckSliceIndex(index, min.Z, max.Z);
                return new VoxelBox(new Int3(min.X, min.Y, index), new Int3(max.X, max.Y, index));
            default:
                throw new ArgumentOutOfRangeException(nameof(axis));
        }
    }

    private static void CheckSliceIndex(int index, int minimum, int maximum)
    {
        if (index < minimum || index > maximum)
        {
            throw new ArgumentOutOfRangeException(
                nameof(index),
                $"Slice index {index} is outside the chunk bounds {minimum} to {maximum}.");
        }
    }

    /// <summary>
    ///     Gets the array shape of a box, slowest axis first.
    /// </summary>
    /// <param name="box">The box.</param>
    /// <returns>(z, y, x), always three entries.</returns>
    public static int[] GetShape(VoxelBox box)
    {
        var size = box.Size;
        return [size.Z, size.Y, size.X];
    }

    /// <summary>
    ///     Gets the shape of a slice box as (v, u).
    /// </summary>
    /// <remarks>
    ///     The axes follow <see cref="SimulationFrameBuilder.GetSliceDimensions" />, so a slice here matches a slice in
    ///     the viewer.
    /// </remarks>
    /// <param name="axis">The axis the slice is perpendicular to.</param>
    /// <param name="box">A box from <see cref="GetSliceBox" />.</param>
    public static int[] GetSliceShape(SliceAxis axis, VoxelBox box)
    {
        (int uSize, int vSize) = SimulationFrameBuilder.GetSliceDimensions(box.Size, axis);
        return [vSize, uSize];
    }

    /// <summary>
    ///     Reads fields over a box and writes them as NumPy arrays, one file per field.
    /// </summary>
    /// <param name="box">The inclusive box in global voxel coordinates.</param>
    /// <param name="fieldNames">Field names from <see cref="Fields" />.</param>
    /// <param name="pathTemplate">
    ///     The file path. It must contain `{field}` when more than one field is given. The session replaces `{field}`
    ///     with the field name and turns `:` into `_`.
    /// </param>
    /// <param name="simulation">The simulation to read, or <see langword="null" /> for the active one.</param>
    /// <param name="options">
    ///     Field options. Without a schlieren reference, `schlieren` normalizes by the largest gradient in the region.
    /// </param>
    /// <returns>One entry per field with the path, the shape (z, y, x), and the finite range.</returns>
    public IReadOnlyList<FieldExport> ExportRegion(
        VoxelBox box,
        IReadOnlyList<string> fieldNames,
        string pathTemplate,
        AtmosSimulationId? simulation = null,
        WorkbenchFieldOptions options = default)
    {
        return Guard(
            "Could not export the region",
            () => ExportCore(box, GetShape(box), fieldNames, pathTemplate, simulation, "region", options));
    }

    /// <summary>
    ///     Reads fields over a slice and writes them as two-dimensional NumPy arrays, one file per field.
    /// </summary>
    /// <param name="axis">The axis the slice is perpendicular to.</param>
    /// <param name="index">The global voxel index along <paramref name="axis" />.</param>
    /// <param name="fieldNames">Field names from <see cref="Fields" />.</param>
    /// <param name="pathTemplate">The file path, as for <see cref="ExportRegion" />.</param>
    /// <param name="simulation">The simulation to read, or <see langword="null" /> for the active one.</param>
    /// <param name="options">
    ///     Field options. Without a schlieren reference, `schlieren` normalizes by the largest gradient in the slice.
    /// </param>
    /// <returns>
    ///     One entry per field. The shape is (v, u), where u runs along the first remaining axis and v along the second.
    ///     For a Z slice, u is x and v is y. For a Y slice, u is x and v is z. For an X slice, u is z and v is y.
    ///     These are the axes of <see cref="SimulationFrameBuilder.GetSliceDimensions" />.
    /// </returns>
    /// <remarks>
    ///     The slice covers the bounding box of all chunks. Voxels without a chunk are NaN.
    /// </remarks>
    public IReadOnlyList<FieldExport> ExportSlice(
        SliceAxis axis,
        int index,
        IReadOnlyList<string> fieldNames,
        string pathTemplate,
        AtmosSimulationId? simulation = null,
        WorkbenchFieldOptions options = default)
    {
        return Guard(
            "Could not export the slice",
            () =>
            {
                var box = GetSliceBox(axis, index, simulation);
                return ExportCore(
                    box,
                    GetSliceShape(axis, box),
                    fieldNames,
                    pathTemplate,
                    simulation,
                    $"{axis} slice {index}",
                    options,
                    axis);
            });
    }

    private static float[] Transpose(float[] source, int rows, int columns)
    {
        var result = new float[source.Length];
        for (int row = 0; row < rows; row++)
        for (int column = 0; column < columns; column++)
            result[column * rows + row] = source[row * columns + column];

        return result;
    }

    /// <summary>
    ///     Reads fields over a box and keeps the values in memory.
    /// </summary>
    /// <param name="box">The inclusive box in global voxel coordinates.</param>
    /// <param name="fieldNames">Field names from <see cref="Fields" />.</param>
    /// <param name="simulation">The simulation to read, or <see langword="null" /> for the active one.</param>
    /// <param name="options">Field options, as for <see cref="ExportRegion" />.</param>
    /// <returns>One entry per field, with a <see langword="null" /> path.</returns>
    public IReadOnlyList<FieldExport> ReadRegion(
        VoxelBox box,
        IReadOnlyList<string> fieldNames,
        AtmosSimulationId? simulation = null,
        WorkbenchFieldOptions options = default)
    {
        return Guard(
            "Could not read the region",
            () => ExportCore(box, GetShape(box), fieldNames, null, simulation, "region", options));
    }

    private IReadOnlyList<FieldExport> ExportCore(
        VoxelBox box,
        int[] shape,
        IReadOnlyList<string> fieldNames,
        string? pathTemplate,
        AtmosSimulationId? simulation,
        string description,
        WorkbenchFieldOptions options,
        SliceAxis? sliceAxis = null)
    {
        RequireWorld();
        if (fieldNames.Count == 0)
            throw new ArgumentException("At least one field is required.", nameof(fieldNames));

        if (pathTemplate != null)
        {
            if (string.IsNullOrWhiteSpace(pathTemplate))
                throw new ArgumentException("A path is required.", nameof(pathTemplate));

            if (fieldNames.Count > 1 && !pathTemplate.Contains("{field}", StringComparison.Ordinal))
                throw new ArgumentException("The path must contain {field} when you export more than one field.");
        }

        float[][] values = ReadBox(box, fieldNames, simulation, options);
        if (sliceAxis == SliceAxis.X)
        {
            // ReadBox returns (z, y, 1). An X slice is stored as (v, u) = (y, z).
            for (int field = 0; field < values.Length; field++)
                values[field] = Transpose(values[field], box.Size.Z, box.Size.Y);
        }

        var exports = new List<FieldExport>(values.Length);
        for (int index = 0; index < values.Length; index++)
        {
            var field = Fields.Resolve(fieldNames[index]);
            string? path = null;
            if (pathTemplate != null)
            {
                string name = field.Name.Replace(':', '_');
                path = System.IO.Path.GetFullPath(pathTemplate.Replace("{field}", name, StringComparison.Ordinal));
                NpyWriter.WriteFile(path, values[index], shape);
            }

            float? minimum = null;
            float? maximum = null;
            foreach (float value in values[index])
            {
                if (!float.IsFinite(value))
                    continue;

                minimum = minimum is { } low ? Math.Min(low, value) : value;
                maximum = maximum is { } high ? Math.Max(high, value) : value;
            }

            exports.Add(new FieldExport(field.Name, field.Unit, path, shape, minimum, maximum, values[index]));
        }

        if (pathTemplate != null)
        {
            Log.Write(
                WorkbenchLogLevel.Info,
                ExportSource,
                $"Exported {string.Join(", ", exports.Select(static export => export.Field))} over {description} " +
                $"({string.Join(" x ", shape)}) to {exports.Count} file{(exports.Count == 1 ? string.Empty : "s")}.",
                null,
                World!.TickCount);
        }

        return exports;
    }

    /// <summary>
    ///     Renders a slice through all chunks with a SimDrawer visualization.
    /// </summary>
    /// <param name="axis">The axis the slice is perpendicular to.</param>
    /// <param name="index">The global voxel index along <paramref name="axis" />.</param>
    /// <param name="visualizationId">A SimDrawer visualization id such as `pressure` or `temperature`.</param>
    /// <param name="range">A fixed color range, or <see langword="null" /> for the range of the finite values in the air voxels of the slice.</param>
    /// <param name="scale">The pixel size of one voxel. Must be at least 1.</param>
    /// <param name="levels">The number of color steps. Use a high number for a smooth gradient.</param>
    /// <param name="simulation">The simulation to render, or <see langword="null" /> for the active one.</param>
    /// <param name="options">
    ///     Field options. For the `schlieren` visualization, <see cref="WorkbenchFieldOptions.SchlierenReference" /> is
    ///     the gradient that maps to black. It applies when <paramref name="range" /> is <see langword="null" />.
    /// </param>
    /// <returns>
    ///     The image. The first axis (u) runs left to right and the second (v) runs bottom to top, as in
    ///     <see cref="ExportSlice" />. Voxels the visualization hides are dark gray if solid and black if no chunk exists.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         The color comes from the visualization, so the image matches what the viewer draws for the same voxel.
    ///         The automatic range covers the finite values of the air voxels in the slice.
    ///     </para>
    ///     <para>
    ///         The `schlieren` visualization maps the range maximum to black. Its automatic range is zero to the
    ///         largest density gradient in the slice, the same normalization as the `schlieren` export field. Pass a
    ///         fixed range or a schlieren reference to keep one scale across frames.
    ///     </para>
    /// </remarks>
    public SliceImage RenderSlice(
        SliceAxis axis,
        int index,
        string visualizationId,
        VisualizationRange? range = null,
        int scale = 1,
        int levels = 256,
        AtmosSimulationId? simulation = null,
        WorkbenchFieldOptions options = default)
    {
        return Guard(
            "Could not render the slice",
            () =>
            {
                ArgumentOutOfRangeException.ThrowIfLessThan(scale, 1);
                ArgumentOutOfRangeException.ThrowIfLessThan(levels, 1);
                var target = RequireSimulation(simulation);
                var config = RequireConfig();
                var box = GetSliceBox(axis, index, simulation);
                var size = box.Size;
                (int uSize, int vSize) = SimulationFrameBuilder.GetSliceDimensions(size, axis);

                if ((long)uSize * scale * vSize * scale > MaximumRenderPixels)
                {
                    throw new ArgumentException(
                        $"The image would hold more than {MaximumRenderPixels} pixels. Lower the scale.");
                }

                var builder = new SimulationFrameBuilder(config);
                var fields = builder.GetRequiredSnapshotFields(visualizationId);
                var requirements = builder.Visualizations.GetRequired(visualizationId).RequiredData;
                bool needsNeighbors = (requirements & VisualizationDataRequirements.DensityGradient) != 0;
                var dimensions = target.ChunkDimensions;
                var minChunk = VoxelBox.SplitGlobal(box.Min, dimensions, out _);
                var maxChunk = VoxelBox.SplitGlobal(box.Max, dimensions, out _);
                var existing = GetChunkPositions(target);

                // A gradient reads the face neighbors, so the chunks next to the slice supply the halo.
                int halo = needsNeighbors ? 1 : 0;
                var snapshots = new List<AtmosChunkSnapshot>();
                var scope = new HashSet<Int3>();
                for (int cz = minChunk.Z - halo; cz <= maxChunk.Z + halo; cz++)
                for (int cy = minChunk.Y - halo; cy <= maxChunk.Y + halo; cy++)
                for (int cx = minChunk.X - halo; cx <= maxChunk.X + halo; cx++)
                {
                    var position = new Int3(cx, cy, cz);
                    if (existing.Contains(position) &&
                        target.TryGetChunkSnapshot(new AtmosChunkHandle(position), default, fields, out var snapshot))
                    {
                        snapshots.Add(snapshot);
                        if (cx >= minChunk.X && cx <= maxChunk.X &&
                            cy >= minChunk.Y && cy <= maxChunk.Y &&
                            cz >= minChunk.Z && cz <= maxChunk.Z)
                        {
                            scope.Add(position);
                        }
                    }
                }

                var effectiveRange = range ??
                                     (needsNeighbors
                                         ? GetGradientRange(box, options, simulation)
                                         : GetAirRange(snapshots, box, dimensions, requirements, config));

                var frame = builder.BuildSimulation(
                    snapshots,
                    visualizationId,
                    0,
                    mappingScope: scope,
                    rangeOverride: effectiveRange,
                    resolution: levels);

                int width = uSize * scale;
                int height = vSize * scale;
                byte[] rgba = new byte[width * height * 4];
                for (int v = 0; v < vSize; v++)
                for (int u = 0; u < uSize; u++)
                {
                    (int dx0, int dy0, int dz0) = SimulationFrameBuilder.MapSliceToLocal(axis, 0, u, v);
                    var voxel = new Int3(box.Min.X + dx0, box.Min.Y + dy0, box.Min.Z + dz0);

                    (byte r, byte g, byte b) = ResolvePixel(frame, voxel, dimensions);
                    int top = (vSize - 1 - v) * scale;
                    for (int dy = 0; dy < scale; dy++)
                    for (int dx = 0; dx < scale; dx++)
                    {
                        int offset = ((top + dy) * width + u * scale + dx) * 4;
                        rgba[offset] = r;
                        rgba[offset + 1] = g;
                        rgba[offset + 2] = b;
                        rgba[offset + 3] = 255;
                    }
                }

                var used = frame.Visualization.Range;
                Log.Write(
                    WorkbenchLogLevel.Info,
                    ExportSource,
                    $"Rendered {axis} slice {index} with '{visualizationId}' at {width} x {height}.",
                    null,
                    World!.TickCount);

                return new SliceImage(width, height, rgba, used.Minimum, used.Maximum);
            });
    }

    /// <summary>
    ///     Finds the finite range of the values a visualization colors, over the air voxels of a slice.
    /// </summary>
    /// <returns>
    ///     The range, or <see langword="null" /> when the slice has no finite air value or the values are all equal.
    ///     The builder then picks its own range.
    /// </returns>
    private static VisualizationRange? GetAirRange(
        List<AtmosChunkSnapshot> snapshots,
        VoxelBox box,
        Int3 dimensions,
        VisualizationDataRequirements requirements,
        AtmosConfig config)
    {
        bool useTemperature = (requirements & VisualizationDataRequirements.Temperature) != 0;
        bool usePressure = (requirements & VisualizationDataRequirements.Pressure) != 0;
        bool useDensity = (requirements & VisualizationDataRequirements.Density) != 0;
        float minimum = float.PositiveInfinity;
        float maximum = float.NegativeInfinity;
        foreach (var snapshot in snapshots)
        {
            var origin = new Int3(
                snapshot.GridPosition.X * dimensions.X,
                snapshot.GridPosition.Y * dimensions.Y,
                snapshot.GridPosition.Z * dimensions.Z);

            for (int index = 0; index < snapshot.VoxelRoomMap.Length; index++)
            {
                int room = snapshot.VoxelRoomMap[index];
                if (room == VoxelClassification.RoomVoid || room == VoxelClassification.RoomSolid)
                    continue;

                int x = origin.X + index % dimensions.X;
                int y = origin.Y + index / dimensions.X % dimensions.Y;
                int z = origin.Z + index / (dimensions.X * dimensions.Y);
                if (x < box.Min.X || x > box.Max.X || y < box.Min.Y || y > box.Max.Y || z < box.Min.Z || z > box.Max.Z)
                    continue;

                if (useTemperature && index < snapshot.Temperature.Length)
                    FiniteRange.Include(snapshot.Temperature[index], ref minimum, ref maximum);

                if (usePressure && index < snapshot.TotalPressure.Length)
                    FiniteRange.Include(snapshot.TotalPressure[index], ref minimum, ref maximum);

                if (useDensity && snapshot.Gases != null)
                {
                    float density = FlowFieldMath.GetDensity(snapshot.Gases, index, config);
                    FiniteRange.Include(density, ref minimum, ref maximum);
                }
            }
        }

        return float.IsFinite(minimum) && float.IsFinite(maximum) && maximum > minimum
            ? new VisualizationRange(minimum, maximum)
            : null;
    }

    /// <summary>
    ///     Picks the schlieren color range of a slice: zero to the reference gradient.
    /// </summary>
    /// <returns>
    ///     The range, or <see langword="null" /> when the slice has no gradient and no valid reference was given. The
    ///     builder then draws a uniform picture.
    /// </returns>
    private VisualizationRange? GetGradientRange(VoxelBox box, WorkbenchFieldOptions options, AtmosSimulationId? simulation)
    {
        float reference = options.SchlierenReference ??
                          FlowFieldMath.MaximumFinite(ReadBox(box, ["densityGradient"], simulation)[0]);

        return float.IsFinite(reference) && reference > 0f ? new VisualizationRange(0f, reference) : null;
    }

    private static (byte R, byte G, byte B) ResolvePixel(SimulationDrawData frame, Int3 voxel, Int3 dimensions)
    {
        var chunk = VoxelBox.SplitGlobal(voxel, dimensions, out var local);
        if (!frame.Chunks.TryGetValue(chunk, out var data))
            return (0, 0, 0);

        var cell = data.GetCell(data.GetLocalIndex(local.X, local.Y, local.Z));
        if (cell.IsVisible)
            return (ToByte(cell.Color.R), ToByte(cell.Color.G), ToByte(cell.Color.B));

        return cell.RoomId == VoxelClassification.RoomSolid ? ((byte)90, (byte)90, (byte)98) : ((byte)24, (byte)24, (byte)28);
    }

    private static byte ToByte(float channel)
    {
        return (byte)Math.Clamp((int)MathF.Round(channel * 255f), 0, 255);
    }
}
