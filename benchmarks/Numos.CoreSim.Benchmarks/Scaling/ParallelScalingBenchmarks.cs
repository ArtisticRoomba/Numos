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
        Workload.FlowStep.Solver(Workload.Context);
    }
}

/// <summary>
///     Measures boundary-flow scaling while the runtime exposes different processor counts.
/// </summary>
[ParallelScalingConfig]
[BenchmarkCategory("Full", "ParallelScaling", "BoundaryScaling")]
public class BoundaryParallelScalingBenchmarks : ScalingBenchmarkBase
{
    internal override ScalingWorkloadOptions Options => new()
    {
        RegisteredChunkCount = 128, AwakeChunkCount = 128, GasCount = 32,
        BoundaryTopology = BoundaryTopology.Grid3D
    };

    /// <summary>
    ///     Restores state and produces boundary events outside the measurement.
    /// </summary>
    [IterationSetup]
    public void PrepareBoundaryEvents()
    {
        Workload.Reset(Workload.BoundaryFlowStep);
    }

    /// <summary>
    ///     Measures transient boundary flow across 128 dense 8×8×8 chunks with 32 gases.
    /// </summary>
    [Benchmark]
    public void Boundary_MultiChunkParallelWorkerScaling_Transient()
    {
        Workload.BoundaryFlowStep.Solver(Workload.Context);
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
        Workload.FlowStep.Solver(Workload.Context);
    }
}

/// <summary>
///     Measures Euler transport scaling across enough chunks that every configured worker sweeps whole chunks.
/// </summary>
/// <remarks>
///     The Euler stage sizes its schedule from <see cref="Environment.ProcessorCount" /> like advection does, so the
///     <c>DOTNET_PROCESSOR_COUNT</c> jobs drive both the thread count and the whole-chunk-versus-tiled choice;
///     <see cref="Numos.CoreSim.Solvers.Euler.EulerFlowSolver.WorkerCountOverride" /> stays unset here. Every chunk
///     starts with the workload's pressure and temperature checkerboard, so the whole grid is live.
/// </remarks>
[ParallelScalingConfig]
[BenchmarkCategory("Full", "ParallelScaling", "EulerParallelScaling")]
public class EulerParallelScalingBenchmarks : ScalingBenchmarkBase
{
    internal override ScalingWorkloadOptions Options => new()
    {
        TransportModel = AtmosTransportModel.Euler,
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
    ///     Measures one transient Euler tick across 128 dense 8×8×8 chunks with 32 gases.
    /// </summary>
    [Benchmark]
    public void EulerFlow_MultiChunkParallelWorkerScaling_Transient()
    {
        Workload.FlowStep.Solver(Workload.Context);
    }
}

/// <summary>
///     Measures whether one dense chunk gives the Euler stage's pencil tiles enough work to occupy multiple workers.
/// </summary>
[ParallelScalingConfig]
[BenchmarkCategory("Full", "ParallelScaling", "EulerParallelScaling")]
public class DenseChunkEulerParallelScalingBenchmarks : ScalingBenchmarkBase
{
    internal override ScalingWorkloadOptions Options => new()
    {
        TransportModel = AtmosTransportModel.Euler,
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
    ///     Measures one transient Euler tick in one dense 16×16×16 chunk with 32 gases.
    /// </summary>
    [Benchmark]
    public void EulerFlow_DenseChunkParallelWorkerScaling_Transient()
    {
        Workload.FlowStep.Solver(Workload.Context);
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
        Workload.ThermodynamicsStep.Solver(Workload.Context);
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
        Workload.ThermodynamicsStep.Solver(Workload.Context);
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
        Workload.ReactionsStep.Solver(Workload.Context);
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
        Workload.ReactionsStep.Solver(Workload.Context);
    }
}