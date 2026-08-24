using Numos.Maths;

namespace Numos.CoreSim;

/// <summary>
///     Normalized solver inputs captured from the live configuration at the start of a tick.
/// </summary>
/// <remarks>
///     Public configuration remains mutable. This reusable snapshot remains stable for the duration of a tick,
///     keeping the tick internally consistent and moving validation out of per-voxel and per-neighbor hot paths.
/// </remarks>
internal sealed class AtmosSolverConfigSnapshot : IAtmosConfig
{
    private float _defaultMolarHeatCapacityAtConstantVolume;
    private float _defaultDiffusionCoefficient;
    private float[] _diffusionCoefficients = [];
    private GasProperties[] _gasRegistry = [];
    private int _gasRegistryCount;
    private float[] _molarHeatCapacitiesAtConstantVolume = [];

    internal void Capture(AtmosConfig config)
    {
        List<GasProperties> gasRegistry = config.GasRegistry;
        int previousGasRegistryCount = _gasRegistryCount;
        if (_gasRegistry.Length < gasRegistry.Count)
        {
            Array.Resize(ref _gasRegistry, gasRegistry.Count);
            Array.Resize(ref _molarHeatCapacitiesAtConstantVolume, gasRegistry.Count);
            Array.Resize(ref _diffusionCoefficients, gasRegistry.Count);
        }

        _gasRegistryCount = gasRegistry.Count;
        GlobalTemperature = FloatMath.IsFinitePositive(config.GlobalTemperature)
            ? config.GlobalTemperature
            : AtmosConfigDefaults.GlobalTemperature;
        DefaultTemperatureFallback = FloatMath.IsFinitePositive(config.DefaultTemperatureFallback)
            ? config.DefaultTemperatureFallback
            : AtmosConfigDefaults.DefaultTemperatureFallback;
        _defaultMolarHeatCapacityAtConstantVolume =
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
        _defaultDiffusionCoefficient = FloatMath.ClampUnitInterval(config.DefaultDiffusionCoefficient);
        SpaceTemperature = FloatMath.IsFinitePositive(config.SpaceTemperature)
            ? config.SpaceTemperature
            : AtmosConfigDefaults.SpaceTemperature;
        for (var gasId = 0; gasId < _gasRegistryCount; gasId++)
        {
            GasProperties properties = gasRegistry[gasId];
            _gasRegistry[gasId] = properties;
            _molarHeatCapacitiesAtConstantVolume[gasId] =
                FloatMath.IsFinitePositive(properties.MolarHeatCapacityAtConstantVolume)
                    ? properties.MolarHeatCapacityAtConstantVolume
                    : _defaultMolarHeatCapacityAtConstantVolume;
            _diffusionCoefficients[gasId] = FloatMath.ClampUnitInterval(properties.DiffusionCoefficient);
        }

        if (_gasRegistryCount < previousGasRegistryCount)
        {
            int removedCount = previousGasRegistryCount - _gasRegistryCount;
            Array.Clear(_gasRegistry, _gasRegistryCount, removedCount);
            Array.Clear(_molarHeatCapacitiesAtConstantVolume, _gasRegistryCount, removedCount);
            Array.Clear(_diffusionCoefficients, _gasRegistryCount, removedCount);
        }

        BulkFlowCoefficient = FloatMath.ClampUnitInterval(config.BulkFlowCoefficient);
        BulkFlowDamping = FloatMath.ClampUnitInterval(config.BulkFlowDamping);
        LowPressureDeltaThreshold = FloatMath.GetNonnegativeFinite(config.LowPressureDeltaThreshold);
        MinimumPressureTransfer = FloatMath.GetNonnegativeFinite(config.MinimumPressureTransfer);
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
    }

    public float GlobalTemperature { get; private set; }
    public float DefaultTemperatureFallback { get; private set; }
    public float VoxelVolume { get; private set; }
    public float PressurePerMoleKelvin { get; private set; }
    public float SaturationReferencePressure { get; private set; }
    public float SpaceTemperature { get; private set; }
    public float BulkFlowCoefficient { get; private set; }
    public float BulkFlowDamping { get; private set; }
    public float LowPressureDeltaThreshold { get; private set; }
    public float MinimumPressureTransfer { get; private set; }
    public float VacuumThreshold { get; private set; }
    public int SleepThreshold { get; private set; }
    public float SleepEpsilon { get; private set; }
    public float ThermalConductance { get; private set; }
    public float CondensationRateFactor { get; private set; }
    public float MaxPressureTransferFractionPerNeighbor { get; private set; }
    public float AccumulatorWakeThreshold { get; private set; }
    public int AccumulatorMaxAliveTicks { get; private set; }

    public float DefaultMolarHeatCapacityAtConstantVolume => _defaultMolarHeatCapacityAtConstantVolume;
    public float DefaultDiffusionCoefficient => _defaultDiffusionCoefficient;

    public float GetVoxelVolume()
    {
        return VoxelVolume;
    }

    public float GetValidatedTemp(float storedTemperature)
    {
        return FloatMath.IsFinitePositive(storedTemperature) ? storedTemperature : DefaultTemperatureFallback;
    }

    public float GetMolarHeatCapacityAtConstantVolume(int gasId)
    {
        return (uint)gasId < (uint)_gasRegistryCount
            ? _molarHeatCapacitiesAtConstantVolume[gasId]
            : _defaultMolarHeatCapacityAtConstantVolume;
    }

    public float GetDiffusionCoefficient(int gasId)
    {
        return (uint)gasId < (uint)_gasRegistryCount
            ? _diffusionCoefficients[gasId]
            : _defaultDiffusionCoefficient;
    }

    public bool TryGetGasProperties(int gasId, out GasProperties properties)
    {
        if ((uint)gasId < (uint)_gasRegistryCount)
        {
            properties = _gasRegistry[gasId];
            return true;
        }

        properties = default;
        return false;
    }
}