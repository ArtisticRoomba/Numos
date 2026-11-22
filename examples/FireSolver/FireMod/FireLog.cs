using Numos.API;
using Numos.CoreSim.State;

namespace FireMod;

/// <summary>
/// The cells of one simulation that burned, in the order they burned.
/// </summary>
[SimulationComponent("fire-mod/log")]
public sealed partial class FireLog
{
    /// <summary>
    /// One entry per burn. A cell that burns twice appears twice.
    /// </summary>
    /// <remarks>
    /// Only <see cref="FireSolver"/> adds to the list, so the boundary hash skips it.
    /// </remarks>
    [NumosSerialize(HostEditable = false)] public readonly List<AtmosCellRef> BurntCells = [];
}
