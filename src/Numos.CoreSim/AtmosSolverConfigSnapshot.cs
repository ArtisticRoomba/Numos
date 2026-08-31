using Numos.Maths;

namespace Numos.CoreSim;

/// <summary>
///     Normalized solver inputs captured from the live configuration at the start of a tick.
/// </summary>
/// <remarks>
///     Public configuration remains mutable. This reusable snapshot remains stable for the duration of a tick,
///     keeping the tick internally consistent and moving validation out of per-voxel and per-neighbor hot paths.
/// </remarks>
internal sealed class AtmosSolverConfigSnapshot
{
    private JoulePerMoleKelvin _defaultMolarHeatCapacityAtConstantVolume;
    private Scalar _defaultDiffusionCoefficient;
    private Scalar[] _diffusionCoefficients = [];
    private GasProperties[] _gasRegistry = [];
    private int _gasRegistryCount;
    private JoulePerMoleKelvin[] _molarHeatCapacitiesAtConstantVolume = [];

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
        VacuumThreshold = FloatMath.GetNonnegativeFinite(config.VacuumThreshold);
        SleepThreshold = Math.Max(0, config.SleepThreshold);
        SleepEpsilon = FloatMath.GetNonnegativeFinite(config.SleepEpsilon);
        ThermalConductance = FloatMath.IsFinitePositive(config.ThermalConductance)
            ? config.ThermalConductance
            : 0f;
        CondensationRateFactor = FloatMath.ClampUnitInterval(config.CondensationRateFactor);
        MaxPressureTransferFractionPerNeighbor =
            FloatMath.ClampUnitInterval(config.MaxPressureTransferFractionPerNeighbor);
    }

    internal Kelvin DefaultTemperatureFallback { get; private set; }
    internal CubicMetre VoxelVolume { get; private set; }
    internal PascalPerMoleKelvin PressurePerMoleKelvin { get; private set; }
    internal Pascal SaturationReferencePressure { get; private set; }
    internal Scalar BulkFlowCoefficient { get; private set; }
    internal Pascal VacuumThreshold { get; private set; }
    internal int SleepThreshold { get; private set; }
    internal Pascal SleepEpsilon { get; private set; }
    internal JoulePerKelvin ThermalConductance { get; private set; }
    internal Scalar CondensationRateFactor { get; private set; }
    internal Scalar MaxPressureTransferFractionPerNeighbor { get; private set; }

    internal Kelvin GetValidatedTemp(Kelvin storedTemperature)
    {
        return FloatMath.IsFinitePositive(storedTemperature) ? storedTemperature : DefaultTemperatureFallback;
    }

    internal JoulePerMoleKelvin GetMolarHeatCapacityAtConstantVolume(int gasId)
    {
        return (uint)gasId < (uint)_gasRegistryCount
            ? _molarHeatCapacitiesAtConstantVolume[gasId]
            : _defaultMolarHeatCapacityAtConstantVolume;
    }

    internal Scalar GetDiffusionCoefficient(int gasId)
    {
        return (uint)gasId < (uint)_gasRegistryCount
            ? _diffusionCoefficients[gasId]
            : _defaultDiffusionCoefficient;
    }

    internal bool TryGetGasProperties(int gasId, out GasProperties properties)
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