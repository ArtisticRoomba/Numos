using BenchmarkDotNet.Attributes;
using Numos.CoreSim.Benchmarks.Infrastructure;
using Numos.CoreSim.Datatypes.Events;
using Numos.CoreSim.Solvers;

namespace Numos.CoreSim.Benchmarks.Scaling;

/// <summary>
///     Varies allocated voxel count with full occupancy.
/// </summary>
[BenchmarkCategory("PR", "VoxelScaling")]
public class VoxelScalingBenchmarks : ScalingBenchmarkBase
{
    private readonly ThermalDiffusionSolver _thermal = new();
    private ThermalBoundaryEvent[] _events = [];

    /// <summary>
    ///     Gets or sets each side of a cubic chunk.
    /// </summary>
    [ParamsSource(nameof(Edges))]
    public int Edge { get; set; }

    /// <summary>
    ///     Gets routine or full chunk-edge values.
    /// </summary>
    public IEnumerable<int> Edges => BenchmarkDimensions.Choose([4, 8, 16], [4, 6, 8, 10, 12, 16]);

    internal override ScalingWorkloadOptions Options => new()
    {
        ChunkWidth = Edge, ChunkHeight = Edge, ChunkDepth = Edge,
        RegisteredChunkCount = 4, AwakeChunkCount = 4, GasCount = 8
    };

    /// <inheritdoc />
    public override void Setup()
    {
        base.Setup();
        _events = new ThermalBoundaryEvent[VoxelCount];
    }

    /// <summary>
    ///     Measures allocated-voxel and gas-vector work in advection.
    /// </summary>
    [Benchmark]
    public void Advection_VoxelScaling()
    {
        Workload.Steps[0].Solver(Workload.Context);
    }

    /// <summary>
    ///     Measures allocated-voxel work in thermal diffusion.
    /// </summary>
    [Benchmark]
    public void ThermalDiffusion_VoxelScaling()
    {
        foreach (var chunk in Workload.Chunks)
            _thermal.Solve(chunk, Workload.Config, _events);
    }
}

/// <summary>
///     Varies active air occupancy inside a fixed 8×8×8 chunk.
/// </summary>
[BenchmarkCategory("PR", "OccupancyScaling")]
public class OccupancyScalingBenchmarks : ScalingBenchmarkBase
{
    /// <summary>
    ///     Gets or sets active air occupancy.
    /// </summary>
    [ParamsSource(nameof(Occupancies))]
    public double Occupancy { get; set; }

    /// <summary>
    ///     Gets active-air fractions.
    /// </summary>
    public IEnumerable<double> Occupancies => BenchmarkDimensions.Smoke(0d, 0.125d, 0.25d, 0.5d, 0.75d, 1d);

    internal override ScalingWorkloadOptions Options => new()
    {
        RegisteredChunkCount = 4, AwakeChunkCount = 4, ActiveVoxelFraction = Occupancy, GasCount = 8
    };

    /// <summary>
    ///     Measures active-air scaling separately from allocation size.
    /// </summary>
    [Benchmark]
    public void Advection_OccupancyScaling()
    {
        Workload.Steps[0].Solver(Workload.Context);
    }
}