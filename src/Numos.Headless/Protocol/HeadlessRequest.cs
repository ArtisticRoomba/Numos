using Numos.CoreSim;

namespace Numos.Headless.Protocol;

/// <summary>
///     One versioned JSONL request accepted by the headless simulation host.
/// </summary>
internal sealed class HeadlessRequest
{
    public int? ProtocolVersion { get; init; }
    public string? Id { get; init; }
    public string? Op { get; init; }

    // Simulation construction and configuration.
    public string? Name { get; init; }
    public Coordinate? Dimensions { get; init; }
    public ConfigurationPatch? Config { get; init; }
    public GasDefinition[]? Gases { get; init; }

    // Chunk and voxel addressing.
    public Coordinate? Position { get; init; }
    public Coordinate? Voxel { get; init; }
    public int? Classification { get; init; }
    public int? RoomId { get; init; }

    // Gas operations.
    public GasDefinition? Gas { get; init; }
    public int? GasId { get; init; }
    public float? Moles { get; init; }
    public float? TemperatureK { get; init; }

    // Stepping and solver isolation.
    public int? Count { get; init; }
    public string? Solver { get; init; }
    public bool? Enabled { get; init; }

    // Observation detail. Summary data is always returned by observe.
    public bool? IncludeVoxels { get; init; }
    public bool? OnlyGasBearingVoxels { get; init; }
    public int? MaxIssueLocations { get; init; }
}

/// <summary>JSON representation of one gas registry entry.</summary>
internal sealed class GasDefinition
{
    public string? Name { get; init; }
    public float? MolarHeatCapacityAtConstantVolume { get; init; }
    public float? BoilingPointK { get; init; }
    public bool? CondensationEnabled { get; init; }
    public float? MolarEnthalpyOfVaporization { get; init; }
    public int? LiquidId { get; init; }
    public float? DiffusionCoefficient { get; init; }

    internal GasProperties ToGasProperties()
    {
        if (string.IsNullOrWhiteSpace(Name))
            throw new HeadlessRequestException("invalidGas", "Every gas requires a non-empty name.");

        return new GasProperties
        {
            Name = Name.Trim(),
            MolarHeatCapacityAtConstantVolume = MolarHeatCapacityAtConstantVolume ?? 0f,
            BoilingPoint = BoilingPointK ?? 0f,
            CondensationEnabled = CondensationEnabled ?? false,
            MolarEnthalpyOfVaporization = MolarEnthalpyOfVaporization ?? 0f,
            LiquidId = LiquidId ?? -1,
            DiffusionCoefficient = DiffusionCoefficient ?? 0f
        };
    }
}

/// <summary>
///     Optional configuration values. Missing properties preserve the current Numos default or live value.
/// </summary>
internal sealed class ConfigurationPatch
{
    public float? GlobalTemperatureK { get; init; }
    public float? DefaultTemperatureFallbackK { get; init; }
    public float? DefaultMolarHeatCapacityAtConstantVolume { get; init; }
    public float? VoxelVolumeM3 { get; init; }
    public float? SaturationReferencePressurePa { get; init; }
    public float? DefaultDiffusionCoefficient { get; init; }
    public float? SpaceTemperatureK { get; init; }
    public float? BulkFlowCoefficient { get; init; }
    public float? VacuumThresholdPa { get; init; }
    public int? SleepThreshold { get; init; }
    public float? SleepEpsilonPa { get; init; }
    public float? ThermalConductance { get; init; }
    public float? CondensationRateFactor { get; init; }
    public float? MaxPressureTransferFractionPerNeighbor { get; init; }
    public float? AccumulatorWakeThresholdPa { get; init; }
    public int? AccumulatorMaxAliveTicks { get; init; }

    internal void ApplyTo(AtmosConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (GlobalTemperatureK.HasValue)
            config.GlobalTemperature = GlobalTemperatureK.Value;
        if (DefaultTemperatureFallbackK.HasValue)
            config.DefaultTemperatureFallback = DefaultTemperatureFallbackK.Value;
        if (DefaultMolarHeatCapacityAtConstantVolume.HasValue)
            config.DefaultMolarHeatCapacityAtConstantVolume = DefaultMolarHeatCapacityAtConstantVolume.Value;
        if (VoxelVolumeM3.HasValue)
            config.VoxelVolume = VoxelVolumeM3.Value;
        if (SaturationReferencePressurePa.HasValue)
            config.SaturationReferencePressure = SaturationReferencePressurePa.Value;
        if (DefaultDiffusionCoefficient.HasValue)
            config.DefaultDiffusionCoefficient = DefaultDiffusionCoefficient.Value;
        if (SpaceTemperatureK.HasValue)
            config.SpaceTemperature = SpaceTemperatureK.Value;
        if (BulkFlowCoefficient.HasValue)
            config.BulkFlowCoefficient = BulkFlowCoefficient.Value;
        if (VacuumThresholdPa.HasValue)
            config.VacuumThreshold = VacuumThresholdPa.Value;
        if (SleepThreshold.HasValue)
            config.SleepThreshold = SleepThreshold.Value;
        if (SleepEpsilonPa.HasValue)
            config.SleepEpsilon = SleepEpsilonPa.Value;
        if (ThermalConductance.HasValue)
            config.ThermalConductance = ThermalConductance.Value;
        if (CondensationRateFactor.HasValue)
            config.CondensationRateFactor = CondensationRateFactor.Value;
        if (MaxPressureTransferFractionPerNeighbor.HasValue)
            config.MaxPressureTransferFractionPerNeighbor = MaxPressureTransferFractionPerNeighbor.Value;
        if (AccumulatorWakeThresholdPa.HasValue)
            config.AccumulatorWakeThreshold = AccumulatorWakeThresholdPa.Value;
        if (AccumulatorMaxAliveTicks.HasValue)
            config.AccumulatorMaxAliveTicks = AccumulatorMaxAliveTicks.Value;
    }
}
