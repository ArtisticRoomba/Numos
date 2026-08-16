namespace Numos.CoreSim;

/// <summary>
///     Configuration values for the simulation.
/// </summary>
public class AtmosConfig
{
    /// <summary>
    ///     List of gases actively registered to the sim.
    /// </summary>
    public List<GasProperties> GasRegistry { get; set; } = [];

    /// <summary>
    ///     Reference ambient temperature.
    /// </summary>
    public float GlobalTemperature { get; set; } = 293.15f;

    /// <summary>
    ///     Effective temperature used for pressure and sensible-energy calculations when a gas-bearing voxel
    ///     has a non-finite or nonpositive stored temperature.
    /// </summary>
    /// <remarks>
    ///     This value must be finite and positive; the simulation does not normalize an invalid configured
    ///     temperature fallback.
    ///     Energy evolution uses this value as the voxel's starting temperature, then stores the resulting
    ///     blended or transferred temperature.
    /// </remarks>
    public float DefaultTemperatureFallback { get; set; } = 293.15f;

    /// <summary>
    ///     Effective molar heat capacity used when a gas is not registered or its configured
    ///     <see cref="GasProperties.SpecificHeatCapacity" /> is non-finite or nonpositive, in joules per
    ///     mole-kelvin (J/(mol·K)).
    /// </summary>
    /// <remarks>
    ///     Non-finite and nonpositive fallback values are normalized to <c>1 J/(mol·K)</c> by the simulation.
    /// </remarks>
    public float DefaultSpecificHeatCapacity { get; set; } = 1f;

    /// <summary>
    ///     Default temperature of space.
    /// </summary>
    public float SpaceTemperature { get; set; } = 2.7f;

    /// <summary>
    ///     Fraction of pressure delta converted to flow per tick.
    /// </summary>
    public float FlowFriction { get; set; } = 0.25f;

    /// <summary>
    ///     Multiplier applied to <see cref="FlowFriction" /> during large-delta advection.
    ///     Used to reduce oscillation in the sim.
    /// </summary>
    public float DampingFactor { get; set; } = 0.5f;

    /// <summary>
    ///     Below this pressure delta, flow uses the <see cref="CflFlowCap" /> directly
    ///     instead of <see cref="FlowFriction" /> * <see cref="DampingFactor" />
    /// </summary>
    public float SnapThreshold { get; set; } = 5.0f;

    /// <summary>
    ///     Flows below this magnitude are discarded.
    /// </summary>
    public float MinFlowCutoff { get; set; } = 0.1f;

    /// <summary>
    ///     Below this pressure, voxel contents are zeroed out.
    /// </summary>
    public float VacuumThreshold { get; set; } = 1.0f;

    /// <summary>
    ///     Consecutive ticks below <see cref="SleepEpsilon" /> before a chunk goes to sleep.
    /// </summary>
    public int SleepThreshold { get; set; } = 100;

    /// <summary>
    ///     Maximum pressure delta considered "at rest".
    /// </summary>
    public float SleepEpsilon { get; set; } = 3.5f;

    /// <summary>
    ///     Effective thermal conductance between adjacent voxels, in joules per kelvin (J/K) per
    ///     thermodynamics tick (currently every second simulation tick).
    /// </summary>
    /// <remarks>
    ///     The simulation applies equal-and-opposite energy transfers to conserve sensible energy. Transfer
    ///     limiting makes each updated gas-bearing temperature a convex combination of temperatures participating
    ///     in the solve, preventing negative temperatures and new temperature extrema. Non-finite or nonpositive
    ///     values disable thermal diffusion.
    /// </remarks>
    public float ThermalConductivity { get; set; } = 0.05f;

    /// <summary>
    ///     Rate multiplier for phase-change condensation.
    /// </summary>
    public float CondensationRateFactor { get; set; } = 0.5f;

    /// <summary>
    ///     Maximum fraction of a source voxel's pressure used by the bulk-advection term for one neighbor per tick.
    /// </summary>
    /// <remarks>Passive Fickian diffusion is calculated separately and is not capped by this value.</remarks>
    public float CflFlowCap { get; set; } = 0.16f;

    public float AccumulatorWakeThreshold { get; set; } = 15.0f;
    public int AccumulatorMaxAliveTicks { get; set; } = 20;
}
