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
        DefaultTemperatureFallback = IsFinitePositive(config.DefaultTemperatureFallback)
            ? config.DefaultTemperatureFallback
            : AtmosConfigDefaults.DefaultTemperatureFallback;
        _defaultMolarHeatCapacityAtConstantVolume =
            IsFinitePositive(config.DefaultMolarHeatCapacityAtConstantVolume)
                ? config.DefaultMolarHeatCapacityAtConstantVolume
                : AtmosConfigDefaults.DefaultMolarHeatCapacityAtConstantVolume;
        VoxelVolume = IsFinitePositive(config.VoxelVolume)
            ? config.VoxelVolume
            : AtmosConfigDefaults.VoxelVolume;
        PressurePerMoleKelvin = AtmosPhysicalConstants.MolarGasConstant / VoxelVolume;
        SaturationReferencePressure = IsFinitePositive(config.SaturationReferencePressure)
            ? config.SaturationReferencePressure
            : AtmosConfigDefaults.SaturationReferencePressure;
        _defaultDiffusionCoefficient = ClampUnitInterval(config.DefaultDiffusionCoefficient);
        for (var gasId = 0; gasId < _gasRegistryCount; gasId++)
        {
            GasProperties properties = gasRegistry[gasId];
            _gasRegistry[gasId] = properties;
            _molarHeatCapacitiesAtConstantVolume[gasId] =
                IsFinitePositive(properties.MolarHeatCapacityAtConstantVolume)
                    ? properties.MolarHeatCapacityAtConstantVolume
                    : _defaultMolarHeatCapacityAtConstantVolume;
            _diffusionCoefficients[gasId] = ClampUnitInterval(properties.DiffusionCoefficient);
        }

        if (_gasRegistryCount < previousGasRegistryCount)
        {
            int removedCount = previousGasRegistryCount - _gasRegistryCount;
            Array.Clear(_gasRegistry, _gasRegistryCount, removedCount);
            Array.Clear(_molarHeatCapacitiesAtConstantVolume, _gasRegistryCount, removedCount);
            Array.Clear(_diffusionCoefficients, _gasRegistryCount, removedCount);
        }

        BulkFlowCoefficient = ClampUnitInterval(config.BulkFlowCoefficient);
        BulkFlowDamping = ClampUnitInterval(config.BulkFlowDamping);
        LowPressureDeltaThreshold = GetNonnegativeFinite(config.LowPressureDeltaThreshold);
        MinimumPressureTransfer = GetNonnegativeFinite(config.MinimumPressureTransfer);
        VacuumThreshold = GetNonnegativeFinite(config.VacuumThreshold);
        SleepThreshold = Math.Max(0, config.SleepThreshold);
        SleepEpsilon = GetNonnegativeFinite(config.SleepEpsilon);
        ThermalConductance = IsFinitePositive(config.ThermalConductance)
            ? config.ThermalConductance
            : 0f;
        CondensationRateFactor = ClampUnitInterval(config.CondensationRateFactor);
        MaxPressureTransferFractionPerNeighbor =
            ClampUnitInterval(config.MaxPressureTransferFractionPerNeighbor);
    }

    internal float DefaultTemperatureFallback { get; private set; }
    internal float VoxelVolume { get; private set; }
    internal float PressurePerMoleKelvin { get; private set; }
    internal float SaturationReferencePressure { get; private set; }
    internal float BulkFlowCoefficient { get; private set; }
    internal float BulkFlowDamping { get; private set; }
    internal float LowPressureDeltaThreshold { get; private set; }
    internal float MinimumPressureTransfer { get; private set; }
    internal float VacuumThreshold { get; private set; }
    internal int SleepThreshold { get; private set; }
    internal float SleepEpsilon { get; private set; }
    internal float ThermalConductance { get; private set; }
    internal float CondensationRateFactor { get; private set; }
    internal float MaxPressureTransferFractionPerNeighbor { get; private set; }

    internal float GetEffectiveTemperature(float storedTemperature)
    {
        return IsFinitePositive(storedTemperature) ? storedTemperature : DefaultTemperatureFallback;
    }

    internal float GetMolarHeatCapacityAtConstantVolume(int gasId)
    {
        return (uint)gasId < (uint)_gasRegistryCount
            ? _molarHeatCapacitiesAtConstantVolume[gasId]
            : _defaultMolarHeatCapacityAtConstantVolume;
    }

    internal float GetDiffusionCoefficient(int gasId)
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

    private static bool IsFinitePositive(float value)
    {
        return float.IsFinite(value) && value > 0f;
    }

    private static float ClampUnitInterval(float value)
    {
        return float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 0f;
    }

    private static float GetNonnegativeFinite(float value)
    {
        return float.IsFinite(value) ? MathF.Max(0f, value) : 0f;
    }
}
