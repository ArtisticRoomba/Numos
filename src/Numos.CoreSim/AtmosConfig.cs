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
    ///     Reference ambient temperature, in kelvins (K).
    /// </summary>
    public float GlobalTemperature { get; set; } = AtmosConfigDefaults.GlobalTemperature;

    /// <summary>
    ///     Effective temperature used for pressure and sensible-energy calculations when a gas-bearing voxel
    ///     has a non-finite or nonpositive stored temperature.
    /// </summary>
    /// <remarks>
    ///     Non-finite and nonpositive values are normalized to
    ///     <see cref="AtmosPhysicalConstants.RoomTemperature" /> by the simulation.
    ///     Energy evolution uses this value as the voxel's starting temperature, then stores the resulting
    ///     blended or transferred temperature.
    /// </remarks>
    public float DefaultTemperatureFallback { get; set; } = AtmosConfigDefaults.DefaultTemperatureFallback;

    /// <summary>
    ///     Molar heat capacity at constant volume used when a gas is not registered or its configured
    ///     <see cref="GasProperties.MolarHeatCapacityAtConstantVolume" /> is non-finite or nonpositive, in joules per
    ///     mole-kelvin (J/(mol·K)).
    /// </summary>
    /// <remarks>
    ///     Non-finite and nonpositive fallback values are normalized to the ideal-diatomic value
    ///     <c>5R/2</c> by the simulation.
    /// </remarks>
    public float DefaultMolarHeatCapacityAtConstantVolume { get; set; } =
        AtmosConfigDefaults.DefaultMolarHeatCapacityAtConstantVolume;

    /// <summary>
    ///     Physical volume represented by one voxel, in cubic metres (m³).
    /// </summary>
    /// <remarks>
    ///     Numos calculates pressure in pascals from <c>P = nRT/V</c>. Non-finite and nonpositive values, and
    ///     positive values for which the single-precision <c>R/V</c> coefficient is unrepresentable, are
    ///     normalized to <c>1 m³</c> by the simulation.
    /// </remarks>
    public float VoxelVolume { get; set; } = AtmosConfigDefaults.VoxelVolume;

    /// <summary>
    ///     Saturation pressure associated with <see cref="GasProperties.BoilingPoint" />, in pascals (Pa).
    /// </summary>
    /// <remarks>
    ///     The default is one standard atmosphere. Non-finite and nonpositive values are normalized to that
    ///     default by the phase-change solver.
    /// </remarks>
    public float SaturationReferencePressure { get; set; } =
        AtmosConfigDefaults.SaturationReferencePressure;

    /// <summary>
    ///     Per-tick Fickian mixing fraction used for gas IDs missing from <see cref="GasRegistry" />.
    /// </summary>
    /// <remarks>
    ///     Values are normalized to [0, 1] and the explicit face update caps the effective fraction at 0.5;
    ///     non-finite values disable fallback diffusion.
    /// </remarks>
    public float DefaultDiffusionCoefficient { get; set; } = AtmosConfigDefaults.DefaultDiffusionCoefficient;

    /// <summary>
    ///     Default temperature of space, in kelvins (K).
    /// </summary>
    public float SpaceTemperature { get; set; } = AtmosConfigDefaults.SpaceTemperature;

    /// <summary>
    ///     Dimensionless fraction of a pressure delta requested as bulk flow per simulation tick.
    /// </summary>
    /// <remarks>Values are clamped to [0, 1]; non-finite values disable large-delta bulk flow.</remarks>
    public float BulkFlowCoefficient { get; set; } = AtmosConfigDefaults.BulkFlowCoefficient;

    /// <summary>
    ///     Multiplier applied to <see cref="BulkFlowCoefficient" /> during large-delta advection.
    ///     Used to reduce oscillation in the sim.
    /// </summary>
    /// <remarks>Values are clamped to [0, 1]; non-finite values disable large-delta bulk flow.</remarks>
    public float BulkFlowDamping { get; set; } = AtmosConfigDefaults.BulkFlowDamping;

    /// <summary>
    ///     Below this pressure delta, in pascals (Pa), flow uses
    ///     <see cref="MaxPressureTransferFractionPerNeighbor" /> directly
    ///     instead of <see cref="BulkFlowCoefficient" /> * <see cref="BulkFlowDamping" />
    /// </summary>
    /// <remarks>Non-finite and negative values are normalized to zero.</remarks>
    public float LowPressureDeltaThreshold { get; set; } = AtmosConfigDefaults.LowPressureDeltaThreshold;

    /// <summary>
    ///     Candidate pressure transfers below this magnitude, in pascals (Pa) per simulation tick, are discarded.
    /// </summary>
    /// <remarks>Non-finite and negative values are normalized to zero.</remarks>
    public float MinimumPressureTransfer { get; set; } = AtmosConfigDefaults.MinimumPressureTransfer;

    /// <summary>
    ///     Below this pressure, in pascals (Pa), voxel contents are zeroed out.
    /// </summary>
    /// <remarks>Non-finite and negative values are normalized to zero.</remarks>
    public float VacuumThreshold { get; set; } = AtmosConfigDefaults.VacuumThreshold;

    /// <summary>
    ///     Consecutive stable verification ticks required before a chunk goes to sleep.
    /// </summary>
    /// <remarks>
    ///     Negative values are normalized to zero. Snap-assisted sleep observes at least one complete built-in
    ///     thermodynamics cadence even when this value is smaller. Legacy pressure-only automatic sleep is
    ///     evaluated by the advection stage and therefore requires that stage to be enabled.
    /// </remarks>
    public int SleepThreshold { get; set; } = AtmosConfigDefaults.SleepThreshold;

    /// <summary>
    ///     Maximum pressure correction considered "at rest", in pascals (Pa).
    /// </summary>
    /// <remarks>Non-finite and negative values are normalized to zero.</remarks>
    public float SleepEpsilon { get; set; } = AtmosConfigDefaults.SleepEpsilon;

    /// <summary>
    ///     Whether neighboring, nearly equilibrated voxels are conservatively combined before automatic sleep.
    /// </summary>
    /// <remarks>
    ///     Snapping is confined to face-connected voxels in one chunk. Disabling it retains the legacy
    ///     pressure-only automatic-sleep behavior while advection is enabled.
    /// </remarks>
    public bool VoxelSnappingEnabled { get; set; } = AtmosConfigDefaults.VoxelSnappingEnabled;

    /// <summary>
    ///     Maximum relative pressure correction allowed when snapping neighboring voxels.
    /// </summary>
    /// <remarks>
    ///     Each member's pressure correction is compared with this fraction of the greatest of its current
    ///     pressure, the proposed aggregate equilibrium pressure, and <see cref="VacuumThreshold" />. The allowed
    ///     correction is never smaller than <see cref="SleepEpsilon" />. The setting has no effect while voxel
    ///     snapping is disabled. Finite values are clamped to [0, 1]; non-finite values normalize to zero.
    /// </remarks>
    public float VoxelSnapPressureRelativeEpsilon { get; set; } =
        AtmosConfigDefaults.VoxelSnapPressureRelativeEpsilon;

    /// <summary>
    ///     Maximum absolute temperature correction allowed when snapping neighboring voxels, in kelvins (K).
    /// </summary>
    /// <remarks>
    ///     This bounds each member's correction to a proposed aggregate equilibrium. The setting has no effect
    ///     while voxel snapping is disabled. Non-finite and negative values normalize to zero.
    /// </remarks>
    public float VoxelSnapTemperatureEpsilon { get; set; } =
        AtmosConfigDefaults.VoxelSnapTemperatureEpsilon;

    /// <summary>
    ///     Maximum absolute per-species mole-fraction correction allowed when snapping neighboring voxels.
    /// </summary>
    /// <remarks>Finite values are clamped to [0, 1]; non-finite values are normalized to zero.</remarks>
    public float VoxelSnapMoleFractionEpsilon { get; set; } =
        AtmosConfigDefaults.VoxelSnapMoleFractionEpsilon;

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
    public float ThermalConductance { get; set; } = AtmosConfigDefaults.ThermalConductance;

    /// <summary>
    ///     Dimensionless fraction of the heat-coupled equilibrium condensation amount applied per
    ///     thermodynamics tick.
    /// </summary>
    /// <remarks>Values are clamped to [0, 1]; non-finite values disable condensation.</remarks>
    public float CondensationRateFactor { get; set; } = AtmosConfigDefaults.CondensationRateFactor;

    /// <summary>
    ///     Maximum fraction of a source voxel's pressure used by the bulk-advection term for one neighbor per tick.
    /// </summary>
    /// <remarks>
    ///     Values are clamped to [0, 1]; non-finite values disable bulk flow. Passive Fickian diffusion is
    ///     calculated separately and is not capped by this value.
    /// </remarks>
    public float MaxPressureTransferFractionPerNeighbor { get; set; } =
        AtmosConfigDefaults.MaxPressureTransferFractionPerNeighbor;

    /// <summary>
    ///     Minimum accumulated pressure activity required to wake a sleeping chunk, in pascals (Pa).
    /// </summary>
    public float AccumulatorWakeThreshold { get; set; } = AtmosConfigDefaults.AccumulatorWakeThreshold;

    /// <summary>
    ///     Maximum number of ticks that an accumulated activity value remains alive.
    /// </summary>
    public int AccumulatorMaxAliveTicks { get; set; } = AtmosConfigDefaults.AccumulatorMaxAliveTicks;
}
