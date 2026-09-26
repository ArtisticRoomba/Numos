using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using JetBrains.Annotations;
using Numos.Chunks;
using Numos.Collections;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.CoreSim.Solvers;
using Numos.Maths;
using Numos.Units;

namespace Numos.CoreSim;

/// <summary>
///     Represents the simulation state for a fixed-size voxel chunk.
/// </summary>
/// <remarks>
///     Chunk-owned per-voxel data supports both flat-index and <see cref="Int3" /> coordinate access.
///     Use <see cref="Chunk.GetIndex(int,int,int)" /> and <see cref="Chunk.GetXyzInt3" /> when converting indices
///     for scalar-indexed storage such as gas channels (because... you know.... they aren't physical).
/// </remarks>
internal sealed class AtmosChunk : Chunk, IChunkInitializer<AtmosChunk>
{
    private static long _nextGeneration;

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

    private long _generation;
    private long _revision;
    private Dictionary<object, SolverArrayStorage>? _solverArrays;

    /// <summary>
    ///     Creates a chunk with the specified dimensions.
    /// </summary>
    /// <param name="width">The number of voxels along the x axis.</param>
    /// <param name="height">The number of voxels along the y axis.</param>
    /// <param name="depth">The number of voxels along the z axis.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     A dimension is non-positive or the combined voxel count exceeds
    ///     <see cref="ChunkConstants.MaximumVoxelCount" />.
    /// </exception>
    public AtmosChunk(
        int width = ChunkConstants.DefaultWidth,
        int height = ChunkConstants.DefaultHeight,
        int depth = ChunkConstants.DefaultDepth)
    {
        int voxelCount = GetValidatedVoxelCount(width, height, depth);
        Width = width;
        Height = height;
        Depth = depth;
        VoxelCount = voxelCount;
        EnsureInitialized();
        IsVacuum.Fill(true);
    }

    internal bool HasCapturedSolverArrays => _solverArrays?.Values.Any(static array => array.CaptureForRollback) == true;

    /// <summary>
    ///     Identity and revision used by conditional snapshot consumers.
    /// </summary>
    public AtmosChunkVersion Version => new(_generation, Interlocked.Read(ref _revision));

    /// <summary>
    ///     Ensures that the chunk's per-voxel arrays are initialized for its current dimensions.
    /// </summary>
    /// <remarks>
    ///     Existing arrays are reused when they already have the required length. This method does not
    ///     clear existing values or reset active counts; use <see cref="Initialize" /> to reset the chunk.
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
        EnsureInitialized(ref IsVacuum, dimensions);
        if (ActiveGases == null)
            ActiveGases = new GasChannel[AtmosChunkConstants.InitialGasChannelCapacity];
    }

    /// <summary>
    ///     Initializes or reinitializes the chunk with the specified position and dimensions.
    /// </summary>
    /// <param name="position">The chunk's position in the grid of chunks.</param>
    /// <param name="width">The width of the chunk.</param>
    /// <param name="height">The height of the chunk.</param>
    /// <param name="depth">The depth of the chunk.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     A dimension is non-positive or the combined voxel count exceeds
    ///     <see cref="ChunkConstants.MaximumVoxelCount" />.
    /// </exception>
    /// <remarks>
    ///     Initialization puts the chunk to sleep, resets all active counts and timers, and clears
    ///     its per-voxel and gas-channel data. Solver arrays are detached.
    /// </remarks>
    [PublicAPI]
    public void Initialize(
        Int3 position,
        int width = ChunkConstants.DefaultWidth,
        int height = ChunkConstants.DefaultHeight,
        int depth = ChunkConstants.DefaultDepth)
    {
        int voxelCount = GetValidatedVoxelCount(width, height, depth);
        _solverArrays = null;
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
        IsVacuum.Fill(true);
        Array.Clear(ActiveGases, 0, ActiveGases.Length);

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
    public override void Release()
    {
        _solverArrays = null;
        if (ActiveGases != null)
        {
            for (int i = 0; i < ActiveGasCount; i++)
            {
                ActiveGases[i].Release();
            }
        }
    }

    /// <summary>
    ///     Gets solver storage for a key, allocating it on first use in this chunk.
    /// </summary>
    /// <typeparam name="T">The exact element type stored under the key.</typeparam>
    /// <param name="key">An ordinal string key, or a reference-identity object for transient storage.</param>
    /// <param name="captureForRollback">Whether to capture and restore the array with its chunk.</param>
    /// <param name="length">The required array length, or null for one element per voxel.</param>
    /// <returns>The live array, initially filled with default values.</returns>
    /// <remarks>
    ///     Access each chunk from one worker at a time. Arrays survive ticks and sleep, but are detached on
    ///     initialization or release. Captured storage requires a stable string key and reference-free elements.
    /// </remarks>
    internal T[] GetOrCreateSolverArray<T>(object key, bool captureForRollback, int? length = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        int arrayLength = length ?? VoxelCount;
        ArgumentOutOfRangeException.ThrowIfNegative(arrayLength, nameof(length));

        if (captureForRollback)
        {
            if (key is not string name || string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Captured solver arrays require a nonempty, stable string key.", nameof(key));

            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            {
                throw new ArgumentException(
                    "Captured solver array elements cannot contain managed references.",
                    nameof(captureForRollback));
            }
        }

        if (_solverArrays != null && _solverArrays.TryGetValue(key, out var existing))
        {
            if (existing is not SolverArrayStorage<T> typed ||
                existing.Length != arrayLength ||
                existing.CaptureForRollback != captureForRollback)
            {
                throw new InvalidOperationException(
                    "The solver array key already has a different element type, length, or capture policy.");
            }

            return typed.Values;
        }

        var array = new T[arrayLength];
        _solverArrays ??= new Dictionary<object, SolverArrayStorage>(SolverArrayKeyComparer.Instance);
        _solverArrays.Add(key, new SolverArrayStorage<T>(array, captureForRollback));
        if (captureForRollback)
            MarkChanged();

        return array;
    }

    /// <summary>
    ///     Gets a chunk-shaped view over the same storage used by <see cref="GetOrCreateSolverArray{T}" />.
    /// </summary>
    /// <typeparam name="T">The exact element type stored under the key.</typeparam>
    /// <param name="key">An ordinal string key, or a reference-identity object for transient storage.</param>
    /// <param name="captureForRollback">Whether to capture and restore the array with its chunk.</param>
    /// <returns>A live view with one element per voxel and the chunk's coordinate indexing.</returns>
    internal FlatArray<T> GetOrCreateSolverFlatArray<T>(object key, bool captureForRollback)
    {
        return new FlatArray<T>(GetOrCreateSolverArray<T>(key, captureForRollback), Dimensions);
    }

    internal AtmosSolverArraySnapshot[] CaptureSolverArrays()
    {
        if (_solverArrays == null)
            return [];

        return _solverArrays.Where(static entry => entry.Value.CaptureForRollback)
            .OrderBy(static entry => (string)entry.Key, StringComparer.Ordinal)
            .Select(static entry => new AtmosSolverArraySnapshot((string)entry.Key, entry.Value)).ToArray();
    }

    internal void RestoreSolverArrays(IReadOnlyList<AtmosSolverArraySnapshot> snapshots)
    {
        if (snapshots.Count == 0)
            return;

        _solverArrays = new Dictionary<object, SolverArrayStorage>(SolverArrayKeyComparer.Instance);
        foreach (var snapshot in snapshots)
            _solverArrays.Add(snapshot.Key, snapshot.Materialize());
    }

    /// <summary>
    ///     Wakes the chunk and rebuilds its active gas-bearing voxel index.
    /// </summary>
    /// <remarks>
    ///     Every non-solid, non-void voxel participates while the chunk is awake. Classification IDs do not
    ///     partition simulation work.
    /// </remarks>
    public void Wake()
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
    ///     Marks the chunk as sleeping so that it is skipped by simulation ticks.
    /// </summary>
    public void Sleep()
    {
        IsAwake = false;
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
    ///     Tries to get the temperatures and heatCapacity of a specific voxel
    ///     Does not recalculate <see cref="TotalHeatCapacity" />
    /// </summary>
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
    ///     Sets a specific voxel to a vacuum. This sets TotalPressure, ActiveGases, and TotalHeatCapacity to 0 and IsVacuum to
    ///     true.
    /// </summary>
    /// <param name="idx">Index of voxel</param>
    [PublicAPI]
    public void SetVoxelToVacuum(ushort idx)
    {
        TotalPressure[idx] = 0f;
        for (int g = 0; g < ActiveGasCount; g++)
        {
            ActiveGases[g].Moles[idx] = 0f;
        }

        TotalHeatCapacity[idx] = 0f;
        IsVacuum[idx] = true;
    }

    /// <summary>
    ///     Sets a specific voxel to a vacuum. This sets TotalPressure, ActiveGases, and TotalHeatCapacity to 0 and IsVacuum to
    ///     true.
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
        IsVacuum.Fill(true);
    }

    /// <summary>
    ///     Sets a specific voxel classification. Solid and void classifications clear the voxel to vacuum.
    /// </summary>
    /// <param name="idx">Index of voxel</param>
    /// <param name="roomId">Classification value to assign.</param>
    [PublicAPI]
    public void SetVoxelClassification(ushort idx, int roomId)
    {
        if (roomId < 0)
            SetVoxelToVacuum(idx);

        VoxelRoomMap[idx] = roomId;
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
        if (roomId < 0)
            SetChunkToVacuum();

        VoxelRoomMap.Fill(roomId);
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


    internal int GetOrCreateGasChannel(int gasId)
    {
        for (int index = 0; index < ActiveGasCount; index++)
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
    /// <returns>A snapshot containing selected physical fields and solver arrays opted into capture.</returns>
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

    public static AtmosChunk CreateInitializeChunk(
        Int3 position,
        int width = ChunkConstants.DefaultWidth,
        int height = ChunkConstants.DefaultHeight,
        int depth = ChunkConstants.DefaultDepth)
    {
        var chunk = new AtmosChunk(width, height, depth);
        chunk.Initialize(position, width, height, depth);
        return chunk;
    }
}
