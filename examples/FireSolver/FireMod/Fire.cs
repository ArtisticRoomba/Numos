using Numos.API;
using Numos.CoreSim.State;

namespace FireMod;

/// <summary>
/// Per-chunk fire state that <see cref="FireSolver"/> keeps.
/// </summary>
/// <remarks>
/// A voxel that turns to vacuum loses its exposure, because there is nothing left to burn.
/// </remarks>
[ChunkComponent("fire-mod/fire", ClearOnVacuum = true)]
public partial struct Fire
{
    /// <summary>
    /// Seconds the voxel has spent above the ignition temperature since it last burned.
    /// </summary>
    [NumosSerialize] public float Exposure;

    /// <summary>
    /// The number of burns in the chunk since it was created.
    /// </summary>
    [NumosSerialize, PerChunk] public int BurnCount;
}
