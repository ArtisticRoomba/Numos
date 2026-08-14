using System.Diagnostics.CodeAnalysis;
using JetBrains.Annotations;
using Numos.CoreSim.Collections;
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
    /// <summary>
    ///     Largest voxel count representable by the chunk's <see cref="ushort" /> flat indexes.
    /// </summary>
    internal const int MaxVoxelCount = ushort.MaxValue;

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
    ///     Temperature value for each voxel, indexed by flat voxel index or local coordinate.
    /// </summary>
    public FlatArray<float> Temperature;

    /// <summary>
    ///     Cached pressure value for each voxel, indexed by flat voxel index or local coordinate.
    /// </summary>
    /// <remarks>These values are recomputed by the simulation each tick.</remarks>
    public FlatArray<float> TotalPressure;

    // J/K. Not J/K mol.
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
    ///     <see cref="RoomUnassigned" />, <see cref="RoomVoid" />, and <see cref="RoomSolid" />
    ///     identify unassigned, void, and solid voxels respectively.
    /// </remarks>
    /// <seealso cref="RoomSolid" />
    /// <seealso cref="RoomVoid" />
    /// <seealso cref="RoomUnassigned" />
    public FlatArray<int> VoxelRoomMap;

    /// <summary>
    ///     Creates a chunk with the specified dimensions and active-room capacity.
    /// </summary>
    /// <param name="width">The number of voxels along the x axis.</param>
    /// <param name="height">The number of voxels along the y axis.</param>
    /// <param name="depth">The number of voxels along the z axis.</param>
    /// <param name="maxActiveRooms">The maximum number of rooms that can be active at once.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     A dimension is non-positive or the combined voxel count exceeds <see cref="MaxVoxelCount" />.
    /// </exception>
    public AtmosChunk(int width = 16, int height = 16, int depth = 16, int maxActiveRooms = 64)
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
    /// <exception cref="ArgumentOutOfRangeException">
    ///     A dimension is non-positive or the combined voxel count exceeds <see cref="MaxVoxelCount" />.
    /// </exception>
    /// <remarks>
    ///     Initialization puts the chunk to sleep, resets all active counts and timers, and clears
    ///     its per-voxel, gas-channel, and active-room data.
    /// </remarks>
    [PublicAPI]
    public void Initialize(Int3 position, int width = 16, int height = 16, int depth = 16, int maxActiveRooms = 64)
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
            TotalPressure = TotalPressure.ToArray(),
            Temperature = Temperature.ToArray(),
            Gases = new GasSnapshot[ActiveGasCount],
            VoxelRoomMap = VoxelRoomMap.ToArray()
        };

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
        if (width > MaxVoxelCount || height > MaxVoxelCount || depth > MaxVoxelCount)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width,
                $"No chunk dimension may exceed {MaxVoxelCount}.");
        }

        long voxelCount = (long)width * height * depth;
        if (voxelCount > MaxVoxelCount)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width,
                $"Chunk dimensions contain {voxelCount} voxels, but at most {MaxVoxelCount} are supported.");
        }

        return (int)voxelCount;
    }
}