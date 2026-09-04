using System.Diagnostics.CodeAnalysis;
using Numos.CoreSim.GasReactions;
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
    private Scalar[] _diffusionCoefficients = [];
    private GasProperties[] _gasRegistry = [];
    private int _gasRegistryCount;
    private JoulePerMoleKelvin[] _molarHeatCapacitiesAtConstantVolume = [];

    private IGasReaction[] _reactionsRegistry = [];
    private int _reactionRegistryCount;

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
        return (uint)gasId < (uint)_gasRegistryCount
            ? _molarHeatCapacitiesAtConstantVolume[gasId]
            : DefaultMolarHeatCapacityAtConstantVolume;
    }

    public Scalar GetDiffusionCoefficient(int gasId)
    {
        return (uint)gasId < (uint)_gasRegistryCount
            ? _diffusionCoefficients[gasId]
            : DefaultDiffusionCoefficient;
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

    public int GasPropertyCount => _gasRegistryCount;
    public bool TryGetGasReaction(int reactionId, [NotNullWhen(true)] out IGasReaction? reaction)
    {
        reaction = null;
        if (reactionId >= _reactionRegistryCount)
            return false;

        reaction = _reactionsRegistry[reactionId];
        return true;
    }

    public int GasReactionCount => _reactionRegistryCount;

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

        for (int gasId = 0; gasId < _gasRegistryCount; gasId++)
        {
            var properties = gasRegistry[gasId];
            _gasRegistry[gasId] = properties;
            _molarHeatCapacitiesAtConstantVolume[gasId] =
                FloatMath.IsFinitePositive(properties.MolarHeatCapacityAtConstantVolume)
                    ? properties.MolarHeatCapacityAtConstantVolume
                    : DefaultMolarHeatCapacityAtConstantVolume;

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

        AccumulatorWakeThreshold = FloatMath.GetNonnegativeFinite(config.AccumulatorWakeThreshold);
        AccumulatorMaxAliveTicks = Math.Max(0, config.AccumulatorMaxAliveTicks);


        int previousReactionRegistryCount = _reactionRegistryCount;

        if (_reactionRegistryCount < config.GasReactionCount)
        {
            Array.Resize(ref _reactionsRegistry, config.GasReactionCount);
        }

        _reactionRegistryCount = config.GasReactionCount;

        for (int i = 0; i < config.GasReactionCount; i++)
        {
            if (config.TryGetGasReaction(i, out var reaction))
                _reactionsRegistry[i] = reaction;
        }
    }
}