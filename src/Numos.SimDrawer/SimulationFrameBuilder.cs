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
    private long _nextFrameVersion;

    public SimulationFrameBuilder(AtmosConfig config, VisualizationRegistry? visualizations = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        Visualizations = visualizations ?? VisualizationRegistry.CreateDefault(config);
        if (Visualizations.Methods.Count == 0)
        {
            throw new ArgumentException("At least one visualization method must be registered.",
                nameof(visualizations));
        }
    }

    public VisualizationRegistry Visualizations { get; }

    /// <summary>
    ///     Returns the minimal detached snapshot fields needed to map one visualization.
    /// </summary>
    public AtmosChunkSnapshotFields GetRequiredSnapshotFields(string visualizationId)
    {
        var visualization = Visualizations.GetRequired(visualizationId);
        var fields = AtmosChunkSnapshotFields.VoxelClassification;
        if ((visualization.RequiredData & VisualizationDataRequirements.Temperature) != 0)
            fields |= AtmosChunkSnapshotFields.Temperature;
        if ((visualization.RequiredData & VisualizationDataRequirements.Pressure) != 0)
            fields |= AtmosChunkSnapshotFields.Pressure;
        if ((visualization.RequiredData & VisualizationDataRequirements.Gases) != 0)
            fields |= AtmosChunkSnapshotFields.Gases;
        return fields;
    }

    /// <summary>
    ///     Builds a complete presentation frame while reusing unchanged immutable chunks from a previous frame.
    ///     When <paramref name="mappingScope" /> is supplied, chunks outside that scope retain their
    ///     previous immutable mapping and incur no visualization work until the scope expands. An
    ///     out-of-scope chunk without a previous mapping is omitted until it enters a later scope.
    /// </summary>
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
        var snapshotList = snapshots as IReadOnlyCollection<AtmosChunkSnapshot> ?? snapshots.ToArray();
        var visualization = Visualizations.GetRequired(visualizationId);
        ulong visualizationMappingRevision = visualization.MappingRevision;
        var range = GetVisualizationRange(
            snapshotList,
            visualization.RequiredData,
            resolution,
            automaticRangeOffset,
            rangeOverride);
        bool rangeChanged = previousFrame == null || previousFrame.Visualization.Range != range;
        var chunks = new Dictionary<Int3, ChunkDrawData>();
        var seenPositions = new HashSet<Int3>();
        var activeGasIds = new HashSet<int>();

        foreach (var snapshot in snapshotList)
        {
            if (!seenPositions.Add(snapshot.GridPosition))
                throw new ArgumentException($"Duplicate chunk position {snapshot.GridPosition}.", nameof(snapshots));

            var identity = new ChunkIdentity(snapshot.GridPosition, snapshot.Version.Generation);
            bool shouldMap = mappingScope == null || mappingScope.Contains(snapshot.GridPosition);
            if (!shouldMap)
            {
                // Focused presentation is intentionally sparse in work, not in retained state.
                // Keep the last immutable mapping for hidden chunks without inspecting fields that
                // may not even have been copied for the newly selected visualization.
                if (TryRetainChunk(previousFrame, snapshot.GridPosition, identity, out var retainedChunk))
                    chunks.Add(snapshot.GridPosition, retainedChunk);
                continue;
            }

            AddGasIds(snapshot, activeGasIds);
            if (!forceRemap && !rangeChanged && CanReuse(
                    previousFrame,
                    visualizationId,
                    visualizationMappingRevision,
                    snapshot,
                    identity,
                    out var previousChunk))
            {
                chunks[snapshot.GridPosition] = previousChunk;
                continue;
            }

            chunks.Add(
                snapshot.GridPosition,
                BuildChunk(snapshot, identity, visualization, visualizationMappingRevision, range));
        }

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

    private static VisualizationRange GetVisualizationRange(
        IEnumerable<AtmosChunkSnapshot> snapshots,
        VisualizationDataRequirements requirements,
        int resolution,
        float automaticRangeOffset,
        VisualizationRange? rangeOverride)
    {
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

        float minimum = float.PositiveInfinity;
        float maximum = float.NegativeInfinity;
        foreach (var snapshot in snapshots)
        {
            if ((requirements & VisualizationDataRequirements.Temperature) != 0)
                IncludeFiniteRange(snapshot.Temperature, ref minimum, ref maximum);
            if ((requirements & VisualizationDataRequirements.Pressure) != 0)
                IncludeFiniteRange(snapshot.TotalPressure, ref minimum, ref maximum);
        }

        if (!float.IsFinite(minimum) || !float.IsFinite(maximum))
            return new VisualizationRange(0f, 1f, safeResolution);

        float offset = float.IsFinite(automaticRangeOffset)
            ? Math.Max(automaticRangeOffset, 0f)
            : 0f;
        return new VisualizationRange(minimum - offset, maximum + offset, safeResolution);
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

    /// <summary>
    ///     Projects one local slice from a focused chunk without reading the simulation again.
    /// </summary>
    public SimulationSliceDrawData BuildChunkSlice(
        SimulationDrawData frame,
        ChunkIdentity chunkIdentity,
        SliceAxis axis,
        int sliceIndex)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!frame.Chunks.TryGetValue(chunkIdentity.Position, out var chunk) || chunk.Identity != chunkIdentity)
            throw new KeyNotFoundException($"Chunk {chunkIdentity.Position} is not present in this frame.");

        int clampedIndex = ClampSliceIndex(chunk.Dimensions, axis, sliceIndex);
        (int width, int height) = GetSliceDimensions(chunk.Dimensions, axis);
        var cells = new List<SliceCellDrawData>(Math.Min(width * height, chunk.VisibleCellCount));
        var lookup = new int[checked(width * height)];

        for (var v = 0; v < height; v++)
        {
            for (var u = 0; u < width; u++)
            {
                (int x, int y, int z) = MapSliceToLocal(axis, clampedIndex, u, v);
                ushort localIndex = chunk.GetLocalIndex(x, y, z);
                ref readonly var voxel = ref chunk.GetCell(localIndex);
                if (!voxel.IsVisible)
                    continue;

                var sliceCell = new SliceCellDrawData(
                    u,
                    v,
                    new VoxelAddress(chunk.Identity, localIndex),
                    voxel);
                cells.Add(sliceCell);
                lookup[u + width * v] = cells.Count;
            }
        }

        var hash = new StableHash64();
        hash.Add(chunk.TopologyVersion);
        hash.Add(chunk.StyleVersion);
        hash.Add((int)axis);
        hash.Add(clampedIndex);

        return new SimulationSliceDrawData(
            chunk.Identity,
            axis,
            clampedIndex,
            width,
            height,
            cells.ToArray(),
            lookup,
            hash.Value);
    }

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
        VisualizationRange range)
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

        var visibleCount = 0;
        bool summarizeGases = (visualization.RequiredData & VisualizationDataRequirements.Gases) != 0;
        for (var index = 0; index < voxelCount; index++)
        {
            var localIndex = checked((ushort)index);
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
            var sample = new VoxelSample(
                localIndex,
                roomId,
                temperature,
                pressure,
                totalMoles,
                primaryGasId,
                new VoxelGasData(snapshot.Gases, localIndex));
            sample = sample with { Range = range };

            var color = default(ColorRgba);
            bool visible = isInVisualizationDomain && visualization.TryGetColor(sample, out color);
            if (visible)
                color = ToOpaqueFiniteColor(color);
            cells[index] = new VoxelDrawData(
                visible,
                VoxelFaceMask.None,
                sample.Temperature,
                sample.Pressure,
                sample.TotalMoles,
                sample.PrimaryGasId,
                sample.RoomId,
                visible ? color : default);

            topologyHash.Add(visible);
            if (visible)
            {
                visibleCount++;
                styleHash.Add(index);
                styleHash.Add(color);
            }
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
        // Chunk-boundary faces intentionally remain exposed. Each retained chunk is therefore
        // self-contained and can be focused or hidden without remeshing its neighbors. Only
        // intra-chunk faces are removed here; a future seam-occlusion pass can suppress boundary
        // overdraw at draw time without changing this focus contract.
        var surfaceFaceCount = 0;
        for (var index = 0; index < cells.Length; index++)
        {
            if (!cells[index].IsVisible)
                continue;

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
            topologyHash.Add(index);
            topologyHash.Add((byte)mask);
        }

        return surfaceFaceCount;
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
            return float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 0f;
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
            throw new ArgumentException("Snapshot voxel count exceeds the supported ushort index range.",
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

        if (snapshot.Gases == null)
            throw new ArgumentException("Snapshot gas channels are not initialized.", nameof(snapshot));

        if ((requirements & VisualizationDataRequirements.Gases) == 0)
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
        var totalMoles = 0f;
        var maximumMoles = 0f;
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

    private static (int width, int height) GetSliceDimensions(Int3 dimensions, SliceAxis axis)
    {
        return axis switch
        {
            SliceAxis.X => (dimensions.Z, dimensions.Y),
            SliceAxis.Y => (dimensions.X, dimensions.Z),
            SliceAxis.Z => (dimensions.X, dimensions.Y),
            _ => throw new ArgumentOutOfRangeException(nameof(axis))
        };
    }

    private static (int x, int y, int z) MapSliceToLocal(
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
        var count = 0;
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
            EnsureInitialized();
            for (var shift = 0; shift < 64; shift += 8)
            {
                _value ^= (byte)(value >> shift);
                _value *= Prime;
            }
        }

        public void Add(uint value)
        {
            EnsureInitialized();
            for (var shift = 0; shift < 32; shift += 8)
            {
                _value ^= (byte)(value >> shift);
                _value *= Prime;
            }
        }

        public void Add(string value)
        {
            foreach (char character in value)
                Add(character);
        }

        public void Add(ColorRgba color)
        {
            Add(BitConverter.SingleToUInt32Bits(color.R));
            Add(BitConverter.SingleToUInt32Bits(color.G));
            Add(BitConverter.SingleToUInt32Bits(color.B));
            Add(BitConverter.SingleToUInt32Bits(color.A));
        }

        private void EnsureInitialized()
        {
            if (_value == 0)
                _value = OffsetBasis;
        }
    }
}