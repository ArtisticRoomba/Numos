namespace Numos.API;

/// <summary>
///     Bulk velocity of the gas in one voxel, in metres per second (m/s), derived from its stored momentum and
///     current gas mass.
/// </summary>
/// <param name="X">Velocity along the local x-axis, in metres per second (m/s).</param>
/// <param name="Y">Velocity along the local y-axis, in metres per second (m/s).</param>
/// <param name="Z">Velocity along the local z-axis, in metres per second (m/s).</param>
/// <remarks>
///     A value, not a handle: it describes the voxel at the moment <see cref="AtmosSimulation.GetVoxelVelocity(AtmosChunkHandle, ushort)" />
///     read it. A vacuum voxel reads as zero.
/// </remarks>
public readonly record struct AtmosVoxelVelocity(float X, float Y, float Z);
