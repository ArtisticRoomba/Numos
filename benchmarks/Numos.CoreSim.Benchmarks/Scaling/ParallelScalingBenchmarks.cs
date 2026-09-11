using BenchmarkDotNet.Attributes;
using Numos.CoreSim.Benchmarks.Infrastructure;

namespace Numos.CoreSim.Benchmarks.Scaling;

/// <summary>
///     Measures wall-clock scaling while the runtime exposes different processor counts.
/// </summary>
[ParallelScalingConfig]
[BenchmarkCategory("Full", "ParallelScaling")]
public class ParallelScalingBenchmarks : ScalingBenchmarkBase
{
    internal override ScalingWorkloadOptions Options => new()
    {
        RegisteredChunkCount = 128, AwakeChunkCount = 128, GasCount = 32,
        BoundaryTopology = BoundaryTopology.Grid3D
    };

    /// <summary>
    ///     Restores the same large workload before every measured tick.
    /// </summary>
    [IterationSetup]
    public void ResetTransient()
    {
        Workload.Reset();
    }

    /// <summary>
    ///     Measures a full transient tick under each processor-count job.
    /// </summary>
    [Benchmark]
    public void Tick_ParallelWorkerScaling()
    {
        Workload.Kernel.Tick();
    }
}