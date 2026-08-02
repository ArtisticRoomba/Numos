using System.Diagnostics.CodeAnalysis;
using JetBrains.Annotations;
using Maths;
using Numos.Datatypes.Snapshots;

namespace Numos;

/// <summary>
///     Represents the simulation state for a fixed-size voxel chunk.
/// </summary>
/// <remarks>
///     Per-voxel data is stored in flat arrays. Use <see cref="GetIndex(int, int, int)" /> or
///     <see cref="GetIndex(Int3)" /> to convert local coordinates to an array index, and
///     <see cref="GetXyz(ushort)" /> or <see cref="GetXyzInt3(ushort)" /> for the reverse conversion.
/// </remarks>
public class AtmosChunk
{
    /// <summary>
    ///     Indicates that a voxel has not been assigned to a room.
    /// </summary>
    public const int RoomUnassigned = 0;

    /// <summary>
    ///     Indicates that a voxel represents the space outside the simulated map.
    /// </summary>
    public const int RoomVoid = -1;

    /// <summary>
    ///     Indicates that a voxel is solid and cannot contain or exchange gas.
    /// </summary>
    public const int RoomSolid = -2;

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
    ///     The position of this chunk in the chunk grid.
    /// </summary>
    public Int3 GridPosition;

    /// <summary>The number of voxels along the z axis.</summary>
    public int Depth;

    /// <summary>The number of voxels along the y axis.</summary>
    public int Height;

    /// <summary>The number of voxels along the x axis.</summary>
    public int Width;

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
    ///     Temperature value for each voxel, indexed by flat voxel index.
    /// </summary>
    public float[] Temperature;

    /// <summary>
    ///     Cached pressure value for each voxel, indexed by flat voxel index.
    /// </summary>
    /// <remarks>These values are recomputed by the simulation each tick.</remarks>
    public float[] TotalPressure;

    /// <summary>
    ///     Total number of voxels in this chunk, equal to <c>Width * Height * Depth</c>.
    /// </summary>
    public int VoxelCount;

    /// <summary>
    ///     Room classification for each voxel, indexed by flat voxel index.
    /// </summary>
    /// <remarks>
    ///     Positive or otherwise application-defined IDs identify rooms. The reserved values
    ///     <see cref="RoomUnassigned" />, <see cref="RoomVoid" />, and <see cref="RoomSolid" />
    ///     identify unassigned, void, and solid voxels respectively.
    /// </remarks>
    /// <seealso cref="RoomSolid" />
    /// <seealso cref="RoomVoid" />
    /// <seealso cref="RoomUnassigned" />
    public int[] VoxelRoomMap;

    /// <summary>
    ///     Creates a chunk with the specified dimensions and active-room capacity.
    /// </summary>
    /// <param name="width">The number of voxels along the x axis.</param>
    /// <param name="height">The number of voxels along the y axis.</param>
    /// <param name="depth">The number of voxels along the z axis.</param>
    /// <param name="maxActiveRooms">The maximum number of rooms that can be active at once.</param>
    public AtmosChunk(int width = 16, int height = 16, int depth = 16, int maxActiveRooms = 64)
    {
        MaxActiveRooms = maxActiveRooms;
        Width = width;
        Height = height;
        Depth = depth;
        VoxelCount = width * height * depth;
        EnsureInitialized();
    }

    /// <summary>
    ///     Ensures that the chunk's per-voxel and active-room arrays are initialized for its current dimensions.
    /// </summary>
    /// <remarks>
    ///     Existing arrays are reused when they already have the required length. This method does not
    ///     clear existing values or reset active counts; use <see cref="Initialize" /> to reset the chunk.
    /// </remarks>
    [MemberNotNull(nameof(VoxelRoomMap),
        nameof(ActiveAirIndices),
        nameof(TotalPressure),
        nameof(Temperature),
        nameof(ActiveGases),
        nameof(ActiveRoomIds))]
    [PublicAPI]
    public void EnsureInitialized()
    {
        if (VoxelRoomMap == null || VoxelRoomMap.Length != VoxelCount)
            VoxelRoomMap = new int[VoxelCount];
        if (ActiveAirIndices == null || ActiveAirIndices.Length != VoxelCount)
            ActiveAirIndices = new ushort[VoxelCount];
        if (TotalPressure == null || TotalPressure.Length != VoxelCount)
            TotalPressure = new float[VoxelCount];
        if (Temperature == null || Temperature.Length != VoxelCount)
            Temperature = new float[VoxelCount];
        if (ActiveGases == null)
            ActiveGases = new GasChannel[16]; // TODO unhardcode maxgases
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
    /// <remarks>
    ///     Initialization puts the chunk to sleep, resets all active counts and timers, and clears
    ///     its per-voxel, gas-channel, and active-room data.
    /// </remarks>
    [PublicAPI]
    public void Initialize(Int3 position, int width = 16, int height = 16, int depth = 16, int maxActiveRooms = 64)
    {
        GridPosition = position;
        MaxActiveRooms = maxActiveRooms;
        IsAwake = false;
        Width = width;
        Height = height;
        Depth = depth;
        VoxelCount = width * height * depth;

        EnsureInitialized();

        ActiveAirCount = 0;
        ActiveRoomCount = 0;
        ActiveGasCount = 0;
        SleepTimer = 0;

        Array.Clear(VoxelRoomMap, 0, VoxelRoomMap.Length);
        Array.Clear(ActiveAirIndices, 0, ActiveAirIndices.Length);
        Array.Clear(TotalPressure, 0, TotalPressure.Length);
        Array.Clear(Temperature, 0, Temperature.Length);
        Array.Clear(ActiveGases, 0, ActiveGases.Length);
        Array.Clear(ActiveRoomIds, 0, ActiveRoomIds.Length);
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
    ///     the sleep timer. When a new room is activated, <see cref="ActiveAirIndices" /> is rebuilt.
    /// </remarks>
    /// <exception cref="Exception">Thrown when <paramref name="targetRoomId" /> would exceed <see cref="MaxActiveRooms" />.</exception>
    public virtual void WakeRoom(int targetRoomId)
    {
        if (targetRoomId == RoomSolid || targetRoomId == RoomVoid)
            return;

        if (IsAwake)
        {
            for (var r = 0; r < ActiveRoomCount; r++)
            {
                if (ActiveRoomIds[r] == targetRoomId)
                {
                    SleepTimer = 0;
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
            for (var r = 0; r < ActiveRoomCount; r++)
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
    }

    /// <summary>
    ///     Adds gas to a voxel and updates that voxel's temperature and total pressure.
    /// </summary>
    /// <param name="localVoxelIndex">The flat index of the target voxel within this chunk.</param>
    /// <param name="gasId">The ID of the gas to add.</param>
    /// <param name="molesToAdd">The number of moles to add.</param>
    /// <param name="temperature">The temperature of the injected gas.</param>
    /// <remarks>
    ///     Injection is ignored when the chunk is sleeping or the target voxel is solid or void.
    ///     A new gas channel is created when this gas is not already present in the chunk.
    /// </remarks>
    public void InjectGasToVoxel(ushort localVoxelIndex, int gasId, float molesToAdd, float temperature)
    {
        if (!IsAwake)
            return;

        int room = VoxelRoomMap[localVoxelIndex];
        if (room == RoomSolid)
            return;
        if (room == RoomVoid)
            return;

        SleepTimer = 0;

        int targetChannelIndex = -1;
        for (var i = 0; i < ActiveGasCount; i++)
        {
            if (ActiveGases[i].GasId == gasId)
            {
                targetChannelIndex = i;
                break;
            }
        }

        if (targetChannelIndex == -1)
        {
            if (ActiveGasCount >= ActiveGases.Length)
            {
                throw new Exception("Maximum unique gas channels reached for this chunk!");
            }

            ActiveGases[ActiveGasCount] = new GasChannel();
            ActiveGases[ActiveGasCount].Initialize(gasId, VoxelCount);

            targetChannelIndex = ActiveGasCount;
            ActiveGasCount++;
        }

        ActiveGases[targetChannelIndex].Moles[localVoxelIndex] += molesToAdd;

        var currentTotalMoles = 0f;
        for (var g = 0; g < ActiveGasCount; g++)
        {
            currentTotalMoles += ActiveGases[g].Moles[localVoxelIndex];
        }

        float currentTemp = Temperature[localVoxelIndex];
        float newTemp = ((currentTotalMoles - molesToAdd) * currentTemp + molesToAdd * temperature) / currentTotalMoles;
        Temperature[localVoxelIndex] = newTemp;

        TotalPressure[localVoxelIndex] = currentTotalMoles * newTemp;
    }

    /// <summary>
    ///     Creates a snapshot of the chunk's current network state.
    /// </summary>
    /// <returns>A snapshot containing copies of the chunk's position, pressure, temperature, gas, and room data.</returns>
    [PublicAPI]
    public AtmosChunkSnapshot GetNetworkSnapshot()
    {
        var snapshot = new AtmosChunkSnapshot
        {
            GridPosition = GridPosition,
            TotalPressure = new float[VoxelCount],
            Temperature = new float[VoxelCount],
            Gases = new GasSnapshot[ActiveGasCount],
            VoxelRoomMap = new int[VoxelCount]
        };

        Array.Copy(TotalPressure, snapshot.TotalPressure, VoxelCount);
        Array.Copy(Temperature, snapshot.Temperature, VoxelCount);
        Array.Copy(VoxelRoomMap, snapshot.VoxelRoomMap, VoxelCount);

        for (var g = 0; g < ActiveGasCount; g++)
        {
            snapshot.Gases[g] = new GasSnapshot
            {
                GasId = ActiveGases[g].GasId,
                Moles = new float[VoxelCount]
            };
            Array.Copy(ActiveGases[g].Moles, snapshot.Gases[g].Moles, VoxelCount);
        }

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
        return (ushort)(x + y * Width + z * Width * Height);
    }

    /// <inheritdoc cref="GetIndex(int, int, int)" />
    [PublicAPI]
    public ushort GetIndex(Int3 vec)
    {
        return (ushort)(vec.X + vec.Y * Width + vec.Z * Width * Height);
    }

    /// <summary>
    ///     Converts a flat voxel index to local x, y, and z coordinates.
    /// </summary>
    /// <param name="index">The flat voxel index.</param>
    /// <returns>The local coordinates as an <c>(x, y, z)</c> tuple.</returns>
    [PublicAPI]
    public (int x, int y, int z) GetXyz(ushort index)
    {
        return (index % Width, index / Width % Height, index / (Width * Height));
    }

    /// <summary>
    ///     Converts a flat voxel index to local coordinates as an <see cref="Int3" />.
    /// </summary>
    /// <param name="index">The flat voxel index.</param>
    /// <returns>The local voxel coordinates.</returns>
    [PublicAPI]
    public Int3 GetXyzInt3(ushort index)
    {
        return new Int3(index % Width, index / Width % Height, index / (Width * Height));
    }
}