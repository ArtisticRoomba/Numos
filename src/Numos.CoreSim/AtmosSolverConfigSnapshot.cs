using Numos.CoreSim.EulerFlow;
using Numos.CoreSim.GasReactions;
using Numos.CoreSim.Solvers;
using Numos.Maths;

namespace Numos.CoreSim;

/// <summary>
///     Normalized solver inputs captured from the applied immutable configuration at the start of a tick.
/// </summary>
/// <remarks>
///     This reusable snapshot stays stable for the duration of a tick, so the tick sees one consistent configuration.
///     It also moves validation out of per-voxel and per-neighbor hot paths.
/// </remarks>
internal sealed class AtmosSolverConfigSnapshot : IAtmosConfig
{
    private Scalar[] _diffusionCoefficients = [];
    private GasRegistrySnapshot _gasRegistry = default!;
    private JoulePerMoleKelvin[] _molarHeatCapacitiesAtConstantVolume = [];
    private KilogramPerMole[] _molarMasses = [];

    // Only used when the captured configuration is not an AtmosConfigSnapshot, which has its own shared copy.
    private ReactionTables? _reactionTables;

    private AtmosConfigSnapshot? _sourceConfig;

    /// <summary>
    ///     Gets the simulation's solver data. Every capture of a different configuration clears it, so values must be
    ///     caches a factory can rebuild from the configuration and authoritative state.
    /// </summary>
    /// <remarks>
    ///     The data lives here, not on the kernel, so it stays tied to the configuration it was built from.
    ///     Recapturing the same <see cref="AtmosConfigSnapshot" /> returns early. A kernel that holds only
    ///     snapshots therefore keeps the data from tick to tick.
    /// </remarks>
    internal SolverDataStorage SolverData { get; } = new();

    public IReadOnlyList<IAtmosSolverConfiguration> SolverConfigurations { get; private set; } = [];

    /// <summary>
    ///     Gets the Euler transport settings resolved once per capture, so the stage never searches
    ///     <see cref="SolverConfigurations" /> on the tick path.
    /// </summary>
    internal EulerFlowConfig EulerFlow { get; private set; } = EulerFlowConfig.Default;

    public Kelvin GlobalTemperature { get; private set; }
    public Kelvin DefaultTemperatureFallback { get; private set; }
    public CubicMetre VoxelVolume { get; private set; }
    public PascalPerMoleKelvin PressurePerMoleKelvin { get; private set; }
    public Pascal SaturationReferencePressure { get; private set; }
    public Kelvin SpaceTemperature { get; private set; }
    public Scalar BulkFlowCoefficient { get; private set; }
    public Pascal VacuumThreshold { get; private set; }
    public int SleepThreshold { get; private set; }
    public Scalar SleepEpsilon { get; private set; }
    public JoulePerKelvin ThermalConductance { get; private set; }
    public Scalar CondensationRateFactor { get; private set; }
    public Scalar MaxPressureTransferFractionPerNeighbor { get; private set; }
    public Pascal AccumulatorWakeThreshold { get; private set; }
    public int AccumulatorMaxAliveTicks { get; private set; }
    public KilogramPerMole ReferenceMolarMass { get; private set; }

    public JoulePerMoleKelvin DefaultMolarHeatCapacityAtConstantVolume { get; private set; }
    public Scalar DefaultDiffusionCoefficient { get; private set; }

    public CubicMetre GetVoxelVolume()
    {
        return VoxelVolume;
    }

    public Kelvin GetValidatedTemp(Kelvin storedTemperature)
    {
        return FloatMath.IsFinitePositive(storedTemperature) ? storedTemperature : DefaultTemperatureFallback;
    }

    public JoulePerMoleKelvin GetMolarHeatCapacityAtConstantVolume(int gasId)
    {
        return (uint)gasId < (uint)GasPropertyCount
            ? _molarHeatCapacitiesAtConstantVolume[gasId]
            : DefaultMolarHeatCapacityAtConstantVolume;
    }

    public Scalar GetDiffusionCoefficient(int gasId)
    {
        return (uint)gasId < (uint)GasPropertyCount
            ? _diffusionCoefficients[gasId]
            : DefaultDiffusionCoefficient;
    }

    public KilogramPerMole GetMolarMass(int gasId)
    {
        return (uint)gasId < (uint)GasPropertyCount
            ? _molarMasses[gasId]
            : ReferenceMolarMass;
    }

    public bool TryGetGasProperties(int gasId, out GasProperties properties)
    {
        if ((uint)gasId < (uint)GasPropertyCount)
        {
            properties = _gasRegistry[gasId];
            return true;
        }

        properties = default;
        return false;
    }

    public int GasPropertyCount { get; private set; }

    internal void Capture(IAtmosConfig config)
    {
        // Only immutable configurations can safely retain derived data across captures.
        if (config is AtmosConfigSnapshot snapshot && ReferenceEquals(snapshot, _sourceConfig))
            return;

        _sourceConfig = null;
        int gasPropertyCount = config.GasPropertyCount;
        int previousGasRegistryCount = GasPropertyCount;
        if (_molarHeatCapacitiesAtConstantVolume.Length < gasPropertyCount)
        {
            Array.Resize(ref _molarHeatCapacitiesAtConstantVolume, gasPropertyCount);
            Array.Resize(ref _diffusionCoefficients, gasPropertyCount);
            Array.Resize(ref _molarMasses, gasPropertyCount);
        }

        GasPropertyCount = gasPropertyCount;
        GlobalTemperature = FloatMath.IsFinitePositive(config.GlobalTemperature)
            ? config.GlobalTemperature
            : AtmosConfigDefaults.GlobalTemperature;

        DefaultTemperatureFallback = FloatMath.IsFinitePositive(config.DefaultTemperatureFallback)
            ? config.DefaultTemperatureFallback
            : AtmosConfigDefaults.DefaultTemperatureFallback;

        DefaultMolarHeatCapacityAtConstantVolume =
            FloatMath.IsFinitePositive(config.DefaultMolarHeatCapacityAtConstantVolume)
                ? config.DefaultMolarHeatCapacityAtConstantVolume
                : AtmosConfigDefaults.DefaultMolarHeatCapacityAtConstantVolume;

        VoxelVolume = FloatMath.IsFinitePositive(config.VoxelVolume)
            ? config.VoxelVolume
            : AtmosConfigDefaults.VoxelVolume;

        PressurePerMoleKelvin = AtmosPhysicalConstants.MolarGasConstant / VoxelVolume;
        SaturationReferencePressure = FloatMath.IsFinitePositive(config.SaturationReferencePressure)
            ? config.SaturationReferencePressure
            : AtmosConfigDefaults.SaturationReferencePressure;

        DefaultDiffusionCoefficient = FloatMath.ClampUnitInterval(config.DefaultDiffusionCoefficient);
        SpaceTemperature = FloatMath.IsFinitePositive(config.SpaceTemperature)
            ? config.SpaceTemperature
            : AtmosConfigDefaults.SpaceTemperature;

        // Resolved before the per-gas loop because unset gas masses fall back to it.
        ReferenceMolarMass = FloatMath.IsFinitePositive(config.ReferenceMolarMass)
            ? config.ReferenceMolarMass
            : AtmosConfigDefaults.ReferenceMolarMass;

        var gases = new GasRegistry();
        for (int gasId = 0; gasId < GasPropertyCount; gasId++)
        {
            config.TryGetGasProperties(gasId, out var properties);
            gases.Add(properties);
            _molarHeatCapacitiesAtConstantVolume[gasId] =
                FloatMath.IsFinitePositive(properties.MolarHeatCapacityAtConstantVolume)
                    ? properties.MolarHeatCapacityAtConstantVolume
                    : DefaultMolarHeatCapacityAtConstantVolume;

            _diffusionCoefficients[gasId] = FloatMath.ClampUnitInterval(properties.DiffusionCoefficient);
            _molarMasses[gasId] = FloatMath.IsFinitePositive(properties.MolarMass)
                ? properties.MolarMass
                : ReferenceMolarMass;
        }

        _gasRegistry = new GasRegistrySnapshot(gases);

        if (GasPropertyCount < previousGasRegistryCount)
        {
            int removedCount = previousGasRegistryCount - GasPropertyCount;
            Array.Clear(_molarHeatCapacitiesAtConstantVolume, GasPropertyCount, removedCount);
            Array.Clear(_diffusionCoefficients, GasPropertyCount, removedCount);
            Array.Clear(_molarMasses, GasPropertyCount, removedCount);
        }

        BulkFlowCoefficient = FloatMath.ClampUnitInterval(config.BulkFlowCoefficient);
        VacuumThreshold = FloatMath.GetNonnegativeFinite(config.VacuumThreshold);
        SleepThreshold = Math.Max(0, config.SleepThreshold);
        SleepEpsilon = FloatMath.GetNonnegativeFinite(config.SleepEpsilon);
        ThermalConductance = FloatMath.IsFinitePositive(config.ThermalConductance)
            ? config.ThermalConductance
            : 0f;

        CondensationRateFactor = FloatMath.ClampUnitInterval(config.CondensationRateFactor);
        MaxPressureTransferFractionPerNeighbor =
            FloatMath.ClampUnitInterval(config.MaxPressureTransferFractionPerNeighbor);

        AccumulatorWakeThreshold = FloatMath.GetNonnegativeFinite(config.AccumulatorWakeThreshold);
        AccumulatorMaxAliveTicks = Math.Max(0, config.AccumulatorMaxAliveTicks);


        SolverConfigurations = config.SolverConfigurations;
        EulerFlow = EulerFlowConfig.Get(config);

        SolverData.Clear();
        _reactionTables = null;
        _sourceConfig = config as AtmosConfigSnapshot;
    }

    /// <summary>
    ///     Gets the reaction solver's tables for this configuration.
    /// </summary>
    internal ReactionTables GetReactionTables()
    {
        return _sourceConfig?.GetReactionTables() ?? (_reactionTables ??= ReactionTables.Create(this));
    }

    public void ValidateGasRegistry()
    {
        _gasRegistry.ValidateGasRegistry();
    }
}