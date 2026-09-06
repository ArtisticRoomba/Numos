using System.Diagnostics.CodeAnalysis;
using Numos.CoreSim.GasReactions;
using Numos.CoreSim.Solvers;
using Numos.Maths;
using Numos.Units;

namespace Numos.CoreSim;

/// <summary>
///     Configuration values for the simulation.
/// </summary>
public class AtmosConfig : IAtmosConfig
{
    public AtmosConfig()
    {
    }

    /// <summary>Creates an editable copy of an immutable simulation configuration.</summary>
    public AtmosConfig(AtmosConfigSnapshot source)
    {
        ArgumentNullException.ThrowIfNull(source);
        GasRegistry = new GasRegistry();
        foreach (var gas in source.GasRegistry)
            GasRegistry.Add(gas);

        LinearGasReactions = source.LinearGasReactions.ToList();
        StandardGasReactions = source.StandardGasReactions.ToList();
        GlobalTemperature = source.GlobalTemperature;
        DefaultTemperatureFallback = source.DefaultTemperatureFallback;
        DefaultMolarHeatCapacityAtConstantVolume = source.DefaultMolarHeatCapacityAtConstantVolume;
        VoxelVolume = source.VoxelVolume;
        SaturationReferencePressure = source.SaturationReferencePressure;
        DefaultDiffusionCoefficient = source.DefaultDiffusionCoefficient;
        SpaceTemperature = source.SpaceTemperature;
        BulkFlowCoefficient = source.BulkFlowCoefficient;
        VacuumThreshold = source.VacuumThreshold;
        SleepThreshold = source.SleepThreshold;
        SleepEpsilon = source.SleepEpsilon;
        ThermalConductance = source.ThermalConductance;
        CondensationRateFactor = source.CondensationRateFactor;
        MaxPressureTransferFractionPerNeighbor = source.MaxPressureTransferFractionPerNeighbor;
        AccumulatorWakeThreshold = source.AccumulatorWakeThreshold;
        AccumulatorMaxAliveTicks = source.AccumulatorMaxAliveTicks;
    }

    /// <summary>
    ///     List of gases actively registered to the sim.
    /// </summary>
    public GasRegistry GasRegistry { get; set; } = [];

    /// <summary>
    ///     List of gas reactions using a more efficient but less realistic linear model for rate speed.
    /// </summary>
    public List<LinearGasReaction> LinearGasReactions { get; set; } = [];
    /// <summary>
    ///     List of gas reactions using the standard rate law.
    /// </summary>
    public List<StandardGasReaction> StandardGasReactions { get; set; } = [];

    /// <summary>
    ///     Reference ambient temperature, in kelvins (K).
    /// </summary>
    [Quantity("temperature")]
    public Kelvin GlobalTemperature { get; set; } = AtmosConfigDefaults.GlobalTemperature;

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
    [Quantity("temperature")]
    public Kelvin DefaultTemperatureFallback { get; set; } = AtmosConfigDefaults.DefaultTemperatureFallback;

    /// <summary>
    ///     Molar heat capacity at constant volume used when a gas is not registered or its configured
    ///     <see cref="GasProperties.MolarHeatCapacityAtConstantVolume" /> is non-finite or nonpositive, in joules per
    ///     mole-kelvin (J/(mol·K)).
    /// </summary>
    /// <remarks>
    ///     Non-finite and nonpositive fallback values are normalized to the ideal-diatomic value
    ///     <c>5R/2</c> by the simulation.
    /// </remarks>
    [Quantity("molarHeatCapacity")]
    public JoulePerMoleKelvin DefaultMolarHeatCapacityAtConstantVolume { get; set; } =
        AtmosConfigDefaults.DefaultMolarHeatCapacityAtConstantVolume;

    /// <summary>
    ///     Physical volume represented by one voxel, in cubic metres (m³).
    /// </summary>
    /// <remarks>
    ///     Numos calculates pressure in pascals from <c>P = nRT/V</c>. Non-finite and nonpositive values are
    ///     normalized to <c>1 m³</c> by the simulation.
    /// </remarks>
    [Quantity("volume")]
    public CubicMetre VoxelVolume { get; set; } = AtmosConfigDefaults.VoxelVolume;

    /// <summary>
    ///     Saturation pressure associated with <see cref="GasProperties.BoilingPoint" />, in pascals (Pa).
    /// </summary>
    /// <remarks>
    ///     The default is one standard atmosphere. Non-finite and nonpositive values are normalized to that
    ///     default by the phase-change solver.
    /// </remarks>
    [Quantity("pressure")]
    public Pascal SaturationReferencePressure { get; set; } =
        AtmosConfigDefaults.SaturationReferencePressure;

    /// <summary>
    ///     Per-tick Fickian mixing fraction used for gas IDs missing from <see cref="GasRegistry" />.
    /// </summary>
    /// <remarks>Values are clamped to [0, 1]; non-finite values disable fallback diffusion.</remarks>
    public Scalar DefaultDiffusionCoefficient { get; set; } = AtmosConfigDefaults.DefaultDiffusionCoefficient;

    /// <summary>
    ///     Default temperature of space, in kelvins (K).
    /// </summary>
    [Quantity("temperature")]
    public Kelvin SpaceTemperature { get; set; } = AtmosConfigDefaults.SpaceTemperature;

    /// <summary>
    ///     Dimensionless fraction of a pressure delta requested as bulk flow per simulation tick.
    /// </summary>
    /// <remarks>Values are clamped to [0, 1]; non-finite values disable large-delta bulk flow.</remarks>
    public Scalar BulkFlowCoefficient { get; set; } = AtmosConfigDefaults.BulkFlowCoefficient;

    /// <summary>
    ///     Below this pressure, in pascals (Pa), voxel contents are zeroed out.
    /// </summary>
    /// <remarks>Non-finite and negative values are normalized to zero.</remarks>
    [Quantity("pressure")]
    public Pascal VacuumThreshold { get; set; } = AtmosConfigDefaults.VacuumThreshold;

    /// <summary>
    ///     Consecutive ticks below <see cref="SleepEpsilon" /> before a chunk goes to sleep.
    /// </summary>
    /// <remarks>Negative values are normalized to zero.</remarks>
    public int SleepThreshold { get; set; } = AtmosConfigDefaults.SleepThreshold;

    /// <summary>
    ///     Maximum pressure delta considered "at rest", in pascals (Pa).
    /// </summary>
    /// <remarks>Non-finite and negative values are normalized to zero.</remarks>
    [Quantity("pressure")]
    public Pascal SleepEpsilon { get; set; } = AtmosConfigDefaults.SleepEpsilon;

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
    [Quantity("heatCapacity")]
    public JoulePerKelvin ThermalConductance { get; set; } = AtmosConfigDefaults.ThermalConductance;

    /// <summary>
    ///     Dimensionless fraction of the heat-coupled equilibrium condensation amount applied per
    ///     thermodynamics tick.
    /// </summary>
    /// <remarks>Values are clamped to [0, 1]; non-finite values disable condensation.</remarks>
    public Scalar CondensationRateFactor { get; set; } = AtmosConfigDefaults.CondensationRateFactor;

    /// <summary>
    ///     Maximum fraction of a source voxel's pressure used by the bulk-advection term for one neighbor per tick.
    /// </summary>
    /// <remarks>
    ///     Values are clamped to [0, 1]; non-finite values disable bulk flow. Passive Fickian diffusion is
    ///     calculated separately and is not capped by this value.
    /// </remarks>
    public Scalar MaxPressureTransferFractionPerNeighbor { get; set; } =
        AtmosConfigDefaults.MaxPressureTransferFractionPerNeighbor;

    /// <summary>
    ///     Minimum accumulated pressure activity required to wake a sleeping chunk, in pascals (Pa).
    /// </summary>
    [Quantity("pressure")]
    public Pascal AccumulatorWakeThreshold { get; set; } = AtmosConfigDefaults.AccumulatorWakeThreshold;

    /// <summary>
    ///     Maximum number of ticks that an accumulated activity value remains alive.
    /// </summary>
    public int AccumulatorMaxAliveTicks { get; set; } = AtmosConfigDefaults.AccumulatorMaxAliveTicks;

    public PascalPerMoleKelvin PressurePerMoleKelvin =>
        AtmosPhysicalConstants.MolarGasConstant / GetVoxelVolume();

    public Kelvin GetValidatedTemp(Kelvin storedTemperature)
    {
        return FloatMath.IsFinitePositive(storedTemperature) ? storedTemperature : DefaultTemperatureFallback;
    }

    public CubicMetre GetVoxelVolume()
    {
        return FloatMath.IsFinitePositive(VoxelVolume)
            ? VoxelVolume
            : AtmosConfigDefaults.VoxelVolume;
    }

    public JoulePerMoleKelvin GetMolarHeatCapacityAtConstantVolume(int gasId)
    {
        JoulePerMoleKelvin fallback = AtmosSolverMath.IsFinitePositive(DefaultMolarHeatCapacityAtConstantVolume)
            ? DefaultMolarHeatCapacityAtConstantVolume
            : AtmosConfigDefaults.DefaultMolarHeatCapacityAtConstantVolume;

        if ((uint)gasId < (uint)GasRegistry.Count)
        {
            JoulePerMoleKelvin configured = GasRegistry[gasId].MolarHeatCapacityAtConstantVolume;
            if (AtmosSolverMath.IsFinitePositive(configured))
                return configured;
        }

        return fallback;
    }

    public Scalar GetDiffusionCoefficient(int gasId)
    {
        return (uint)gasId < (uint)GasRegistry.Count
            ? FloatMath.ClampUnitInterval(GasRegistry[gasId].DiffusionCoefficient)
            : FloatMath.ClampUnitInterval(DefaultDiffusionCoefficient);
    }

    public bool TryGetGasProperties(int gasId, out GasProperties properties)
    {
        if ((uint)gasId < (uint)GasRegistry.Count)
        {
            properties = GasRegistry[gasId];
            return true;
        }

        properties = default;
        return false;
    }

    public int GasPropertyCount => GasRegistry.Count;

    public bool TryGetGasReaction(int reactionId, [NotNullWhen(true)] out IGasReaction? reaction)
    {
        reaction = null;
        if (reactionId < LinearGasReactions.Count)
        {
            reaction = new LinearGasReaction.Mapped(LinearGasReactions[reactionId], GasRegistry);
            return true;
        }

        reactionId -= LinearGasReactions.Count;
        if (reactionId < StandardGasReactions.Count)
        {
            reaction = new StandardGasReaction.Mapped(StandardGasReactions[reactionId], GasRegistry);
            return true;
        }

        return false;
    }

    public int GasReactionCount => LinearGasReactions.Count + StandardGasReactions.Count;

    /// <summary>Captures an immutable, detached copy of this configuration.</summary>
    public AtmosConfigSnapshot CreateSnapshot()
    {
        return new AtmosConfigSnapshot(this);
    }

    public void ValidateGasRegistry()
    {
        GasRegistry.ValidateGasRegistry();
    }
}