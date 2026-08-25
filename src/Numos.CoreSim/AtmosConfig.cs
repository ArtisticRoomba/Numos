namespace Numos.CoreSim;
using System.Collections.Frozen;
using System.Collections.Immutable;
using Numos.CoreSim.GasReactions;


/// <summary>
///     Configuration values for the simulation.
/// </summary>
public class AtmosConfig
{
    /// <summary>
    ///     List of gases actively registered to the sim.
    /// </summary>
    public List<GasProperties> GasRegistry { get; set; } = [];

    public ImmutableArray<LinearGasReaction> LinearReactionRegistry { get; set; } = [];
    public ImmutableArray<StandardGasReaction> StandardReactionRegistry { get; set; } = [];

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
    ///     Numos calculates pressure in pascals from <c>P = nRT/V</c>. Non-finite and nonpositive values are
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
    /// <remarks>Values are clamped to [0, 1]; non-finite values disable fallback diffusion.</remarks>
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
    ///     Consecutive ticks below <see cref="SleepEpsilon" /> before a chunk goes to sleep.
    /// </summary>
    /// <remarks>Negative values are normalized to zero.</remarks>
    public int SleepThreshold { get; set; } = AtmosConfigDefaults.SleepThreshold;

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
    public float ThermalConductance { get; set; } = AtmosConfigDefaults.ThermalConductance;

    /// <summary>
    ///     Rate multiplier for phase-change condensation.
    /// </summary>
    public float CondensationRateFactor { get; set; } = 0.5f;

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
    /// <summary>
    /// Largest Time Span by which the reaction simulation is allowed to move [ms]
    /// </summary>
    public float MaxDeltaForReactionSteps { get; set; } = 0.15f;
}