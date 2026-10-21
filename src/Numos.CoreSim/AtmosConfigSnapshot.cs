using System.Diagnostics.CodeAnalysis;
using Numos.CoreSim.GasReactions;
using Numos.CoreSim.Replay;
using Numos.CoreSim.State;
using Numos.CoreSim.State.Advanced;
using Numos.Maths;

namespace Numos.CoreSim;

/// <summary>
///     Immutable, detached configuration used by an atmospheric simulation.
/// </summary>
/// <remarks>
///     Create an editable <see cref="AtmosConfig" />, then apply it with
///     <c>AtmosSimulation.SetAtmosConfig</c>. Retaining or changing the editable object cannot mutate this snapshot.
///     Every value has already been normalized by the rules documented on the matching <see cref="AtmosConfig" />
///     member.
/// </remarks>
public sealed class AtmosConfigSnapshot : IAtmosConfig
{
    private ReactionTables? _reactionTables;

    internal AtmosConfigSnapshot(AtmosConfig source)
    {
        ArgumentNullException.ThrowIfNull(source);
        source.ValidateGasRegistry();

        // The normalized values live in a private AtmosConfig that nothing else references, so the snapshot gets the
        // config's generated equality, hash, and wire code without a second field list.
        var fields = new AtmosConfig
        {
            GlobalTemperature = FloatMath.IsFinitePositive(source.GlobalTemperature)
                ? source.GlobalTemperature
                : AtmosConfigDefaults.GlobalTemperature,
            DefaultTemperatureFallback = FloatMath.IsFinitePositive(source.DefaultTemperatureFallback)
                ? source.DefaultTemperatureFallback
                : AtmosConfigDefaults.DefaultTemperatureFallback,
            DefaultMolarHeatCapacityAtConstantVolume =
                FloatMath.IsFinitePositive(source.DefaultMolarHeatCapacityAtConstantVolume)
                    ? source.DefaultMolarHeatCapacityAtConstantVolume
                    : AtmosConfigDefaults.DefaultMolarHeatCapacityAtConstantVolume,
            VoxelVolume = FloatMath.IsFinitePositive(source.VoxelVolume)
                ? source.VoxelVolume
                : AtmosConfigDefaults.VoxelVolume,
            SaturationReferencePressure = FloatMath.IsFinitePositive(source.SaturationReferencePressure)
                ? source.SaturationReferencePressure
                : AtmosConfigDefaults.SaturationReferencePressure,
            DefaultDiffusionCoefficient = FloatMath.GetNonnegativeFinite(source.DefaultDiffusionCoefficient),
            SpaceTemperature = FloatMath.IsFinitePositive(source.SpaceTemperature)
                ? source.SpaceTemperature
                : AtmosConfigDefaults.SpaceTemperature,
            BulkFlowCoefficient = FloatMath.ClampUnitInterval(source.BulkFlowCoefficient),
            VacuumThreshold = FloatMath.GetNonnegativeFinite(source.VacuumThreshold),
            SleepThreshold = Math.Max(0, source.SleepThreshold),
            SleepEpsilon = FloatMath.GetNonnegativeFinite(source.SleepEpsilon),
            ThermalConductance = FloatMath.IsFinitePositive(source.ThermalConductance)
                ? source.ThermalConductance
                : 0f,
            CondensationRateFactor = FloatMath.ClampUnitInterval(source.CondensationRateFactor),
            MaxPressureTransferFractionPerNeighbor =
                FloatMath.ClampUnitInterval(source.MaxPressureTransferFractionPerNeighbor),
            AccumulatorWakeThreshold = FloatMath.GetNonnegativeFinite(source.AccumulatorWakeThreshold),
            AccumulatorMaxAliveTicks = Math.Max(0, source.AccumulatorMaxAliveTicks),
            ReferenceMolarMass = FloatMath.IsFinitePositive(source.ReferenceMolarMass)
                ? source.ReferenceMolarMass
                : AtmosConfigDefaults.ReferenceMolarMass,
        };

        var gasRegistry = new GasRegistry();
        foreach (var sourceProperties in source.GasRegistry)
        {
            var properties = sourceProperties;
            if (!FloatMath.IsFinitePositive(properties.MolarHeatCapacityAtConstantVolume))
            {
                properties.MolarHeatCapacityAtConstantVolume =
                    fields.DefaultMolarHeatCapacityAtConstantVolume;
            }

            properties.DiffusionCoefficient = FloatMath.GetNonnegativeFinite(properties.DiffusionCoefficient);

            // Every invalid molar mass already means "unknown", so store the one canonical spelling of it.
            if (!FloatMath.IsFinitePositive(properties.MolarMass))
                properties.MolarMass = 0f;

            gasRegistry.Add(properties);
        }

        fields.GasRegistry = gasRegistry;
        Fields = fields;
        GasRegistry = new GasRegistrySnapshot(gasRegistry);

        var settings = new List<IAtmosSolverConfiguration>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var configuration in source.SolverConfigurations)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            if (string.IsNullOrWhiteSpace(configuration.Key) || !keys.Add(configuration.Key))
                throw new ArgumentException("Solver configuration keys must be nonempty and unique.", nameof(source));

            var snapshot = configuration.CreateSnapshot(GasRegistry);
            if (snapshot == null || snapshot.Key != configuration.Key)
                throw new InvalidOperationException("Solver configuration snapshots must retain their key.");

            settings.Add(snapshot);
        }

        SolverConfigurations = Array.AsReadOnly(settings.OrderBy(static value => value.Key, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    ///     Gets the registered gases, with invalid heat capacities replaced by
    ///     <see cref="DefaultMolarHeatCapacityAtConstantVolume" />, diffusion coefficients clamped to zero or greater, and
    ///     invalid molar masses normalized to zero.
    /// </summary>
    public GasRegistrySnapshot GasRegistry { get; }

    /// <summary>
    ///     Gets immutable solver-owned configurations ordered by their ordinal keys.
    /// </summary>
    public IReadOnlyList<IAtmosSolverConfiguration> SolverConfigurations { get; }

    /// <summary>
    ///     Gets the normalized schema fields. Never handed out or mutated after construction.
    /// </summary>
    internal AtmosConfig Fields { get; }

    /// <inheritdoc cref="AtmosConfig.GlobalTemperature" />
    public Kelvin GlobalTemperature => Fields.GlobalTemperature;
    /// <inheritdoc cref="AtmosConfig.DefaultTemperatureFallback" />
    public Kelvin DefaultTemperatureFallback => Fields.DefaultTemperatureFallback;
    /// <inheritdoc cref="AtmosConfig.DefaultMolarHeatCapacityAtConstantVolume" />
    public JoulePerMoleKelvin DefaultMolarHeatCapacityAtConstantVolume => Fields.DefaultMolarHeatCapacityAtConstantVolume;
    /// <inheritdoc cref="AtmosConfig.VoxelVolume" />
    public CubicMetre VoxelVolume => Fields.VoxelVolume;
    /// <inheritdoc cref="AtmosConfig.SaturationReferencePressure" />
    public Pascal SaturationReferencePressure => Fields.SaturationReferencePressure;
    /// <inheritdoc cref="AtmosConfig.DefaultDiffusionCoefficient" />
    public SquareMetrePerSecond DefaultDiffusionCoefficient => Fields.DefaultDiffusionCoefficient;
    /// <inheritdoc cref="AtmosConfig.SpaceTemperature" />
    public Kelvin SpaceTemperature => Fields.SpaceTemperature;
    /// <inheritdoc cref="AtmosConfig.BulkFlowCoefficient" />
    public Scalar BulkFlowCoefficient => Fields.BulkFlowCoefficient;
    /// <inheritdoc cref="AtmosConfig.VacuumThreshold" />
    public Pascal VacuumThreshold => Fields.VacuumThreshold;
    /// <inheritdoc cref="AtmosConfig.SleepThreshold" />
    public int SleepThreshold => Fields.SleepThreshold;
    /// <inheritdoc cref="AtmosConfig.SleepEpsilon" />
    public Scalar SleepEpsilon => Fields.SleepEpsilon;
    /// <inheritdoc cref="AtmosConfig.ThermalConductance" />
    public JoulePerKelvin ThermalConductance => Fields.ThermalConductance;
    /// <inheritdoc cref="AtmosConfig.CondensationRateFactor" />
    public Scalar CondensationRateFactor => Fields.CondensationRateFactor;
    /// <inheritdoc cref="AtmosConfig.MaxPressureTransferFractionPerNeighbor" />
    public Scalar MaxPressureTransferFractionPerNeighbor => Fields.MaxPressureTransferFractionPerNeighbor;
    /// <inheritdoc cref="AtmosConfig.AccumulatorWakeThreshold" />
    public Pascal AccumulatorWakeThreshold => Fields.AccumulatorWakeThreshold;
    /// <inheritdoc cref="AtmosConfig.AccumulatorMaxAliveTicks" />
    public int AccumulatorMaxAliveTicks => Fields.AccumulatorMaxAliveTicks;
    /// <inheritdoc cref="AtmosConfig.ReferenceMolarMass" />
    public KilogramPerMole ReferenceMolarMass => Fields.ReferenceMolarMass;

    /// <inheritdoc cref="AtmosConfig.PressurePerMoleKelvin" />
    public PascalPerMoleKelvin PressurePerMoleKelvin =>
        AtmosPhysicalConstants.MolarGasConstant / GetVoxelVolume();

    /// <inheritdoc cref="AtmosConfig.GetValidatedTemp" />
    public Kelvin GetValidatedTemp(Kelvin storedTemperature)
    {
        return FloatMath.IsFinitePositive(storedTemperature) ? storedTemperature : DefaultTemperatureFallback;
    }

    /// <inheritdoc cref="AtmosConfig.GetVoxelVolume" />
    public CubicMetre GetVoxelVolume()
    {
        return FloatMath.IsFinitePositive(VoxelVolume) ? VoxelVolume : AtmosConfigDefaults.VoxelVolume;
    }

    /// <inheritdoc cref="AtmosConfig.GetMolarHeatCapacityAtConstantVolume" />
    public JoulePerMoleKelvin GetMolarHeatCapacityAtConstantVolume(int gasId)
    {
        JoulePerMoleKelvin fallback = FloatMath.IsFinitePositive(DefaultMolarHeatCapacityAtConstantVolume)
            ? DefaultMolarHeatCapacityAtConstantVolume
            : AtmosConfigDefaults.DefaultMolarHeatCapacityAtConstantVolume;

        if ((uint)gasId < (uint)GasRegistry.Count)
        {
            JoulePerMoleKelvin configured = GasRegistry[gasId].MolarHeatCapacityAtConstantVolume;
            if (FloatMath.IsFinitePositive(configured))
                return configured;
        }

        return fallback;
    }

    /// <inheritdoc cref="AtmosConfig.GetDiffusionCoefficient" />
    public SquareMetrePerSecond GetDiffusionCoefficient(int gasId)
    {
        return (uint)gasId < (uint)GasRegistry.Count
            ? FloatMath.GetNonnegativeFinite(GasRegistry[gasId].DiffusionCoefficient)
            : FloatMath.GetNonnegativeFinite(DefaultDiffusionCoefficient);
    }

    /// <inheritdoc cref="AtmosConfig.GetMolarMass" />
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

    /// <inheritdoc cref="AtmosConfig.TryGetGasProperties" />
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

    /// <inheritdoc cref="AtmosConfig.GasPropertyCount" />
    public int GasPropertyCount => GasRegistry.Count;

    /// <summary>
    ///     Finds the only solver configuration of type <typeparamref name="T" />.
    /// </summary>
    /// <typeparam name="T">
    ///     The captured configuration type: whatever <see cref="IAtmosSolverConfiguration.CreateSnapshot" /> returned,
    ///     which need not be the type added to <see cref="AtmosConfig.SolverConfigurations" />.
    /// </typeparam>
    /// <param name="configuration">The match, or <see langword="null" /> when there is none.</param>
    /// <returns><see langword="true" /> when exactly one configuration is a <typeparamref name="T" />.</returns>
    /// <remarks>
    ///     Use this when a configuration type is only ever registered once. Types that can appear under several keys
    ///     should use <see cref="TryGetSolverConfiguration{T}(string, out T)" /> instead.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    ///     More than one configuration is a <typeparamref name="T" />.
    /// </exception>
    /// <example>
    ///     <code>
    ///     if (simulation.Config.TryGetSolverConfiguration(out HeaterSettings? heater))
    ///         rate = heater.Rate;
    ///     </code>
    /// </example>
    public bool TryGetSolverConfiguration<T>([NotNullWhen(true)] out T? configuration)
        where T : class, IAtmosSolverConfiguration
    {
        return SolverConfigurationLookup.TryGet(SolverConfigurations, out configuration);
    }

    /// <summary>
    ///     Finds the solver configuration registered under <paramref name="key" />.
    /// </summary>
    /// <typeparam name="T">The captured configuration type expected under <paramref name="key" />.</typeparam>
    /// <param name="key">The configuration's <see cref="IAtmosSolverConfiguration.Key" />, compared ordinally.</param>
    /// <param name="configuration">The match, or <see langword="null" /> when no configuration uses the key.</param>
    /// <returns><see langword="true" /> when a configuration uses <paramref name="key" />.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key" /> is <see langword="null" />.</exception>
    /// <exception cref="InvalidOperationException">
    ///     The configuration under <paramref name="key" /> is not a <typeparamref name="T" />.
    /// </exception>
    /// <example>
    ///     <code>
    ///     if (simulation.Config.TryGetSolverConfiguration("my-mod/heater", out HeaterSettings? heater))
    ///         rate = heater.Rate;
    ///     </code>
    /// </example>
    public bool TryGetSolverConfiguration<T>(string key, [NotNullWhen(true)] out T? configuration)
        where T : class, IAtmosSolverConfiguration
    {
        return SolverConfigurationLookup.TryGet(SolverConfigurations, key, out configuration);
    }

    /// <inheritdoc cref="AtmosConfig.ValidateGasRegistry" />
    public void ValidateGasRegistry()
    {
        GasRegistry.ValidateGasRegistry();
    }

    /// <summary>
    ///     Gets the reaction solver's tables for this configuration, building them on first use.
    /// </summary>
    /// <remarks>
    ///     The tables are a pure function of this snapshot, so every simulation using it shares one copy and keeps it
    ///     across restores. Concurrent first calls may each build a copy; only one is published.
    /// </remarks>
    internal ReactionTables GetReactionTables()
    {
        var tables = Volatile.Read(ref _reactionTables);
        if (tables != null)
            return tables;

        tables = ReactionTables.Create(this);
        return Interlocked.CompareExchange(ref _reactionTables, tables, null) ?? tables;
    }

    /// <summary>
    ///     Writes this configuration into a state hash: the schema fields, then the solver configurations.
    /// </summary>
    /// <remarks>
    ///     The same walk <see cref="AtmosConfigSnapshotCodec" /> saves, minus its length prefixes: the schema fields,
    ///     then the count and each configuration's key and generated walk. A hand-written configuration can't be
    ///     saved, so it contributes its own <see cref="IAtmosSolverConfiguration.ComputeStateHash" /> instead.
    /// </remarks>
    internal void WriteHash(ref StateHashSink sink)
    {
        AtmosConfigSnapshotCodec.Write(ref sink, this);
    }

    // The schema fields compare bitwise on floats, matching the hash, so 0f and -0f differ.
    internal bool SemanticallyEquals(AtmosConfigSnapshot other)
    {
        return NumosState.StateEquals(Fields, other.Fields) &&
               SolverConfigurations.Count == other.SolverConfigurations.Count &&
               SolverConfigurations.Zip(other.SolverConfigurations).All(static pair =>
                   pair.First.Key == pair.Second.Key && pair.First.SemanticallyEquals(pair.Second));
    }
}