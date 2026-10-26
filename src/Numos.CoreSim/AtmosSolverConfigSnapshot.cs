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
    private SquareMetrePerSecond[] _diffusionCoefficients = [];
    private WattsPerMeterKelvin[] _thermalConductivities = [];
    private GasRegistrySnapshot _gasRegistry = default!;
    private JoulePerMoleKelvin[] _molarHeatCapacitiesAtConstantVolume = [];
    private KilogramPerMole[] _molarMasses = [];
    private AtmosConfigSnapshot? _sourceConfig;

    /// <summary>
    ///     Gets the simulation's solver data. Every capture of a different configuration clears it, so values must be
    ///     caches a factory can rebuild from the configuration and authoritative state.
    /// </summary>
    /// <remarks>
    ///     The data lives here, not on the kernel, so it stays tied to the configuration it was built from.
    ///     Recapturing the same <see cref="AtmosConfigSnapshot" /> returns early, so the data survives from tick to
    ///     tick.
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
    public Scalar FlameSpeedScale { get; private set; }
    public MetrePerSecond MinimumFlameSpeed { get; private set; }
    public bool IgnitionTransferEnabled { get; private set; }
    public Scalar TransferSeedFraction { get; private set; }
    public JoulePerMoleKelvin DefaultMolarHeatCapacityAtConstantVolume { get; private set; }
    public SquareMetrePerSecond DefaultDiffusionCoefficient { get; private set; }
    public WattsPerMeterKelvin DefaultThermalConductivity { get; private set; }

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

    public SquareMetrePerSecond GetDiffusionCoefficient(int gasId)
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

    public WattsPerMeterKelvin GetThermalConductivity(int gasId)
    {
        return (uint)gasId < (uint)GasPropertyCount
            ? _thermalConductivities[gasId]
            : DefaultThermalConductivity;
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

    /// <summary>
    ///     Copies the scalars of an applied configuration and builds the per-gas tables.
    /// </summary>
    /// <param name="config">
    ///     The applied configuration. The <see cref="AtmosConfigSnapshot" /> constructor already normalized its
    ///     values, so this method copies them as they are.
    /// </param>
    internal void Capture(AtmosConfigSnapshot config)
    {
        if (ReferenceEquals(config, _sourceConfig))
            return;

        _sourceConfig = null;
        int gasPropertyCount = config.GasPropertyCount;
        int previousGasRegistryCount = GasPropertyCount;
        if (_molarHeatCapacitiesAtConstantVolume.Length < gasPropertyCount)
        {
            Array.Resize(ref _molarHeatCapacitiesAtConstantVolume, gasPropertyCount);
            Array.Resize(ref _diffusionCoefficients, gasPropertyCount);
            Array.Resize(ref _molarMasses, gasPropertyCount);
            Array.Resize(ref _thermalConductivities, gasPropertyCount);
        }

        GasPropertyCount = gasPropertyCount;
        GlobalTemperature = config.GlobalTemperature;
        DefaultTemperatureFallback = config.DefaultTemperatureFallback;
        DefaultMolarHeatCapacityAtConstantVolume = config.DefaultMolarHeatCapacityAtConstantVolume;
        VoxelVolume = config.VoxelVolume;
        PressurePerMoleKelvin = config.PressurePerMoleKelvin;
        SaturationReferencePressure = config.SaturationReferencePressure;
        DefaultDiffusionCoefficient = config.DefaultDiffusionCoefficient;
        DefaultThermalConductivity = config.DefaultThermalConductivity;
        SpaceTemperature = config.SpaceTemperature;
        ReferenceMolarMass = config.ReferenceMolarMass;

        _gasRegistry = config.GasRegistry;
        for (int gasId = 0; gasId < GasPropertyCount; gasId++)
        {
            _molarHeatCapacitiesAtConstantVolume[gasId] = config.GetMolarHeatCapacityAtConstantVolume(gasId);
            _diffusionCoefficients[gasId] = config.GetDiffusionCoefficient(gasId);
            _molarMasses[gasId] = config.GetMolarMass(gasId);
            config.TryGetGasProperties(gasId, out var properties);
            _thermalConductivities[gasId] = FloatMath.IsFinitePositive(properties.ThermalConductivity)
                ? properties.ThermalConductivity
                : DefaultThermalConductivity;
        }

        if (GasPropertyCount < previousGasRegistryCount)
        {
            int removedCount = previousGasRegistryCount - GasPropertyCount;
            Array.Clear(_molarHeatCapacitiesAtConstantVolume, GasPropertyCount, removedCount);
            Array.Clear(_diffusionCoefficients, GasPropertyCount, removedCount);
            Array.Clear(_molarMasses, GasPropertyCount, removedCount);
            Array.Clear(_thermalConductivities, GasPropertyCount, removedCount);
         }

        BulkFlowCoefficient = config.BulkFlowCoefficient;
        VacuumThreshold = config.VacuumThreshold;
        SleepThreshold = config.SleepThreshold;
        SleepEpsilon = config.SleepEpsilon;
        ThermalConductance = config.ThermalConductance;
        CondensationRateFactor = config.CondensationRateFactor;
        MaxPressureTransferFractionPerNeighbor = config.MaxPressureTransferFractionPerNeighbor;
        AccumulatorWakeThreshold = config.AccumulatorWakeThreshold;
        AccumulatorMaxAliveTicks = config.AccumulatorMaxAliveTicks;
        FlameSpeedScale = config.FlameSpeedScale;
        MinimumFlameSpeed = config.MinimumFlameSpeed;
        IgnitionTransferEnabled = config.IgnitionTransferEnabled;
        TransferSeedFraction = config.TransferSeedFraction;

        SolverConfigurations = config.SolverConfigurations;
        EulerFlow = EulerFlowConfig.Get(config);

        SolverData.Clear();
        _sourceConfig = config;
    }

    /// <summary>
    ///     Gets the reaction solver's tables for this configuration.
    /// </summary>
    internal ReactionTables GetReactionTables()
    {
        return _sourceConfig!.GetReactionTables();
    }

    public void ValidateGasRegistry()
    {
        _gasRegistry.ValidateGasRegistry();
    }
}