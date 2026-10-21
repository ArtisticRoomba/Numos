using System.Text.Json;
using Numos.CoreSim;
using Numos.CoreSim.EulerFlow;
using Numos.Workbench;

namespace Numos.Headless.Protocol;

/// <summary>
///     One versioned JSONL request accepted by the headless simulation host.
/// </summary>
internal sealed class HeadlessRequest
{
    public int? ProtocolVersion { get; init; }
    public string? Id { get; init; }
    public string? Op { get; init; }

    /// <summary>
    ///     A simulation name (string) or numeric id. Missing means the active simulation.
    /// </summary>
    public JsonElement? Simulation { get; init; }

    // Project and simulation construction.
    public string? Name { get; init; }
    public Coordinate? ChunkDimensions { get; init; }
    public bool? IncludeDefaultGases { get; init; }
    public bool? Activate { get; init; }
    public ConfigurationPatch? Config { get; init; }
    public GasDefinition[]? Gases { get; init; }

    // Chunk and voxel addressing.
    public Coordinate? Position { get; init; }
    public Coordinate? Voxel { get; init; }
    public int? Classification { get; init; }

    /// <summary>
    ///     A <see cref="GasDefinition" /> object for addGas, or an array of <see cref="RegionGasRequest" /> for editRegion.
    /// </summary>
    public JsonElement? Gas { get; init; }

    // Gas operations.
    public int? GasId { get; init; }
    public float? Moles { get; init; }
    public float? TemperatureK { get; init; }
    public float? GasTemperatureK { get; init; }

    // Momentum writes, in kg·m/s.
    public float? MomentumX { get; init; }
    public float? MomentumY { get; init; }
    public float? MomentumZ { get; init; }

    // Regions, sampling, and links. Region and sample coordinates are global voxels.
    public Coordinate? Min { get; init; }
    public Coordinate? Max { get; init; }
    public bool? CreateChunks { get; init; }
    public bool? ClearGas { get; init; }
    public Coordinate[]? Points { get; init; }
    public string[]? Fields { get; init; }
    public LinkEndpoint? First { get; init; }
    public LinkEndpoint? Second { get; init; }
    public string? Flags { get; init; }
    public int? Rotation { get; init; }
    public bool? FlipU { get; init; }
    public bool? FlipV { get; init; }
    public LinkSetHandleRequest? LinkSet { get; init; }

    // Stepping, solvers, and the timeline.
    public int? Count { get; init; }
    public int? ProgressInterval { get; init; }
    public string? Solver { get; init; }
    public bool? Enabled { get; init; }
    public long? Tick { get; init; }
    public int? BranchId { get; init; }

    // Data extraction. Boxes, slices, and probe points are global voxels.
    public BoxRequest? Region { get; init; }
    public SliceRequest? Slice { get; init; }
    public string? Format { get; init; }
    public string? Axis { get; init; }
    public int? Index { get; init; }
    public string? Visualization { get; init; }
    public int? Scale { get; init; }
    public int? Levels { get; init; }
    public RangeRequest? Range { get; init; }
    public ProbeRequest? Probes { get; init; }

    // Replay files.
    public string? Path { get; init; }
    public bool? ThroughCurrentPosition { get; init; }
    public bool? Overwrite { get; init; }
    public bool? AtHead { get; init; }

    // Log polling.
    public long? AfterSequence { get; init; }
    public int? MaxEntries { get; init; }
    public string? MinLevel { get; init; }

    // Observation detail. Summary data is always returned by observe.
    public bool? IncludeVoxels { get; init; }
    public bool? OnlyGasBearingVoxels { get; init; }
    public int? MaxIssueLocations { get; init; }
}

/// <summary>
///     An inclusive box in global voxels.
/// </summary>
internal sealed class BoxRequest
{
    public Coordinate? Min { get; init; }
    public Coordinate? Max { get; init; }
}

/// <summary>
///     A slice perpendicular to one axis, at a global voxel index.
/// </summary>
internal sealed class SliceRequest
{
    public string? Axis { get; init; }
    public int? Index { get; init; }
}

/// <summary>
///     A fixed color range.
/// </summary>
internal sealed class RangeRequest
{
    public float? Min { get; init; }
    public float? Max { get; init; }
}

/// <summary>
///     Probe samples written to a CSV file during a tick request.
/// </summary>
internal sealed class ProbeRequest
{
    public ProbePointRequest[]? Points { get; init; }
    public string[]? Fields { get; init; }
    public string? Path { get; init; }
    public int? Every { get; init; }
}

/// <summary>
///     One named probe voxel.
/// </summary>
internal sealed class ProbePointRequest
{
    public string? Name { get; init; }
    public Coordinate? Voxel { get; init; }
}

/// <summary>
///     One gas that editRegion adds to every voxel. Set exactly one of moles and partialPressurePa.
/// </summary>
internal sealed class RegionGasRequest
{
    public int? GasId { get; init; }
    public float? Moles { get; init; }
    public float? PartialPressurePa { get; init; }
}

/// <summary>
///     One end of a portal or dock.
/// </summary>
internal sealed class LinkEndpoint
{
    public JsonElement? Simulation { get; init; }
    public Coordinate? Position { get; init; }
    public Coordinate? Voxel { get; init; }
    public string? Face { get; init; }
}

/// <summary>
///     The handle of a link set, as listLinkSets reports it.
/// </summary>
internal sealed class LinkSetHandleRequest
{
    public int? Index { get; init; }
    public uint? Generation { get; init; }
}

/// <summary>
///     JSON representation of one gas registry entry.
/// </summary>
internal sealed class GasDefinition
{
    public string? Name { get; init; }
    public float? MolarHeatCapacityAtConstantVolume { get; init; }
    public float? BoilingPointK { get; init; }
    public bool? CondensationEnabled { get; init; }
    public float? MolarEnthalpyOfVaporization { get; init; }
    public int? LiquidId { get; init; }
    public float? DiffusionCoefficient { get; init; }
    public float? MolarMassKgPerMol { get; init; }

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
            DiffusionCoefficient = DiffusionCoefficient ?? 0f,
            MolarMass = MolarMassKgPerMol ?? 0f
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
    public float? SleepEpsilonPercent { get; init; }
    public float? ThermalConductance { get; init; }
    public float? CondensationRateFactor { get; init; }
    public float? MaxPressureTransferFractionPerNeighbor { get; init; }
    public float? AccumulatorWakeThresholdPa { get; init; }
    public int? AccumulatorMaxAliveTicks { get; init; }
    public float? ReferenceMolarMassKgPerMol { get; init; }
    public EulerFlowPatch? EulerFlow { get; init; }

    /// <summary>
    ///     Rejects values outside the domain of their field, as the viewer does.
    /// </summary>
    /// <exception cref="ArgumentException">A value is not finite or is outside its range.</exception>
    internal void Validate()
    {
        Check(AtmosConfigFields.GlobalTemperature, GlobalTemperatureK, ConfigDomain.Positive);
        Check(AtmosConfigFields.DefaultTemperatureFallback, DefaultTemperatureFallbackK, ConfigDomain.Positive);
        Check(
            AtmosConfigFields.DefaultMolarHeatCapacityAtConstantVolume,
            DefaultMolarHeatCapacityAtConstantVolume,
            ConfigDomain.Positive);

        Check(AtmosConfigFields.VoxelVolume, VoxelVolumeM3, ConfigDomain.Positive);
        Check(AtmosConfigFields.SaturationReferencePressure, SaturationReferencePressurePa, ConfigDomain.Positive);
        Check(AtmosConfigFields.DefaultDiffusionCoefficient, DefaultDiffusionCoefficient, ConfigDomain.NonNegative);
        Check(AtmosConfigFields.SpaceTemperature, SpaceTemperatureK, ConfigDomain.Positive);
        Check(AtmosConfigFields.BulkFlowCoefficient, BulkFlowCoefficient, ConfigDomain.UnitInterval);
        Check(AtmosConfigFields.VacuumThreshold, VacuumThresholdPa, ConfigDomain.NonNegative);
        Check(AtmosConfigFields.SleepEpsilon, SleepEpsilonPercent, ConfigDomain.NonNegative);
        Check(AtmosConfigFields.ThermalConductance, ThermalConductance, ConfigDomain.NonNegative);
        Check(AtmosConfigFields.CondensationRateFactor, CondensationRateFactor, ConfigDomain.UnitInterval);
        Check(
            AtmosConfigFields.MaxPressureTransferFractionPerNeighbor,
            MaxPressureTransferFractionPerNeighbor,
            ConfigDomain.UnitInterval);

        Check(AtmosConfigFields.AccumulatorWakeThreshold, AccumulatorWakeThresholdPa, ConfigDomain.NonNegative);
        Check(AtmosConfigFields.SleepThreshold, SleepThreshold, ConfigDomain.NonNegative);
        Check(AtmosConfigFields.AccumulatorMaxAliveTicks, AccumulatorMaxAliveTicks, ConfigDomain.NonNegative);
    }

    private static void Check(AtmosConfigFieldInfo field, float? value, ConfigDomain domain)
    {
        if (value is { } present && AtmosConfigSettings.ValidateConfigValue(field, present, domain) is { } message)
            throw new ArgumentException(message);
    }

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

        if (SleepEpsilonPercent.HasValue)
            config.SleepEpsilon = SleepEpsilonPercent.Value;

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

        if (ReferenceMolarMassKgPerMol.HasValue)
            config.ReferenceMolarMass = ReferenceMolarMassKgPerMol.Value;

        EulerFlow?.ApplyTo(config);
    }
}

/// <summary>
///     Optional Euler transport settings. Missing properties keep the value of the <see cref="EulerFlowConfig" />
///     already in the configuration, or the library default when there is none.
/// </summary>
internal sealed class EulerFlowPatch
{
    public float? CourantNumber { get; init; }
    public int? MaxSubstepsPerTick { get; init; }
    public float? VelocityFloorMps { get; init; }
    public float? MaxVelocityMps { get; init; }
    public float? WakePressureDeltaPa { get; init; }
    public bool? AllMachCorrection { get; init; }
    public string? Reconstruction { get; init; }
    public float? MomentumDampingPerSecond { get; init; }
    public float? DefaultLinkAreaFraction { get; init; }

    /// <summary>
    ///     Replaces the configuration's <see cref="EulerFlowConfig" /> entry with one that applies this patch.
    /// </summary>
    internal void ApplyTo(AtmosConfig config)
    {
        var current = config.SolverConfigurations.OfType<EulerFlowConfig>().FirstOrDefault() ?? EulerFlowConfig.Default;
        var patched = new EulerFlowConfig
        {
            CourantNumber = CourantNumber ?? current.CourantNumber,
            MaxSubstepsPerTick = MaxSubstepsPerTick ?? current.MaxSubstepsPerTick,
            VelocityFloor = VelocityFloorMps ?? current.VelocityFloor,
            MaxVelocity = MaxVelocityMps ?? current.MaxVelocity,
            WakePressureDelta = WakePressureDeltaPa ?? current.WakePressureDelta,
            AllMachCorrection = AllMachCorrection ?? current.AllMachCorrection,
            Reconstruction = ParseReconstruction(Reconstruction) ?? current.Reconstruction,
            MomentumDamping = MomentumDampingPerSecond ?? current.MomentumDamping,
            DefaultLinkAreaFraction = DefaultLinkAreaFraction ?? current.DefaultLinkAreaFraction
        };

        config.SolverConfigurations.RemoveAll(static entry => entry is EulerFlowConfig);
        config.SolverConfigurations.Add(patched);
    }

    private static EulerReconstruction? ParseReconstruction(string? value)
    {
        if (value == null)
            return null;

        if (Enum.TryParse(value, true, out EulerReconstruction reconstruction) && Enum.IsDefined(reconstruction))
            return reconstruction;

        throw new HeadlessRequestException(
            "invalidConfig",
            $"Unknown Euler reconstruction '{value}'. Use 'firstOrder' or 'musclHancock'.");
    }
}