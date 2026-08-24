using Numos.Maths;

namespace Numos.CoreSim.Datatypes.Snapshots;

/// <summary>
///     One gas channel sampled at a single voxel.
/// </summary>
/// <param name="GasId">Gas registry ID.</param>
/// <param name="Moles">Sampled amount, in moles (mol).</param>
public readonly record struct VoxelGasSnapshot(
    int GasId,
    float Moles);

/// <summary>
/// Detached values for one voxel, intended for interaction details and tooltips.
/// </summary>
/// <param name="ChunkVersion">Version of the source chunk.</param>
/// <param name="ChunkPosition">Grid position of the source chunk.</param>
/// <param name="LocalIndex">Local voxel index within the chunk.</param>
/// <param name="RoomId">ID of the voxel's room.</param>
/// <param name="Pressure">Pressure in pascals (Pa).</param>
/// <param name="Temperature">Temperature in kelvins (K).</param>
/// <param name="Gases">Gas-channel values at the voxel.</param>
public readonly record struct AtmosVoxelSnapshot(
    AtmosChunkVersion ChunkVersion,
    Int3 ChunkPosition,
    ushort LocalIndex,
    int RoomId,
    float Pressure,
    float Temperature,
    VoxelGasSnapshot[] Gases);