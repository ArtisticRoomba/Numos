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
        VoxelVolume = Solvers.AtmosSolverMath.GetVoxelVolume(config);
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
        VoxelSnappingEnabled = config.VoxelSnappingEnabled;
        VoxelSnapPressureRelativeEpsilon = ClampUnitInterval(config.VoxelSnapPressureRelativeEpsilon);
        VoxelSnapTemperatureEpsilon = GetNonnegativeFinite(config.VoxelSnapTemperatureEpsilon);
        VoxelSnapMoleFractionEpsilon = ClampUnitInterval(config.VoxelSnapMoleFractionEpsilon);
        ThermalConductance = IsFinitePositive(config.ThermalConductance)
            ? config.ThermalConductance
            : 0f;
        CondensationRateFactor = ClampUnitInterval(config.CondensationRateFactor);
        MaxPressureTransferFractionPerNeighbor =
            ClampUnitInterval(config.MaxPressureTransferFractionPerNeighbor);
        ConfigurationFingerprint = CalculateConfigurationFingerprint();
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
    internal bool VoxelSnappingEnabled { get; private set; }
    internal float VoxelSnapPressureRelativeEpsilon { get; private set; }
    internal float VoxelSnapTemperatureEpsilon { get; private set; }
    internal float VoxelSnapMoleFractionEpsilon { get; private set; }
    internal float ThermalConductance { get; private set; }
    internal float CondensationRateFactor { get; private set; }
    internal float MaxPressureTransferFractionPerNeighbor { get; private set; }
    internal ulong ConfigurationFingerprint { get; private set; }

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

    private ulong CalculateConfigurationFingerprint()
    {
        const ulong offsetBasis = 14695981039346656037UL;
        ulong hash = offsetBasis;
        Add(ref hash, DefaultTemperatureFallback);
        Add(ref hash, _defaultMolarHeatCapacityAtConstantVolume);
        Add(ref hash, VoxelVolume);
        Add(ref hash, SaturationReferencePressure);
        Add(ref hash, _defaultDiffusionCoefficient);
        Add(ref hash, BulkFlowCoefficient);
        Add(ref hash, BulkFlowDamping);
        Add(ref hash, LowPressureDeltaThreshold);
        Add(ref hash, MinimumPressureTransfer);
        Add(ref hash, VacuumThreshold);
        Add(ref hash, SleepThreshold);
        Add(ref hash, SleepEpsilon);
        Add(ref hash, VoxelSnappingEnabled ? 1 : 0);
        Add(ref hash, VoxelSnapPressureRelativeEpsilon);
        Add(ref hash, VoxelSnapTemperatureEpsilon);
        Add(ref hash, VoxelSnapMoleFractionEpsilon);
        Add(ref hash, ThermalConductance);
        Add(ref hash, CondensationRateFactor);
        Add(ref hash, MaxPressureTransferFractionPerNeighbor);
        Add(ref hash, _gasRegistryCount);
        for (var gasId = 0; gasId < _gasRegistryCount; gasId++)
        {
            GasProperties properties = _gasRegistry[gasId];
            Add(ref hash, _molarHeatCapacitiesAtConstantVolume[gasId]);
            Add(ref hash, _diffusionCoefficients[gasId]);
            Add(ref hash, properties.BoilingPoint);
            Add(ref hash, properties.CondensationEnabled ? 1 : 0);
            Add(ref hash, properties.MolarEnthalpyOfVaporization);
        }

        return hash;
    }

    private static void Add(ref ulong hash, float value)
    {
        Add(ref hash, BitConverter.SingleToInt32Bits(value));
    }

    private static void Add(ref ulong hash, int value)
    {
        const ulong prime = 1099511628211UL;
        uint bits = unchecked((uint)value);
        for (var shift = 0; shift < 32; shift += 8)
        {
            hash ^= (byte)(bits >> shift);
            hash *= prime;
        }
    }
}
