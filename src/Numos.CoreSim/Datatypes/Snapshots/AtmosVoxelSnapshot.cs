using Numos.Maths;

namespace Numos.CoreSim.Datatypes.Snapshots;

/// <summary>
///     One gas channel sampled at a single voxel.
/// </summary>
public readonly record struct VoxelGasSnapshot(int GasId, float Moles);

/// <summary>
///     Detached values for one voxel, intended for interaction details and tooltips.
/// </summary>
public readonly record struct AtmosVoxelSnapshot(
    AtmosChunkVersion ChunkVersion,
    Int3 ChunkPosition,
    ushort LocalIndex,
    int RoomId,
    float Pressure,
    float Temperature,
    VoxelGasSnapshot[] Gases);