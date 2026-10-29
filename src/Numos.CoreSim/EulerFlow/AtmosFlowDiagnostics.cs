using Magnitudo;

namespace Numos.CoreSim.EulerFlow;

/// <summary>
///     What the <c>euler-flow</c> stage did during its most recent run on one simulation.
/// </summary>
/// <param name="Substeps">Number of CFL substeps the run took.</param>
/// <param name="SimulatedSeconds">
///     Real gas time the run advanced, in seconds, whatever the acoustic scaling factor. Equal to the stage's time step unless
///     <see cref="EulerFlowConfig.MaxSubstepsPerSecond" /> cut the run short.
/// </param>
/// <param name="SubstepCapEngaged">
///     Whether the substep cap stopped the run before it had simulated the stage's whole time step.
/// </param>
/// <param name="MaxCourantNumber">
///     Largest <c>λ·Δt/Δx</c> any substep actually ran at, measured with the fastest signal speed <c>λ</c> found
///     after the substep's sweeps. A value above <see cref="EulerFlowConfig.CourantNumber" /> means a signal sped
///     up inside a substep faster than the speed it was sized for. Values below 1 are still linearly stable.
/// </param>
/// <param name="MaxSignalSpeed">
///     Fastest <c>|u| + c</c> seen in any active voxel during the run, in metres per second. Under acoustic scaling
///     <c>u</c> is the scaled velocity, so this and <paramref name="MaxCourantNumber" /> describe the solve that ran,
///     not the real flow.
/// </param>
/// <param name="ClampedVelocityCount">
///     Number of cell updates whose speed was clamped to <see cref="EulerFlowConfig.MaxVelocity" />. Nonzero
///     means a state went wrong somewhere. The clamp is a safety net, not physics.
/// </param>
/// <param name="NegativeInternalEnergyCount">
///     Number of voxels whose kinetic energy exceeded their total energy at write-back, so they were written at
///     0 K. Rare rounding at a vacuum front is tolerable. A steady count points at a bug.
/// </param>
/// <param name="NegativeMolesCount">
///     Number of species updates that went below zero and were floored. Same reading as
///     <paramref name="NegativeInternalEnergyCount" />: a face drained more than the upwind cell held.
/// </param>
/// <param name="NonFiniteCellCount">
///     Number of cell states, gathered or swept, whose energy density, temperature, pressure, or sound speed did not
///     fit in <see langword="float" />, plus cells whose species update came out non-finite (written as zero) and
///     written-back voxels whose pressure is not finite (kept awake). A non-finite cell keeps its gas but transports
///     like a vacuum for the rest of the sweep, with no momentum. Nonzero means a gas's heat capacity or a voxel's
///     energy is far outside the range the stage is built for. Check the registry before anything else.
/// </param>
/// <param name="SoundSpeedReduction">
///     Largest acoustic scaling factor α the run used. 1 when <see cref="EulerFlowConfig.MaxSoundSpeedReduction" />
///     is 1 or the run was not slowed. Substeps and the signal speeds above are those of the scaled solve.
/// </param>
/// <param name="FlowGroups">
///     Number of flow groups the run solved: sets of active chunks that share no open face, each with its own
///     substep loop and scaling factor. 1 when <see cref="EulerFlowConfig.MaxSoundSpeedReduction" /> is 1. The
///     substeps, Courant number, and signal speed above are the largest over groups, and the simulated time is the
///     smallest.
/// </param>
/// <param name="SlowedChunks">
///     Number of chunks in groups that ran at a scaling factor above 1, counting chunks activated during the run.
/// </param>
/// <remarks>
///     A plain measurement, not simulation state: it is neither checkpointed nor hashed, and it reads as all zeros
///     under the legacy transport model, after a checkpoint restore, and before the first run. The values stay the
///     same on the ticks between runs.
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
    int NonFiniteCellCount,
    [property: Quantity("dimensionless")] Scalar SoundSpeedReduction = 1f,
    int FlowGroups = 0,
    int SlowedChunks = 0);
