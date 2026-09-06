using Numos.CoreSim.Solvers;
using Numos.Maths;

namespace Numos.CoreSim;

/// <summary>
///     Normalized solver inputs captured from the applied immutable configuration at the start of a tick.
/// </summary>
/// <remarks>
///     This reusable snapshot remains stable for the duration of a tick, keeping the tick internally consistent and
///     moving validation out of per-voxel and per-neighbor hot paths.
/// </remarks>
internal sealed class AtmosSolverConfigSnapshot : IAtmosConfig
{
    private Scalar[] _diffusionCoefficients = [];
    private GasRegistrySnapshot _gasRegistry = default!;
    private GasSolverDataStorage _gasSolverData = new(0);
    private JoulePerMoleKelvin[] _molarHeatCapacitiesAtConstantVolume = [];

    private AtmosConfigSnapshot? _sourceConfig;

    public IReadOnlyList<IAtmosSolverConfiguration> SolverConfigurations { get; private set; } = [];

    public Kelvin GlobalTemperature { get; private set; }
    public Kelvin DefaultTemperatureFallback { get; private set; }
    public CubicMetre VoxelVolume { get; private set; }
    public PascalPerMoleKelvin PressurePerMoleKelvin { get; private set; }
    public Pascal SaturationReferencePressure { get; private set; }
    public Kelvin SpaceTemperature { get; private set; }
    public Scalar BulkFlowCoefficient { get; private set; }
    public Pascal VacuumThreshold { get; private set; }
    public int SleepThreshold { get; private set; }
    public Pascal SleepEpsilon { get; private set; }
    public JoulePerKelvin ThermalConductance { get; private set; }
    public Scalar CondensationRateFactor { get; private set; }
    public Scalar MaxPressureTransferFractionPerNeighbor { get; private set; }
    public Pascal AccumulatorWakeThreshold { get; private set; }
    public int AccumulatorMaxAliveTicks { get; private set; }

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

    internal T GetOrCreateGasSolverData<T>(int gasId, object key, Func<GasProperties, T> factory) where T : notnull
    {
        if (!TryGetGasProperties(gasId, out var properties))
            throw new ArgumentOutOfRangeException(nameof(gasId), "The gas is not registered.");

        return _gasSolverData.GetOrCreate(gasId, key, properties, factory);
    }

    internal void ClearGasSolverData()
    {
        _gasSolverData = new GasSolverDataStorage(GasPropertyCount);
    }

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
        }

        _gasRegistry = new GasRegistrySnapshot(gases);

        if (GasPropertyCount < previousGasRegistryCount)
        {
            int removedCount = previousGasRegistryCount - GasPropertyCount;
            Array.Clear(_molarHeatCapacitiesAtConstantVolume, GasPropertyCount, removedCount);
            Array.Clear(_diffusionCoefficients, GasPropertyCount, removedCount);
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

        ClearGasSolverData();
        _sourceConfig = config as AtmosConfigSnapshot;
    }

    public void ValidateGasRegistry()
    {
        _gasRegistry.ValidateGasRegistry();
    }
}