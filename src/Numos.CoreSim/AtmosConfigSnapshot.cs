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
    [ConfigCheckpointField(0)] public Kelvin GlobalTemperature { get; }
    /// <inheritdoc cref="AtmosConfig.DefaultTemperatureFallback" />
    [ConfigCheckpointField(1)] public Kelvin DefaultTemperatureFallback { get; }
    /// <inheritdoc cref="AtmosConfig.DefaultMolarHeatCapacityAtConstantVolume" />
    [ConfigCheckpointField(2)] public JoulePerMoleKelvin DefaultMolarHeatCapacityAtConstantVolume { get; }
    /// <inheritdoc cref="AtmosConfig.VoxelVolume" />
    [ConfigCheckpointField(3)] public CubicMetre VoxelVolume { get; }
    /// <inheritdoc cref="AtmosConfig.SaturationReferencePressure" />
    [ConfigCheckpointField(4)] public Pascal SaturationReferencePressure { get; }
    /// <inheritdoc cref="AtmosConfig.DefaultDiffusionCoefficient" />
    [ConfigCheckpointField(5)] public Scalar DefaultDiffusionCoefficient { get; }
    /// <inheritdoc cref="AtmosConfig.SpaceTemperature" />
    [ConfigCheckpointField(6)] public Kelvin SpaceTemperature { get; }
    /// <inheritdoc cref="AtmosConfig.BulkFlowCoefficient" />
    [ConfigCheckpointField(7)] public Scalar BulkFlowCoefficient { get; }
    /// <inheritdoc cref="AtmosConfig.VacuumThreshold" />
    [ConfigCheckpointField(8)] public Pascal VacuumThreshold { get; }
    /// <inheritdoc cref="AtmosConfig.SleepThreshold" />
    [ConfigCheckpointField(9)] public int SleepThreshold { get; }
    /// <inheritdoc cref="AtmosConfig.SleepEpsilon" />
    [ConfigCheckpointField(10)] public Scalar SleepEpsilon { get; }
    /// <inheritdoc cref="AtmosConfig.ThermalConductance" />
    [ConfigCheckpointField(11)] public JoulePerKelvin ThermalConductance { get; }
    /// <inheritdoc cref="AtmosConfig.CondensationRateFactor" />
    [ConfigCheckpointField(12)] public Scalar CondensationRateFactor { get; }
    /// <inheritdoc cref="AtmosConfig.MaxPressureTransferFractionPerNeighbor" />
    [ConfigCheckpointField(13)] public Scalar MaxPressureTransferFractionPerNeighbor { get; }
    /// <inheritdoc cref="AtmosConfig.AccumulatorWakeThreshold" />
    [ConfigCheckpointField(14)] public Pascal AccumulatorWakeThreshold { get; }
    /// <inheritdoc cref="AtmosConfig.AccumulatorMaxAliveTicks" />
    [ConfigCheckpointField(15)] public int AccumulatorMaxAliveTicks { get; }

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

    /// <inheritdoc cref="AtmosConfig.ValidateGasRegistry" />
    public void ValidateGasRegistry()
    {
        GasRegistry.ValidateGasRegistry();
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