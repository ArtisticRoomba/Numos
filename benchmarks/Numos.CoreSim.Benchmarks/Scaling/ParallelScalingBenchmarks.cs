using BenchmarkDotNet.Attributes;
using Numos.CoreSim.Benchmarks.Infrastructure;

namespace Numos.CoreSim.Benchmarks.Scaling;

/// <summary>
///     Measures advection scaling while the runtime exposes different processor counts.
/// </summary>
[ParallelScalingConfig]
//[EventPipeProfiler(EventPipeProfile.CpuSampling)]
//[DotMemoryDiagnoser]
[BenchmarkCategory("Full", "ParallelScaling")]
public class ParallelScalingBenchmarks : ScalingBenchmarkBase
{
    internal override ScalingWorkloadOptions Options => new()
    {
        RegisteredChunkCount = 128, AwakeChunkCount = 128, GasCount = 32,
        BoundaryTopology = BoundaryTopology.Grid3D
    };

    /// <summary>
    ///     Restores the same multi-chunk workload before every measured solve.
    /// </summary>
    [IterationSetup]
    public void ResetTransient()
    {
        Workload.Reset();
    }

    /// <summary>
    ///     Measures transient advection across 128 dense 8×8×8 chunks with 32 gases.
    /// </summary>
    [Benchmark]
    public void Advection_MultiChunkParallelWorkerScaling_Transient()
    {
        Workload.Steps[0].Solver(Workload.Context);
    }
}

/// <summary>
///     Measures whether one dense chunk supplies enough independent advection work to occupy multiple workers.
/// </summary>
[ParallelScalingConfig]
[BenchmarkCategory("Full", "ParallelScaling")]
public class DenseChunkParallelScalingBenchmarks : ScalingBenchmarkBase
{
    internal override ScalingWorkloadOptions Options => new()
    {
        ChunkWidth = 16, ChunkHeight = 16, ChunkDepth = 16,
        RegisteredChunkCount = 1, AwakeChunkCount = 1, GasCount = 32,
        BoundaryTopology = BoundaryTopology.Isolated
    };

    /// <summary>
    ///     Restores the same dense chunk before every measured solve.
    /// </summary>
    [IterationSetup]
    public void ResetTransient()
    {
        Workload.Reset();
    }

    /// <summary>
    ///     Measures transient advection in one dense 16×16×16 chunk with 32 gases.
    /// </summary>
    [Benchmark]
    public void Advection_DenseChunkParallelWorkerScaling_Transient()
    {
        Workload.Steps[0].Solver(Workload.Context);
    }
}