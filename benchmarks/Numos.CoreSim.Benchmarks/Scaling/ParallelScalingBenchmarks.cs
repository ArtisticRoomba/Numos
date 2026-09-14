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

/// <summary>
///     Measures thermodynamics scaling across enough chunks to keep every configured worker busy.
/// </summary>
[ParallelScalingConfig]
[BenchmarkCategory("Full", "ParallelScaling", "ThermodynamicsParallelScaling")]
public class ThermodynamicsParallelScalingBenchmarks : ScalingBenchmarkBase
{
    internal override ScalingWorkloadOptions Options => new()
    {
        RegisteredChunkCount = 128, AwakeChunkCount = 128, GasCount = 32,
        CondensingGasCount = 32, SupersaturatedVoxelFraction = 0.25d,
        BoundaryTopology = BoundaryTopology.Grid3D
    };

    /// <summary>
    ///     Restores the same thermal gradients and supersaturated mixture before every measured solve.
    /// </summary>
    [IterationSetup]
    public void ResetTransient()
    {
        Workload.Reset();
    }

    /// <summary>
    ///     Measures thermal diffusion and phase change across 128 dense 8×8×8 chunks with 32 condensing gases.
    /// </summary>
    [Benchmark]
    public void Thermodynamics_MultiChunkParallelWorkerScaling_Transient()
    {
        Workload.Steps[2].Solver(Workload.Context);
    }
}

/// <summary>
///     Measures whether thermodynamics can use additional workers when all work belongs to one chunk.
/// </summary>
[ParallelScalingConfig]
[BenchmarkCategory("Full", "ParallelScaling", "ThermodynamicsParallelScaling")]
public class DenseChunkThermodynamicsParallelScalingBenchmarks : ScalingBenchmarkBase
{
    internal override ScalingWorkloadOptions Options => new()
    {
        ChunkWidth = 16, ChunkHeight = 16, ChunkDepth = 16,
        RegisteredChunkCount = 1, AwakeChunkCount = 1, GasCount = 32,
        CondensingGasCount = 32, SupersaturatedVoxelFraction = 0.25d,
        BoundaryTopology = BoundaryTopology.Isolated
    };

    /// <summary>
    ///     Restores the same thermal gradients and supersaturated mixture before every measured solve.
    /// </summary>
    [IterationSetup]
    public void ResetTransient()
    {
        Workload.Reset();
    }

    /// <summary>
    ///     Measures thermal diffusion and phase change in one dense 16×16×16 chunk with 32 condensing gases.
    /// </summary>
    [Benchmark]
    public void Thermodynamics_DenseChunkParallelWorkerScaling_Transient()
    {
        Workload.Steps[2].Solver(Workload.Context);
    }
}

/// <summary>
///     Measures reaction scaling across enough chunks to keep every configured worker busy.
/// </summary>
[ParallelScalingConfig]
[BenchmarkCategory("Full", "ParallelScaling", "ReactionParallelScaling")]
public class ReactionParallelScalingBenchmarks : ScalingBenchmarkBase
{
    internal override ScalingWorkloadOptions Options => new()
    {
        RegisteredChunkCount = 128, AwakeChunkCount = 128, GasCount = 32,
        ReactionCount = 2, ReactionWorkload = ReactionWorkload.ActiveSparse,
        BoundaryTopology = BoundaryTopology.Grid3D
    };

    /// <summary>
    ///     Restores the same reactant mixture before every measured solve.
    /// </summary>
    [IterationSetup]
    public void ResetTransient()
    {
        Workload.Reset();
    }

    /// <summary>
    ///     Measures two active reactions across 128 dense 8×8×8 chunks with 32 gases.
    /// </summary>
    [Benchmark]
    public void Reactions_MultiChunkParallelWorkerScaling_Transient()
    {
        Workload.Steps[4].Solver(Workload.Context);
    }
}

/// <summary>
///     Measures whether one dense chunk supplies enough independent reaction work to occupy multiple workers.
/// </summary>
[ParallelScalingConfig]
[BenchmarkCategory("Full", "ParallelScaling", "ReactionParallelScaling")]
public class DenseChunkReactionParallelScalingBenchmarks : ScalingBenchmarkBase
{
    internal override ScalingWorkloadOptions Options => new()
    {
        ChunkWidth = 16, ChunkHeight = 16, ChunkDepth = 16,
        RegisteredChunkCount = 1, AwakeChunkCount = 1, GasCount = 32,
        ReactionCount = 2, ReactionWorkload = ReactionWorkload.ActiveSparse,
        BoundaryTopology = BoundaryTopology.Isolated
    };

    /// <summary>
    ///     Restores the same reactant mixture before every measured solve.
    /// </summary>
    [IterationSetup]
    public void ResetTransient()
    {
        Workload.Reset();
    }

    /// <summary>
    ///     Measures two active reactions in one dense 16×16×16 chunk with 32 gases.
    /// </summary>
    [Benchmark]
    public void Reactions_DenseChunkParallelWorkerScaling_Transient()
    {
        Workload.Steps[4].Solver(Workload.Context);
    }
}