using JetBrains.Annotations;
using Numos.Datatypes.Snapshots;
using Numos.Maths;

namespace Numos;

/// <summary>
///     Represents the active voxel grid data for a chunk, completely decoupled from any game engine.
/// </summary>
public class AtmosChunk
{
    // TODO Voxel Classification, make this its own primtype trollhaps
    public const int RoomUnassigned = 0;
    public const int RoomVoid = -1;
    public const int RoomSolid = -2;
    public int ActiveAirCount;

    /// <summary>
    /// 1D array of indices belonging to voxels that are part of active rooms in this chunk.
    /// This is used to iterate over only the voxels that are relevant for simulation.
    /// </summary>
    public ushort[] ActiveAirIndices;
    public int ActiveGasCount;

    public GasChannel[] ActiveGases;
    public int ActiveRoomCount;
    public int[] ActiveRoomIds;
    public int Depth;
    public Int3 GridPosition;
    public int Height;
    public bool IsAwake;
    public int MaxActiveRooms;

    public int SleepTimer;

    /// <summary>
    /// 1D array of temperature values for each voxel in the chunk.
    /// </summary>
    public float[] Temperature;

    /// <summary>
    /// Cached 1D array of pressure values for each voxel in the chunk.
    /// Recomputed each tick.
    /// </summary>
    public float[] TotalPressure;

    /// <summary>
    /// Number of voxels in this chunk.
    /// </summary>
    public int VoxelCount;

    /// <summary>
    /// 1D array that classifies each voxel in the chunk into a room.
    /// Room IDs are arbitrary integers, with 0 being unassigned, -1 being void, and -2 being solid.
    /// </summary>
    /// <seealso cref="RoomSolid"/>
    /// <seealso cref="RoomVoid"/>
    /// <seealso cref="RoomUnassigned"/>
    public int[] VoxelRoomMap;

    public int Width;

    public AtmosChunk(int width = 16, int height = 16, int depth = 16, int maxActiveRooms = 64)
    {
        MaxActiveRooms = maxActiveRooms;
        Width = width;
        Height = height;
        Depth = depth;
        VoxelCount = width * height * depth;
        EnsureInitialized();
    }

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
            ActiveGases = new GasChannel[16];
        if (ActiveRoomIds == null || ActiveRoomIds.Length != MaxActiveRooms)
            ActiveRoomIds = new int[MaxActiveRooms];
    }

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

    protected void RebuildActiveAirIndices()
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

    public virtual void Sleep()
    {
        IsAwake = false;
    }

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
    /// Gets the index into any flat 1D array.
    /// </summary>
    [PublicAPI]
    public ushort GetIndex(int x, int y, int z)
    {
        return (ushort)(x + y * Width + z * Width * Height);
    }

    /// <inheritdoc cref="GetIndex(int, int, int)"/>
    [PublicAPI]
    public ushort GetIndex(Int3 vec)
    {
        return (ushort)(vec.X + vec.Y * Width + vec.Z * Width * Height);
    }

    /// <summary>
    /// Gets the 3D coords given an index for a flat 1D array.
    /// </summary>
    [PublicAPI]
    public (int x, int y, int z) GetXyz(ushort index)
    {
        return (index % Width, index / Width % Height, index / (Width * Height));
    }

    /// <summary>
    /// Gets the 3D coords as an <see cref="Int3"/> given an index for a flat 1D array.
    /// </summary>
    [PublicAPI]
    public Int3 GetXyzInt3(ushort index)
    {
        return new Int3(index % Width, index / Width % Height, index / (Width * Height));
    }
}