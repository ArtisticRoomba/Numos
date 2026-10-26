using Numos.CoreSim.Solvers;
using Numos.CoreSim.State;
using Numos.Maths;
using Magnitudo;

namespace Numos.CoreSim;

/// <summary>
///     Editable simulation configuration.
/// </summary>
/// <remarks>
///     <para>
///         Nothing here is validated on assignment. <see cref="CreateSnapshot" /> normalizes out-of-range values and
///         produces the immutable <see cref="AtmosConfigSnapshot" /> a simulation actually runs with.
///     </para>
///     <para>
///         The <see cref="NumosSerializeAttribute" /> members are the configuration's state schema. A snapshot keeps
///         its normalized values in one of these, so the generated equality, hash, and wire code cover both types. A
///         new setting needs <see cref="NumosSerializeAttribute" /> here, its normalization in the snapshot
///         constructor, and a snapshot property of the same name and type.
///     </para>
/// </remarks>
public partial class AtmosConfig : IAtmosConfig
{
    /// <summary>
    ///     Creates a configuration with no registered gases and every setting at its <see cref="AtmosConfigDefaults" />
    ///     value.
    /// </summary>
    public AtmosConfig()
    {
    }

    /// <summary>
    ///     Creates an editable copy of an immutable simulation configuration.
    /// </summary>
    /// <param name="source">The configuration to copy. Its values are already normalized.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source" /> is <see langword="null" />.</exception>
    public AtmosConfig(AtmosConfigSnapshot source)
    {
        ArgumentNullException.ThrowIfNull(source);
        GasRegistry = new GasRegistry();
        foreach (var gas in source.GasRegistry)
            GasRegistry.Add(gas);

        SolverConfigurations = source.SolverConfigurations.ToList();
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
        ReferenceMolarMass = source.ReferenceMolarMass;
    }

    /// <summary>
    ///     Gases registered to the simulation. A gas's index in this registry is its gas ID.
    /// </summary>
    [NumosSerialize]
    public GasRegistry GasRegistry { get; set; } = [];

    /// <summary>
    ///     Gets or sets solver-owned settings, captured through each implementation's immutable snapshot contract.
    /// </summary>
    /// <remarks>
    ///     Keys must be unique and nonempty. Register a <c>GasReactionConfig</c> here to configure reactions,
    ///     or supply an <see cref="IAtmosSolverConfiguration" /> for a custom solver without changing Numos.
    /// </remarks>
    // Saved and hashed after the schema fields, as keyed sections: entries are polymorphic, so the codec needs the key
    // to find each one's type (see AtmosConfigSnapshotCodec).
    [NumosIgnore]
    public List<IAtmosSolverConfiguration> SolverConfigurations { get; set; } = [];

    IReadOnlyList<IAtmosSolverConfiguration> IAtmosConfig.SolverConfigurations => SolverConfigurations;

    /// <summary>
    ///     Reference ambient temperature, in kelvins (K).
    /// </summary>
    /// <remarks>Non-finite and nonpositive values are normalized to <see cref="AtmosConfigDefaults.GlobalTemperature" />.</remarks>
    [Quantity("temperature")]
    [NumosSerialize]
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
    [NumosSerialize]
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
    [NumosSerialize]
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
    [NumosSerialize]
    public CubicMetre VoxelVolume { get; set; } = AtmosConfigDefaults.VoxelVolume;

    /// <summary>
    ///     Saturation pressure associated with <see cref="GasProperties.BoilingPoint" />, in pascals (Pa).
    /// </summary>
    /// <remarks>
    ///     The default is one standard atmosphere. Non-finite and nonpositive values are normalized to that
    ///     default by the phase-change solver.
    /// </remarks>
    [Quantity("pressure")]
    [NumosSerialize]
    public Pascal SaturationReferencePressure { get; set; } =
        AtmosConfigDefaults.SaturationReferencePressure;

    /// <summary>
    ///     Reference diffusivity used for gas IDs missing from <see cref="GasRegistry" />.
    /// </summary>
    /// <remarks>
    ///     In square metres per second (m²/s), scaled the same way as <see cref="GasProperties.DiffusionCoefficient" />.
    ///     Negative values clamp to zero. Non-finite values disable fallback diffusion.
    /// </remarks>
    [Quantity("diffusivity")]
    [NumosSerialize]
    public SquareMetrePerSecond DefaultDiffusionCoefficient { get; set; } =
        AtmosConfigDefaults.DefaultDiffusionCoefficient;

    /// <summary>
    ///     Modeled temperature of space, in kelvins (K).
    /// </summary>
    /// <remarks>Non-finite and nonpositive values are normalized to <see cref="AtmosConfigDefaults.SpaceTemperature" />.</remarks>
    [Quantity("temperature")]
    [NumosSerialize]
    public Kelvin SpaceTemperature { get; set; } = AtmosConfigDefaults.SpaceTemperature;

    /// <summary>
    ///     Dimensionless fraction of a pressure delta requested as bulk flow per simulation tick.
    /// </summary>
    /// <remarks>
    ///     Values are clamped to [0, 1]. Non-finite values disable large-delta bulk flow. The solvers cap the
    ///     effective coefficient at 0.5.
    /// </remarks>
    [NumosSerialize]
    public Scalar BulkFlowCoefficient { get; set; } = AtmosConfigDefaults.BulkFlowCoefficient;

    /// <summary>
    ///     Pressure below which a voxel is eligible for vacuum cleanup, in pascals (Pa).
    /// </summary>
    /// <remarks>
    ///     Cleanup removes the voxel's gas only when every orthogonally adjacent air voxel is also below this
    ///     threshold. Non-finite and negative values are normalized to zero.
    /// </remarks>
    [Quantity("pressure")]
    [NumosSerialize]
    public Pascal VacuumThreshold { get; set; } = AtmosConfigDefaults.VacuumThreshold;

    /// <summary>
    ///     Consecutive ticks below <see cref="SleepEpsilon" /> before a chunk goes to sleep.
    /// </summary>
    /// <remarks>Negative values are normalized to zero.</remarks>
    [NumosSerialize]
    public int SleepThreshold { get; set; } = AtmosConfigDefaults.SleepThreshold;

    /// <summary>
    ///     Maximum relative pressure difference considered "at rest", as a percentage of the higher neighboring
    ///     pressure.
    /// </summary>
    /// <remarks>
    ///     For example, <c>3.5</c> allows a neighboring pressure difference below 3.5%. Non-finite and negative values
    ///     are normalized to zero.
    /// </remarks>
    [NumosSerialize]
    public Scalar SleepEpsilon { get; set; } = AtmosConfigDefaults.SleepEpsilon;

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
    [NumosSerialize]
    public JoulePerKelvin ThermalConductance { get; set; } = AtmosConfigDefaults.ThermalConductance;

    /// <summary>
    ///     Dimensionless fraction of the heat-coupled equilibrium condensation amount applied per
    ///     thermodynamics tick.
    /// </summary>
    /// <remarks>Values are clamped to [0, 1]. Non-finite values disable condensation.</remarks>
    [NumosSerialize]
    public Scalar CondensationRateFactor { get; set; } = AtmosConfigDefaults.CondensationRateFactor;

    /// <summary>
    ///     Maximum fraction of a source voxel's pressure used by the bulk-advection term for one neighbor per tick.
    /// </summary>
    /// <remarks>
    ///     Values are clamped to [0, 1]. Passive Fickian diffusion is calculated separately and is not capped by this
    ///     value. The built-in solvers do not read this setting, but Numos still captures, hashes, and records it.
    /// </remarks>
    [NumosSerialize]
    public Scalar MaxPressureTransferFractionPerNeighbor { get; set; } =
        AtmosConfigDefaults.MaxPressureTransferFractionPerNeighbor;

    /// <summary>
    ///     Minimum accumulated pressure activity required to wake a sleeping chunk, in pascals (Pa).
    /// </summary>
    /// <remarks>
    ///     Non-finite and negative values are normalized to zero. The built-in solvers do not read this
    ///     setting, but Numos still captures, hashes, and records it.
    /// </remarks>
    [Quantity("pressure")]
    [NumosSerialize]
    public Pascal AccumulatorWakeThreshold { get; set; } = AtmosConfigDefaults.AccumulatorWakeThreshold;

    /// <summary>
    ///     Maximum number of ticks that an accumulated activity value remains alive.
    /// </summary>
    /// <remarks>
    ///     Negative values are normalized to zero. The built-in solvers do not read this setting, but Numos still
    ///     captures, hashes, and records it.
    /// </remarks>
    [NumosSerialize]
    public int AccumulatorMaxAliveTicks { get; set; } = AtmosConfigDefaults.AccumulatorMaxAliveTicks;

    /// <summary>
    ///     Molar mass assumed for gases whose <see cref="GasProperties.MolarMass" /> is unset, in kilograms per mole
    ///     (kg/mol).
    /// </summary>
    /// <remarks>
    ///     The compressible transport solver needs a mass for every gas it moves, so a gas without one weighs this
    ///     much. Non-finite and nonpositive values are normalized to
    ///     <see cref="AtmosConfigDefaults.ReferenceMolarMass" />, dry air.
    /// </remarks>
    [Quantity("molarMass")]
    [NumosSerialize]
    public KilogramPerMole ReferenceMolarMass { get; set; } = AtmosConfigDefaults.ReferenceMolarMass;

    /// <summary>
    ///     Ideal-gas pressure coefficient <c>R/V</c> for one voxel, in pascals per mole-kelvin (Pa/(mol·K)).
    /// </summary>
    public PascalPerMoleKelvin PressurePerMoleKelvin =>
        AtmosPhysicalConstants.MolarGasConstant / GetVoxelVolume();

    /// <summary>
    ///     Returns the temperature the solvers use for a stored voxel temperature.
    /// </summary>
    /// <param name="storedTemperature">A voxel's stored temperature, in kelvins (K).</param>
    /// <returns>
    ///     <paramref name="storedTemperature" /> when it is finite and positive; otherwise
    ///     <see cref="DefaultTemperatureFallback" />.
    /// </returns>
    public Kelvin GetValidatedTemp(Kelvin storedTemperature)
    {
        return FloatMath.IsFinitePositive(storedTemperature) ? storedTemperature : DefaultTemperatureFallback;
    }

    /// <summary>
    ///     Returns <see cref="VoxelVolume" />, or the default of <c>1 m³</c> when it is non-finite or nonpositive.
    /// </summary>
    public CubicMetre GetVoxelVolume()
    {
        return FloatMath.IsFinitePositive(VoxelVolume)
            ? VoxelVolume
            : AtmosConfigDefaults.VoxelVolume;
    }

    /// <summary>
    ///     Resolves the molar heat capacity at constant volume used for a gas, in J/(mol·K).
    /// </summary>
    /// <param name="gasId">The gas ID. Unregistered IDs are allowed.</param>
    /// <returns>
    ///     The gas's configured value when it is finite and positive; otherwise
    ///     <see cref="DefaultMolarHeatCapacityAtConstantVolume" />, or the ideal-diatomic value if that is invalid too.
    /// </returns>
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

    /// <summary>
    ///     Resolves the molar mass used for a gas, in kg/mol.
    /// </summary>
    /// <param name="gasId">The gas ID. Unregistered IDs are allowed.</param>
    /// <returns>
    ///     The gas's configured value when it is finite and positive.
    ///     Otherwise <see cref="ReferenceMolarMass" />, or dry air if that is invalid too.
    /// </returns>
    public KilogramPerMole GetMolarMass(int gasId)
    {
        if ((uint)gasId < (uint)GasRegistry.Count)
        {
            KilogramPerMole configured = GasRegistry[gasId].MolarMass;
            if (FloatMath.IsFinitePositive(configured))
                return configured;
        }

        return FloatMath.IsFinitePositive(ReferenceMolarMass)
            ? ReferenceMolarMass
            : AtmosConfigDefaults.ReferenceMolarMass;
    }

    /// <summary>
    ///     Resolves the reference diffusivity for a gas in m²/s, clamped to zero or greater.
    /// </summary>
    /// <param name="gasId">The gas ID. Unregistered IDs use <see cref="DefaultDiffusionCoefficient" />.</param>
    public SquareMetrePerSecond GetDiffusionCoefficient(int gasId)
    {
        return (uint)gasId < (uint)GasRegistry.Count
            ? FloatMath.GetNonnegativeFinite(GasRegistry[gasId].DiffusionCoefficient)
            : FloatMath.GetNonnegativeFinite(DefaultDiffusionCoefficient);
    }

    /// <summary>
    ///     Looks up a registered gas by ID.
    /// </summary>
    /// <param name="gasId">The gas ID.</param>
    /// <param name="properties">The registered properties, or <see langword="default" /> when not found.</param>
    /// <returns><see langword="true" /> if <paramref name="gasId" /> is registered.</returns>
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

    /// <summary>
    ///     Number of registered gases; valid gas IDs are <c>0</c> through <c>GasPropertyCount - 1</c>.
    /// </summary>
    public int GasPropertyCount => GasRegistry.Count;

    /// <summary>
    ///     Captures an immutable, detached, normalized copy of this configuration.
    /// </summary>
    /// <returns>A snapshot unaffected by later changes to this object.</returns>
    /// <exception cref="InvalidOperationException">
    ///     <see cref="GasRegistry" /> contains duplicate names, or a solver configuration snapshot changed its key.
    /// </exception>
    /// <exception cref="ArgumentException">Solver configuration keys are empty or not unique.</exception>
    public AtmosConfigSnapshot CreateSnapshot()
    {
        return new AtmosConfigSnapshot(this);
    }

    /// <summary>
    ///     Checks <see cref="GasRegistry" /> for duplicate gas names.
    /// </summary>
    /// <exception cref="InvalidOperationException">Two or more gases share a name.</exception>
    public void ValidateGasRegistry()
    {
        GasRegistry.ValidateGasRegistry();
    }
}