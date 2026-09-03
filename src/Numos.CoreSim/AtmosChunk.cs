using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using JetBrains.Annotations;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.Collections;
using Numos.Maths;
using Numos.Units;

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
    ///     Flat voxel indices belonging to active rooms in this chunk.
    /// </summary>
    /// <remarks>
    ///     Only the first <see cref="ActiveAirCount" /> entries are valid. Rebuild this list with
    ///     <see cref="RebuildActiveAirIndices" /> after changing <see cref="VoxelRoomMap" /> or the active rooms.
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
    ///     Room IDs currently being processed in this chunk.
    /// </summary>
    /// <remarks>
    ///     Only the first <see cref="ActiveRoomCount" /> entries are valid. The number of active rooms
    ///     cannot exceed <see cref="MaxActiveRooms" />.
    /// </remarks>
    public int[] ActiveRoomIds;

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
    ///     Maximum number of rooms that can be active in this chunk simultaneously.
    /// </summary>
    public int MaxActiveRooms;

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

    private long _generation;
    private long _revision;

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
    ///     Identity and revision used by conditional snapshot consumers.
    /// </summary>
    public AtmosChunkVersion Version => new(_generation, Interlocked.Read(ref _revision));

    /// <summary>
    ///     The number of voxels along each axis.
    /// </summary>
    public Int3 Dimensions => new(Width, Height, Depth);

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
        Width = width;
        Height = height;
        Depth = depth;
        VoxelCount = voxelCount;

        EnsureInitialized();

        ActiveAirCount = 0;
        ActiveRoomCount = 0;
        ActiveGasCount = 0;
        SleepTimer = 0;

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
        if (ActiveGases != null)
        {
            for (int i = 0; i < ActiveGasCount; i++)
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
    ///     the sleep timer. When a new room is activated, <see cref="ActiveAirIndices" /> is rebuilt.
    /// </remarks>
    /// <exception cref="Exception">Thrown when <paramref name="targetRoomId" /> would exceed <see cref="MaxActiveRooms" />.</exception>
    public virtual void WakeRoom(int targetRoomId)
    {
        if (targetRoomId == VoxelClassification.RoomSolid || targetRoomId == VoxelClassification.RoomVoid)
            return;

        if (IsAwake)
        {
            for (int r = 0; r < ActiveRoomCount; r++)
            {
                if (ActiveRoomIds[r] == targetRoomId)
                {
                    SleepTimer = 0;
                    MarkChanged();
                    return;
                }
            }
        }

        if (!IsAwake)
        {
            ActiveRoomCount = 0;
            IsAwake = true;
        }

        if (ActiveRoomCount >= MaxActiveRooms)
        {
            throw new Exception("Maximum active rooms reached for this chunk!");
        }

        ActiveRoomIds[ActiveRoomCount] = targetRoomId;
        ActiveRoomCount++;
        SleepTimer = 0;
        RebuildActiveAirIndices();
        MarkChanged();
    }

    /// <summary>
    ///     Rebuilds the dense list of voxel indices belonging to active rooms.
    /// </summary>
    /// <remarks>
    ///     The resulting list is stored in <see cref="ActiveAirIndices" /> and its valid length is written
    ///     to <see cref="ActiveAirCount" />. Call this after modifying room classifications or active room IDs.
    /// </remarks>
    public void RebuildActiveAirIndices()
    {
        ActiveAirCount = 0;
        for (ushort i = 0; i < VoxelCount; i++)
        {
            int roomId = VoxelRoomMap[i];
            for (int r = 0; r < ActiveRoomCount; r++)
            {
                if (ActiveRoomIds[r] == roomId)
                {
                    ActiveAirIndices[ActiveAirCount] = i;
                    ActiveAirCount++;
                    break;
                }
            }
        }
    }

    /// <summary>
    ///     Marks the chunk as sleeping so that it is skipped by simulation ticks.
    /// </summary>
    public virtual void Sleep()
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
    public void InjectGasToVoxel(
        ushort localVoxelIndex, int gasId, Mole molesToAdd, Kelvin temperature,
        JoulePerMoleKelvin effectiveMolarHeatCapacityAtConstantVolume,
        PascalPerMoleKelvin pressurePerMoleKelvin)
    {
        Debug.Assert(
            float.IsFinite(effectiveMolarHeatCapacityAtConstantVolume) &&
            effectiveMolarHeatCapacityAtConstantVolume > 0f);

        Debug.Assert(float.IsFinite(pressurePerMoleKelvin) && pressurePerMoleKelvin > 0f);

        if (!IsAwake)
            return;

        int room = VoxelRoomMap[localVoxelIndex];
        if (room == VoxelClassification.RoomSolid)
            return;

        if (room == VoxelClassification.RoomVoid)
            return;

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
        MarkChanged();
    }

    /// <summary>
    ///     Tries to get the temperatures and heatCapacity of a specific voxel
    ///     Does not recalculate <see cref="TotalHeatCapacity"/>
    /// </summary>
    [PublicAPI]
    public bool TryGetThermalState(AtmosSolverConfigSnapshot config,
        ushort voxelIndex, out Kelvin temperature, out JoulePerKelvin heatCapacity)
    {
        heatCapacity = TotalHeatCapacity[voxelIndex];
        if (!float.IsFinite(heatCapacity) || heatCapacity <= 0f || TotalPressure[voxelIndex] == 0f)
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
    }

    /// <summary>
    ///     Sets a specific voxel to a vacuum. This sets TotalPressure, ActiveGases, and TotalHeatCapacity to 0 and IsVacuum to true.
    /// </summary>
    [PublicAPI]
    public void SetChunkToVacuum()
    {
        TotalPressure.Fill(0f);
        for (var g = 0; g < ActiveGasCount; g++)
        {
            Array.Clear(ActiveGases[g].Moles, 0, ActiveGases[g].Moles.Length);
        }

        TotalHeatCapacity.Fill(0f);
    }


    /// <summary>
    ///     Sets a specific voxel to a specific room id.
    ///     If the room id is a wall or void is sets the voxel to a vacuum.
    /// </summary>
    /// <param name="idx">Index of voxel</param>
    /// <param name="roomId">room id to set the room to</param>
    [PublicAPI]
    public void SetVoxelClassification(ushort idx, int roomId)
    {
        if (roomId < 0)
            SetVoxelToVacuum(idx);

        VoxelRoomMap[idx] = roomId;
    }

    /// <summary>
    ///     Sets a specific voxel to a specific room id.
    ///     If the room id is a wall or void is sets the voxel to a vacuum.
    /// </summary>
    /// <param name="idx">Index of voxel</param>
    /// <param name="classification">VoxelClassification to set room id to</param>
    [PublicAPI]
    public void SetVoxelClassification(ushort idx, VoxelClassification classification)
    {
        if (classification.IsSolid || classification.IsVoid)
            SetVoxelToVacuum(idx);

        VoxelRoomMap[idx] = classification.RoomId;
    }


    /// <summary>
    ///     Sets a specific voxel to a specific room id.
    ///     If the room id is a wall or void is sets the voxel to a vacuum.
    /// </summary>
    /// <param name="roomId">room id to set the room to</param>
    [PublicAPI]
    public void SetChunkClassification(int roomId)
    {
        if (roomId < 0)
            SetChunkToVacuum();

        VoxelRoomMap.Fill(roomId);
    }


    /// <summary>
    ///     Sets the entire chunk to a specific room id
    ///     If the room id is a wall or void is sets the voxel to a vacuum.
    /// </summary>
    /// <param name="classification">VoxelClassification to set room id to</param>
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
            TotalHeatCapacity = fields.HasFlag(AtmosChunkSnapshotFields.TotalHeatCapacity)
                ? TotalHeatCapacity.ToArray()
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