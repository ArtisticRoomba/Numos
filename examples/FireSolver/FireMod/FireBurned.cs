using Numos.API;

namespace FireMod;

/// <summary>
/// Tells the host that a voxel burned.
/// </summary>
/// <param name="Cell">The voxel that burned.</param>
/// <param name="Temperature">The voxel temperature after the burn, in kelvins.</param>
/// <param name="Spread">Whether the burn passed the fire to a neighbor in the same chunk.</param>
public readonly record struct FireBurned(AtmosCellRef Cell, float Temperature, bool Spread);
