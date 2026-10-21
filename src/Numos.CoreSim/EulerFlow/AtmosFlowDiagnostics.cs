using Magnitudo;

namespace Numos.CoreSim.EulerFlow;

/// <summary>
///     What the <c>euler-flow</c> stage did during one simulation's most recent tick.
/// </summary>
/// <param name="Substeps">Number of CFL substeps the tick ran.</param>
/// <param name="SimulatedSeconds">
///     Gas time the tick advanced, in seconds. Equal to the fixed time step unless
///     <see cref="EulerFlowConfig.MaxSubstepsPerTick" /> cut the tick short.
/// </param>
/// <param name="SubstepCapEngaged">
///     Whether the substep cap stopped the tick before it had simulated the whole fixed time step.
/// </param>
/// <param name="MaxCourantNumber">
///     Largest <c>λ·Δt/Δx</c> any substep actually ran at, measured with the fastest signal speed <c>λ</c> found
///     after the substep's sweeps. A value above <see cref="EulerFlowConfig.CourantNumber" /> means a signal sped
///     up inside a substep faster than the speed it was sized for; values below 1 are still linearly stable.
/// </param>
/// <param name="MaxSignalSpeed">Fastest <c>|u| + c</c> seen in any active voxel during the tick, in metres per second.</param>
/// <param name="ClampedVelocityCount">
///     Number of cell updates whose speed was clamped to <see cref="EulerFlowConfig.MaxVelocity" />. Nonzero
///     means a state went wrong somewhere; the clamp is a safety net, not physics.
/// </param>
/// <param name="NegativeInternalEnergyCount">
///     Number of voxels whose kinetic energy exceeded their total energy at write-back, so they were written at
///     0 K. Rare rounding at a vacuum front is tolerable; a steady count points at a bug.
/// </param>
/// <param name="NegativeMolesCount">
///     Number of species updates that went below zero and were floored. Same reading as
///     <paramref name="NegativeInternalEnergyCount" />: a face drained more than the upwind cell held.
/// </param>
/// <param name="NonFiniteCellCount">
///     Number of cell states, gathered or swept, whose energy density, temperature, pressure, or sound speed did not
///     fit in <see langword="float" />. Such a cell keeps its gas but transports like a vacuum for the rest of the
///     sweep, with no momentum. Nonzero means a gas's heat capacity or a voxel's energy is far outside the range
///     the stage is built for; check the registry before anything else.
/// </param>
/// <remarks>
///     A plain measurement, not simulation state: it is neither checkpointed nor hashed, and it reads as all zeros
///     under the legacy transport model, after a checkpoint restore, and before the first tick.
/// </remarks>
public readonly record struct AtmosFlowDiagnostics(
    int Substeps,
    [property: Quantity("seconds64")] Second64 SimulatedSeconds,
    bool SubstepCapEngaged,
    [property: Quantity("dimensionless")] Scalar MaxCourantNumber,
    [property: Quantity("velocity")] MetrePerSecond MaxSignalSpeed,
    int ClampedVelocityCount,
    int NegativeInternalEnergyCount,
    int NegativeMolesCount,
    int NonFiniteCellCount);
