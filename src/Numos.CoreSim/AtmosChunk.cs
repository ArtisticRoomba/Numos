using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using JetBrains.Annotations;
using Numos.CoreSim.Collections;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.Maths;

namespace Numos.CoreSim;

/// <summary>
///     Represents the simulation state for a fixed-size voxel chunk.
/// </summary>
/// <remarks>
///     Chunk-owned per-voxel data supports both flat-index and <see cref="Int3" /> coordinate access.
///     Use <see cref="GetIndex(Int3)" /> and <see cref="GetXyzInt3(ushort)" /> when converting indices
///     for scalar-indexed storage such as gas channels (because... you know.... they aren't physical).
/// </remarks>
internal class AtmosChunk
{
    private static long _nextGeneration;

    /// <summary>
    ///     Number of valid entries at the beginning of <see cref="ActiveAirIndices" />.
    /// </summary>
    public int ActiveAirCount;

    /// <summary>
    ///     Flat voxel indices in passable components reached from active-room seeds.
    /// </summary>
    /// <remarks>
    ///     Only the first <see cref="ActiveAirCount" /> entries are valid. Room IDs seed activation but are
    ///     not flow barriers: every face-connected non-solid/non-void voxel is included. Rebuild this list
    ///     with <see cref="RebuildActiveAirIndices" /> after changing <see cref="VoxelRoomMap" /> or active rooms.
    /// </remarks>
    public ushort[] ActiveAirIndices;

    /// <summary>
    ///     Number of valid gas channels at the beginning of <see cref="ActiveGases" />.
    /// </summary>
    public int ActiveGasCount;

    /// <summary>
    ///     Gas channels currently present in this chunk.
    /// </summary>
    /// <remarks>
    ///     Only the first <see cref="ActiveGasCount" /> entries are valid. Each valid channel contains
    ///     one moles value for every voxel in the chunk.
    /// </remarks>
    public GasChannel[] ActiveGases;

    /// <summary>
    ///     Number of valid room IDs at the beginning of <see cref="ActiveRoomIds" />.
    /// </summary>
    public int ActiveRoomCount;

    /// <summary>
    ///     Identity and revision used by conditional snapshot consumers.
    /// </summary>
    public AtmosChunkVersion Version => new(_generation, Interlocked.Read(ref _revision));

    private long _generation;
    private long _revision;
    private bool _wasAutomaticallySlept;

    /// <summary>
    ///     Room IDs currently being processed in this chunk.
    /// </summary>
    /// <remarks>
    ///     Only the first <see cref="ActiveRoomCount" /> entries are valid. The number of active rooms
    ///     cannot exceed <see cref="MaxActiveRooms" />.
    /// </remarks>
    public int[] ActiveRoomIds;

    /// <summary>
    /// The number of voxels along the z-axis.
    /// </summary>
    public int Depth;

    /// <summary>
    /// The number of voxels along the x-axis.
    /// </summary>
    public int Width;

    /// <summary>
    /// The number of voxels along the y-axis.
    /// </summary>
    public int Height;

    /// <summary>
    ///     The number of voxels along each axis.
    /// </summary>
    public Int3 Dimensions => new(Width, Height, Depth);

    /// <summary>
    ///     The position of this chunk in the chunk grid.
    /// </summary>
    public Int3 GridPosition;

    /// <summary>
    ///     Whether this chunk is eligible to be processed by the simulation.
    ///     A sleeping chunk is skipped during simulation ticks.
    /// </summary>
    public bool IsAwake;

    /// <summary>
    ///     Maximum number of rooms that can be active in this chunk simultaneously.
    /// </summary>
    public int MaxActiveRooms;

    /// <summary>
    ///     Number of consecutive simulation ticks for which this chunk has remained below the sleep threshold.
    /// </summary>
    /// <seealso cref="AtmosConfig.SleepThreshold" />
    public int SleepTimer;

    /// <summary>
    ///     Progressive intra-chunk aggregate topology used by snap-assisted automatic sleep.
    /// </summary>
    internal AggregateVoxels VoxelAggregates { get; } = new();

    /// <summary>
    ///     Temperature for each voxel, in kelvins (K), indexed by flat voxel index or local coordinate.
    /// </summary>
    public FlatArray<float> Temperature;

    /// <summary>
    ///     Cached pressure for each voxel, in pascals (Pa), indexed by flat voxel index or local coordinate.
    /// </summary>
    /// <remarks>
    ///     Active entries are refreshed by the pressure solver. Entries outside
    ///     <see cref="ActiveAirIndices" /> retain their last refreshed value and are not authoritative after unchecked
    ///     dangerous-context writes. Supported public mutations and configuration changes refresh affected entries.
    /// </remarks>
    public FlatArray<float> TotalPressure;

    /// <summary>
    ///     Cached total heat capacity for each voxel, in joules per kelvin (J/K).
    /// </summary>
    /// <remarks>
    ///     For each refreshed active voxel, the value is the sum of
    ///     <c>moles × effective molar heat capacity</c> for its gases. Entries outside
    ///     <see cref="ActiveAirIndices" /> are not authoritative until that voxel is active and refreshed.
    ///     Values are total heat capacities, not molar quantities.
    /// </remarks>
    public FlatArray<float> TotalHeatCapacity;

    /// <summary>
    ///     Total number of voxels in this chunk, equal to <c>Width * Height * Depth</c>.
    /// </summary>
    public int VoxelCount;

    /// <summary>
    ///     Room classification for each voxel, indexed by flat voxel index or local coordinate.
    /// </summary>
    /// <remarks>
    ///     Positive IDs identify rooms. The reserved values
    ///     <see cref="VoxelClassification.RoomUnassigned" />, <see cref="VoxelClassification.RoomVoid" />, and
    ///     <see cref="VoxelClassification.RoomSolid" />
    ///     identify unassigned, void, and solid voxels respectively.
    /// </remarks>
    /// <seealso cref="VoxelClassification.RoomSolid" />
    /// <seealso cref="VoxelClassification.RoomVoid" />
    /// <seealso cref="VoxelClassification.RoomUnassigned" />
    public FlatArray<int> VoxelRoomMap;

    /// <summary>
    ///     Creates a chunk with the specified dimensions and active-room capacity.
    /// </summary>
    /// <param name="width">The number of voxels along the x axis.</param>
    /// <param name="height">The number of voxels along the y axis.</param>
    /// <param name="depth">The number of voxels along the z axis.</param>
    /// <param name="maxActiveRooms">The maximum number of rooms that can be active at once.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     A dimension is non-positive or the combined voxel count exceeds
    ///     <see cref="AtmosChunkConstants.MaximumVoxelCount" />.
    /// </exception>
    public AtmosChunk(
        int width = AtmosChunkConstants.DefaultWidth,
        int height = AtmosChunkConstants.DefaultHeight,
        int depth = AtmosChunkConstants.DefaultDepth,
        int maxActiveRooms = AtmosChunkConstants.DefaultMaxActiveRooms)
    {
        int voxelCount = GetValidatedVoxelCount(width, height, depth);
        MaxActiveRooms = maxActiveRooms;
        Width = width;
        Height = height;
        Depth = depth;
        VoxelCount = voxelCount;
        EnsureInitialized();
    }

    /// <summary>
    ///     Ensures that the chunk's per-voxel and active-room arrays are initialized for its current dimensions.
    /// </summary>
    /// <remarks>
    ///     Existing arrays are reused when they already have the required length. This method does not
    ///     clear existing values or reset active counts; use <see cref="Initialize" /> to reset the chunk.
    /// </remarks>
    [MemberNotNull(nameof(ActiveAirIndices), nameof(ActiveGases), nameof(ActiveRoomIds))]
    [PublicAPI]
    public void EnsureInitialized()
    {
        var dimensions = Dimensions;
        EnsureInitialized(ref VoxelRoomMap, dimensions);
        if (ActiveAirIndices == null || ActiveAirIndices.Length != VoxelCount)
            ActiveAirIndices = new ushort[VoxelCount];
        EnsureInitialized(ref TotalPressure, dimensions);
        EnsureInitialized(ref TotalHeatCapacity, dimensions);
        EnsureInitialized(ref Temperature, dimensions);
        if (ActiveGases == null)
            ActiveGases = new GasChannel[AtmosChunkConstants.InitialGasChannelCapacity];
        if (ActiveRoomIds == null || ActiveRoomIds.Length != MaxActiveRooms)
            ActiveRoomIds = new int[MaxActiveRooms];
    }

    /// <summary>
    ///     Initializes or reinitializes the chunk with the specified position and dimensions.
    /// </summary>
    /// <param name="position">The chunk's position in the grid of chunks.</param>
    /// <param name="width">The width of the chunk.</param>
    /// <param name="height">The height of the chunk.</param>
    /// <param name="depth">The depth of the chunk.</param>
    /// <param name="maxActiveRooms">The maximum number of rooms that can be active in this chunk simultaneously.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     A dimension is non-positive or the combined voxel count exceeds
    ///     <see cref="AtmosChunkConstants.MaximumVoxelCount" />.
    /// </exception>
    /// <remarks>
    ///     Initialization puts the chunk to sleep, resets all active counts and timers, and clears
    ///     its per-voxel, gas-channel, and active-room data.
    /// </remarks>
    [PublicAPI]
    public void Initialize(
        Int3 position,
        int width = AtmosChunkConstants.DefaultWidth,
        int height = AtmosChunkConstants.DefaultHeight,
        int depth = AtmosChunkConstants.DefaultDepth,
        int maxActiveRooms = AtmosChunkConstants.DefaultMaxActiveRooms)
    {
        int voxelCount = GetValidatedVoxelCount(width, height, depth);
        GridPosition = position;
        MaxActiveRooms = maxActiveRooms;
        IsAwake = false;
        _wasAutomaticallySlept = false;
        Width = width;
        Height = height;
        Depth = depth;
        VoxelCount = voxelCount;

        EnsureInitialized();

        ActiveAirCount = 0;
        ActiveRoomCount = 0;
        ActiveGasCount = 0;
        SleepTimer = 0;
        VoxelAggregates.Reset();

        VoxelRoomMap.Clear();
        Array.Clear(ActiveAirIndices, 0, ActiveAirIndices.Length);
        TotalPressure.Clear();
        TotalHeatCapacity.Clear();
        Temperature.Clear();
        Array.Clear(ActiveGases, 0, ActiveGases.Length);
        Array.Clear(ActiveRoomIds, 0, ActiveRoomIds.Length);

        _generation = Interlocked.Increment(ref _nextGeneration);
        Interlocked.Exchange(ref _revision, 1);
    }

    /// <summary>
    ///     Marks the externally observable state as potentially changed.
    /// </summary>
    public void MarkChanged()
    {
        Interlocked.Increment(ref _revision);
    }

    /// <summary>
    ///     Releases resources held by the chunk's active gas channels.
    /// </summary>
    /// <remarks>
    ///     After releasing a chunk, do not use its active gas channels until they have been initialized again.
    /// </remarks>
    public void Release()
    {
        VoxelAggregates.Reset();
        if (ActiveGases != null)
        {
            for (var i = 0; i < ActiveGasCount; i++)
            {
                ActiveGases[i].Release();
            }
        }
    }

    /// <summary>
    ///     Wakes the chunk and activates the specified room for simulation.
    /// </summary>
    /// <param name="targetRoomId">The room ID to activate.</param>
    /// <remarks>
    ///     Solid and void classifications are ignored. Activating an already active room only resets
    ///     the sleep timer. Any wake after automatic sleep first resumes the complete retained active domain.
    ///     When a new room is activated, <see cref="ActiveAirIndices" /> is rebuilt.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    ///     <paramref name="targetRoomId" /> would exceed <see cref="MaxActiveRooms" />.
    /// </exception>
    public virtual void WakeRoom(int targetRoomId)
    {
        if (targetRoomId == VoxelClassification.RoomSolid || targetRoomId == VoxelClassification.RoomVoid)
            return;

        if (WasAutomaticallySlept)
        {
            if (!CanResumeAutomaticallySleptDomainWithRoom(targetRoomId))
                throw new InvalidOperationException("Maximum active rooms reached for this chunk.");

            ResumeAutomaticallySleptDomain();
        }

        if (IsAwake)
        {
            for (var r = 0; r < ActiveRoomCount; r++)
            {
                if (ActiveRoomIds[r] == targetRoomId)
                {
                    // A successful wake is a local disturbance. Rebuild aggregate membership progressively
                    // from the materialized voxel state instead of retaining a pre-disturbance grouping.
                    VoxelAggregates.Reset();
                    _wasAutomaticallySlept = false;
                    SleepTimer = 0;
                    MarkChanged();
                    return;
                }
            }

        }

        int prospectiveRoomCount = IsAwake ? ActiveRoomCount : 0;
        if (prospectiveRoomCount >= MaxActiveRooms)
        {
            throw new InvalidOperationException("Maximum active rooms reached for this chunk.");
        }

        VoxelAggregates.Reset();
        _wasAutomaticallySlept = false;
        if (!IsAwake)
        {
            ActiveRoomCount = 0;
            IsAwake = true;
        }

        ActiveRoomIds[ActiveRoomCount] = targetRoomId;
        ActiveRoomCount++;
        SleepTimer = 0;
        RebuildActiveAirIndices();
        MarkChanged();
    }

    /// <summary>
    ///     Wakes the classification seed addressed by a specific voxel, or only resets the lifecycle when that
    ///     voxel is already in the active solver domain. Because room labels are activation seeds, disconnected
    ///     passable regions carrying the same label are activated together.
    /// </summary>
    internal void WakeVoxel(ushort localVoxelIndex)
    {
        int roomId = VoxelRoomMap[localVoxelIndex];
        if (roomId == VoxelClassification.RoomSolid || roomId == VoxelClassification.RoomVoid)
            return;

        // Automatic sleep is chunk-wide. Any successful local wake first restores the exact solver domain that
        // qualified for sleep, then adds a new target component only if retained room capacity permits it.
        if (WasAutomaticallySlept)
        {
            Span<ushort> requestedVoxel = stackalloc ushort[1];
            requestedVoxel[0] = localVoxelIndex;
            if (!CanWakeVoxels(requestedVoxel))
                throw new InvalidOperationException("Maximum active rooms reached for this chunk.");

            ResumeAutomaticallySleptDomain();
        }

        if (IsAwake && IsVoxelActive(localVoxelIndex))
        {
            VoxelAggregates.Reset();
            _wasAutomaticallySlept = false;
            SleepTimer = 0;
            MarkChanged();
            return;
        }

        WakeRoom(roomId);
    }

    private bool CanResumeAutomaticallySleptDomainWithRoom(int targetRoomId)
    {
        var retainedRoomCount = 0;
        for (var roomIndex = 0; roomIndex < ActiveRoomCount; roomIndex++)
        {
            int roomId = ActiveRoomIds[roomIndex];
            if (!HasPassableVoxelForRoom(roomId))
                continue;

            retainedRoomCount++;
            if (roomId == targetRoomId)
                return true;
        }

        return retainedRoomCount < MaxActiveRooms;
    }

    private void ResumeAutomaticallySleptDomain()
    {
        Debug.Assert(WasAutomaticallySlept);

        // Topology edits can wake a retained voxel after changing its classification. Remove seeds that no
        // longer exist before rebuilding so stale labels cannot consume room capacity or seed empty domains.
        var retainedRoomCount = 0;
        for (var roomIndex = 0; roomIndex < ActiveRoomCount; roomIndex++)
        {
            int roomId = ActiveRoomIds[roomIndex];
            if (HasPassableVoxelForRoom(roomId))
                ActiveRoomIds[retainedRoomCount++] = roomId;
        }

        ActiveRoomCount = retainedRoomCount;
        _wasAutomaticallySlept = false;
        IsAwake = true;
        SleepTimer = 0;
        RebuildActiveAirIndices();
    }

    private bool HasPassableVoxelForRoom(int roomId)
    {
        if (roomId == VoxelClassification.RoomSolid || roomId == VoxelClassification.RoomVoid)
            return false;

        for (var voxelIndex = 0; voxelIndex < VoxelCount; voxelIndex++)
        {
            if (VoxelRoomMap[voxelIndex] == roomId)
                return true;
        }

        return false;
    }

    /// <summary>Returns whether a voxel is present in the current sorted active-air domain.</summary>
    internal bool IsVoxelActive(ushort localVoxelIndex)
    {
        return IsAwake &&
               Array.BinarySearch(ActiveAirIndices, 0, ActiveAirCount, localVoxelIndex) >= 0;
    }

    /// <summary>
    ///     Determines whether all requested voxel components can be activated without exceeding room-seed
    ///     capacity. The simulation is prospective: each accepted seed expands through the same passable
    ///     closure as <see cref="RebuildActiveAirIndices" /> before the next request is evaluated. If any
    ///     request would resume an automatically slept domain, all retained seeds participate in the preflight.
    /// </summary>
    internal bool CanWakeVoxels(ReadOnlySpan<ushort> localVoxelIndices)
    {
        bool[] included = ArrayPool<bool>.Shared.Rent(VoxelCount);
        int[] queue = ArrayPool<int>.Shared.Rent(VoxelCount);
        Array.Clear(included, 0, VoxelCount);
        try
        {
            var activeRooms = new HashSet<int>();
            if (IsAwake)
            {
                for (var activeIndex = 0; activeIndex < ActiveAirCount; activeIndex++)
                    included[ActiveAirIndices[activeIndex]] = true;
                for (var roomIndex = 0; roomIndex < ActiveRoomCount; roomIndex++)
                    activeRooms.Add(ActiveRoomIds[roomIndex]);
            }
            else if (WasAutomaticallySlept &&
                     ContainsWakeableVoxel(localVoxelIndices))
            {
                // An automatic sleeper resumes every still-valid retained seed before any state is applied.
                // Model that union during preflight so a later request cannot exceed capacity after an earlier
                // mutation has already committed.
                for (var roomIndex = 0; roomIndex < ActiveRoomCount; roomIndex++)
                {
                    int roomId = ActiveRoomIds[roomIndex];
                    if (!HasPassableVoxelForRoom(roomId) || !activeRooms.Add(roomId))
                        continue;
                    if (activeRooms.Count > MaxActiveRooms)
                        return false;

                    IncludeProspectiveRoomClosure(roomId, included, queue);
                }
            }

            foreach (ushort localVoxelIndex in localVoxelIndices)
            {
                if (included[localVoxelIndex])
                    continue;

                int roomId = VoxelRoomMap[localVoxelIndex];
                if (roomId == VoxelClassification.RoomSolid || roomId == VoxelClassification.RoomVoid)
                    continue;
                if (activeRooms.Add(roomId) && activeRooms.Count > MaxActiveRooms)
                    return false;

                IncludeProspectiveRoomClosure(roomId, included, queue);
            }

            return true;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(queue);
            ArrayPool<bool>.Shared.Return(included);
        }
    }

    private bool ContainsWakeableVoxel(ReadOnlySpan<ushort> localVoxelIndices)
    {
        foreach (ushort localVoxelIndex in localVoxelIndices)
        {
            int roomId = VoxelRoomMap[localVoxelIndex];
            if (roomId != VoxelClassification.RoomSolid &&
                roomId != VoxelClassification.RoomVoid)
                return true;
        }

        return false;
    }

    private void IncludeProspectiveRoomClosure(int roomId, bool[] included, int[] queue)
    {
        var queuedCount = 0;
        for (var voxelIndex = 0; voxelIndex < VoxelCount; voxelIndex++)
        {
            if (!included[voxelIndex] && VoxelRoomMap[voxelIndex] == roomId)
            {
                included[voxelIndex] = true;
                queue[queuedCount++] = voxelIndex;
            }
        }

        for (var queuedIndex = 0; queuedIndex < queuedCount; queuedIndex++)
        {
            int componentVoxel = queue[queuedIndex];
            int x = componentVoxel % Width;
            int yz = componentVoxel / Width;
            int y = yz % Height;
            int z = yz / Height;
            if (x > 0)
                TryEnqueuePassableVoxel(componentVoxel - 1, included, queue, ref queuedCount);
            if (x + 1 < Width)
                TryEnqueuePassableVoxel(componentVoxel + 1, included, queue, ref queuedCount);
            if (y > 0)
                TryEnqueuePassableVoxel(componentVoxel - Width, included, queue, ref queuedCount);
            if (y + 1 < Height)
                TryEnqueuePassableVoxel(componentVoxel + Width, included, queue, ref queuedCount);
            int layerSize = Width * Height;
            if (z > 0)
                TryEnqueuePassableVoxel(componentVoxel - layerSize,
                    included, queue, ref queuedCount);
            if (z + 1 < Depth)
                TryEnqueuePassableVoxel(componentVoxel + layerSize,
                    included, queue, ref queuedCount);
        }
    }

    private void TryEnqueuePassableVoxel(int voxelIndex, bool[] included,
        int[] queue, ref int queuedCount)
    {
        if (included[voxelIndex])
            return;

        int roomId = VoxelRoomMap[voxelIndex];
        if (roomId == VoxelClassification.RoomSolid || roomId == VoxelClassification.RoomVoid)
            return;

        included[voxelIndex] = true;
        queue[queuedCount++] = voxelIndex;
    }

    /// <summary>
    ///     Rebuilds the dense list of voxel indices in passable components seeded by active rooms.
    /// </summary>
    /// <remarks>
    ///     Room IDs select starting voxels, not barriers. The traversal expands through every face-connected
    ///     voxel except solid and void classifications, matching the domain used by intra-chunk gas and heat
    ///     transfer. Disconnected components without an active-room seed remain inactive. The final list is
    ///     stored in ascending flat-index order for deterministic solver traversal.
    /// </remarks>
    public void RebuildActiveAirIndices()
    {
        VoxelAggregates.Reset();
        bool[] included = ArrayPool<bool>.Shared.Rent(VoxelCount);
        Array.Clear(included, 0, VoxelCount);
        var queuedCount = 0;
        try
        {
            for (ushort voxelIndex = 0; voxelIndex < VoxelCount; voxelIndex++)
            {
                int roomId = VoxelRoomMap[voxelIndex];
                for (var roomIndex = 0; roomIndex < ActiveRoomCount; roomIndex++)
                {
                    if (ActiveRoomIds[roomIndex] != roomId)
                        continue;

                    included[voxelIndex] = true;
                    ActiveAirIndices[queuedCount++] = voxelIndex;
                    break;
                }
            }

            for (var queuedIndex = 0; queuedIndex < queuedCount; queuedIndex++)
            {
                int voxelIndex = ActiveAirIndices[queuedIndex];
                int x = voxelIndex % Width;
                int yz = voxelIndex / Width;
                int y = yz % Height;
                int z = yz / Height;
                if (x > 0)
                    TryEnqueuePassableVoxel(voxelIndex - 1, included, ref queuedCount);
                if (x + 1 < Width)
                    TryEnqueuePassableVoxel(voxelIndex + 1, included, ref queuedCount);
                if (y > 0)
                    TryEnqueuePassableVoxel(voxelIndex - Width, included, ref queuedCount);
                if (y + 1 < Height)
                    TryEnqueuePassableVoxel(voxelIndex + Width, included, ref queuedCount);
                int layerSize = Width * Height;
                if (z > 0)
                    TryEnqueuePassableVoxel(voxelIndex - layerSize, included, ref queuedCount);
                if (z + 1 < Depth)
                    TryEnqueuePassableVoxel(voxelIndex + layerSize, included, ref queuedCount);
            }

            ActiveAirCount = 0;
            for (ushort voxelIndex = 0; voxelIndex < VoxelCount; voxelIndex++)
            {
                if (included[voxelIndex])
                    ActiveAirIndices[ActiveAirCount++] = voxelIndex;
            }
        }
        finally
        {
            ArrayPool<bool>.Shared.Return(included);
        }
    }

    private void TryEnqueuePassableVoxel(int voxelIndex, bool[] included, ref int queuedCount)
    {
        if (included[voxelIndex])
            return;

        int roomId = VoxelRoomMap[voxelIndex];
        if (roomId == VoxelClassification.RoomSolid || roomId == VoxelClassification.RoomVoid)
            return;

        included[voxelIndex] = true;
        ActiveAirIndices[queuedCount++] = (ushort)voxelIndex;
    }

    /// <summary>
    ///     Marks the chunk as sleeping so that it is skipped by simulation ticks.
    /// </summary>
    public virtual void Sleep()
    {
        _wasAutomaticallySlept = false;
        IsAwake = false;
        MarkChanged();
    }

    /// <summary>Enters solver-qualified sleep while retaining provenance for configuration invalidation.</summary>
    internal void SleepAutomatically()
    {
        _wasAutomaticallySlept = true;
        IsAwake = false;
        MarkChanged();
    }

    /// <summary>Whether the current sleeping state was entered by automatic convergence logic.</summary>
    internal bool WasAutomaticallySlept => !IsAwake && _wasAutomaticallySlept;

    /// <summary>
    ///     Invalidates solver-derived equilibrium state after a physics configuration or pipeline change.
    ///     Explicitly slept chunks remain frozen; automatic sleepers resume their retained active domain.
    /// </summary>
    internal void InvalidateSolverDerivedState()
    {
        if (!IsAwake && !_wasAutomaticallySlept)
            return;

        VoxelAggregates.Reset();
        SleepTimer = 0;
        if (_wasAutomaticallySlept)
            ResumeAutomaticallySleptDomain();

        MarkChanged();
    }

    /// <summary>
    ///     Adds gas to a voxel and updates pressure with the supplied ideal-gas pressure coefficient.
    /// </summary>
    /// <param name="localVoxelIndex">The flat index of the target voxel within this chunk.</param>
    /// <param name="gasId">The ID of the gas to add.</param>
    /// <param name="molesToAdd">The number of moles to add.</param>
    /// <param name="temperature">The temperature of the injected gas, in kelvins (K).</param>
    /// <param name="effectiveMolarHeatCapacityAtConstantVolume">
    ///     The already-resolved, finite, positive molar heat capacity at constant volume, in J/(mol·K).
    /// </param>
    /// <param name="pressurePerMoleKelvin">
    ///     The already-resolved ideal-gas coefficient <c>R/V</c>, in Pa/(mol·K).
    /// </param>
    public void InjectGasToVoxel(ushort localVoxelIndex, int gasId, float molesToAdd, float temperature,
        float effectiveMolarHeatCapacityAtConstantVolume, float pressurePerMoleKelvin)
    {
        Debug.Assert(float.IsFinite(effectiveMolarHeatCapacityAtConstantVolume) &&
                     effectiveMolarHeatCapacityAtConstantVolume > 0f);
        Debug.Assert(float.IsFinite(pressurePerMoleKelvin) && pressurePerMoleKelvin > 0f);

        if (!IsAwake)
            return;

        int room = VoxelRoomMap[localVoxelIndex];
        if (room == VoxelClassification.RoomSolid)
            return;
        if (room == VoxelClassification.RoomVoid)
            return;

        float currentHeatCapacity = TotalHeatCapacity[localVoxelIndex];
        var currentTotalMoles = 0d;
        var currentGasMoles = 0f;
        for (var gas = 0; gas < ActiveGasCount; gas++)
        {
            float storedMoles = ActiveGases[gas].Moles[localVoxelIndex];
            if (!float.IsFinite(storedMoles) || storedMoles < 0f)
                throw new InvalidOperationException("The existing gas amount is not representable.");
            currentTotalMoles += storedMoles;
            if (ActiveGases[gas].GasId == gasId)
                currentGasMoles = storedMoles;
        }

        float combinedGasMoles = (float)((double)currentGasMoles + molesToAdd);
        float combinedTotalMoles = (float)(currentTotalMoles + molesToAdd);
        double incomingHeatCapacity = (double)molesToAdd *
                                      effectiveMolarHeatCapacityAtConstantVolume;
        float newHeatCapacity = (float)(currentHeatCapacity + incomingHeatCapacity);
        if (!float.IsFinite(combinedGasMoles) || !float.IsFinite(combinedTotalMoles) ||
            !float.IsFinite(newHeatCapacity))
        {
            throw new InvalidOperationException(
                "The injected mixture exceeds the supported numeric range.");
        }

        float currentTemp = Temperature[localVoxelIndex];
        float newTemp = currentHeatCapacity > 0f && newHeatCapacity > 0f
            ? currentTemp == temperature
                ? currentTemp
                // Interpolation avoids the overflow-prone sum C1*T1 + C2*T2.
                : (float)(currentTemp + ((double)temperature - currentTemp) *
                    incomingHeatCapacity / newHeatCapacity)
            : temperature;
        float newPressure = (float)((double)combinedTotalMoles * newTemp * pressurePerMoleKelvin);
        if (!float.IsFinite(newTemp) || newTemp < 0f || !float.IsFinite(newPressure))
        {
            throw new InvalidOperationException(
                "The injected mixture exceeds the supported numeric range.");
        }

        VoxelAggregates.Reset();
        SleepTimer = 0;

        int targetChannelIndex = GetOrCreateGasChannel(gasId);
        ActiveGases[targetChannelIndex].Moles[localVoxelIndex] = combinedGasMoles;

        TotalHeatCapacity[localVoxelIndex] = newHeatCapacity;
        Temperature[localVoxelIndex] = newTemp;
        TotalPressure[localVoxelIndex] = newPressure;
        MarkChanged();
    }

    internal int GetOrCreateGasChannel(int gasId)
    {
        for (var index = 0; index < ActiveGasCount; index++)
        {
            if (ActiveGases[index].GasId == gasId)
                return index;
        }

        if (ActiveGasCount == ActiveGases.Length)
        {
            int newLength = checked(Math.Max(ActiveGases.Length * 2, ActiveGasCount + 1));
            Array.Resize(ref ActiveGases, newLength);
        }

        int channelIndex = ActiveGasCount;
        var channel = new GasChannel();
        channel.Initialize(gasId, VoxelCount);
        ActiveGases[channelIndex] = channel;
        ActiveGasCount++;
        return channelIndex;
    }

    /// <summary>
    ///     Creates a snapshot of the chunk's current network state.
    /// </summary>
    /// <returns>A snapshot containing copies of the chunk's position, pressure, temperature, gas, and room data.</returns>
    [PublicAPI]
    public AtmosChunkSnapshot GetNetworkSnapshot(
        AtmosChunkSnapshotFields fields = AtmosChunkSnapshotFields.All)
    {
        if ((fields & ~AtmosChunkSnapshotFields.All) != 0)
            throw new ArgumentOutOfRangeException(nameof(fields));

        var snapshot = new AtmosChunkSnapshot
        {
            Fields = fields,
            HasExplicitFields = true,
            GridPosition = GridPosition,
            Dimensions = Dimensions,
            TotalPressure = fields.HasFlag(AtmosChunkSnapshotFields.Pressure)
                ? TotalPressure.ToArray()
                : [],
            Temperature = fields.HasFlag(AtmosChunkSnapshotFields.Temperature)
                ? Temperature.ToArray()
                : [],
            Gases = fields.HasFlag(AtmosChunkSnapshotFields.Gases)
                ? new GasSnapshot[ActiveGasCount]
                : [],
            VoxelRoomMap = fields.HasFlag(AtmosChunkSnapshotFields.VoxelClassification)
                ? VoxelRoomMap.ToArray()
                : [],
            ActiveAirCount = ActiveAirCount,
            ActiveGasCount = ActiveGasCount,
            IsAwake = IsAwake,
            SleepTimer = SleepTimer
        };

        if (fields.HasFlag(AtmosChunkSnapshotFields.Gases))
        {
            for (var g = 0; g < ActiveGasCount; g++)
            {
                snapshot.Gases[g] = new GasSnapshot
                {
                    GasId = ActiveGases[g].GasId,
                    Moles = new float[VoxelCount]
                };
                Array.Copy(ActiveGases[g].Moles, snapshot.Gases[g].Moles, VoxelCount);
            }
        }

        // Kernel snapshot entry points serialize this copy against ticks and direct mutations.
        // Capture the version after all requested fields have been detached.
        snapshot.Version = Version;
        return snapshot;
    }

    /// <summary>
    ///     Converts local voxel coordinates to an index into the chunk's flat arrays.
    /// </summary>
    /// <param name="x">The local x coordinate, from zero through <see cref="Width" /> minus one.</param>
    /// <param name="y">The local y coordinate, from zero through <see cref="Height" /> minus one.</param>
    /// <param name="z">The local z coordinate, from zero through <see cref="Depth" /> minus one.</param>
    /// <returns>The flat voxel index.</returns>
    [PublicAPI]
    public ushort GetIndex(int x, int y, int z)
    {
        return GetIndex(new Int3(x, y, z));
    }

    /// <inheritdoc cref="GetIndex(int, int, int)" />
    [PublicAPI]
    public ushort GetIndex(Int3 vec)
    {
        return (ushort)VoxelRoomMap.GetIndex(vec);
    }

    /// <summary>
    ///     Converts a flat voxel index to local x, y, and z coordinates.
    /// </summary>
    /// <param name="index">The flat voxel index.</param>
    /// <returns>The local coordinates as an <c>(x, y, z)</c> tuple.</returns>
    [PublicAPI]
    public (int x, int y, int z) GetXyz(ushort index)
    {
        var position = GetXyzInt3(index);
        return (position.X, position.Y, position.Z);
    }

    /// <summary>
    ///     Converts a flat voxel index to local coordinates as an <see cref="Int3" />.
    /// </summary>
    /// <param name="index">The flat voxel index.</param>
    /// <returns>The local voxel coordinates.</returns>
    [PublicAPI]
    public Int3 GetXyzInt3(ushort index)
    {
        return VoxelRoomMap.GetPosition(index);
    }

    private void EnsureInitialized<T>(ref FlatArray<T> array, Int3 dimensions)
    {
        if (!array.IsInitialized || array.Length != VoxelCount)
            array = new FlatArray<T>(new T[VoxelCount], dimensions);
        else if (array.Dimensions != dimensions)
            array = array.Reshape(dimensions);
    }

    private static int GetValidatedVoxelCount(int width, int height, int depth)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(depth);
        if (width > AtmosChunkConstants.MaximumVoxelCount || height > AtmosChunkConstants.MaximumVoxelCount ||
            depth > AtmosChunkConstants.MaximumVoxelCount)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width,
                $"No chunk dimension may exceed {AtmosChunkConstants.MaximumVoxelCount}.");
        }

        long voxelCount = (long)width * height * depth;
        if (voxelCount > AtmosChunkConstants.MaximumVoxelCount)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width,
                $"Chunk dimensions contain {voxelCount} voxels, but at most " +
                $"{AtmosChunkConstants.MaximumVoxelCount} are supported.");
        }

        return (int)voxelCount;
    }
}
