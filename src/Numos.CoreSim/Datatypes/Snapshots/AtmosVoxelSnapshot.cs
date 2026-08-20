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
///     Detached values for one voxel, intended for interaction details and tooltips.
/// </summary>
/// <param name="Pressure">Cached pressure in pascals (Pa) at the sampled chunk version.</param>
/// <param name="Temperature">Temperature in kelvins (K).</param>
public readonly record struct AtmosVoxelSnapshot(
    AtmosChunkVersion ChunkVersion,
    Int3 ChunkPosition,
    ushort LocalIndex,
    int RoomId,
    float Pressure,
    float Temperature,
    VoxelGasSnapshot[] Gases);
