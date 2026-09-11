using BenchmarkDotNet.Attributes;
using Numos.CoreSim.Benchmarks.Infrastructure;

namespace Numos.CoreSim.Benchmarks.Scaling;

/// <summary>
///     Measures solver scaling as awake chunk count grows.
/// </summary>
[BenchmarkCategory("PR", "ChunkScaling")]
public class ChunkScalingBenchmarks : ScalingBenchmarkBase
{
    /// <summary>
    ///     Gets or sets awake and registered chunks.
    /// </summary>
    [ParamsSource(nameof(ChunkCounts))]
    public int Chunks { get; set; }

    /// <summary>
    ///     Gets routine or full sweep values.
    /// </summary>
    public IEnumerable<int> ChunkCounts => BenchmarkDimensions.Choose([1, 8, 32, 128], [1, 2, 4, 8, 16, 32, 64, 128, 256]);

    internal override ScalingWorkloadOptions Options => new()
    {
        RegisteredChunkCount = Chunks, AwakeChunkCount = Chunks, GasCount = 8,
        ReactionCount = 2, ReactionWorkload = ReactionWorkload.ActiveSparse,
        CondensingGasCount = 2, SupersaturatedVoxelFraction = 0.25d,
        BoundaryTopology = BoundaryTopology.Line
    };

    /// <summary>
    ///     Measures local advection work as awake chunks grow.
    /// </summary>
    [Benchmark]
    public void Advection_AwakeChunkScaling()
    {
        Workload.Steps[0].Solver(Workload.Context);
    }

    /// <summary>
    ///     Measures thermal diffusion and phase change as awake chunks grow.
    /// </summary>
    [Benchmark]
    public void Thermodynamics_AwakeChunkScaling()
    {
        Workload.Steps[2].Solver(Workload.Context);
    }

    /// <summary>
    ///     Measures configured reaction work as awake chunks grow.
    /// </summary>
    [Benchmark]
    public void Reactions_AwakeChunkScaling()
    {
        Workload.Steps[4].Solver(Workload.Context);
    }

    /// <summary>
    ///     Measures an evolved complete tick.
    /// </summary>
    [Benchmark]
    public void Tick_AwakeChunkScaling_SteadyState()
    {
        Workload.Kernel.Tick();
    }

    /// <summary>
    ///     Restores the initial state outside timing for every tick.
    /// </summary>
    [IterationSetup(Target = nameof(Tick_AwakeChunkScaling_Transient))]
    public void ResetTransient()
    {
        Workload.Reset();
    }

    /// <summary>
    ///     Measures a complete tick from the same initial state.
    /// </summary>
    [Benchmark]
    public void Tick_AwakeChunkScaling_Transient()
    {
        Workload.Kernel.Tick();
    }
}

internal static class BenchmarkDimensions
{
    internal static IEnumerable<T> Choose<T>(T[] routine, T[] full)
    {
        if (Environment.GetEnvironmentVariable("NUMOS_BENCHMARK_SMOKE") == "1") return [routine[0]];

        return Environment.GetEnvironmentVariable("NUMOS_BENCHMARK_FULL") == "1" ? full : routine;
    }

    internal static IEnumerable<T> Smoke<T>(params T[] values)
    {
        return Environment.GetEnvironmentVariable("NUMOS_BENCHMARK_SMOKE") == "1" ? [values[0]] : values;
    }
}