using BenchmarkDotNet.Attributes;
using Numos.CoreSim.Benchmarks.Infrastructure;

namespace Numos.CoreSim.Benchmarks.Scaling;

/// <summary>
///     Measures event production and deterministic cross-chunk boundary processing.
/// </summary>
[BenchmarkCategory("Full", "BoundaryScaling")]
public class BoundaryScalingBenchmarks : ScalingBenchmarkBase
{
    /// <summary>
    ///     Gets or sets connected chunk count.
    /// </summary>
    [ParamsSource(nameof(ChunkCounts))]
    public int Chunks { get; set; }

    /// <summary>
    ///     Gets boundary chunk-count values.
    /// </summary>
    public IEnumerable<int> ChunkCounts => BenchmarkDimensions.Smoke(2, 8, 32, 128);

    /// <summary>
    ///     Gets or sets deterministic chunk arrangement.
    /// </summary>
    [ParamsSource(nameof(Topologies))]
    public BoundaryTopology Topology { get; set; }

    /// <summary>
    ///     Gets boundary arrangements.
    /// </summary>
    public IEnumerable<BoundaryTopology> Topologies => BenchmarkDimensions.Smoke(
        BoundaryTopology.Line,
        BoundaryTopology.Isolated,
        BoundaryTopology.Grid2D,
        BoundaryTopology.Grid3D);

    /// <summary>
    ///     Gets or sets pressure transfer strength.
    /// </summary>
    [ParamsSource(nameof(PressureGradients))]
    public GradientStrength PressureGradient { get; set; }

    /// <summary>
    ///     Gets pressure-gradient strengths.
    /// </summary>
    public IEnumerable<GradientStrength> PressureGradients => BenchmarkDimensions.Smoke(
        GradientStrength.Large,
        GradientStrength.None,
        GradientStrength.Small);

    internal override ScalingWorkloadOptions Options => new()
    {
        RegisteredChunkCount = Chunks, AwakeChunkCount = Chunks, GasCount = 16,
        BoundaryTopology = Topology, PressureGradient = PressureGradient
    };

    /// <summary>
    ///     Restores state and produces boundary events outside the measurement.
    /// </summary>
    [IterationSetup(Target = nameof(Boundary_ChunkScaling_Transient))]
    public void PrepareBoundaryEvents()
    {
        Workload.Reset(1);
    }

    /// <summary>
    ///     Measures sorting and gas transfer for prepared boundary events.
    /// </summary>
    [Benchmark]
    public void Boundary_ChunkScaling_Transient()
    {
        Workload.Steps[1].Solver(Workload.Context);
    }
}

/// <summary>
///     Measures thermal-edge collection and heat exchange independently from its producer.
/// </summary>
[BenchmarkCategory("Full", "ThermalBoundaryScaling")]
public class ThermalBoundaryScalingBenchmarks : ScalingBenchmarkBase
{
    /// <summary>
    ///     Gets or sets connected chunk count.
    /// </summary>
    [ParamsSource(nameof(ChunkCounts))]
    public int Chunks { get; set; }

    /// <summary>
    ///     Gets thermal-boundary chunk-count values.
    /// </summary>
    public IEnumerable<int> ChunkCounts => BenchmarkDimensions.Smoke(2, 8, 32, 128);

    /// <summary>
    ///     Gets or sets deterministic chunk arrangement.
    /// </summary>
    [ParamsSource(nameof(Topologies))]
    public BoundaryTopology Topology { get; set; }

    /// <summary>
    ///     Gets connected thermal-boundary arrangements.
    /// </summary>
    public IEnumerable<BoundaryTopology> Topologies => BenchmarkDimensions.Smoke(
        BoundaryTopology.Line,
        BoundaryTopology.Grid2D,
        BoundaryTopology.Grid3D);

    /// <summary>
    ///     Gets or sets temperature transfer strength.
    /// </summary>
    [ParamsSource(nameof(TemperatureGradients))]
    public GradientStrength TemperatureGradient { get; set; }

    /// <summary>
    ///     Gets equal and varying temperature states.
    /// </summary>
    public IEnumerable<GradientStrength> TemperatureGradients => BenchmarkDimensions.Smoke(
        GradientStrength.Large,
        GradientStrength.None);

    internal override ScalingWorkloadOptions Options => new()
    {
        RegisteredChunkCount = Chunks, AwakeChunkCount = Chunks, GasCount = 16,
        BoundaryTopology = Topology, TemperatureGradient = TemperatureGradient
    };

    /// <summary>
    ///     Restores state and produces thermal events outside the measurement.
    /// </summary>
    [IterationSetup(Target = nameof(ThermalBoundary_EdgeScaling_Transient))]
    public void PrepareThermalEvents()
    {
        Workload.Reset(3);
    }

    /// <summary>
    ///     Measures sorting and heat transfer for prepared thermal edges.
    /// </summary>
    [Benchmark]
    public void ThermalBoundary_EdgeScaling_Transient()
    {
        Workload.Steps[3].Solver(Workload.Context);
    }
}