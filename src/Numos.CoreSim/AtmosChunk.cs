using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using JetBrains.Annotations;
using Magnitudo;
using Numos.Collections;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.CoreSim.Solvers;
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
    private readonly Lock _fieldGate = new();

    /// <summary>
    ///     Number of valid entries at the beginning of <see cref="ActiveAirIndices" />.
    /// </summary>
    public int ActiveAirCount;

    /// <summary>
    ///     Flat indices of gas-bearing voxels in this chunk while it is awake.
    /// </summary>
    /// <remarks>
    ///     Only the first <see cref="ActiveAirCount" /> entries are valid. Rebuild this list with
    ///     <see cref="RebuildActiveAirIndices" /> after changing <see cref="VoxelRoomMap" />.
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
    ///     A process-local index assigned when this chunk is registered.
    /// </summary>
    /// <remarks>
    ///     Lets hot per-tick solver lookups (such as the boundary flow injection buffer) use array
    ///     indexing instead of a dictionary keyed on <see cref="GridPosition" />. The ID is unique only among
    ///     currently registered chunks. It is not observable simulation state, so <see cref="AtmosChunkVersion" />
    ///     and the state hash exclude it. Use it only to index a solver-owned lookup table.
    ///     Numos recycles IDs after a chunk is unregistered. A lookup table keyed on this ID must record which
    ///     chunk owns each slot and check that before it trusts old contents.
    /// </remarks>
    public int DenseId;

    /// <summary>
    ///     The number of voxels along the z-axis.
    /// </summary>
    public int Depth;

    /// <summary>
    ///     The position of this chunk in the chunk grid.
    /// </summary>
    public Int3 GridPosition;

    /// <summary>
    ///     The number of voxels along the y-axis.
    /// </summary>
    public int Height;

    /// <summary>
    ///     Whether this chunk is eligible to be processed by the simulation.
    ///     A sleeping chunk is skipped during simulation ticks.
    /// </summary>
    public bool IsAwake;

    /// <summary>
    ///     Whether each voxel currently represents vacuum.
    /// </summary>
    /// <remarks>
    ///     The advection stage derives this state from the complete pressure field before vacuum cleanup.
    ///     Consumers should use it instead of interpreting a vacuum voxel's temperature.
    /// </remarks>
    public FlatArray<bool> IsVacuum;

    /// <summary>
    ///     Number of consecutive simulation ticks for which this chunk has remained below the sleep threshold.
    /// </summary>
    /// <seealso cref="AtmosConfig.SleepThreshold" />
    public int SleepTimer;

    /// <summary>
    ///     Temperature for each voxel, in kelvins (K), indexed by flat voxel index or local coordinate.
    /// </summary>
    [ElementQuantity("temperature")]
    public FlatArray<Kelvin> Temperature;

    /// <summary>
    ///     Total x-momentum of the gas in each voxel, in kilogram metres per second (kg·m/s), indexed by flat voxel
    ///     index or local coordinate.
    /// </summary>
    /// <remarks>
    ///     This is the momentum of the voxel's whole gas content, not a velocity, so adding gas at rest lowers the
    ///     implied velocity without touching the stored value. It is zero whenever the voxel holds no gas: every path
    ///     that empties a voxel (<see cref="SetVoxelToVacuum" />, a classification change, a mixture write that
    ///     removes the last mole) zeroes it, so a vacuum voxel never carries stale momentum into a later tick.
    /// </remarks>
    [ElementQuantity("momentum")]
    public FlatArray<KilogramMetrePerSecond> MomentumX;

    /// <summary>
    ///     Total y-momentum of the gas in each voxel, in kilogram metres per second (kg·m/s). Shares the contract of
    ///     <see cref="MomentumX" />.
    /// </summary>
    [ElementQuantity("momentum")]
    public FlatArray<KilogramMetrePerSecond> MomentumY;

    /// <summary>
    ///     Total z-momentum of the gas in each voxel, in kilogram metres per second (kg·m/s). Shares the contract of
    ///     <see cref="MomentumX" />.
    /// </summary>
    [ElementQuantity("momentum")]
    public FlatArray<KilogramMetrePerSecond> MomentumZ;

    /// <summary>
    ///     Cached total heat capacity for each voxel, in joules per kelvin (J/K).
    /// </summary>
    /// <remarks>
    ///     For each refreshed active voxel, the value is the sum of
    ///     <c>moles × effective molar heat capacity</c> for its gases. Entries outside
    ///     <see cref="ActiveAirIndices" /> are not authoritative until that voxel is active and refreshed.
    ///     Values are total heat capacities, not molar quantities.
    /// </remarks>
    [ElementQuantity("heatCapacity")]
    public FlatArray<JoulePerKelvin> TotalHeatCapacity;

    /// <summary>
    ///     Cached pressure for each voxel, in pascals (Pa), indexed by flat voxel index or local coordinate.
    /// </summary>
    /// <remarks>These values are recomputed by the simulation each tick.</remarks>
    [ElementQuantity("pressure")]
    public FlatArray<Pascal> TotalPressure;

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
    ///     The number of voxels along the x-axis.
    /// </summary>
    public int Width;
    private int _capturedFieldCount;

    // Indexed by ChunkFieldDescriptor.Slot. Readers load the array without a lock; writers hold _fieldGate and
    // publish a grown copy instead of resizing in place.
    private SolverArrayStorage?[]? _fields;

    private long _generation;
    private long _revision;

    /// <summary>
    ///     Creates a chunk with the specified dimensions.
    /// </summary>
    /// <param name="width">The number of voxels along the x axis.</param>
    /// <param name="height">The number of voxels along the y axis.</param>
    /// <param name="depth">The number of voxels along the z axis.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     A dimension is non-positive or the combined voxel count exceeds
    ///     <see cref="AtmosChunkConstants.MaximumVoxelCount" />.
    /// </exception>
    public AtmosChunk(
        int width = AtmosChunkConstants.DefaultWidth,
        int height = AtmosChunkConstants.DefaultHeight,
        int depth = AtmosChunkConstants.DefaultDepth)
    {
        int voxelCount = GetValidatedVoxelCount(width, height, depth);
        Width = width;
        Height = height;
        Depth = depth;
        VoxelCount = voxelCount;
        EnsureInitialized();
        IsVacuum.Fill(true);
    }

    internal bool HasCapturedSolverArrays => Volatile.Read(ref _capturedFieldCount) > 0;

    /// <summary>
    ///     Identity and revision used by conditional snapshot consumers.
    /// </summary>
    public AtmosChunkVersion Version => new(_generation, Interlocked.Read(ref _revision));

    /// <summary>
    ///     The number of voxels along each axis.
    /// </summary>
    public Int3 Dimensions => new(Width, Height, Depth);

    /// <summary>
    ///     Allocates any per-voxel array that is missing or sized for different dimensions.
    /// </summary>
    /// <remarks>
    ///     Existing arrays are reused when they already have the required length. This method does not
    ///     clear existing values or reset active counts. Use <see cref="Initialize" /> to reset the chunk.
    /// </remarks>
    [MemberNotNull(nameof(ActiveAirIndices), nameof(ActiveGases))]
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
        EnsureInitialized(ref MomentumX, dimensions);
        EnsureInitialized(ref MomentumY, dimensions);
        EnsureInitialized(ref MomentumZ, dimensions);
        EnsureInitialized(ref IsVacuum, dimensions);
        if (ActiveGases == null)
            ActiveGases = new GasChannel[AtmosChunkConstants.InitialGasChannelCapacity];
    }

    /// <summary>
    ///     Initializes or reinitializes the chunk with the specified position and dimensions.
    /// </summary>
    /// <param name="position">The chunk's position in the grid of chunks.</param>
    /// <param name="width">The number of voxels along the x axis.</param>
    /// <param name="height">The number of voxels along the y axis.</param>
    /// <param name="depth">The number of voxels along the z axis.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     A dimension is non-positive or the combined voxel count exceeds
    ///     <see cref="AtmosChunkConstants.MaximumVoxelCount" />.
    /// </exception>
    /// <remarks>
    ///     Initialization puts the chunk to sleep and resets all active counts and timers. It clears
    ///     the per-voxel and gas-channel data and detaches solver arrays.
    /// </remarks>
    [PublicAPI]
    public void Initialize(
        Int3 position,
        int width = AtmosChunkConstants.DefaultWidth,
        int height = AtmosChunkConstants.DefaultHeight,
        int depth = AtmosChunkConstants.DefaultDepth)
    {
        int voxelCount = GetValidatedVoxelCount(width, height, depth);
        ResetFields();
        GridPosition = position;
        IsAwake = false;
        Width = width;
        Height = height;
        Depth = depth;
        VoxelCount = voxelCount;

        EnsureInitialized();

        ActiveAirCount = 0;
        ActiveGasCount = 0;
        SleepTimer = 0;

        VoxelRoomMap.Clear();
        Array.Clear(ActiveAirIndices, 0, ActiveAirIndices.Length);
        TotalPressure.Clear();
        TotalHeatCapacity.Clear();
        Temperature.Clear();
        MomentumX.Clear();
        MomentumY.Clear();
        MomentumZ.Clear();
        IsVacuum.Fill(true);
        Array.Clear(ActiveGases, 0, ActiveGases.Length);

        _generation = Interlocked.Increment(ref _nextGeneration);
        Interlocked.Exchange(ref _revision, 1);
    }

    /// <summary>
    ///     Gives a chunk straight out of the constructor its position and a fresh generation. This equals
    ///     <see cref="Initialize" /> with the constructor's dimensions, except that it skips clearing new arrays.
    /// </summary>
    internal void InitializeConstructed(Int3 position)
    {
        GridPosition = position;
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
        ResetFields();
        if (ActiveGases != null)
        {
            for (int i = 0; i < ActiveGasCount; i++)
            {
                ActiveGases[i].Release();
            }
        }
    }

    /// <summary>
    ///     Gets a field's storage, allocating it on first use in this chunk.
    /// </summary>
    /// <typeparam name="T">The field's element type.</typeparam>
    /// <param name="field">The field descriptor.</param>
    /// <returns>The live array, initially filled with default values.</returns>
    /// <remarks>
    ///     Safe to call from parallel workers, including the first touch of a field. Storage stays lazy because
    ///     allocating every registered field up front would add zero-filled arrays to every checkpoint and
    ///     change state hashes. Arrays survive ticks. Sleep and vacuum clear them only when the descriptor opts in. Initialization or
    ///     release detaches them.
    /// </remarks>
    internal T[] GetOrCreateField<T>(ChunkFieldDescriptor<T> field)
    {
        return FindField(Volatile.Read(ref _fields), field)?.Values ?? CreateField(field);
    }

    /// <summary>
    ///     Gets a field's storage if this chunk has already allocated it.
    /// </summary>
    /// <typeparam name="T">The field's element type.</typeparam>
    /// <param name="field">The field descriptor.</param>
    /// <param name="array">The live array, or <see langword="null" /> when the chunk has no storage for the field.</param>
    /// <returns><see langword="true" /> when the chunk has storage for <paramref name="field" />.</returns>
    /// <remarks>
    ///     Unlike <see cref="GetOrCreateField{T}" />, this never allocates. A read through it leaves checkpoints and
    ///     state hashes unchanged. Safe to call from parallel workers.
    /// </remarks>
    internal bool TryGetField<T>(ChunkFieldDescriptor<T> field, [NotNullWhen(true)] out T[]? array)
    {
        array = FindField(Volatile.Read(ref _fields), field)?.Values;
        return array != null;
    }

    private static SolverArrayStorage<T>? FindField<T>(SolverArrayStorage?[]? fields, ChunkFieldDescriptor<T> field)
    {
        int slot = field.Slot;
        return fields != null && (uint)slot < (uint)fields.Length ? fields[slot] as SolverArrayStorage<T> : null;
    }

    private T[] CreateField<T>(ChunkFieldDescriptor<T> field)
    {
        lock (_fieldGate)
        {
            if (FindField(_fields, field) is { } existing)
                return existing.Values;

            var storage = field.Allocate(VoxelCount);
            PublishField(field.Slot, storage);
            if (field.CaptureForRollback)
            {
                Interlocked.Increment(ref _capturedFieldCount);
                MarkChanged();
            }

            return storage.Values;
        }
    }

    // Caller holds _fieldGate.
    private void PublishField(int slot, SolverArrayStorage storage)
    {
        SolverArrayStorage?[]? fields = _fields;
        if (fields == null || slot >= fields.Length)
        {
            var grown = new SolverArrayStorage?[Math.Max(slot + 1, ChunkFieldRegistry.SlotCount)];
            fields?.CopyTo(grown, 0);
            grown[slot] = storage;
            Volatile.Write(ref _fields, grown);
            return;
        }

        Volatile.Write(ref fields[slot], storage);
    }

    private void ResetFields()
    {
        lock (_fieldGate)
        {
            Volatile.Write(ref _fields, null);
            Volatile.Write(ref _capturedFieldCount, 0);
        }
    }

    internal AtmosSolverArraySnapshot[] CaptureSolverArrays()
    {
        SolverArrayStorage?[]? fields = Volatile.Read(ref _fields);
        List<AtmosSolverArraySnapshot>? captured = null;
        if (fields != null)
        {
            foreach (var storage in fields)
            {
                if (storage is { CaptureForRollback: true })
                    (captured ??= []).Add(new AtmosSolverArraySnapshot(storage));
            }
        }

        if (captured == null)
            return [];

        // Slot order follows static initialization order, so it must never leak into checkpoints or hashes.
        captured.Sort(static (left, right) => string.CompareOrdinal(left.Key, right.Key));
        return captured.ToArray();
    }

    internal void RestoreSolverArrays(IReadOnlyList<AtmosSolverArraySnapshot> snapshots)
    {
        if (snapshots.Count == 0)
            return;

        lock (_fieldGate)
        {
            var fields = new SolverArrayStorage?[ChunkFieldRegistry.SlotCount];
            foreach (var snapshot in snapshots)
            {
                var storage = snapshot.Materialize();
                fields[storage.Field.Slot] = storage;
            }

            Volatile.Write(ref _fields, fields);
            Volatile.Write(ref _capturedFieldCount, snapshots.Count);
        }
    }

    /// <summary>
    ///     Wakes the chunk and rebuilds its active gas-bearing voxel index.
    /// </summary>
    /// <remarks>
    ///     Every non-solid, non-void voxel participates while the chunk is awake. Classification IDs do not
    ///     partition simulation work.
    /// </remarks>
    public virtual void Wake()
    {
        IsAwake = true;
        SleepTimer = 0;
        RebuildActiveAirIndices();
        MarkChanged();
    }

    /// <summary>
    ///     Rebuilds the dense list of gas-bearing voxel indices.
    /// </summary>
    /// <remarks>
    ///     The resulting list is stored in <see cref="ActiveAirIndices" /> and its valid length is written
    ///     to <see cref="ActiveAirCount" />. Call this after modifying voxel classifications.
    /// </remarks>
    public void RebuildActiveAirIndices()
    {
        ActiveAirCount = 0;
        for (ushort i = 0; i < VoxelCount; i++)
        {
            int classification = VoxelRoomMap[i];
            if (classification == VoxelClassification.RoomSolid || classification == VoxelClassification.RoomVoid)
                continue;

            ActiveAirIndices[ActiveAirCount] = i;
            ActiveAirCount++;
        }
    }

    /// <summary>
    ///     Marks the chunk as sleeping. Simulation ticks skip it.
    /// </summary>
    public virtual void Sleep()
    {
        IsAwake = false;
        MarkChanged();
        SolverArrayStorage?[]? fields = Volatile.Read(ref _fields);
        if (fields == null)
            return;

        foreach (var storage in fields)
        {
            if (storage is { Field.ClearOnSleep: true })
                storage.ClearAll();
        }
    }

    /// <summary>
    ///     Adds gas at <paramref name="temperature" /> to a voxel, blending the voxel's temperature by heat
    ///     capacity and recomputing its pressure. Solid and void voxels are ignored. A sleeping chunk wakes.
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
    /// TODO
    /// molesToAdd can be negative, this is helpful but should have related checks.
    public void InjectGasToVoxel(
        ushort localVoxelIndex, int gasId, Mole molesToAdd, Kelvin temperature,
        JoulePerMoleKelvin effectiveMolarHeatCapacityAtConstantVolume,
        PascalPerMoleKelvin pressurePerMoleKelvin)
    {
        Debug.Assert(
            float.IsFinite(effectiveMolarHeatCapacityAtConstantVolume) &&
            effectiveMolarHeatCapacityAtConstantVolume > 0f);

        Debug.Assert(float.IsFinite(pressurePerMoleKelvin) && pressurePerMoleKelvin > 0f);

        int classification = VoxelRoomMap[localVoxelIndex];
        if (classification == VoxelClassification.RoomSolid)
            return;

        if (classification == VoxelClassification.RoomVoid)
            return;

        if (!IsAwake)
            Wake();

        SleepTimer = 0;

        JoulePerKelvin currentHeatCapacity = TotalHeatCapacity[localVoxelIndex];

        int targetChannelIndex = GetOrCreateGasChannel(gasId);

        ActiveGases[targetChannelIndex].Moles[localVoxelIndex] += molesToAdd;

        Mole currentTotalMoles = 0f;
        for (int g = 0; g < ActiveGasCount; g++)
        {
            currentTotalMoles += ActiveGases[g].Moles[localVoxelIndex];
        }

        JoulePerKelvin incomingHeatCapacity = molesToAdd * effectiveMolarHeatCapacityAtConstantVolume;
        JoulePerKelvin newHeatCapacity = currentHeatCapacity + incomingHeatCapacity;
        Kelvin currentTemp = Temperature[localVoxelIndex];
        Kelvin newTemp = currentHeatCapacity > 0f && newHeatCapacity > 0f
            ? currentTemp == temperature
                ? currentTemp
                // Interpolation avoids the overflow-prone sum C1*T1 + C2*T2.
                : currentTemp + (temperature - currentTemp) * incomingHeatCapacity / newHeatCapacity
            : temperature;

        TotalHeatCapacity[localVoxelIndex] = newHeatCapacity;
        Temperature[localVoxelIndex] = newTemp;

        TotalPressure[localVoxelIndex] = currentTotalMoles * newTemp * pressurePerMoleKelvin;
        IsVacuum[localVoxelIndex] = TotalPressure[localVoxelIndex] <= 0f;
        MarkChanged();
    }

    /// <summary>
    ///     Reads a voxel's validated temperature and cached <see cref="TotalHeatCapacity" /> without recomputing it.
    /// </summary>
    /// <param name="config">Supplies the fallback for an invalid stored temperature.</param>
    /// <param name="voxelIndex">The flat voxel index.</param>
    /// <param name="temperature">The validated temperature, or zero when this returns <see langword="false" />.</param>
    /// <param name="heatCapacity">The cached heat capacity, or zero when this returns <see langword="false" />.</param>
    /// <returns>
    ///     <see langword="false" /> for a vacuum voxel or one whose cached heat capacity is non-finite or nonpositive.
    /// </returns>
    [PublicAPI]
    public bool TryGetThermalState(
        AtmosSolverConfigSnapshot config,
        ushort voxelIndex, out Kelvin temperature, out JoulePerKelvin heatCapacity)
    {
        heatCapacity = TotalHeatCapacity[voxelIndex];
        if (!float.IsFinite(heatCapacity) || heatCapacity <= 0f || IsVacuum[voxelIndex])
        {
            temperature = 0f;
            heatCapacity = 0f;
            return false;
        }

        temperature = config.GetValidatedTemp(Temperature[voxelIndex]);
        return true;
    }


    /// <summary>
    ///     Clears one voxel to vacuum: zeroes <see cref="TotalPressure" />, every gas channel,
    ///     <see cref="TotalHeatCapacity" />, and the momentum components, and sets <see cref="IsVacuum" />. Per-voxel fields that opt into vacuum
    ///     clearing are reset for this voxel.
    /// </summary>
    /// <param name="idx">Index of voxel</param>
    /// <remarks>Temperature is left as is, and the chunk revision is not advanced.</remarks>
    [PublicAPI]
    public void SetVoxelToVacuum(ushort idx)
    {
        TotalPressure[idx] = 0f;
        for (int g = 0; g < ActiveGasCount; g++)
        {
            ActiveGases[g].Moles[idx] = 0f;
        }

        TotalHeatCapacity[idx] = 0f;
        ZeroMomentum(idx);
        IsVacuum[idx] = true;

        // Solver workers call this per voxel, so walk only this chunk's allocated fields without locking.
        SolverArrayStorage?[]? fields = Volatile.Read(ref _fields);
        if (fields == null)
            return;

        foreach (var storage in fields)
        {
            if (storage is { Field: { ClearOnVacuum: true, FixedLength: null } })
                storage.ClearElement(idx);
        }
    }

    /// <summary>
    ///     Zeroes every momentum component of one voxel. Call it whenever a write leaves the voxel without gas, so
    ///     the "no gas, no momentum" invariant on <see cref="MomentumX" /> holds.
    /// </summary>
    internal void ZeroMomentum(ushort localVoxelIndex)
    {
        MomentumX[localVoxelIndex] = 0f;
        MomentumY[localVoxelIndex] = 0f;
        MomentumZ[localVoxelIndex] = 0f;
    }

    /// <summary>
    ///     Clears every voxel in the chunk to vacuum, as <see cref="SetVoxelToVacuum" /> does for one voxel.
    ///     Fixed-length fields that opt into vacuum clearing are also reset.
    /// </summary>
    [PublicAPI]
    public void SetChunkToVacuum()
    {
        TotalPressure.Fill(0f);
        for (int g = 0; g < ActiveGasCount; g++)
        {
            Array.Clear(ActiveGases[g].Moles, 0, ActiveGases[g].Moles.Length);
        }

        TotalHeatCapacity.Fill(0f);
        MomentumX.Fill(0f);
        MomentumY.Fill(0f);
        MomentumZ.Fill(0f);
        IsVacuum.Fill(true);

        SolverArrayStorage?[]? fields = Volatile.Read(ref _fields);
        if (fields == null)
            return;

        foreach (var storage in fields)
        {
            if (storage is { Field.ClearOnVacuum: true })
                storage.ClearAll();
        }
    }

    /// <summary>
    ///     Sets a specific voxel classification. Solid and void classifications clear the voxel to vacuum.
    /// </summary>
    /// <param name="idx">Index of voxel</param>
    /// <param name="roomId">Classification value to assign.</param>
    [PublicAPI]
    public void SetVoxelClassification(ushort idx, int roomId)
    {
        SetVoxelClassification(idx, new VoxelClassification(roomId));
    }

    /// <summary>
    ///     Sets a specific voxel classification. Solid and void classifications clear the voxel to vacuum.
    /// </summary>
    /// <param name="idx">Index of voxel</param>
    /// <param name="classification">Classification value to assign.</param>
    [PublicAPI]
    public void SetVoxelClassification(ushort idx, VoxelClassification classification)
    {
        if (classification.IsSolid || classification.IsVoid)
            SetVoxelToVacuum(idx);

        VoxelRoomMap[idx] = classification.RoomId;
    }


    /// <summary>
    ///     Sets every voxel classification. Solid and void classifications clear the chunk to vacuum.
    /// </summary>
    /// <param name="roomId">Classification value to assign.</param>
    [PublicAPI]
    public void SetChunkClassification(int roomId)
    {
        SetChunkClassification(new VoxelClassification(roomId));
    }

    /// <summary>
    ///     Sets the entire chunk classification. Solid and void classifications clear the chunk to vacuum.
    /// </summary>
    /// <param name="classification">Classification value to assign.</param>
    [PublicAPI]
    public void SetChunkClassification(VoxelClassification classification)
    {
        if (classification.IsSolid || classification.IsVoid)
            SetChunkToVacuum();

        VoxelRoomMap.Fill(classification.RoomId);
    }


    /// <summary>
    ///     Finds the channel index holding <paramref name="gasId" />, or -1 if this chunk has no channel for it.
    /// </summary>
    /// <remarks>
    ///     Channels are appended in the order gases first reach the chunk, so the same index can hold different
    ///     gases in different chunks. Look gas up by ID when you compare chunks.
    /// </remarks>
    internal int FindGasChannel(int gasId)
    {
        for (int index = 0; index < ActiveGasCount; index++)
        {
            if (ActiveGases[index].GasId == gasId)
                return index;
        }

        return -1;
    }

    internal int GetOrCreateGasChannel(int gasId)
    {
        int existing = FindGasChannel(gasId);
        if (existing >= 0)
            return existing;

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
    ///     Creates a detached snapshot of the chunk's current state.
    /// </summary>
    /// <param name="fields">The per-voxel fields to copy.</param>
    /// <returns>A snapshot containing selected physical fields and solver arrays opted into capture.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="fields" /> contains undefined flags.</exception>
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
            TotalHeatCapacity = fields.HasFlag(AtmosChunkSnapshotFields.TotalHeatCapacity)
                ? TotalHeatCapacity.ToArray()
                : [],
            Gases = fields.HasFlag(AtmosChunkSnapshotFields.Gases)
                ? new GasSnapshot[ActiveGasCount]
                : [],
            VoxelRoomMap = fields.HasFlag(AtmosChunkSnapshotFields.VoxelClassification)
                ? VoxelRoomMap.ToArray()
                : [],
            SolverArrays = fields.HasFlag(AtmosChunkSnapshotFields.SolverArrays)
                ? Array.AsReadOnly(CaptureSolverArrays())
                : Array.Empty<AtmosSolverArraySnapshot>(),
            ActiveAirCount = ActiveAirCount,
            ActiveGasCount = ActiveGasCount,
            IsAwake = IsAwake,
            SleepTimer = SleepTimer
        };

        if (fields.HasFlag(AtmosChunkSnapshotFields.Gases))
        {
            for (int g = 0; g < ActiveGasCount; g++)
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

    /// <inheritdoc cref="GetIndex(int, int, int)" />
    [PublicAPI]
    public ushort GetIndexUnsafe(Int3 vec)
    {
        return (ushort)VoxelRoomMap.GetIndexUnsafe(vec);
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
        if (width > AtmosChunkConstants.MaximumVoxelCount ||
            height > AtmosChunkConstants.MaximumVoxelCount ||
            depth > AtmosChunkConstants.MaximumVoxelCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                width,
                $"No chunk dimension may exceed {AtmosChunkConstants.MaximumVoxelCount}.");
        }

        long voxelCount = (long)width * height * depth;
        if (voxelCount > AtmosChunkConstants.MaximumVoxelCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                width,
                $"Chunk dimensions contain {voxelCount} voxels, but at most " +
                $"{AtmosChunkConstants.MaximumVoxelCount} are supported.");
        }

        return (int)voxelCount;
    }
}