using System.Diagnostics.CodeAnalysis;
using Numos.CoreSim.GasReactions;
using Numos.CoreSim.Replay;
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

        GlobalTemperature = FloatMath.IsFinitePositive(source.GlobalTemperature)
            ? source.GlobalTemperature
            : AtmosConfigDefaults.GlobalTemperature;

        DefaultTemperatureFallback = FloatMath.IsFinitePositive(source.DefaultTemperatureFallback)
            ? source.DefaultTemperatureFallback
            : AtmosConfigDefaults.DefaultTemperatureFallback;

        DefaultMolarHeatCapacityAtConstantVolume =
            FloatMath.IsFinitePositive(source.DefaultMolarHeatCapacityAtConstantVolume)
                ? source.DefaultMolarHeatCapacityAtConstantVolume
                : AtmosConfigDefaults.DefaultMolarHeatCapacityAtConstantVolume;

        VoxelVolume = FloatMath.IsFinitePositive(source.VoxelVolume)
            ? source.VoxelVolume
            : AtmosConfigDefaults.VoxelVolume;

        SaturationReferencePressure = FloatMath.IsFinitePositive(source.SaturationReferencePressure)
            ? source.SaturationReferencePressure
            : AtmosConfigDefaults.SaturationReferencePressure;

        DefaultDiffusionCoefficient = FloatMath.ClampUnitInterval(source.DefaultDiffusionCoefficient);
        DefaultThermalConductivity = FloatMath.IsFinitePositive(source.DefaultThermalConductivity)
            ? source.DefaultThermalConductivity
            : AtmosConfigDefaults.DefaultThermalConductivity;
        SpaceTemperature = FloatMath.IsFinitePositive(source.SpaceTemperature)
            ? source.SpaceTemperature
            : AtmosConfigDefaults.SpaceTemperature;

        BulkFlowCoefficient = FloatMath.ClampUnitInterval(source.BulkFlowCoefficient);
        VacuumThreshold = FloatMath.GetNonnegativeFinite(source.VacuumThreshold);
        SleepThreshold = Math.Max(0, source.SleepThreshold);
        SleepEpsilon = FloatMath.GetNonnegativeFinite(source.SleepEpsilon);
        ThermalConductance = FloatMath.IsFinitePositive(source.ThermalConductance)
            ? source.ThermalConductance
            : 0f;

        CondensationRateFactor = FloatMath.ClampUnitInterval(source.CondensationRateFactor);
        MaxPressureTransferFractionPerNeighbor =
            FloatMath.ClampUnitInterval(source.MaxPressureTransferFractionPerNeighbor);

        AccumulatorWakeThreshold = FloatMath.GetNonnegativeFinite(source.AccumulatorWakeThreshold);
        AccumulatorMaxAliveTicks = Math.Max(0, source.AccumulatorMaxAliveTicks);

        var gasRegistry = new GasRegistry();
        foreach (var sourceProperties in source.GasRegistry)
        {
            var properties = sourceProperties;
            if (!FloatMath.IsFinitePositive(properties.MolarHeatCapacityAtConstantVolume))
            {
                properties.MolarHeatCapacityAtConstantVolume =
                    DefaultMolarHeatCapacityAtConstantVolume;
            }

            properties.DiffusionCoefficient = FloatMath.ClampUnitInterval(properties.DiffusionCoefficient);
            gasRegistry.Add(properties);
        }

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
    ///     <see cref="DefaultMolarHeatCapacityAtConstantVolume" /> and diffusion coefficients clamped to [0, 1].
    /// </summary>
    public GasRegistrySnapshot GasRegistry { get; }

    /// <summary>
    ///     Gets immutable solver-owned configurations ordered by their ordinal keys.
    /// </summary>
    public IReadOnlyList<IAtmosSolverConfiguration> SolverConfigurations { get; }

    /// <inheritdoc cref="AtmosConfig.GlobalTemperature" />
    [ConfigCheckpointField(0)]
    public Kelvin GlobalTemperature { get; }
    /// <inheritdoc cref="AtmosConfig.DefaultTemperatureFallback" />
    [ConfigCheckpointField(1)]
    public Kelvin DefaultTemperatureFallback { get; }
    /// <inheritdoc cref="AtmosConfig.DefaultMolarHeatCapacityAtConstantVolume" />
    [ConfigCheckpointField(2)]
    public JoulePerMoleKelvin DefaultMolarHeatCapacityAtConstantVolume { get; }
    /// <inheritdoc cref="AtmosConfig.VoxelVolume" />
    [ConfigCheckpointField(3)]
    public CubicMetre VoxelVolume { get; }
    /// <inheritdoc cref="AtmosConfig.SaturationReferencePressure" />
    [ConfigCheckpointField(4)]
    public Pascal SaturationReferencePressure { get; }
    /// <inheritdoc cref="AtmosConfig.DefaultDiffusionCoefficient" />
    [ConfigCheckpointField(5)]
    public Scalar DefaultDiffusionCoefficient { get; }
    /// <inheritdoc cref="AtmosConfig.DefaultThermalConductivity" />

    [ConfigCheckpointField(6)] public WattsPerMeterKelvin DefaultThermalConductivity { get; }
    /// <inheritdoc cref="AtmosConfig.SpaceTemperature" />
    [ConfigCheckpointField(7)]
    public Kelvin SpaceTemperature { get; }
    /// <inheritdoc cref="AtmosConfig.BulkFlowCoefficient" />
    [ConfigCheckpointField(8)]
    public Scalar BulkFlowCoefficient { get; }
    /// <inheritdoc cref="AtmosConfig.VacuumThreshold" />
    [ConfigCheckpointField(9)]
    public Pascal VacuumThreshold { get; }
    /// <inheritdoc cref="AtmosConfig.SleepThreshold" />
    [ConfigCheckpointField(10)]
    public int SleepThreshold { get; }
    /// <inheritdoc cref="AtmosConfig.SleepEpsilon" />
    [ConfigCheckpointField(11)]
    public Scalar SleepEpsilon { get; }
    /// <inheritdoc cref="AtmosConfig.ThermalConductance" />
    [ConfigCheckpointField(12)]
    public JoulePerKelvin ThermalConductance { get; }
    /// <inheritdoc cref="AtmosConfig.CondensationRateFactor" />
    [ConfigCheckpointField(13)]
    public Scalar CondensationRateFactor { get; }
    /// <inheritdoc cref="AtmosConfig.MaxPressureTransferFractionPerNeighbor" />
    [ConfigCheckpointField(14)]
    public Scalar MaxPressureTransferFractionPerNeighbor { get; }
    /// <inheritdoc cref="AtmosConfig.AccumulatorWakeThreshold" />
    [ConfigCheckpointField(15)]
    public Pascal AccumulatorWakeThreshold { get; }
    /// <inheritdoc cref="AtmosConfig.AccumulatorMaxAliveTicks" />
    [ConfigCheckpointField(16)]
    public int AccumulatorMaxAliveTicks { get; }

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
    public Scalar GetDiffusionCoefficient(int gasId)
    {
        return (uint)gasId < (uint)GasRegistry.Count
            ? FloatMath.ClampUnitInterval(GasRegistry[gasId].DiffusionCoefficient)
            : FloatMath.ClampUnitInterval(DefaultDiffusionCoefficient);
    }

    /// <inheritdoc cref="AtmosConfig.GetThermalConductivity" />
    public WattsPerMeterKelvin GetThermalConductivity(int gasId, Kelvin temperature)
    {
        WattsPerMeterKelvin referenceThermalConductivity = (uint)gasId < (uint)GasRegistry.Count
            ? FloatMath.ClampUnitInterval(GasRegistry[gasId].ThermalConductivity)
            : FloatMath.ClampUnitInterval(DefaultThermalConductivity);
        float ReferenceConductivityTemperature = 300f;
        float ThermalConductivityExponent = 0.7f;
        // Conductivity rises roughly as a power of temperature. The stored value is the one at the reference temperature.
        Scalar temperatureRatio = temperature / ReferenceConductivityTemperature;
        return referenceThermalConductivity * MathF.Pow(temperatureRatio, ThermalConductivityExponent);
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

    internal void AppendHash(ref AtmosStateHasher hash)
    {
        GeneratedCheckpointFields.AppendConfigFields(ref hash, this);
        hash.Add(GasRegistry.Count);
        foreach (var gas in GasRegistry) hash.Add(gas);
        if (SolverConfigurations.Count != 0)
        {
            hash.Add("solver-configurations");
            hash.Add(SolverConfigurations.Count);
            foreach (var configuration in SolverConfigurations)
            {
                hash.Add(configuration.Key);
                hash.Add(configuration.ComputeStateHash());
            }
        }
    }

    internal bool SemanticallyEquals(AtmosConfigSnapshot other)
    {
        return GlobalTemperature.Equals(other.GlobalTemperature) &&
               DefaultTemperatureFallback.Equals(other.DefaultTemperatureFallback) &&
               DefaultMolarHeatCapacityAtConstantVolume.Equals(other.DefaultMolarHeatCapacityAtConstantVolume) &&
               VoxelVolume.Equals(other.VoxelVolume) &&
               SaturationReferencePressure.Equals(other.SaturationReferencePressure) &&
               DefaultDiffusionCoefficient.Equals(other.DefaultDiffusionCoefficient) &&
               SpaceTemperature.Equals(other.SpaceTemperature) &&
               BulkFlowCoefficient.Equals(other.BulkFlowCoefficient) &&
               VacuumThreshold.Equals(other.VacuumThreshold) &&
               SleepThreshold == other.SleepThreshold &&
               SleepEpsilon.Equals(other.SleepEpsilon) &&
               ThermalConductance.Equals(other.ThermalConductance) &&
               CondensationRateFactor.Equals(other.CondensationRateFactor) &&
               MaxPressureTransferFractionPerNeighbor.Equals(other.MaxPressureTransferFractionPerNeighbor) &&
               AccumulatorWakeThreshold.Equals(other.AccumulatorWakeThreshold) &&
               AccumulatorMaxAliveTicks == other.AccumulatorMaxAliveTicks &&
               GasRegistriesEqual(GasRegistry, other.GasRegistry) &&
               SolverConfigurations.Count == other.SolverConfigurations.Count &&
               SolverConfigurations.Zip(other.SolverConfigurations).All(static pair =>
                   pair.First.Key == pair.Second.Key && pair.First.SemanticallyEquals(pair.Second));
    }

    private static bool GasRegistriesEqual(IGasRegistry first, IGasRegistry second)
    {
        if (first.Count != second.Count)
            return false;

        for (int i = 0; i < first.Count; i++)
        {
            var left = first[i];
            var right = second[i];
            if (!string.Equals(left.Name, right.Name, StringComparison.Ordinal) ||
                !left.MolarHeatCapacityAtConstantVolume.Equals(right.MolarHeatCapacityAtConstantVolume) ||
                !left.BoilingPoint.Equals(right.BoilingPoint) ||
                left.CondensationEnabled != right.CondensationEnabled ||
                !left.MolarEnthalpyOfVaporization.Equals(right.MolarEnthalpyOfVaporization) ||
                left.LiquidId != right.LiquidId ||
                !left.DiffusionCoefficient.Equals(right.DiffusionCoefficient))
            {
                return false;
            }
        }

        return true;
    }
}