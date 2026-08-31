using Numos.Headless.Protocol;

namespace Numos.Headless.Diagnostics;

/// <summary>Controls the scope and detail of one detached simulation observation.</summary>
public sealed class SimulationObservationOptions
{
    public const int DefaultMaxIssueLocations = 32;
    public const int MaximumMaxIssueLocations = 1_024;

    /// <summary>Limits the report to one chunk. A missing chunk is rejected.</summary>
    public Coordinate? Chunk { get; init; }

    /// <summary>Specific voxels to include even when <see cref="IncludeVoxels" /> is false.</summary>
    public IReadOnlyList<VoxelSelection>? Voxels { get; init; }

    /// <summary>Includes detailed voxel reports for every voxel in the selected chunk scope.</summary>
    public bool IncludeVoxels { get; init; }

    /// <summary>
    ///     When full voxel detail is enabled, omits voxels without a positive gas amount. Explicit
    ///     <see cref="Voxels" /> selections are still returned.
    /// </summary>
    public bool OnlyGasBearingVoxels { get; init; }

    /// <summary>Maximum number of deterministic anomaly locations returned with the aggregate counts.</summary>
    public int MaxIssueLocations { get; init; } = DefaultMaxIssueLocations;
}

/// <summary>Identifies one local voxel in a chunk.</summary>
public sealed class VoxelSelection
{
    public VoxelSelection(Coordinate? chunk, Coordinate? voxel)
    {
        Chunk = chunk ?? throw new ArgumentNullException(nameof(chunk));
        Voxel = voxel ?? throw new ArgumentNullException(nameof(voxel));
    }

    public Coordinate Chunk { get; }
    public Coordinate Voxel { get; }
}

/// <summary>A coherent detached observation of a simulation state.</summary>
public sealed record SimulationStateReport(
    int Tick,
    float SimulationRate,
    int SimulationChunkCount,
    SimulationConfigurationReport Config,
    SolverStepReport[] SolverPipeline,
    SimulationSummaryReport Global,
    ChunkStateReport[] Chunks,
    AnomalyIssueReport[] IssueLocations,
    bool IssueLocationsTruncated);

/// <summary>The live configuration values associated with an observation.</summary>
public sealed record SimulationConfigurationReport(
    float GlobalTemperatureK,
    float DefaultTemperatureFallbackK,
    float DefaultMolarHeatCapacityAtConstantVolume,
    float VoxelVolumeM3,
    float SaturationReferencePressurePa,
    float DefaultDiffusionCoefficient,
    float SpaceTemperatureK,
    float BulkFlowCoefficient,
    float VacuumThresholdPa,
    int SleepThreshold,
    float SleepEpsilonPa,
    float ThermalConductance,
    float CondensationRateFactor,
    float MaxPressureTransferFractionPerNeighbor,
    float AccumulatorWakeThresholdPa,
    int AccumulatorMaxAliveTicks,
    GasConfigurationReport[] Gases);

/// <summary>One indexed gas definition captured from the configuration registry.</summary>
public sealed record GasConfigurationReport(
    int GasId,
    string? Name,
    float MolarHeatCapacityAtConstantVolume,
    float BoilingPointK,
    bool CondensationEnabled,
    float MolarEnthalpyOfVaporization,
    int LiquidId,
    float DiffusionCoefficient);

/// <summary>Wire metadata for one solver stage, in execution order.</summary>
public sealed record SolverStepReport(string Name, bool IsEnabled, string Kind);

/// <summary>Aggregate values across the chunks selected for an observation.</summary>
public sealed record SimulationSummaryReport(
    int ChunkCount,
    int VoxelCount,
    int GasCapableVoxelCount,
    int SolidVoxelCount,
    int VoidVoxelCount,
    int GasBearingVoxelCount,
    int ActiveAirCount,
    int ActiveGasChannelCount,
    int AwakeChunkCount,
    int SleepingChunkCount,
    double TotalMoles,
    double SensibleEnergyJ,
    FiniteStatisticsReport PressurePa,
    FiniteStatisticsReport TemperatureK,
    GasTotalReport[] Gases,
    AnomalyCountsReport Anomalies);

/// <summary>Detached state and aggregate values for one chunk.</summary>
public sealed record ChunkStateReport(
    Coordinate Position,
    Coordinate Dimensions,
    long Generation,
    long Revision,
    bool IsAwake,
    int SleepTimer,
    int ActiveAirCount,
    int ActiveGasCount,
    ChunkSummaryReport Summary,
    VoxelStateReport[] Voxels);

/// <summary>Aggregate values for one chunk.</summary>
public sealed record ChunkSummaryReport(
    int VoxelCount,
    int GasCapableVoxelCount,
    int SolidVoxelCount,
    int VoidVoxelCount,
    int GasBearingVoxelCount,
    double TotalMoles,
    double SensibleEnergyJ,
    FiniteStatisticsReport PressurePa,
    FiniteStatisticsReport TemperatureK,
    GasTotalReport[] Gases,
    AnomalyCountsReport Anomalies);

/// <summary>Finite-value statistics while retaining the size of the complete sampled field.</summary>
public sealed record FiniteStatisticsReport(
    int SampleCount,
    int FiniteCount,
    int NonFiniteCount,
    double? Minimum,
    double? Maximum,
    double? Mean);

/// <summary>Total amount for one gas, ordered by gas ID.</summary>
public sealed record GasTotalReport(int GasId, string? Name, double Moles);

/// <summary>Raw amount for one gas in one detailed voxel.</summary>
public sealed record VoxelGasReport(int GasId, string? Name, float Moles);

/// <summary>Detailed raw and derived values for one voxel.</summary>
public sealed record VoxelStateReport(
    int LocalIndex,
    Coordinate Position,
    int RoomId,
    bool IsGasCapable,
    bool IsGasBearing,
    float PressurePa,
    float TemperatureK,
    double TotalMoles,
    double SensibleEnergyJ,
    VoxelGasReport[] Gases);

/// <summary>Counts of physical-value anomalies found while scanning an observation.</summary>
public sealed record AnomalyCountsReport(
    int NonFinitePressureCount,
    int NegativePressureCount,
    int NonFiniteTemperatureCount,
    int NegativeTemperatureCount,
    int NonFiniteMolesCount,
    int NegativeMolesCount)
{
    public int TotalCount =>
        NonFinitePressureCount +
        NegativePressureCount +
        NonFiniteTemperatureCount +
        NegativeTemperatureCount +
        NonFiniteMolesCount +
        NegativeMolesCount;
}

/// <summary>One bounded, deterministic location for an aggregate anomaly.</summary>
public sealed record AnomalyIssueReport(
    string Kind,
    Coordinate Chunk,
    Coordinate Voxel,
    int LocalIndex,
    int? GasId,
    float Value);