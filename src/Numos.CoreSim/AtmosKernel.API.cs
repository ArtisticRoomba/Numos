using System.Buffers;
using System.Diagnostics;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.CoreSim.Solvers;
using Numos.Maths;

namespace Numos.CoreSim;

internal sealed partial class AtmosKernel
{
    /// <summary>
    ///     Gets the number of chunks currently registered with the kernel.
    /// </summary>
    /// <remarks>The count includes both awake and sleeping chunks.</remarks>
    internal int ChunkCount
    {
        get
        {
            lock (_stateGate)
            {
                return _chunkMap.Count;
            }
        }
    }

    /// <summary>
    ///     Returns a detached description of the currently configured solver pipeline.
    /// </summary>
    internal SolverStepInfo[] GetSolverSteps()
    {
        lock (_stateGate)
        {
            return _solverPipeline.GetSteps();
        }
    }

    internal void RegisterSolver(string name, SolverStepKind kind,
        Action<AtmosSolverExecutionContext> solver)
    {
        lock (_stateGate)
        {
            _solverPipeline.Register(name, kind, solver, _solverPipeline.Count);
            _solverPipelineInvalidationPending = true;
        }
    }

    internal void RegisterSolverBefore(string existingName, string name, SolverStepKind kind,
        Action<AtmosSolverExecutionContext> solver)
    {
        lock (_stateGate)
        {
            int index = _solverPipeline.IndexOf(existingName);
            if (index < 0)
                throw new KeyNotFoundException($"No solver named '{existingName}' is registered.");
            _solverPipeline.Register(name, kind, solver, index);
            _solverPipelineInvalidationPending = true;
        }
    }

    internal void RegisterSolverAfter(string existingName, string name, SolverStepKind kind,
        Action<AtmosSolverExecutionContext> solver)
    {
        lock (_stateGate)
        {
            int index = _solverPipeline.IndexOf(existingName);
            if (index < 0)
                throw new KeyNotFoundException($"No solver named '{existingName}' is registered.");
            _solverPipeline.Register(name, kind, solver, index + 1);
            _solverPipelineInvalidationPending = true;
        }
    }

    internal bool UnregisterSolver(string name)
    {
        lock (_stateGate)
        {
            return _solverPipeline.Unregister(name);
        }
    }

    internal bool SetSolverEnabled(string name, bool enabled)
    {
        lock (_stateGate)
        {
            bool found = _solverPipeline.SetEnabled(name, enabled, out bool becameEnabled);
            _solverPipelineInvalidationPending |= becameEnabled;
            return found;
        }
    }

    internal void ResetSolverPipeline()
    {
        lock (_stateGate)
        {
            _solverPipelineInvalidationPending |= _solverPipeline.Reset();
        }
    }

    /// <summary>
    ///     Returns a detached list of the currently registered chunk-grid positions.
    /// </summary>
    internal Int3[] GetChunkPositions()
    {
        lock (_stateGate)
        {
            return _chunkMap.Keys.ToArray();
        }
    }

    /// <summary>
    ///     Returns registered positions only when the chunk collection changed.
    /// </summary>
    internal bool TryGetChunkPositions(
        long knownRevision,
        out long revision,
        out Int3[] positions)
    {
        lock (_stateGate)
        {
            revision = _chunkCollectionRevision;
            if (revision == knownRevision)
            {
                positions = [];
                return false;
            }

            positions = _chunkMap.Keys.ToArray();
            return true;
        }
    }

    /// <summary>
    ///     Updates the simulation.
    /// </summary>
    /// <param name="elapsedSeconds">Elapsed real time, in seconds, since the previous update.</param>
    /// <remarks>
    ///     The kernel runs at <see cref="AtmosSolverConstants.SimulationRate" />. At most
    ///     <see cref="AtmosSolverConstants.MaximumStepsPerUpdate" /> ticks are processed by one call; excess
    ///     accumulated time is discarded to prevent an unbounded catch-up loop. Values smaller than one fixed
    ///     step remain in the accumulator for a later call.
    /// </remarks>
    internal void Update(float elapsedSeconds)
    {
        lock (_stateGate)
        {
            ThrowIfTickExecuting("update the simulation recursively");
            _accumulator += elapsedSeconds;

            if (_accumulator > AtmosSolverConstants.FixedTimeStep * AtmosSolverConstants.MaximumStepsPerUpdate)
            {
                _accumulator =
                    AtmosSolverConstants.FixedTimeStep * AtmosSolverConstants.MaximumStepsPerUpdate;
            }

            LastBoundaryTicks = 0;

            // One elapsed-time update is one externally atomic batch. Solver callbacks may edit the pipeline, but
            // chunk lifecycle changes are rejected until the batch completes.
            var chunks = _chunkMap.Values.ToArray();
            var steps = 0;
            while (_accumulator >= AtmosSolverConstants.FixedTimeStep &&
                   steps < AtmosSolverConstants.MaximumStepsPerUpdate)
            {
                _accumulator -= AtmosSolverConstants.FixedTimeStep;
                steps++;
                TickSimulation(chunks);
            }
        }
    }

    /// <summary>Rejects public operations that would begin another tick from a running solver callback.</summary>
    internal void EnsureCanExecuteTick()
    {
        lock (_stateGate)
        {
            ThrowIfTickExecuting("update the simulation recursively");
        }
    }

    /// <summary>
    ///     Replaces the live configuration used by subsequent simulation ticks.
    /// </summary>
    /// <param name="config">The configuration instance to use. The instance is retained by reference.</param>
    /// <exception cref="ArgumentNullException"><paramref name="config" /> is <see langword="null" />.</exception>
    internal void SetAtmosConfig(AtmosConfig config)
    {
        lock (_stateGate)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }
    }

    /// <summary>
    ///     Registers an initialized chunk at its grid position.
    /// </summary>
    /// <param name="chunk">The chunk whose lifetime becomes owned by this kernel.</param>
    /// <exception cref="InvalidOperationException">
    ///     Another chunk is already registered at <paramref name="chunk" />'s grid position.
    /// </exception>
    internal void RegisterChunk(AtmosChunk chunk)
    {
        lock (_stateGate)
        {
            if (_chunkMap.ContainsKey(chunk.GridPosition))
                throw new InvalidOperationException($"A chunk is already registered at {chunk.GridPosition}.");

            Dictionary<AtmosChunk, SortedSet<ushort>> wakePlan = CreateBoundaryWakePlan(chunk);
            ValidateBoundaryWakePlan(wakePlan);
            bool added = _chunkMap.TryAdd(chunk.GridPosition, chunk);
            Debug.Assert(added);
            _chunkCollectionRevision++;
            ApplyBoundaryWakePlan(wakePlan);
        }
    }

    /// <summary>
    ///     Removes and releases the chunk at a grid position.
    /// </summary>
    /// <param name="position">The chunk-grid position to remove.</param>
    /// <returns><see langword="true" /> if a chunk was removed; otherwise, <see langword="false" />.</returns>
    internal bool UnregisterChunk(Int3 position)
    {
        lock (_stateGate)
        {
            ThrowIfTickExecuting("unregister a chunk used by the current tick");
            if (!_chunkMap.TryRemove(position, out var chunk))
                return false;

            chunk.Release();
            _chunkCollectionRevision++;
            return true;
        }
    }

    /// <summary>
    ///     Creates, initializes, and registers a chunk owned by this kernel.
    /// </summary>
    /// <param name="position">The chunk's position in the chunk grid.</param>
    /// <param name="width">The number of voxels along the local x-axis.</param>
    /// <param name="height">The number of voxels along the local y-axis.</param>
    /// <param name="depth">The number of voxels along the local z-axis.</param>
    /// <param name="maxActiveRooms">The maximum number of room IDs that may be active simultaneously.</param>
    /// <exception cref="InvalidOperationException">A chunk is already registered at <paramref name="position" />.</exception>
    internal void CreateAndRegisterChunk(Int3 position, int width, int height, int depth, int maxActiveRooms,
        int initialClassification = VoxelClassification.RoomUnassigned)
    {
        lock (_stateGate)
        {
            ThrowIfTickExecuting("register a chunk during the current tick");
            var chunk = new AtmosChunk(width, height, depth, maxActiveRooms);
            chunk.Initialize(position, width, height, depth, maxActiveRooms);
            chunk.VoxelRoomMap.Fill(initialClassification);
            RegisterChunk(chunk);
        }
    }

    /// <summary>
    ///     Creates a detached snapshot of the chunk at a grid position.
    /// </summary>
    /// <param name="position">The chunk-grid position to inspect.</param>
    /// <returns>Copies of the chunk's pressure, temperature, gas, and voxel-classification data.</returns>
    /// <exception cref="KeyNotFoundException">No chunk is registered at <paramref name="position" />.</exception>
    internal AtmosChunkSnapshot GetChunkSnapshot(Int3 position)
    {
        lock (_stateGate)
        {
            return GetChunk(position).GetNetworkSnapshot();
        }
    }

    /// <summary>
    ///     Captures detached interaction details for one voxel without cloning whole chunk fields.
    /// </summary>
    internal AtmosVoxelSnapshot GetVoxelSnapshot(Int3 position, ushort localVoxelIndex)
    {
        lock (_stateGate)
        {
            var chunk = GetChunk(position);
            return CreateVoxelSnapshot(chunk, position, localVoxelIndex);
        }
    }

    /// <summary>
    ///     Captures one voxel only when the chunk is still at the exact presentation version.
    /// </summary>
    internal bool TryGetVoxelSnapshot(
        Int3 position,
        ushort localVoxelIndex,
        AtmosChunkVersion expectedVersion,
        out AtmosVoxelSnapshot snapshot)
    {
        lock (_stateGate)
        {
            var chunk = GetChunk(position);
            if (chunk.Version != expectedVersion)
            {
                snapshot = default;
                return false;
            }

            snapshot = CreateVoxelSnapshot(chunk, position, localVoxelIndex);
            return true;
        }
    }

    private static AtmosVoxelSnapshot CreateVoxelSnapshot(
        AtmosChunk chunk,
        Int3 position,
        ushort localVoxelIndex)
    {
        ValidateVoxelIndex(chunk, localVoxelIndex);
        var gases = new VoxelGasSnapshot[chunk.ActiveGasCount];
        for (var gas = 0; gas < gases.Length; gas++)
        {
            gases[gas] = new VoxelGasSnapshot(
                chunk.ActiveGases[gas].GasId,
                chunk.ActiveGases[gas].Moles[localVoxelIndex]);
        }

        return new AtmosVoxelSnapshot(
            chunk.Version,
            position,
            localVoxelIndex,
            chunk.VoxelRoomMap[localVoxelIndex],
            chunk.TotalPressure[localVoxelIndex],
            chunk.Temperature[localVoxelIndex],
            gases);
    }

    /// <summary>
    ///     Creates a detached snapshot only when the chunk differs from a known version.
    /// </summary>
    internal bool TryGetChunkSnapshot(
        Int3 position,
        AtmosChunkVersion knownVersion,
        out AtmosChunkSnapshot snapshot)
    {
        return TryGetChunkSnapshot(
            position,
            knownVersion,
            AtmosChunkSnapshotFields.All,
            out snapshot);
    }

    /// <summary>
    ///     Creates selected detached fields only when the chunk differs from a known version.
    /// </summary>
    internal bool TryGetChunkSnapshot(
        Int3 position,
        AtmosChunkVersion knownVersion,
        AtmosChunkSnapshotFields fields,
        out AtmosChunkSnapshot snapshot)
    {
        if ((fields & ~AtmosChunkSnapshotFields.All) != 0)
            throw new ArgumentOutOfRangeException(nameof(fields));

        lock (_stateGate)
        {
            var chunk = GetChunk(position);
            if (chunk.Version == knownVersion)
            {
                snapshot = default;
                return false;
            }

            snapshot = chunk.GetNetworkSnapshot(fields);
            return true;
        }
    }

    /// <summary>
    ///     Captures every changed request from one coherent simulation state.
    /// </summary>
    internal AtmosChunkSnapshotBatch GetChangedChunkSnapshots(
        IReadOnlyList<AtmosChunkSnapshotRequest> requests)
    {
        lock (_stateGate)
        {
            var positions = new HashSet<Int3>();
            for (var index = 0; index < requests.Count; index++)
            {
                var request = requests[index];
                if ((request.Fields & ~AtmosChunkSnapshotFields.All) != 0)
                    throw new ArgumentOutOfRangeException(nameof(requests), "A request contains invalid fields.");
                if (!positions.Add(request.Position))
                {
                    throw new ArgumentException($"Chunk {request.Position} was requested more than once.",
                        nameof(requests));
                }
            }

            var changed = new List<AtmosChunkSnapshot>(requests.Count);
            for (var index = 0; index < requests.Count; index++)
            {
                var request = requests[index];
                // Handle lists are detached. A concurrent unregistration between enumeration
                // and this batch is represented by the chunk simply not being returned.
                if (!_chunkMap.TryGetValue(request.Position, out var chunk) ||
                    chunk.Version == request.KnownVersion)
                {
                    continue;
                }

                changed.Add(chunk.GetNetworkSnapshot(request.Fields));
            }

            return new AtmosChunkSnapshotBatch(TickCount, changed.ToArray());
        }
    }

    /// <summary>
    ///     Assigns one classification to every voxel in a chunk.
    /// </summary>
    /// <param name="position">The target chunk's grid position.</param>
    /// <param name="classification">The room, solid, or void classification to assign.</param>
    /// <remarks>If the chunk is awake, its active-voxel topology is rebuilt immediately.</remarks>
    /// <exception cref="KeyNotFoundException">No chunk is registered at <paramref name="position" />.</exception>
    internal void SetChunkClassification(Int3 position, VoxelClassification classification)
    {
        lock (_stateGate)
        {
            var chunk = GetChunk(position);
            int[] previousClassifications = chunk.VoxelRoomMap.ToArray();
            ushort[] previouslyActiveVoxels = CaptureActiveVoxels(chunk);
            chunk.VoxelRoomMap.Fill(classification.RoomId);
            Dictionary<AtmosChunk, SortedSet<ushort>> wakePlan;
            try
            {
                wakePlan = CreateChangedBoundaryWakePlan(chunk, previousClassifications);
                AddPreviouslyActiveComponents(
                    wakePlan, chunk, previouslyActiveVoxels, previousClassifications);
                AddGasBearingChangedComponents(wakePlan, chunk, previousClassifications);
                AddGasBearingNewVoidAdjacentComponents(wakePlan, chunk, previousClassifications);
                ValidateBoundaryWakePlan(wakePlan);
            }
            catch
            {
                chunk.VoxelRoomMap.CopyFrom(previousClassifications);
                throw;
            }

            RebuildActiveTopology(chunk);
            ApplyBoundaryWakePlan(wakePlan);
            chunk.MarkChanged();
        }
    }

    /// <summary>
    ///     Assigns one classification to the voxels on every simulated outer face of a chunk.
    /// </summary>
    /// <remarks>
    ///     X and Y faces are always included. Z faces are included only for chunks with more than one
    ///     layer, matching the kernel's two-dimensional boundary behavior for single-layer chunks.
    /// </remarks>
    internal void SetChunkBoundaryClassification(Int3 position, VoxelClassification classification)
    {
        lock (_stateGate)
        {
            var chunk = GetChunk(position);
            int[] previousClassifications = chunk.VoxelRoomMap.ToArray();
            ushort[] previouslyActiveVoxels = CaptureActiveVoxels(chunk);
            var dimensions = chunk.Dimensions;

            for (var z = 0; z < dimensions.Z; z++)
                for (var y = 0; y < dimensions.Y; y++)
                    for (var x = 0; x < dimensions.X; x++)
                    {
                        bool isBoundary =
                            x == 0 || x == dimensions.X - 1 ||
                            y == 0 || y == dimensions.Y - 1 ||
                            dimensions.Z > 1 && (z == 0 || z == dimensions.Z - 1);

                        if (isBoundary)
                            chunk.VoxelRoomMap[chunk.GetIndex(new Int3(x, y, z))] = classification.RoomId;
                    }

            Dictionary<AtmosChunk, SortedSet<ushort>> wakePlan;
            try
            {
                wakePlan = CreateChangedBoundaryWakePlan(chunk, previousClassifications);
                AddPreviouslyActiveComponents(
                    wakePlan, chunk, previouslyActiveVoxels, previousClassifications);
                AddGasBearingChangedComponents(wakePlan, chunk, previousClassifications);
                AddGasBearingNewVoidAdjacentComponents(wakePlan, chunk, previousClassifications);
                ValidateBoundaryWakePlan(wakePlan);
            }
            catch
            {
                chunk.VoxelRoomMap.CopyFrom(previousClassifications);
                throw;
            }

            RebuildActiveTopology(chunk);
            ApplyBoundaryWakePlan(wakePlan);
            chunk.MarkChanged();
        }
    }

    /// <summary>
    ///     Assigns a classification to one voxel addressed by its flat local index.
    /// </summary>
    /// <param name="position">The target chunk's grid position.</param>
    /// <param name="localVoxelIndex">The voxel's zero-based index in the chunk's flattened storage.</param>
    /// <param name="classification">The room, solid, or void classification to assign.</param>
    /// <remarks>If the chunk is awake, its active-voxel topology is rebuilt immediately.</remarks>
    /// <exception cref="KeyNotFoundException">No chunk is registered at <paramref name="position" />.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="localVoxelIndex" /> is outside the chunk.</exception>
    internal void SetVoxelClassification(Int3 position, ushort localVoxelIndex,
        VoxelClassification classification)
    {
        lock (_stateGate)
        {
            var chunk = GetChunk(position);
            ValidateVoxelIndex(chunk, localVoxelIndex);
            int[] previousClassifications = chunk.VoxelRoomMap.ToArray();
            ushort[] previouslyActiveVoxels = CaptureActiveVoxels(chunk);
            chunk.VoxelRoomMap[localVoxelIndex] = classification.RoomId;
            Dictionary<AtmosChunk, SortedSet<ushort>> wakePlan;
            try
            {
                wakePlan = CreateChangedBoundaryWakePlan(chunk, previousClassifications);
                AddPreviouslyActiveComponents(
                    wakePlan, chunk, previouslyActiveVoxels, previousClassifications);
                AddGasBearingChangedComponents(wakePlan, chunk, previousClassifications);
                AddGasBearingNewVoidAdjacentComponents(wakePlan, chunk, previousClassifications);
                ValidateBoundaryWakePlan(wakePlan);
            }
            catch
            {
                chunk.VoxelRoomMap.CopyFrom(previousClassifications);
                throw;
            }

            RebuildActiveTopology(chunk);
            ApplyBoundaryWakePlan(wakePlan);
            chunk.MarkChanged();
        }
    }

    /// <summary>
    ///     Assigns a classification to one voxel addressed by local coordinates.
    /// </summary>
    /// <param name="position">The target chunk's grid position.</param>
    /// <param name="x">The zero-based local x-coordinate.</param>
    /// <param name="y">The zero-based local y-coordinate.</param>
    /// <param name="z">The zero-based local z-coordinate.</param>
    /// <param name="classification">The room, solid, or void classification to assign.</param>
    /// <exception cref="KeyNotFoundException">No chunk is registered at <paramref name="position" />.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A local coordinate is outside the chunk.</exception>
    internal void SetVoxelClassification(Int3 position, int x, int y, int z,
        VoxelClassification classification)
    {
        lock (_stateGate)
        {
            var chunk = GetChunk(position);
            SetVoxelClassification(position, GetValidatedVoxelIndex(chunk, x, y, z), classification);
        }
    }

    /// <summary>
    ///     Sets the temperature of one voxel addressed by its flat local index.
    /// </summary>
    /// <param name="position">The target chunk's grid position.</param>
    /// <param name="localVoxelIndex">The voxel's zero-based index in the chunk's flattened storage.</param>
    /// <param name="temperature">The absolute temperature to store, in kelvins.</param>
    /// <exception cref="KeyNotFoundException">No chunk is registered at <paramref name="position" />.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="localVoxelIndex" /> is outside the chunk.</exception>
    /// <exception cref="InvalidOperationException">
    ///     The requested temperature would make the voxel's derived pressure unrepresentable.
    /// </exception>
    internal void SetVoxelTemperature(Int3 position, ushort localVoxelIndex, float temperature)
    {
        lock (_stateGate)
        {
            var chunk = GetChunk(position);
            ValidateVoxelIndex(chunk, localVoxelIndex);
            VoxelGasMixtureTotals totals = CalculateVoxelMixtureTotals(
                chunk,
                localVoxelIndex,
                temperature);
            if (chunk.IsAwake || chunk.WasAutomaticallySlept)
                chunk.WakeVoxel(localVoxelIndex);
            else
                chunk.VoxelAggregates.Reset();
            chunk.Temperature[localVoxelIndex] = temperature;
            ApplyVoxelMixtureTotals(chunk, localVoxelIndex, totals);
        }
    }

    /// <summary>
    ///     Sets the temperature of one voxel addressed by local coordinates.
    /// </summary>
    /// <param name="position">The target chunk's grid position.</param>
    /// <param name="x">The zero-based local x-coordinate.</param>
    /// <param name="y">The zero-based local y-coordinate.</param>
    /// <param name="z">The zero-based local z-coordinate.</param>
    /// <param name="temperature">The absolute temperature to store, in kelvins.</param>
    /// <exception cref="KeyNotFoundException">No chunk is registered at <paramref name="position" />.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A local coordinate is outside the chunk.</exception>
    internal void SetVoxelTemperature(Int3 position, int x, int y, int z, float temperature)
    {
        lock (_stateGate)
        {
            var chunk = GetChunk(position);
            SetVoxelTemperature(position, GetValidatedVoxelIndex(chunk, x, y, z), temperature);
        }
    }

    /// <summary>
    ///     Adds gas to one voxel addressed by its flat local index and wakes its room.
    /// </summary>
    /// <param name="position">The target chunk's grid position.</param>
    /// <param name="localVoxelIndex">The voxel's zero-based index in the chunk's flattened storage.</param>
    /// <param name="gasId">The gas channel identifier.</param>
    /// <param name="moles">The amount of gas to add, in moles.</param>
    /// <param name="temperature">The temperature of the added gas, in kelvins.</param>
    /// <remarks>Injection into a solid or void voxel is ignored by the chunk.</remarks>
    /// <exception cref="KeyNotFoundException">No chunk is registered at <paramref name="position" />.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="localVoxelIndex" /> is outside the chunk.</exception>
    internal void AddGasToVoxel(Int3 position, ushort localVoxelIndex, int gasId, float moles,
        float temperature)
    {
        lock (_stateGate)
        {
            var chunk = GetChunk(position);
            ValidateVoxelIndex(chunk, localVoxelIndex);
            ValidateGasInjection(gasId, moles, temperature);

            int roomId = chunk.VoxelRoomMap[localVoxelIndex];
            if (roomId == VoxelClassification.RoomSolid || roomId == VoxelClassification.RoomVoid)
                return;

            // Validate the complete projected mixture before waking the target. Individually finite inputs can
            // still overflow an existing species, the voxel total, heat capacity, or pressure; rejecting those
            // states after WakeVoxel would make a failed injection observably mutate lifecycle state.
            VoxelGasAddition addition = PrepareVoxelGasAddition(
                chunk, localVoxelIndex, gasId, moles, temperature);
            chunk.WakeVoxel(localVoxelIndex);
            SetVoxelGasMoles(chunk, localVoxelIndex, gasId, addition.CombinedGasMoles);
            chunk.Temperature[localVoxelIndex] = addition.MixedTemperature;
            ApplyVoxelMixtureTotals(chunk, localVoxelIndex, addition.Totals);
        }
    }

    /// <summary>
    ///     Adds gas to one voxel addressed by local coordinates and wakes its room.
    /// </summary>
    /// <param name="position">The target chunk's grid position.</param>
    /// <param name="x">The zero-based local x-coordinate.</param>
    /// <param name="y">The zero-based local y-coordinate.</param>
    /// <param name="z">The zero-based local z-coordinate.</param>
    /// <param name="gasId">The gas channel identifier.</param>
    /// <param name="moles">The amount of gas to add, in moles.</param>
    /// <param name="temperature">The temperature of the added gas, in kelvins.</param>
    /// <remarks>Injection into a solid or void voxel is ignored by the chunk.</remarks>
    /// <exception cref="KeyNotFoundException">No chunk is registered at <paramref name="position" />.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A local coordinate is outside the chunk.</exception>
    internal void AddGasToVoxel(Int3 position, int x, int y, int z, int gasId, float moles,
        float temperature)
    {
        lock (_stateGate)
        {
            var chunk = GetChunk(position);
            AddGasToVoxel(position, GetValidatedVoxelIndex(chunk, x, y, z), gasId, moles, temperature);
        }
    }

    /// <summary>
    ///     Wakes a room so its voxels participate in subsequent simulation ticks.
    /// </summary>
    /// <param name="position">The target chunk's grid position.</param>
    /// <param name="roomId">The classification ID of the room to activate.</param>
    /// <remarks>Solid and void IDs are ignored. Waking an active room resets its sleep timer.</remarks>
    /// <exception cref="KeyNotFoundException">No chunk is registered at <paramref name="position" />.</exception>
    internal void WakeRoom(Int3 position, int roomId)
    {
        lock (_stateGate)
        {
            GetChunk(position).WakeRoom(roomId);
        }
    }

    /// <summary>
    ///     Puts a chunk to sleep so it is skipped by subsequent simulation ticks.
    /// </summary>
    /// <param name="position">The target chunk's grid position.</param>
    /// <exception cref="KeyNotFoundException">No chunk is registered at <paramref name="position" />.</exception>
    internal void SleepChunk(Int3 position)
    {
        lock (_stateGate)
        {
            GetChunk(position).Sleep();
        }
    }

    /// <summary>
    ///     Runs exactly one fixed simulation tick using the current configuration.
    /// </summary>
    /// <remarks>This bypasses the elapsed-time accumulator and is useful for deterministic driving and tests.</remarks>
    internal void Tick()
    {
        lock (_stateGate)
        {
            var chunks = _chunkMap.Values.ToArray();
            TickSimulation(chunks);
        }
    }

    private AtmosChunk GetChunk(Int3 position)
    {
        if (_chunkMap.TryGetValue(position, out var chunk))
            return chunk;

        throw new KeyNotFoundException(
            $"No atmospheric chunk is registered at ({position.X}, {position.Y}, {position.Z}).");
    }

    private static void RebuildActiveTopology(AtmosChunk chunk)
    {
        if (!chunk.IsAwake)
            return;

        var retainedRoomCount = 0;
        for (var roomIndex = 0; roomIndex < chunk.ActiveRoomCount; roomIndex++)
        {
            int roomId = chunk.ActiveRoomIds[roomIndex];
            if (HasPassableRoomVoxel(chunk, roomId))
                chunk.ActiveRoomIds[retainedRoomCount++] = roomId;
        }

        chunk.ActiveRoomCount = retainedRoomCount;
        chunk.RebuildActiveAirIndices();
    }

    private static bool HasPassableRoomVoxel(AtmosChunk chunk, int roomId)
    {
        if (roomId == VoxelClassification.RoomSolid || roomId == VoxelClassification.RoomVoid)
            return false;

        for (var voxelIndex = 0; voxelIndex < chunk.VoxelCount; voxelIndex++)
        {
            if (chunk.VoxelRoomMap[voxelIndex] == roomId)
                return true;
        }

        return false;
    }

    private static ushort[] CaptureActiveVoxels(AtmosChunk chunk)
    {
        if ((!chunk.IsAwake && !chunk.WasAutomaticallySlept) || chunk.ActiveAirCount == 0)
            return [];

        var activeVoxels = new ushort[chunk.ActiveAirCount];
        Array.Copy(chunk.ActiveAirIndices, activeVoxels, chunk.ActiveAirCount);
        return activeVoxels;
    }

    private static void AddPreviouslyActiveComponents(
        Dictionary<AtmosChunk, SortedSet<ushort>> wakePlan, AtmosChunk chunk,
        ReadOnlySpan<ushort> previouslyActiveVoxels,
        ReadOnlySpan<int> previousClassifications)
    {
        if (previouslyActiveVoxels.IsEmpty)
            return;

        bool activeTopologyChanged = false;
        foreach (ushort voxelIndex in previouslyActiveVoxels)
        {
            if (previousClassifications[voxelIndex] == chunk.VoxelRoomMap[voxelIndex])
                continue;
            activeTopologyChanged = true;
            break;
        }

        if (!activeTopologyChanged)
            return;

        bool[] visited = ArrayPool<bool>.Shared.Rent(chunk.VoxelCount);
        int[] queue = ArrayPool<int>.Shared.Rent(chunk.VoxelCount);
        Array.Clear(visited, 0, chunk.VoxelCount);
        try
        {
            foreach (ushort seedVoxel in previouslyActiveVoxels)
            {
                int roomId = chunk.VoxelRoomMap[seedVoxel];
                if (visited[seedVoxel] ||
                    roomId == VoxelClassification.RoomSolid ||
                    roomId == VoxelClassification.RoomVoid)
                    continue;

                if (!wakePlan.TryGetValue(chunk, out SortedSet<ushort>? componentSeeds))
                {
                    componentSeeds = [];
                    wakePlan.Add(chunk, componentSeeds);
                }

                componentSeeds.Add(seedVoxel);
                var queuedCount = 0;
                visited[seedVoxel] = true;
                queue[queuedCount++] = seedVoxel;
                for (var queuedIndex = 0; queuedIndex < queuedCount; queuedIndex++)
                {
                    int componentVoxel = queue[queuedIndex];
                    int x = componentVoxel % chunk.Width;
                    int yz = componentVoxel / chunk.Width;
                    int y = yz % chunk.Height;
                    int z = yz / chunk.Height;
                    if (x > 0)
                        TryEnqueuePassableVoxel(chunk, componentVoxel - 1,
                            visited, queue, ref queuedCount);
                    if (x + 1 < chunk.Width)
                        TryEnqueuePassableVoxel(chunk, componentVoxel + 1,
                            visited, queue, ref queuedCount);
                    if (y > 0)
                        TryEnqueuePassableVoxel(chunk, componentVoxel - chunk.Width,
                            visited, queue, ref queuedCount);
                    if (y + 1 < chunk.Height)
                        TryEnqueuePassableVoxel(chunk, componentVoxel + chunk.Width,
                            visited, queue, ref queuedCount);
                    int layerSize = chunk.Width * chunk.Height;
                    if (z > 0)
                        TryEnqueuePassableVoxel(chunk, componentVoxel - layerSize,
                            visited, queue, ref queuedCount);
                    if (z + 1 < chunk.Depth)
                        TryEnqueuePassableVoxel(chunk, componentVoxel + layerSize,
                            visited, queue, ref queuedCount);
                }
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(queue);
            ArrayPool<bool>.Shared.Return(visited);
        }
    }

    private static void AddGasBearingChangedComponents(
        Dictionary<AtmosChunk, SortedSet<ushort>> wakePlan,
        AtmosChunk chunk,
        ReadOnlySpan<int> previousClassifications)
    {
        bool[] visited = ArrayPool<bool>.Shared.Rent(chunk.VoxelCount);
        int[] queue = ArrayPool<int>.Shared.Rent(chunk.VoxelCount);
        Array.Clear(visited, 0, chunk.VoxelCount);
        try
        {
            for (ushort seedVoxel = 0; seedVoxel < chunk.VoxelCount; seedVoxel++)
            {
                int seedRoom = chunk.VoxelRoomMap[seedVoxel];
                if (visited[seedVoxel] ||
                    seedRoom == VoxelClassification.RoomSolid ||
                    seedRoom == VoxelClassification.RoomVoid)
                    continue;

                var queuedCount = 0;
                visited[seedVoxel] = true;
                queue[queuedCount++] = seedVoxel;
                var hasGas = false;
                var containsChangedVoxel = false;
                for (var queuedIndex = 0; queuedIndex < queuedCount; queuedIndex++)
                {
                    int componentVoxel = queue[queuedIndex];
                    hasGas |= HasGasAtVoxel(chunk, (ushort)componentVoxel);
                    containsChangedVoxel |= previousClassifications[componentVoxel] !=
                                            chunk.VoxelRoomMap[componentVoxel];
                    int x = componentVoxel % chunk.Width;
                    int yz = componentVoxel / chunk.Width;
                    int y = yz % chunk.Height;
                    int z = yz / chunk.Height;
                    if (x > 0)
                        TryEnqueuePassableVoxel(chunk, componentVoxel - 1,
                            visited, queue, ref queuedCount);
                    if (x + 1 < chunk.Width)
                        TryEnqueuePassableVoxel(chunk, componentVoxel + 1,
                            visited, queue, ref queuedCount);
                    if (y > 0)
                        TryEnqueuePassableVoxel(chunk, componentVoxel - chunk.Width,
                            visited, queue, ref queuedCount);
                    if (y + 1 < chunk.Height)
                        TryEnqueuePassableVoxel(chunk, componentVoxel + chunk.Width,
                            visited, queue, ref queuedCount);
                    int layerSize = chunk.Width * chunk.Height;
                    if (z > 0)
                        TryEnqueuePassableVoxel(chunk, componentVoxel - layerSize,
                            visited, queue, ref queuedCount);
                    if (z + 1 < chunk.Depth)
                        TryEnqueuePassableVoxel(chunk, componentVoxel + layerSize,
                            visited, queue, ref queuedCount);
                }

                if (!hasGas || !containsChangedVoxel)
                    continue;
                if (!wakePlan.TryGetValue(chunk, out SortedSet<ushort>? componentSeeds))
                {
                    componentSeeds = [];
                    wakePlan.Add(chunk, componentSeeds);
                }

                componentSeeds.Add(seedVoxel);
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(queue);
            ArrayPool<bool>.Shared.Return(visited);
        }
    }

    private static void AddGasBearingNewVoidAdjacentComponents(
        Dictionary<AtmosChunk, SortedSet<ushort>> wakePlan,
        AtmosChunk chunk,
        ReadOnlySpan<int> previousClassifications)
    {
        bool[] visited = ArrayPool<bool>.Shared.Rent(chunk.VoxelCount);
        int[] queue = ArrayPool<int>.Shared.Rent(chunk.VoxelCount);
        Array.Clear(visited, 0, chunk.VoxelCount);
        try
        {
            for (ushort seedVoxel = 0; seedVoxel < chunk.VoxelCount; seedVoxel++)
            {
                int seedRoom = chunk.VoxelRoomMap[seedVoxel];
                if (visited[seedVoxel] ||
                    seedRoom == VoxelClassification.RoomSolid ||
                    seedRoom == VoxelClassification.RoomVoid)
                    continue;

                var queuedCount = 0;
                visited[seedVoxel] = true;
                queue[queuedCount++] = seedVoxel;
                var hasGas = false;
                var touchesVoid = false;
                for (var queuedIndex = 0; queuedIndex < queuedCount; queuedIndex++)
                {
                    int componentVoxel = queue[queuedIndex];
                    hasGas |= HasGasAtVoxel(chunk, (ushort)componentVoxel);
                    int x = componentVoxel % chunk.Width;
                    int yz = componentVoxel / chunk.Width;
                    int y = yz % chunk.Height;
                    int z = yz / chunk.Height;
                    if (x > 0)
                        VisitTopologyNeighbor(chunk, componentVoxel - 1,
                            visited, queue, ref queuedCount, previousClassifications, ref touchesVoid);
                    if (x + 1 < chunk.Width)
                        VisitTopologyNeighbor(chunk, componentVoxel + 1,
                            visited, queue, ref queuedCount, previousClassifications, ref touchesVoid);
                    if (y > 0)
                        VisitTopologyNeighbor(chunk, componentVoxel - chunk.Width,
                            visited, queue, ref queuedCount, previousClassifications, ref touchesVoid);
                    if (y + 1 < chunk.Height)
                        VisitTopologyNeighbor(chunk, componentVoxel + chunk.Width,
                            visited, queue, ref queuedCount, previousClassifications, ref touchesVoid);
                    int layerSize = chunk.Width * chunk.Height;
                    if (z > 0)
                        VisitTopologyNeighbor(chunk, componentVoxel - layerSize,
                            visited, queue, ref queuedCount, previousClassifications, ref touchesVoid);
                    if (z + 1 < chunk.Depth)
                        VisitTopologyNeighbor(chunk, componentVoxel + layerSize,
                            visited, queue, ref queuedCount, previousClassifications, ref touchesVoid);
                }

                if (!hasGas || !touchesVoid)
                    continue;
                if (!wakePlan.TryGetValue(chunk, out SortedSet<ushort>? componentSeeds))
                {
                    componentSeeds = [];
                    wakePlan.Add(chunk, componentSeeds);
                }

                componentSeeds.Add(seedVoxel);
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(queue);
            ArrayPool<bool>.Shared.Return(visited);
        }
    }

    private static void VisitTopologyNeighbor(AtmosChunk chunk, int voxelIndex,
        bool[] visited, int[] queue, ref int queuedCount,
        ReadOnlySpan<int> previousClassifications, ref bool touchesVoid)
    {
        int roomId = chunk.VoxelRoomMap[voxelIndex];
        if (roomId == VoxelClassification.RoomVoid)
        {
            touchesVoid |= previousClassifications[voxelIndex] != VoxelClassification.RoomVoid;
            return;
        }

        if (roomId == VoxelClassification.RoomSolid || visited[voxelIndex])
            return;

        visited[voxelIndex] = true;
        queue[queuedCount++] = voxelIndex;
    }

    private Dictionary<AtmosChunk, SortedSet<ushort>> CreateBoundaryWakePlan(AtmosChunk chunk)
    {
        var wakePlan = new Dictionary<AtmosChunk, SortedSet<ushort>>();
        AddBoundaryWakeConnection(wakePlan, chunk, Int3.NegX, Int3.PosX);
        AddBoundaryWakeConnection(wakePlan, chunk, Int3.PosX, Int3.NegX);
        AddBoundaryWakeConnection(wakePlan, chunk, Int3.NegY, Int3.PosY);
        AddBoundaryWakeConnection(wakePlan, chunk, Int3.PosY, Int3.NegY);
        if (chunk.Depth > 1)
        {
            AddBoundaryWakeConnection(wakePlan, chunk, Int3.NegZ, Int3.PosZ);
            AddBoundaryWakeConnection(wakePlan, chunk, Int3.PosZ, Int3.NegZ);
        }

        return wakePlan;
    }

    private Dictionary<AtmosChunk, SortedSet<ushort>> CreateChangedBoundaryWakePlan(
        AtmosChunk chunk,
        ReadOnlySpan<int> previousClassifications)
    {
        var wakePlan = new Dictionary<AtmosChunk, SortedSet<ushort>>();
        AddChangedBoundaryWakeConnection(
            wakePlan, chunk, previousClassifications, Int3.NegX);
        AddChangedBoundaryWakeConnection(
            wakePlan, chunk, previousClassifications, Int3.PosX);
        AddChangedBoundaryWakeConnection(
            wakePlan, chunk, previousClassifications, Int3.NegY);
        AddChangedBoundaryWakeConnection(
            wakePlan, chunk, previousClassifications, Int3.PosY);
        if (chunk.Depth > 1)
        {
            AddChangedBoundaryWakeConnection(
                wakePlan, chunk, previousClassifications, Int3.NegZ);
            AddChangedBoundaryWakeConnection(
                wakePlan, chunk, previousClassifications, Int3.PosZ);
        }

        return wakePlan;
    }

    private void AddChangedBoundaryWakeConnection(
        Dictionary<AtmosChunk, SortedSet<ushort>> wakePlan,
        AtmosChunk chunk,
        ReadOnlySpan<int> previousClassifications,
        Int3 direction)
    {
        if (!_chunkMap.TryGetValue(chunk.GridPosition + direction, out var neighbor))
            return;

        for (ushort voxelIndex = 0; voxelIndex < chunk.VoxelCount; voxelIndex++)
        {
            Int3 position = chunk.GetXyzInt3(voxelIndex);
            bool isFace = direction.X < 0 && position.X == 0 ||
                          direction.X > 0 && position.X == chunk.Width - 1 ||
                          direction.Y < 0 && position.Y == 0 ||
                          direction.Y > 0 && position.Y == chunk.Height - 1 ||
                          direction.Z < 0 && position.Z == 0 ||
                          direction.Z > 0 && position.Z == chunk.Depth - 1;
            if (!isFace)
                continue;

            Int3 neighborPosition = (position + direction + neighbor.Dimensions) %
                                    neighbor.Dimensions;
            ushort neighborIndex = neighbor.GetIndex(neighborPosition);
            int neighborRoom = neighbor.VoxelRoomMap[neighborIndex];
            int oldRoom = previousClassifications[voxelIndex];
            int newRoom = chunk.VoxelRoomMap[voxelIndex];
            if (!OpensOrChangesBoundaryBehavior(oldRoom, newRoom, neighborRoom))
                continue;

            AddGasBearingComponentAtVoxel(wakePlan, chunk, voxelIndex);
            AddGasBearingComponentAtVoxel(wakePlan, neighbor, neighborIndex);
        }
    }

    private static bool OpensOrChangesBoundaryBehavior(int oldRoom, int newRoom, int neighborRoom)
    {
        if (newRoom == VoxelClassification.RoomSolid ||
            neighborRoom == VoxelClassification.RoomSolid)
            return false;

        if (oldRoom == VoxelClassification.RoomSolid)
            return true;

        bool oldIsVoid = oldRoom == VoxelClassification.RoomVoid;
        bool newIsVoid = newRoom == VoxelClassification.RoomVoid;
        return oldIsVoid != newIsVoid;
    }

    private static void AddGasBearingComponentAtVoxel(
        Dictionary<AtmosChunk, SortedSet<ushort>> wakePlan,
        AtmosChunk chunk,
        ushort seedVoxel)
    {
        int seedRoom = chunk.VoxelRoomMap[seedVoxel];
        if (seedRoom == VoxelClassification.RoomSolid ||
            seedRoom == VoxelClassification.RoomVoid)
            return;

        bool[] visited = ArrayPool<bool>.Shared.Rent(chunk.VoxelCount);
        int[] queue = ArrayPool<int>.Shared.Rent(chunk.VoxelCount);
        Array.Clear(visited, 0, chunk.VoxelCount);
        try
        {
            var queuedCount = 0;
            visited[seedVoxel] = true;
            queue[queuedCount++] = seedVoxel;
            var hasGas = false;
            for (var queuedIndex = 0; queuedIndex < queuedCount; queuedIndex++)
            {
                int componentVoxel = queue[queuedIndex];
                hasGas |= HasGasAtVoxel(chunk, (ushort)componentVoxel);
                int x = componentVoxel % chunk.Width;
                int yz = componentVoxel / chunk.Width;
                int y = yz % chunk.Height;
                int z = yz / chunk.Height;
                if (x > 0)
                    TryEnqueuePassableVoxel(chunk, componentVoxel - 1,
                        visited, queue, ref queuedCount);
                if (x + 1 < chunk.Width)
                    TryEnqueuePassableVoxel(chunk, componentVoxel + 1,
                        visited, queue, ref queuedCount);
                if (y > 0)
                    TryEnqueuePassableVoxel(chunk, componentVoxel - chunk.Width,
                        visited, queue, ref queuedCount);
                if (y + 1 < chunk.Height)
                    TryEnqueuePassableVoxel(chunk, componentVoxel + chunk.Width,
                        visited, queue, ref queuedCount);
                int layerSize = chunk.Width * chunk.Height;
                if (z > 0)
                    TryEnqueuePassableVoxel(chunk, componentVoxel - layerSize,
                        visited, queue, ref queuedCount);
                if (z + 1 < chunk.Depth)
                    TryEnqueuePassableVoxel(chunk, componentVoxel + layerSize,
                        visited, queue, ref queuedCount);
            }

            if (!hasGas)
                return;
            if (!wakePlan.TryGetValue(chunk, out SortedSet<ushort>? componentSeeds))
            {
                componentSeeds = [];
                wakePlan.Add(chunk, componentSeeds);
            }

            componentSeeds.Add(seedVoxel);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(queue);
            ArrayPool<bool>.Shared.Return(visited);
        }
    }

    private void AddBoundaryWakeConnection(Dictionary<AtmosChunk, SortedSet<ushort>> wakePlan,
        AtmosChunk chunk, Int3 direction, Int3 oppositeDirection)
    {
        if (!_chunkMap.TryGetValue(chunk.GridPosition + direction, out var neighbor))
            return;

        AddGasBearingBoundaryRooms(wakePlan, chunk, neighbor, direction);
        if (direction.Z == 0 || neighbor.Depth > 1)
            AddGasBearingBoundaryRooms(wakePlan, neighbor, chunk, oppositeDirection);
    }

    private static void AddGasBearingBoundaryRooms(
        Dictionary<AtmosChunk, SortedSet<ushort>> wakePlan, AtmosChunk chunk,
        AtmosChunk neighbor, Int3 direction)
    {
        bool[] visited = ArrayPool<bool>.Shared.Rent(chunk.VoxelCount);
        int[] queue = ArrayPool<int>.Shared.Rent(chunk.VoxelCount);
        Array.Clear(visited, 0, chunk.VoxelCount);
        try
        {
            for (ushort voxelIndex = 0; voxelIndex < chunk.VoxelCount; voxelIndex++)
            {
                Int3 position = chunk.GetXyzInt3(voxelIndex);
                bool isFace = direction.X < 0 && position.X == 0 ||
                              direction.X > 0 && position.X == chunk.Width - 1 ||
                              direction.Y < 0 && position.Y == 0 ||
                              direction.Y > 0 && position.Y == chunk.Height - 1 ||
                              direction.Z < 0 && position.Z == 0 ||
                              direction.Z > 0 && position.Z == chunk.Depth - 1;
                if (!isFace || visited[voxelIndex])
                    continue;

                int roomId = chunk.VoxelRoomMap[voxelIndex];
                if (roomId == VoxelClassification.RoomSolid ||
                    roomId == VoxelClassification.RoomVoid)
                    continue;

                Int3 neighborPosition = (position + direction + neighbor.Dimensions) %
                                        neighbor.Dimensions;
                int neighborRoom = neighbor.VoxelRoomMap[neighbor.GetIndex(neighborPosition)];
                if (neighborRoom == VoxelClassification.RoomSolid)
                    continue;

                var queuedCount = 0;
                visited[voxelIndex] = true;
                queue[queuedCount++] = voxelIndex;
                var hasGas = false;
                for (var queuedIndex = 0; queuedIndex < queuedCount; queuedIndex++)
                {
                    int componentVoxel = queue[queuedIndex];
                    hasGas |= HasGasAtVoxel(chunk, (ushort)componentVoxel);
                    int x = componentVoxel % chunk.Width;
                    int yz = componentVoxel / chunk.Width;
                    int y = yz % chunk.Height;
                    int z = yz / chunk.Height;
                    if (x > 0)
                        TryEnqueuePassableVoxel(chunk, componentVoxel - 1, visited, queue, ref queuedCount);
                    if (x + 1 < chunk.Width)
                        TryEnqueuePassableVoxel(chunk, componentVoxel + 1, visited, queue, ref queuedCount);
                    if (y > 0)
                        TryEnqueuePassableVoxel(chunk, componentVoxel - chunk.Width,
                            visited, queue, ref queuedCount);
                    if (y + 1 < chunk.Height)
                        TryEnqueuePassableVoxel(chunk, componentVoxel + chunk.Width,
                            visited, queue, ref queuedCount);
                    int layerSize = chunk.Width * chunk.Height;
                    if (z > 0)
                        TryEnqueuePassableVoxel(chunk, componentVoxel - layerSize,
                            visited, queue, ref queuedCount);
                    if (z + 1 < chunk.Depth)
                        TryEnqueuePassableVoxel(chunk, componentVoxel + layerSize,
                            visited, queue, ref queuedCount);
                }

                if (!hasGas)
                    continue;
                if (!wakePlan.TryGetValue(chunk, out SortedSet<ushort>? componentSeeds))
                {
                    componentSeeds = [];
                    wakePlan.Add(chunk, componentSeeds);
                }

                componentSeeds.Add(voxelIndex);
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(queue);
            ArrayPool<bool>.Shared.Return(visited);
        }
    }

    private static void TryEnqueuePassableVoxel(AtmosChunk chunk, int voxelIndex,
        bool[] visited, int[] queue, ref int queuedCount)
    {
        if (visited[voxelIndex])
            return;

        int roomId = chunk.VoxelRoomMap[voxelIndex];
        if (roomId == VoxelClassification.RoomSolid || roomId == VoxelClassification.RoomVoid)
            return;

        visited[voxelIndex] = true;
        queue[queuedCount++] = voxelIndex;
    }

    private static void ValidateBoundaryWakePlan(
        Dictionary<AtmosChunk, SortedSet<ushort>> wakePlan)
    {
        foreach ((AtmosChunk chunk, SortedSet<ushort> requestedVoxels) in wakePlan)
        {
            bool[] included = ArrayPool<bool>.Shared.Rent(chunk.VoxelCount);
            int[] queue = ArrayPool<int>.Shared.Rent(chunk.VoxelCount);
            Array.Clear(included, 0, chunk.VoxelCount);
            var activeRooms = new HashSet<int>();
            try
            {
                if (chunk.IsAwake || chunk.WasAutomaticallySlept)
                {
                    for (var roomIndex = 0; roomIndex < chunk.ActiveRoomCount; roomIndex++)
                    {
                        int activeRoom = chunk.ActiveRoomIds[roomIndex];
                        if (!HasPassableRoomVoxel(chunk, activeRoom))
                            continue;
                        activeRooms.Add(activeRoom);
                        IncludeRoomClosure(chunk, activeRoom, included, queue);
                    }
                }

                foreach (ushort requestedVoxel in requestedVoxels)
                {
                    if (included[requestedVoxel])
                        continue;

                    int requestedRoom = chunk.VoxelRoomMap[requestedVoxel];
                    if (activeRooms.Add(requestedRoom) && activeRooms.Count > chunk.MaxActiveRooms)
                    {
                        throw new InvalidOperationException(
                            "Opening a chunk boundary would exceed an adjacent chunk's active-room capacity.");
                    }

                    IncludeRoomClosure(chunk, requestedRoom, included, queue);
                }
            }
            finally
            {
                ArrayPool<int>.Shared.Return(queue);
                ArrayPool<bool>.Shared.Return(included);
            }
        }
    }

    private static void IncludeRoomClosure(AtmosChunk chunk, int roomId,
        bool[] included, int[] queue)
    {
        var queuedCount = 0;
        for (var voxelIndex = 0; voxelIndex < chunk.VoxelCount; voxelIndex++)
        {
            if (!included[voxelIndex] && chunk.VoxelRoomMap[voxelIndex] == roomId)
            {
                included[voxelIndex] = true;
                queue[queuedCount++] = voxelIndex;
            }
        }

        for (var queuedIndex = 0; queuedIndex < queuedCount; queuedIndex++)
        {
            int componentVoxel = queue[queuedIndex];
            int x = componentVoxel % chunk.Width;
            int yz = componentVoxel / chunk.Width;
            int y = yz % chunk.Height;
            int z = yz / chunk.Height;
            if (x > 0)
                TryEnqueuePassableVoxel(chunk, componentVoxel - 1,
                    included, queue, ref queuedCount);
            if (x + 1 < chunk.Width)
                TryEnqueuePassableVoxel(chunk, componentVoxel + 1,
                    included, queue, ref queuedCount);
            if (y > 0)
                TryEnqueuePassableVoxel(chunk, componentVoxel - chunk.Width,
                    included, queue, ref queuedCount);
            if (y + 1 < chunk.Height)
                TryEnqueuePassableVoxel(chunk, componentVoxel + chunk.Width,
                    included, queue, ref queuedCount);
            int layerSize = chunk.Width * chunk.Height;
            if (z > 0)
                TryEnqueuePassableVoxel(chunk, componentVoxel - layerSize,
                    included, queue, ref queuedCount);
            if (z + 1 < chunk.Depth)
                TryEnqueuePassableVoxel(chunk, componentVoxel + layerSize,
                    included, queue, ref queuedCount);
        }
    }

    private static void ApplyBoundaryWakePlan(
        Dictionary<AtmosChunk, SortedSet<ushort>> wakePlan)
    {
        foreach ((AtmosChunk chunk, SortedSet<ushort> requestedVoxels) in wakePlan
                     .OrderBy(static pair => pair.Key.GridPosition.X)
                     .ThenBy(static pair => pair.Key.GridPosition.Y)
                     .ThenBy(static pair => pair.Key.GridPosition.Z))
        {
            foreach (ushort voxelIndex in requestedVoxels)
                chunk.WakeVoxel(voxelIndex);
        }
    }

    private static bool HasGasAtVoxel(AtmosChunk chunk, ushort voxelIndex)
    {
        for (var gas = 0; gas < chunk.ActiveGasCount; gas++)
        {
            if (chunk.ActiveGases[gas].Moles[voxelIndex] > 0f)
                return true;
        }

        return false;
    }

    private static ushort GetValidatedVoxelIndex(AtmosChunk chunk, int x, int y, int z)
    {
        if (x < 0 || x >= chunk.Width)
            throw new ArgumentOutOfRangeException(nameof(x));
        if (y < 0 || y >= chunk.Height)
            throw new ArgumentOutOfRangeException(nameof(y));
        if (z < 0 || z >= chunk.Depth)
            throw new ArgumentOutOfRangeException(nameof(z));

        return chunk.GetIndex(x, y, z);
    }

    /// <summary>
    ///     Validates that the given local voxel index is within the bounds of the chunk's voxel array.
    /// </summary>
    /// <param name="chunk">The chunk to validate against.</param>
    /// <param name="localVoxelIndex">The local voxel index to validate.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if the local voxel index is out of bounds.</exception>
    private static void ValidateVoxelIndex(AtmosChunk chunk, ushort localVoxelIndex)
    {
        if (localVoxelIndex >= chunk.VoxelCount)
        {
            throw new ArgumentOutOfRangeException(nameof(localVoxelIndex), localVoxelIndex,
                $"Voxel index must be less than the chunk's voxel count ({chunk.VoxelCount}).");
        }
    }

    private static void ValidateGasInjection(int gasId, float moles, float temperature)
    {
        if (gasId < 0)
            throw new ArgumentOutOfRangeException(nameof(gasId), gasId, "Gas ID must be nonnegative.");
        if (!float.IsFinite(moles) || moles <= 0f)
            throw new ArgumentOutOfRangeException(nameof(moles), moles, "Moles must be positive and finite.");
        if (!float.IsFinite(temperature) || temperature < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(temperature), temperature,
                "Temperature must be nonnegative and finite.");
        }
    }
}
