using Numos.Maths;
using Magnitudo;

namespace Numos.CoreSim.Datatypes.Snapshots;

/// <summary>
///     One gas channel sampled at a single voxel.
/// </summary>
/// <param name="GasId">Gas registry ID.</param>
/// <param name="Moles">Sampled amount, in moles (mol).</param>
public readonly record struct VoxelGasSnapshot(
    int GasId,
    [property: Quantity("amount")] Mole Moles);

/// <summary>
///     Detached values for one voxel, intended for interaction details and tooltips.
/// </summary>
/// <param name="ChunkVersion">Version of the source chunk.</param>
/// <param name="ChunkPosition">Grid position of the source chunk.</param>
/// <param name="LocalIndex">Local voxel index within the chunk.</param>
/// <param name="RoomId">ID of the voxel's room.</param>
/// <param name="Pressure">Pressure in pascals (Pa).</param>
/// <param name="Temperature">Temperature in kelvins (K).</param>
/// <param name="VelocityX">Bulk x-velocity in metres per second (m/s), derived from momentum and gas mass.</param>
/// <param name="VelocityY">Bulk y-velocity in metres per second (m/s).</param>
/// <param name="VelocityZ">Bulk z-velocity in metres per second (m/s).</param>
/// <param name="Gases">Gas-channel values at the voxel.</param>
/// <remarks>
///     Velocity is three floats rather than a vector type so each component carries its dimensional annotation.
///     A vacuum voxel reads as zero velocity.
/// </remarks>
public readonly record struct AtmosVoxelSnapshot(
    AtmosChunkVersion ChunkVersion,
    Int3 ChunkPosition,
    ushort LocalIndex,
    int RoomId,
    [property: Quantity("pressure")] Pascal Pressure,
    [property: Quantity("temperature")] Kelvin Temperature,
    [property: Quantity("velocity")] MetrePerSecond VelocityX,
    [property: Quantity("velocity")] MetrePerSecond VelocityY,
    [property: Quantity("velocity")] MetrePerSecond VelocityZ,
    VoxelGasSnapshot[] Gases);