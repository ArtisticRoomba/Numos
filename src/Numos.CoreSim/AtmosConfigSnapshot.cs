using Numos.CoreSim.Replay;
using Numos.Maths;

namespace Numos.CoreSim;

/// <summary>
///     Immutable, detached configuration used by an atmospheric simulation.
/// </summary>
/// <remarks>
///     Create an editable <see cref="AtmosConfig" />, then apply it with
///     <c>AtmosSimulation.SetAtmosConfig</c>. Retaining or changing the editable object cannot mutate this snapshot.
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

    public GasRegistrySnapshot GasRegistry { get; }

    /// <summary>
    ///     Gets immutable solver-owned configurations ordered by their ordinal keys.
    /// </summary>
    public IReadOnlyList<IAtmosSolverConfiguration> SolverConfigurations { get; }
    public Kelvin GlobalTemperature { get; }
    public Kelvin DefaultTemperatureFallback { get; }
    public JoulePerMoleKelvin DefaultMolarHeatCapacityAtConstantVolume { get; }
    public CubicMetre VoxelVolume { get; }
    public Pascal SaturationReferencePressure { get; }
    public Scalar DefaultDiffusionCoefficient { get; }
    public Kelvin SpaceTemperature { get; }
    public Scalar BulkFlowCoefficient { get; }
    public Pascal VacuumThreshold { get; }
    public int SleepThreshold { get; }
    public Pascal SleepEpsilon { get; }
    public JoulePerKelvin ThermalConductance { get; }
    public Scalar CondensationRateFactor { get; }
    public Scalar MaxPressureTransferFractionPerNeighbor { get; }
    public Pascal AccumulatorWakeThreshold { get; }
    public int AccumulatorMaxAliveTicks { get; }

    public PascalPerMoleKelvin PressurePerMoleKelvin =>
        AtmosPhysicalConstants.MolarGasConstant / GetVoxelVolume();

    public Kelvin GetValidatedTemp(Kelvin storedTemperature)
    {
        return FloatMath.IsFinitePositive(storedTemperature) ? storedTemperature : DefaultTemperatureFallback;
    }

    public CubicMetre GetVoxelVolume()
    {
        return FloatMath.IsFinitePositive(VoxelVolume) ? VoxelVolume : AtmosConfigDefaults.VoxelVolume;
    }

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

    public void ValidateGasRegistry()
    {
        GasRegistry.ValidateGasRegistry();
    }

    internal void AppendHash(ref AtmosStateHasher hash)
    {
        hash.Add(GlobalTemperature);
        hash.Add(DefaultTemperatureFallback);
        hash.Add(DefaultMolarHeatCapacityAtConstantVolume);
        hash.Add(VoxelVolume);
        hash.Add(SaturationReferencePressure);
        hash.Add(DefaultDiffusionCoefficient);
        hash.Add(SpaceTemperature);
        hash.Add(BulkFlowCoefficient);
        hash.Add(VacuumThreshold);
        hash.Add(SleepThreshold);
        hash.Add(SleepEpsilon);
        hash.Add(ThermalConductance);
        hash.Add(CondensationRateFactor);
        hash.Add(MaxPressureTransferFractionPerNeighbor);
        hash.Add(AccumulatorWakeThreshold);
        hash.Add(AccumulatorMaxAliveTicks);
        hash.Add(GasRegistry.Count);
        foreach (var gas in GasRegistry) hash.Add(gas);
        // Retain the empty extension framing of schema 1/2 for simulations without solver settings.
        hash.Add(0);
        hash.Add(0);
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