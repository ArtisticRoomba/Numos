using System.Runtime.ExceptionServices;
using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.Maths;

namespace Numos.SimDrawer;

/// <summary>
///     Converts detached simulation snapshots into immutable, backend-independent presentation frames.
/// </summary>
public sealed class SimulationFrameBuilder
{
    private const VisualizationDataRequirements FlowFieldRequirements =
        VisualizationDataRequirements.Density | VisualizationDataRequirements.DensityGradient;

    private readonly AtmosConfig _config;
    private long _nextFrameVersion;
    private int _maxDegreeOfParallelism = Environment.ProcessorCount;

    /// <summary>
    ///     Creates a presentation-frame builder.
    /// </summary>
    /// <param name="config">Simulation configuration used by default visualizations.</param>
    /// <param name="visualizations">
    ///     Registry to draw from, or <see langword="null" /> for <see cref="VisualizationRegistry.CreateDefault" />.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="visualizations" /> has no registered methods.</exception>
    public SimulationFrameBuilder(AtmosConfig config, VisualizationRegistry? visualizations = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
        Visualizations = visualizations ?? VisualizationRegistry.CreateDefault(config);
        if (Visualizations.Methods.Count == 0)
        {
            throw new ArgumentException(
                "At least one visualization method must be registered.",
                nameof(visualizations));
        }
    }

    /// <summary>
    ///     Gets the registered visualization methods.
    /// </summary>
    public VisualizationRegistry Visualizations { get; }

    /// <summary>
    ///     Gets or sets how many chunks <see cref="BuildSimulation" /> maps at once. The default is the processor count.
    /// </summary>
    /// <remarks>
    ///     Chunks are independent, so a large frame maps them in parallel. The result does not depend on this value.
    ///     A visualization method that is not thread-safe needs a value of 1, because its
    ///     <see cref="IVisualizationMethod.TryGetColor" /> then runs on several threads otherwise.
    /// </remarks>
    public int MaxDegreeOfParallelism
    {
        get => _maxDegreeOfParallelism;
        set => _maxDegreeOfParallelism = Math.Max(value, 1);
    }

    /// <summary>
    ///     Returns the minimal detached snapshot fields needed to map one visualization.
    /// </summary>
    /// <param name="visualizationId">Registered visualization ID, compared case-insensitively.</param>
    /// <returns>
    ///     Voxel classification plus whichever of temperature, pressure, gases, and velocity the visualization
    ///     declares in <see cref="IVisualizationMethod.RequiredData" />. Density and density gradient need the gases.
    /// </returns>
    /// <exception cref="KeyNotFoundException">
    ///     No visualization is registered under <paramref name="visualizationId" />.
    /// </exception>
    public AtmosChunkSnapshotFields GetRequiredSnapshotFields(string visualizationId)
    {
        var visualization = Visualizations.GetRequired(visualizationId);
        var fields = AtmosChunkSnapshotFields.VoxelClassification;
        if ((visualization.RequiredData & VisualizationDataRequirements.Temperature) != 0)
            fields |= AtmosChunkSnapshotFields.Temperature;

        if ((visualization.RequiredData & VisualizationDataRequirements.Pressure) != 0)
            fields |= AtmosChunkSnapshotFields.Pressure;

        if ((visualization.RequiredData & (VisualizationDataRequirements.Gases | FlowFieldRequirements)) != 0)
            fields |= AtmosChunkSnapshotFields.Gases;

        if ((visualization.RequiredData & VisualizationDataRequirements.Velocity) != 0)
            fields |= AtmosChunkSnapshotFields.Velocity;

        return fields;
    }

    /// <summary>
    ///     Builds a complete presentation frame while reusing unchanged immutable chunks from a previous frame.
    ///     When <paramref name="mappingScope" /> is supplied, chunks outside that scope retain their
    ///     previous immutable mapping and incur no visualization work until the scope expands. An
    ///     out-of-scope chunk without a previous mapping is omitted until it enters a later scope.
    /// </summary>
    /// <param name="snapshots">
    ///     One snapshot per chunk, carrying at least <see cref="GetRequiredSnapshotFields" />.
    /// </param>
    /// <param name="visualizationId">Registered visualization ID, compared case-insensitively.</param>
    /// <param name="sourceVersion">
    ///     Caller-defined version stamped onto the frame as <see cref="SimulationDrawData.SourceVersion" />.
    /// </param>
    /// <param name="previousFrame">Frame whose chunks may be reused or retained.</param>
    /// <param name="mappingScope">Chunk positions to map, or <see langword="null" /> to map every chunk.</param>
    /// <param name="forceRemap">Rebuilds every in-scope chunk even when its previous mapping is still valid.</param>
    /// <param name="resolution">Color quantization steps for the range; values below 1 are treated as 1.</param>
    /// <param name="automaticRangeOffset">
    ///     Padding added to both ends of the automatic range. Negative or non-finite values are treated as zero.
    /// </param>
    /// <param name="rangeOverride">
    ///     Fixed range to use instead of the automatic one. Its resolution is replaced by
    ///     <paramref name="resolution" />.
    /// </param>
    /// <returns>A new frame with a fresh <see cref="SimulationDrawData.FrameVersion" />.</returns>
    /// <exception cref="ArgumentException">
    ///     Two snapshots share a grid position, or <paramref name="rangeOverride" /> is not finite and increasing.
    /// </exception>
    /// <exception cref="KeyNotFoundException">
    ///     No visualization is registered under <paramref name="visualizationId" />.
    /// </exception>
    /// <remarks>
    ///     The automatic range spans the finite temperature, pressure, or speed values in <paramref name="snapshots" />,
    ///     depending on what the visualization requires. A chunk is reused only when its identity, visualization,
    ///     mapping revision, and snapshot revision all match and the range is unchanged. A range change therefore
    ///     remaps every in-scope chunk.
    ///     A visualization that requires <see cref="VisualizationDataRequirements.DensityGradient" /> also reads the six
    ///     face neighbors of each chunk. Such a chunk is reused only when its neighbors match the previous frame too.
    ///     Include the neighbor snapshots of the chunks in <paramref name="mappingScope" />, otherwise the voxels at
    ///     the scope edge use one-sided differences.
    /// </remarks>
    public SimulationDrawData BuildSimulation(
        IEnumerable<AtmosChunkSnapshot> snapshots,
        string visualizationId,
        long sourceVersion,
        SimulationDrawData? previousFrame = null,
        IReadOnlySet<Int3>? mappingScope = null,
        bool forceRemap = false,
        int resolution = 32,
        float automaticRangeOffset = 0f,
        VisualizationRange? rangeOverride = null)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        IReadOnlyCollection<AtmosChunkSnapshot> snapshotList =
            snapshots as IReadOnlyCollection<AtmosChunkSnapshot> ?? snapshots.ToArray();

        var visualization = Visualizations.GetRequired(visualizationId);
        ulong visualizationMappingRevision = visualization.MappingRevision;
        bool needsGradient = (visualization.RequiredData & VisualizationDataRequirements.DensityGradient) != 0;
        FlowFieldSet? flowFields = (visualization.RequiredData & FlowFieldRequirements) != 0
            ? FlowFieldSet.Build(snapshotList, mappingScope, _config, needsGradient)
            : null;

        Dictionary<Int3, AtmosChunkSnapshot>? snapshotsByPosition = null;
        if (needsGradient)
        {
            snapshotsByPosition = new Dictionary<Int3, AtmosChunkSnapshot>(snapshotList.Count);
            foreach (var snapshot in snapshotList)
                snapshotsByPosition[snapshot.GridPosition] = snapshot;
        }

        var range = GetVisualizationRange(
            snapshotList,
            visualization,
            resolution,
            automaticRangeOffset,
            rangeOverride,
            flowFields,
            mappingScope);

        bool rangeChanged = previousFrame == null || previousFrame.Visualization.Range != range;
        var chunks = new Dictionary<Int3, ChunkDrawData>();
        var seenPositions = new HashSet<Int3>();
        var activeGasIds = new HashSet<int>();
        var pending = new List<(AtmosChunkSnapshot Snapshot, ChunkIdentity Identity)>();

        foreach (var snapshot in snapshotList)
        {
            if (!seenPositions.Add(snapshot.GridPosition))
                throw new ArgumentException($"Duplicate chunk position {snapshot.GridPosition}.", nameof(snapshots));

            var identity = new ChunkIdentity(snapshot.GridPosition, snapshot.Version.Generation);
            bool shouldMap = mappingScope == null || mappingScope.Contains(snapshot.GridPosition);
            if (!shouldMap)
            {
                // Keep the last mapping for hidden chunks. Do not read their fields, because the snapshot may
                // not have copied them for the newly selected visualization.
                if (TryRetainChunk(previousFrame, snapshot.GridPosition, identity, out var retainedChunk))
                    chunks.Add(snapshot.GridPosition, retainedChunk);

                continue;
            }

            AddGasIds(snapshot, activeGasIds);
            if (!forceRemap &&
                !rangeChanged &&
                CanReuse(
                    previousFrame,
                    visualizationId,
                    visualizationMappingRevision,
                    snapshot,
                    identity,
                    out var previousChunk) &&
                (snapshotsByPosition == null ||
                 NeighborsUnchanged(previousFrame!, snapshotsByPosition, snapshot.GridPosition)))
            {
                chunks[snapshot.GridPosition] = previousChunk;
                continue;
            }

            pending.Add((snapshot, identity));
        }

        BuildPendingChunks(pending, chunks, visualization, visualizationMappingRevision, range, flowFields);

        var legend = visualization.CreateLegend(activeGasIds, range);
        legend.Range = range;
        var descriptor = new VisualizationDescriptor(
            visualization.Id,
            visualization.DisplayName,
            legend)
        {
            Range = range
        };

        return new SimulationDrawData(
            chunks,
            descriptor,
            visualizationMappingRevision,
            sourceVersion,
            Interlocked.Increment(ref _nextFrameVersion));
    }

    private void BuildPendingChunks(
        List<(AtmosChunkSnapshot Snapshot, ChunkIdentity Identity)> pending,
        Dictionary<Int3, ChunkDrawData> destination,
        IVisualizationMethod visualization,
        ulong visualizationMappingRevision,
        VisualizationRange range,
        FlowFieldSet? flowFields)
    {
        var built = new ChunkDrawData[pending.Count];
        int parallelism = Math.Min(MaxDegreeOfParallelism, pending.Count);
        if (parallelism > 1)
        {
            try
            {
                Parallel.For(
                    0,
                    pending.Count,
                    new ParallelOptions { MaxDegreeOfParallelism = parallelism },
                    index => built[index] = BuildChunk(
                        pending[index].Snapshot,
                        pending[index].Identity,
                        visualization,
                        visualizationMappingRevision,
                        range,
                        flowFields));
            }
            catch (AggregateException exception) when (exception.InnerExceptions.Count > 0)
            {
                // Callers see the same exception type as a sequential build.
                ExceptionDispatchInfo.Capture(exception.InnerExceptions[0]).Throw();
            }
        }
        else
        {
            for (int index = 0; index < pending.Count; index++)
            {
                built[index] = BuildChunk(
                    pending[index].Snapshot,
                    pending[index].Identity,
                    visualization,
                    visualizationMappingRevision,
                    range,
                    flowFields);
            }
        }

        for (int index = 0; index < pending.Count; index++)
            destination.Add(pending[index].Snapshot.GridPosition, built[index]);
    }

    private static VisualizationRange GetVisualizationRange(
        IEnumerable<AtmosChunkSnapshot> snapshots,
        IVisualizationMethod visualization,
        int resolution,
        float automaticRangeOffset,
        VisualizationRange? rangeOverride,
        FlowFieldSet? flowFields,
        IReadOnlySet<Int3>? scope)
    {
        var requirements = visualization.RequiredData;
        int safeResolution = Math.Max(resolution, 1);
        if (rangeOverride is { } overrideRange)
        {
            if (!float.IsFinite(overrideRange.Minimum) ||
                !float.IsFinite(overrideRange.Maximum) ||
                overrideRange.Maximum <= overrideRange.Minimum)
            {
                throw new ArgumentException(
                    "A visualization range override must contain finite, increasing bounds.",
                    nameof(rangeOverride));
            }

            return overrideRange with
            {
                Resolution = safeResolution
            };
        }

        float offset = float.IsFinite(automaticRangeOffset)
            ? Math.Max(automaticRangeOffset, 0f)
            : 0f;

        switch (visualization.RangeSource)
        {
            case VisualizationRangeSource.DensityGradientFromZero:
                float gradient = flowFields?.GetMaximumGradient() ?? 0f;
                return gradient > 0f
                    ? new VisualizationRange(0f, gradient, safeResolution)
                    : new VisualizationRange(0f, 1f, safeResolution);
            case VisualizationRangeSource.SymmetricVelocityX:
            case VisualizationRangeSource.SymmetricVelocityY:
            case VisualizationRangeSource.SymmetricVelocityZ:
                float extreme = GetVelocityExtreme(snapshots, visualization.RangeSource) + offset;
                return extreme > 0f
                    ? new VisualizationRange(-extreme, extreme, safeResolution)
                    : new VisualizationRange(-1f, 1f, safeResolution);
        }

        float minimum = float.PositiveInfinity;
        float maximum = float.NegativeInfinity;
        if ((requirements & FlowFieldRequirements) != 0)
            flowFields?.IncludeDensityRange(scope, ref minimum, ref maximum);

        foreach (var snapshot in snapshots)
        {
            if ((requirements & VisualizationDataRequirements.Temperature) != 0)
                IncludeFiniteRange(snapshot.Temperature, ref minimum, ref maximum);

            if ((requirements & VisualizationDataRequirements.Pressure) != 0)
                IncludeFiniteRange(snapshot.TotalPressure, ref minimum, ref maximum);

            if ((requirements & VisualizationDataRequirements.Velocity) != 0)
                IncludeFiniteSpeedRange(snapshot, ref minimum, ref maximum);
        }

        if (!float.IsFinite(minimum) || !float.IsFinite(maximum))
            return new VisualizationRange(0f, 1f, safeResolution);

        return new VisualizationRange(minimum - offset, maximum + offset, safeResolution);
    }

    private static float GetVelocityExtreme(IEnumerable<AtmosChunkSnapshot> snapshots, VisualizationRangeSource source)
    {
        float extreme = 0f;
        foreach (var snapshot in snapshots)
        {
            if (!HasCompleteVelocity(snapshot, out _))
                continue;

            var component = source switch
            {
                VisualizationRangeSource.SymmetricVelocityX => snapshot.VelocityX,
                VisualizationRangeSource.SymmetricVelocityY => snapshot.VelocityY,
                _ => snapshot.VelocityZ
            };

            foreach (float value in component)
            {
                if (float.IsFinite(value))
                    extreme = Math.Max(extreme, Math.Abs(value));
            }
        }

        return extreme;
    }

    private static void IncludeFiniteRange(
        ReadOnlySpan<float> values,
        ref float minimum,
        ref float maximum)
    {
        foreach (float value in values)
        {
            if (!float.IsFinite(value))
                continue;

            minimum = Math.Min(minimum, value);
            maximum = Math.Max(maximum, value);
        }
    }

    private static void IncludeFiniteSpeedRange(
        in AtmosChunkSnapshot snapshot,
        ref float minimum,
        ref float maximum)
    {
        if (!HasCompleteVelocity(snapshot, out int voxelCount))
            return;

        for (int index = 0; index < voxelCount; index++)
        {
            float speed = GetSpeed(snapshot.VelocityX[index], snapshot.VelocityY[index], snapshot.VelocityZ[index]);
            if (!float.IsFinite(speed))
                continue;

            minimum = Math.Min(minimum, speed);
            maximum = Math.Max(maximum, speed);
        }
    }

    private static bool HasCompleteVelocity(in AtmosChunkSnapshot snapshot, out int voxelCount)
    {
        voxelCount = snapshot.Dimensions.X * snapshot.Dimensions.Y * snapshot.Dimensions.Z;
        return snapshot.VelocityX?.Length == voxelCount &&
               snapshot.VelocityY?.Length == voxelCount &&
               snapshot.VelocityZ?.Length == voxelCount;
    }

    private static float GetSpeed(float x, float y, float z)
    {
        return MathF.Sqrt(x * x + y * y + z * z);
    }

    /// <summary>
    ///     Cuts one plane through every chunk of a frame at a global voxel coordinate.
    /// </summary>
    /// <param name="frame">Frame that holds the chunks' presentation data.</param>
    /// <param name="axis">Axis normal to the plane.</param>
    /// <param name="sliceIndex">
    ///     Global voxel coordinate along <paramref name="axis" />, clamped into <see cref="SimulationDrawData.Bounds" />.
    /// </param>
    /// <param name="previous">
    ///     The slice built last for the same frame lineage, or <see langword="null" />. The result shares its pixels
    ///     and index arrays when the plane and the chunk versions are unchanged, so the builder skips that work.
    /// </param>
    /// <returns>
    ///     A dense slice over the frame's bounds. Cells that no chunk covers are transparent and cannot be picked.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="frame" /> is <see langword="null" />.</exception>
    /// <remarks>
    ///     The plane assumes every chunk has the same dimensions. A chunk with other dimensions than the first one
    ///     in position order is left out.
    /// </remarks>
    public SimulationSliceDrawData BuildPlaneSlice(
        SimulationDrawData frame,
        SliceAxis axis,
        int sliceIndex,
        SimulationSliceDrawData? previous = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var bounds = frame.Bounds;
        int minimum = bounds.GetMinimum(axis);
        int maximum = Math.Max(bounds.GetMaximumExclusive(axis) - 1, minimum);
        return BuildSlice(frame.Chunks.Values, bounds, axis, Math.Clamp(sliceIndex, minimum, maximum), previous);
    }

    /// <summary>
    ///     Cuts one plane through a single chunk.
    /// </summary>
    /// <param name="frame">Frame that holds the chunk's presentation data.</param>
    /// <param name="chunkIdentity">Chunk to slice. Its generation must match the frame's chunk.</param>
    /// <param name="axis">Axis normal to the slice.</param>
    /// <param name="sliceIndex">Local index along <paramref name="axis" />, clamped into the chunk.</param>
    /// <returns>
    ///     A slice that covers only the chunk, including filtered cells for picking. Its coordinates start at the chunk's
    ///     corner.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="frame" /> is <see langword="null" />.</exception>
    /// <exception cref="KeyNotFoundException">
    ///     The frame has no chunk with <paramref name="chunkIdentity" />.
    /// </exception>
    public SimulationSliceDrawData BuildChunkSlice(
        SimulationDrawData frame,
        ChunkIdentity chunkIdentity,
        SliceAxis axis,
        int sliceIndex)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!frame.Chunks.TryGetValue(chunkIdentity.Position, out var chunk) || chunk.Identity != chunkIdentity)
            throw new KeyNotFoundException($"Chunk {chunkIdentity.Position} is not present in this frame.");

        var bounds = VoxelBounds.Enclosing([chunk]);
        int clampedIndex = ClampSliceIndex(chunk.Dimensions, axis, sliceIndex);
        return BuildSlice([chunk], bounds, axis, bounds.GetMinimum(axis) + clampedIndex, null);
    }

    private static SimulationSliceDrawData BuildSlice(
        IEnumerable<ChunkDrawData> source,
        VoxelBounds bounds,
        SliceAxis axis,
        int index,
        SimulationSliceDrawData? previous)
    {
        // The plane assumes one chunk size. The lowest chunk in position order sets it, so the choice does not
        // depend on dictionary order.
        ChunkDrawData? reference = null;
        foreach (var chunk in source)
        {
            if (chunk.CellCount > 0 && (reference == null || ComparePositions(chunk, reference) < 0))
                reference = chunk;
        }

        var dimensions = reference?.Dimensions ?? default;
        var candidates = new List<ChunkDrawData>();
        foreach (var chunk in source)
        {
            if (chunk.CellCount == 0 || chunk.Dimensions != dimensions)
                continue;

            int length = GetSliceAxisLength(dimensions, axis);
            int start = GetAxisComponent(chunk.ChunkPosition, axis) * length;
            if (index >= start && index < start + length)
                candidates.Add(chunk);
        }

        candidates.Sort(ComparePositions);

        Int3 size = bounds.Size;
        (int width, int height) = GetSliceDimensions(size, axis);
        (int originU, int originV) = SimulationSliceDrawData.Project(
            axis,
            bounds.Minimum.X,
            bounds.Minimum.Y,
            bounds.Minimum.Z);

        var layout = new StableHash64();
        layout.Add((int)axis);
        layout.Add(index);
        layout.Add(originU);
        layout.Add(originV);
        layout.Add(width);
        layout.Add(height);
        layout.Add(dimensions.X);
        layout.Add(dimensions.Y);
        layout.Add(dimensions.Z);
        foreach (var chunk in candidates)
        {
            layout.Add(chunk.ChunkPosition.X);
            layout.Add(chunk.ChunkPosition.Y);
            layout.Add(chunk.ChunkPosition.Z);
        }

        var render = layout;
        foreach (var chunk in candidates)
        {
            render.Add(chunk.Identity.Generation);
            render.Add(chunk.TopologyVersion);
            render.Add(chunk.StyleVersion);
        }

        ulong layoutVersion = layout.Value;
        ulong renderVersion = render.Value;
        bool reuseLayout = previous != null && previous.LayoutVersion == layoutVersion;
        bool reusePixels = reuseLayout && previous!.RenderVersion == renderVersion;

        int cellCount = checked(width * height);
        var table = candidates.ToArray();
        var rects = new SliceChunkRect[table.Length];
        var origins = new (int U, int V)[table.Length];
        for (int slot = 0; slot < table.Length; slot++)
        {
            var chunk = table[slot];
            (int chunkU, int chunkV) = SimulationSliceDrawData.Project(
                axis,
                chunk.ChunkPosition.X * dimensions.X,
                chunk.ChunkPosition.Y * dimensions.Y,
                chunk.ChunkPosition.Z * dimensions.Z);

            (int extentU, int extentV) = SimulationSliceDrawData.Project(
                axis,
                dimensions.X,
                dimensions.Y,
                dimensions.Z);

            origins[slot] = (chunkU - originU, chunkV - originV);
            rects[slot] = new SliceChunkRect(chunk.Identity, chunkU - originU, chunkV - originV, extentU, extentV);
        }

        int[] cellChunk;
        ushort[] cellLocal;
        byte[] pixels;
        int visibleCount;
        if (reusePixels)
        {
            cellChunk = previous!.CellChunkArray;
            cellLocal = previous.CellLocalArray;
            pixels = previous.PixelArray;
            visibleCount = previous.VisibleCellCount;
        }
        else
        {
            cellChunk = reuseLayout ? previous!.CellChunkArray : new int[cellCount];
            cellLocal = reuseLayout ? previous!.CellLocalArray : new ushort[cellCount];
            pixels = new byte[checked(cellCount * 4)];
            if (!reuseLayout)
                Array.Fill(cellChunk, -1);

            var counts = new int[table.Length];
            void FillSlot(int slot)
            {
                counts[slot] = FillChunkPlane(
                    table[slot],
                    axis,
                    index,
                    width,
                    origins[slot].U,
                    origins[slot].V,
                    slot,
                    reuseLayout ? null : cellChunk,
                    reuseLayout ? null : cellLocal,
                    pixels);
            }

            if (table.Length >= 16)
                Parallel.For(0, table.Length, FillSlot);
            else
            {
                for (int slot = 0; slot < table.Length; slot++)
                    FillSlot(slot);
            }

            visibleCount = 0;
            foreach (int count in counts)
                visibleCount += count;
        }

        return new SimulationSliceDrawData(
            axis,
            index,
            originU,
            originV,
            width,
            height,
            dimensions,
            table,
            rects,
            cellChunk,
            cellLocal,
            pixels,
            visibleCount,
            layoutVersion,
            renderVersion);
    }

    private static int ComparePositions(ChunkDrawData left, ChunkDrawData right)
    {
        int byZ = left.ChunkPosition.Z.CompareTo(right.ChunkPosition.Z);
        if (byZ != 0)
            return byZ;

        int byY = left.ChunkPosition.Y.CompareTo(right.ChunkPosition.Y);
        return byY != 0 ? byY : left.ChunkPosition.X.CompareTo(right.ChunkPosition.X);
    }

    private static int FillChunkPlane(
        ChunkDrawData chunk,
        SliceAxis axis,
        int index,
        int planeWidth,
        int offsetU,
        int offsetV,
        int slot,
        int[]? cellChunk,
        ushort[]? cellLocal,
        byte[] pixels)
    {
        var dimensions = chunk.Dimensions;
        int planeArea = dimensions.X * dimensions.Y;
        int local = index - GetAxisComponent(chunk.ChunkPosition, axis) * GetSliceAxisLength(dimensions, axis);
        (int extentU, int extentV, int strideU, int strideV, int origin) = axis switch
        {
            SliceAxis.X => (dimensions.Z, dimensions.Y, planeArea, dimensions.X, local),
            SliceAxis.Y => (dimensions.X, dimensions.Z, 1, planeArea, local * dimensions.X),
            _ => (dimensions.X, dimensions.Y, 1, dimensions.X, local * planeArea)
        };

        ReadOnlySpan<VoxelDrawData> cells = chunk.Cells;
        int visible = 0;
        for (int v = 0; v < extentV; v++)
        {
            int dense = (offsetV + v) * planeWidth + offsetU;
            int row = origin + v * strideV;
            for (int u = 0; u < extentU; u++)
            {
                int localIndex = row + u * strideU;
                if (cellChunk != null)
                {
                    cellChunk[dense + u] = slot;
                    cellLocal![dense + u] = (ushort)localIndex;
                }

                ref readonly var cell = ref cells[localIndex];
                if (!cell.IsVisible)
                    continue;

                (byte r, byte g, byte b, _) = cell.Color.ToRgba8();
                int offset = 4 * (dense + u);
                pixels[offset] = r;
                pixels[offset + 1] = g;
                pixels[offset + 2] = b;
                pixels[offset + 3] = byte.MaxValue;
                visible++;
            }
        }

        return visible;
    }

    private static int GetAxisComponent(Int3 position, SliceAxis axis)
    {
        return axis switch
        {
            SliceAxis.X => position.X,
            SliceAxis.Y => position.Y,
            _ => position.Z
        };
    }

    /// <summary>
    ///     Gets the number of slices a chunk has along <paramref name="axis" />.
    /// </summary>
    /// <param name="dimensions">Chunk dimensions.</param>
    /// <param name="axis">Slice axis.</param>
    /// <returns>The component of <paramref name="dimensions" /> along <paramref name="axis" />.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="axis" /> is not a defined axis.</exception>
    public static int GetSliceAxisLength(Int3 dimensions, SliceAxis axis)
    {
        return axis switch
        {
            SliceAxis.X => dimensions.X,
            SliceAxis.Y => dimensions.Y,
            SliceAxis.Z => dimensions.Z,
            _ => throw new ArgumentOutOfRangeException(nameof(axis))
        };
    }

    private static ChunkDrawData BuildChunk(
        AtmosChunkSnapshot snapshot,
        ChunkIdentity identity,
        IVisualizationMethod visualization,
        ulong visualizationMappingRevision,
        VisualizationRange range,
        FlowFieldSet? flowFields)
    {
        int voxelCount = ValidateSnapshot(snapshot, visualization.RequiredData);
        if (voxelCount == 0)
        {
            return new ChunkDrawData(
                identity,
                snapshot.Dimensions,
                snapshot.Version.Revision,
                visualization.Id,
                visualizationMappingRevision,
                [],
                0,
                0,
                0,
                0);
        }

        var cells = new VoxelDrawData[voxelCount];
        var topologyHash = new StableHash64();
        var styleHash = new StableHash64();
        topologyHash.Add(identity.Generation);
        topologyHash.Add(snapshot.Dimensions.X);
        topologyHash.Add(snapshot.Dimensions.Y);
        topologyHash.Add(snapshot.Dimensions.Z);
        styleHash.Add(visualization.Id);

        int visibleCount = 0;
        bool summarizeGases = (visualization.RequiredData & VisualizationDataRequirements.Gases) != 0;
        bool hasVelocity = HasCompleteVelocity(snapshot, out _);
        float[]? densities = null;
        float[]? gradients = null;
        flowFields?.TryGetDensity(snapshot.GridPosition, out densities);
        flowFields?.TryGetGradient(snapshot.GridPosition, out gradients);
        for (int index = 0; index < voxelCount; index++)
        {
            ushort localIndex = checked((ushort)index);
            int roomId = snapshot.VoxelRoomMap[index];
            bool canContainAir = roomId != VoxelClassification.RoomVoid &&
                                 roomId != VoxelClassification.RoomSolid;

            bool isInVisualizationDomain = canContainAir ||
                                           visualization.CellDomain == VisualizationCellDomain.AllCells;

            (float totalMoles, int primaryGasId) = summarizeGases
                ? GetGasSummary(snapshot.Gases, localIndex)
                : (0f, -1);

            float temperature = snapshot.Temperature.Length == voxelCount
                ? snapshot.Temperature[index]
                : float.NaN;

            float pressure = snapshot.TotalPressure.Length == voxelCount
                ? snapshot.TotalPressure[index]
                : float.NaN;

            float velocityX = hasVelocity ? snapshot.VelocityX[index] : float.NaN;
            float velocityY = hasVelocity ? snapshot.VelocityY[index] : float.NaN;
            float velocityZ = hasVelocity ? snapshot.VelocityZ[index] : float.NaN;

            var sample = new VoxelSample(
                localIndex,
                roomId,
                temperature,
                pressure,
                velocityX,
                velocityY,
                velocityZ,
                totalMoles,
                primaryGasId,
                new VoxelGasData(snapshot.Gases, localIndex))
            {
                Range = range,
                Density = densities != null ? densities[index] : float.NaN,
                DensityGradient = gradients != null ? gradients[index] : float.NaN
            };

            var color = default(ColorRgba);
            bool visible = isInVisualizationDomain && visualization.TryGetColor(sample, out color);
            if (visible)
                color = ToOpaqueFiniteColor(color);

            cells[index] = new VoxelDrawData(
                visible,
                VoxelFaceMask.None,
                sample.Temperature,
                sample.Pressure,
                sample.VelocityX,
                sample.VelocityY,
                sample.VelocityZ,
                sample.TotalMoles,
                sample.PrimaryGasId,
                sample.RoomId,
                visible ? color : default);

            if (visible)
            {
                visibleCount++;
                styleHash.Add(PackColor(color));
            }
            else
                styleHash.Add(0u);
        }

        int surfaceFaceCount = AddVisibleFaceMasks(cells, snapshot.Dimensions, ref topologyHash);

        return new ChunkDrawData(
            identity,
            snapshot.Dimensions,
            snapshot.Version.Revision,
            visualization.Id,
            visualizationMappingRevision,
            cells,
            topologyHash.Value,
            styleHash.Value,
            visibleCount,
            surfaceFaceCount);
    }

    private static int AddVisibleFaceMasks(
        VoxelDrawData[] cells,
        Int3 dimensions,
        ref StableHash64 topologyHash)
    {
        // Chunk-boundary faces stay exposed. Each retained chunk is then self-contained and can be focused or
        // hidden without remeshing its neighbors. This method removes only intra-chunk faces.
        // A future seam-occlusion pass can suppress boundary overdraw at draw time.
        int surfaceFaceCount = 0;
        for (int index = 0; index < cells.Length; index++)
        {
            if (!cells[index].IsVisible)
            {
                topologyHash.Add(0u);
                continue;
            }

            int x = index % dimensions.X;
            int yz = index / dimensions.X;
            int y = yz % dimensions.Y;
            int z = yz / dimensions.Y;
            var mask = VoxelFaceMask.None;

            if (x == 0 || !cells[index - 1].IsVisible)
                mask |= VoxelFaceMask.NegativeX;

            if (x == dimensions.X - 1 || !cells[index + 1].IsVisible)
                mask |= VoxelFaceMask.PositiveX;

            if (y == 0 || !cells[index - dimensions.X].IsVisible)
                mask |= VoxelFaceMask.NegativeY;

            if (y == dimensions.Y - 1 || !cells[index + dimensions.X].IsVisible)
                mask |= VoxelFaceMask.PositiveY;

            int xyStride = dimensions.X * dimensions.Y;
            if (z == 0 || !cells[index - xyStride].IsVisible)
                mask |= VoxelFaceMask.NegativeZ;

            if (z == dimensions.Z - 1 || !cells[index + xyStride].IsVisible)
                mask |= VoxelFaceMask.PositiveZ;

            cells[index] = cells[index] with { VisibleFaces = mask };
            int faceCount = CountFaces(mask);
            surfaceFaceCount += faceCount;
            topologyHash.Add(VisibleTopologyFlag | (byte)mask);
        }

        return surfaceFaceCount;
    }

    private const uint VisibleTopologyFlag = 1u << 8;
    private const uint VisibleStyleFlag = 1u << 24;

    /// <summary>
    ///     Packs a color into 8-bit channels plus a visible flag, so the style hash needs one step per voxel. A color
    ///     change that stays below one 8-bit step cannot change a rendered pixel, so the hash may ignore it.
    /// </summary>
    private static uint PackColor(ColorRgba color)
    {
        (byte r, byte g, byte b, _) = color.ToRgba8();
        return VisibleStyleFlag | (uint)r << 16 | (uint)g << 8 | b;
    }

    private static bool CanReuse(
        SimulationDrawData? previousFrame,
        string visualizationId,
        ulong visualizationMappingRevision,
        AtmosChunkSnapshot snapshot,
        ChunkIdentity identity,
        out ChunkDrawData chunk)
    {
        if (snapshot.Version != default &&
            previousFrame != null &&
            previousFrame.Chunks.TryGetValue(snapshot.GridPosition, out chunk!) &&
            chunk.Identity == identity &&
            string.Equals(chunk.VisualizationId, visualizationId, StringComparison.OrdinalIgnoreCase) &&
            chunk.VisualizationMappingRevision == visualizationMappingRevision &&
            chunk.SourceRevision == snapshot.Version.Revision)
        {
            return true;
        }

        chunk = null!;
        return false;
    }

    private static bool NeighborsUnchanged(
        SimulationDrawData previousFrame,
        Dictionary<Int3, AtmosChunkSnapshot> snapshots,
        Int3 position)
    {
        ReadOnlySpan<Int3> offsets =
        [
            new(-1, 0, 0), new(1, 0, 0), new(0, -1, 0), new(0, 1, 0), new(0, 0, -1), new(0, 0, 1)
        ];

        foreach (var offset in offsets)
        {
            var neighbor = new Int3(position.X + offset.X, position.Y + offset.Y, position.Z + offset.Z);
            bool hasCurrent = snapshots.TryGetValue(neighbor, out var current);
            bool hasPrevious = previousFrame.Chunks.TryGetValue(neighbor, out var previous);
            if (hasCurrent != hasPrevious)
                return false;

            if (!hasCurrent)
                continue;

            if (current.Version == default ||
                previous!.Identity != new ChunkIdentity(neighbor, current.Version.Generation) ||
                previous.SourceRevision != current.Version.Revision)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryRetainChunk(
        SimulationDrawData? previousFrame,
        Int3 position,
        ChunkIdentity identity,
        out ChunkDrawData chunk)
    {
        if (previousFrame != null &&
            previousFrame.Chunks.TryGetValue(position, out chunk!) &&
            chunk.Identity == identity)
        {
            return true;
        }

        chunk = null!;
        return false;
    }

    private static ColorRgba ToOpaqueFiniteColor(ColorRgba color)
    {
        static float Channel(float value)
        {
            return FloatMath.ClampUnitInterval(value);
        }

        return new ColorRgba(Channel(color.R), Channel(color.G), Channel(color.B));
    }

    private static int ValidateSnapshot(
        AtmosChunkSnapshot snapshot,
        VisualizationDataRequirements requirements)
    {
        if (!snapshot.IsSnapshotValid)
            throw new ArgumentException("Snapshot metadata and detached arrays are not initialized.", nameof(snapshot));

        if (snapshot.Dimensions.X <= 0 || snapshot.Dimensions.Y <= 0 || snapshot.Dimensions.Z <= 0)
            throw new ArgumentException("Snapshot dimensions must be positive.", nameof(snapshot));

        int voxelCount = checked(snapshot.Dimensions.X * snapshot.Dimensions.Y * snapshot.Dimensions.Z);
        if (voxelCount > ushort.MaxValue)
        {
            throw new ArgumentException(
                "Snapshot voxel count exceeds the supported ushort index range.",
                nameof(snapshot));
        }

        if (snapshot.VoxelRoomMap == null ||
            snapshot.VoxelRoomMap.Length != voxelCount)
        {
            throw new ArgumentException("Snapshot classifications do not match its dimensions.", nameof(snapshot));
        }

        if ((requirements & VisualizationDataRequirements.Pressure) != 0 &&
            snapshot.TotalPressure.Length != voxelCount)
            throw new ArgumentException("The visualization requires a complete pressure field.", nameof(snapshot));

        if ((requirements & VisualizationDataRequirements.Temperature) != 0 &&
            snapshot.Temperature.Length != voxelCount)
            throw new ArgumentException("The visualization requires a complete temperature field.", nameof(snapshot));

        if ((requirements & VisualizationDataRequirements.Velocity) != 0 &&
            !HasCompleteVelocity(snapshot, out _))
            throw new ArgumentException("The visualization requires complete velocity fields.", nameof(snapshot));

        if (snapshot.Gases == null)
            throw new ArgumentException("Snapshot gas channels are not initialized.", nameof(snapshot));

        if ((requirements & (VisualizationDataRequirements.Gases | FlowFieldRequirements)) == 0)
            return voxelCount;

        foreach (var gas in snapshot.Gases)
        {
            if (gas.Moles == null || gas.Moles.Length != voxelCount)
                throw new ArgumentException("A snapshot gas channel does not match its dimensions.", nameof(snapshot));
        }

        return voxelCount;
    }

    private static void AddGasIds(AtmosChunkSnapshot snapshot, ISet<int> destination)
    {
        if (snapshot.Gases == null)
            return;

        foreach (var gas in snapshot.Gases)
            destination.Add(gas.GasId);
    }

    private static (float totalMoles, int primaryGasId) GetGasSummary(
        GasSnapshot[] gases,
        ushort localIndex)
    {
        float totalMoles = 0f;
        float maximumMoles = 0f;
        int primaryGasId = -1;

        foreach (var gas in gases)
        {
            float moles = gas.Moles[localIndex];
            if (float.IsFinite(moles) && moles > 0f)
                totalMoles += moles;

            if (moles > maximumMoles ||
                moles == maximumMoles && moles > 0f && (primaryGasId < 0 || gas.GasId < primaryGasId))
            {
                maximumMoles = moles;
                primaryGasId = gas.GasId;
            }
        }

        return (totalMoles, primaryGasId);
    }

    private static int ClampSliceIndex(Int3 dimensions, SliceAxis axis, int sliceIndex)
    {
        int maximum = Math.Max(GetSliceAxisLength(dimensions, axis) - 1, 0);
        return Math.Clamp(sliceIndex, 0, maximum);
    }

    /// <summary>
    ///     Gets the size of a slice in slice coordinates.
    /// </summary>
    /// <param name="dimensions">The size of the sliced box.</param>
    /// <param name="axis">The axis the slice is perpendicular to.</param>
    /// <returns>
    ///     The extent along u and v. An X slice has u along z and v along y. A Y slice has u along x and v along z.
    ///     A Z slice has u along x and v along y.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="axis" /> is not a defined axis.</exception>
    public static (int width, int height) GetSliceDimensions(Int3 dimensions, SliceAxis axis)
    {
        return axis switch
        {
            SliceAxis.X => (dimensions.Z, dimensions.Y),
            SliceAxis.Y => (dimensions.X, dimensions.Z),
            SliceAxis.Z => (dimensions.X, dimensions.Y),
            _ => throw new ArgumentOutOfRangeException(nameof(axis))
        };
    }

    /// <summary>
    ///     Maps slice coordinates to box coordinates.
    /// </summary>
    /// <param name="axis">The axis the slice is perpendicular to.</param>
    /// <param name="sliceIndex">The index along <paramref name="axis" />.</param>
    /// <param name="u">The first slice coordinate.</param>
    /// <param name="v">The second slice coordinate.</param>
    /// <returns>The x, y, and z coordinates. See <see cref="GetSliceDimensions" /> for the axis order.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="axis" /> is not a defined axis.</exception>
    public static (int x, int y, int z) MapSliceToLocal(
        SliceAxis axis,
        int sliceIndex,
        int u,
        int v)
    {
        return axis switch
        {
            SliceAxis.X => (sliceIndex, v, u),
            SliceAxis.Y => (u, sliceIndex, v),
            SliceAxis.Z => (u, v, sliceIndex),
            _ => throw new ArgumentOutOfRangeException(nameof(axis))
        };
    }

    private static int CountFaces(VoxelFaceMask mask)
    {
        int value = (byte)mask;
        int count = 0;
        while (value != 0)
        {
            count += value & 1;
            value >>= 1;
        }

        return count;
    }

    private struct StableHash64
    {
        private const ulong OffsetBasis = 14695981039346656037UL;
        private const ulong Prime = 1099511628211UL;
        private ulong _value;

        public ulong Value => _value == 0 ? OffsetBasis : _value;

        public void Add(bool value)
        {
            Add(value ? (byte)1 : (byte)0);
        }

        public void Add(byte value)
        {
            EnsureInitialized();
            _value ^= value;
            _value *= Prime;
        }

        public void Add(int value)
        {
            Add(unchecked((uint)value));
        }

        public void Add(long value)
        {
            Add(unchecked((ulong)value));
        }

        public void Add(ulong value)
        {
            Add((uint)value);
            Add((uint)(value >> 32));
        }

        // One xor and one multiply per 32-bit word. The hash only detects change, so it skips FNV's per-byte steps,
        // which cost about twenty dependent multiplies per voxel.
        public void Add(uint value)
        {
            EnsureInitialized();
            _value = (_value ^ value) * Prime;
        }

        public void Add(string value)
        {
            foreach (char character in value)
                Add(character);
        }

        public void Add(ColorRgba color)
        {
            Add(PackColor(color));
        }

        private void EnsureInitialized()
        {
            if (_value == 0)
                _value = OffsetBasis;
        }
    }
}