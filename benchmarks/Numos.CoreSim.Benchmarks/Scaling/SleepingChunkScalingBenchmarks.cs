using BenchmarkDotNet.Attributes;
using Numos.CoreSim.Benchmarks.Infrastructure;

namespace Numos.CoreSim.Benchmarks.Scaling;

/// <summary>
///     Separates registered-chunk traversal from awake simulation work.
/// </summary>
[BenchmarkCategory("PR", "SleepingChunkScaling")]
public class SleepingChunkScalingBenchmarks : ScalingBenchmarkBase
{
    /// <summary>
    ///     Gets or sets the registered chunk count while 32 chunks stay awake.
    /// </summary>
    [ParamsSource(nameof(RegisteredCounts))]
    public int Registered { get; set; }

    /// <summary>
    ///     Gets routine or full registered-count values.
    /// </summary>
    public IEnumerable<int> RegisteredCounts => BenchmarkDimensions.Choose([32, 128, 512], [32, 64, 128, 256, 512, 1024, 4096]);

    internal override ScalingWorkloadOptions Options => new()
    {
        RegisteredChunkCount = Registered, AwakeChunkCount = 32, GasCount = 8,
        BoundaryTopology = BoundaryTopology.Isolated
    };

    /// <summary>
    ///     Measures one tick while sleeping registrations grow.
    /// </summary>
    [Benchmark]
    public void Tick_RegisteredChunkScaling()
    {
        Workload.Kernel.Tick();
    }
}

/// <summary>
///     Measures awake work while the registered population stays fixed.
/// </summary>
[BenchmarkCategory("Full", "SleepingChunkScaling")]
public class AwakeWithinRegisteredScalingBenchmarks : ScalingBenchmarkBase
{
    /// <summary>
    ///     Gets or sets awake chunks among 256 registrations.
    /// </summary>
    [Params(1, 8, 32, 128, 256)]
    public int Awake { get; set; }

    internal override ScalingWorkloadOptions Options => new()
    {
        RegisteredChunkCount = 256, AwakeChunkCount = Awake, GasCount = 8,
        BoundaryTopology = BoundaryTopology.Isolated
    };

    /// <summary>
    ///     Measures one tick as awake work grows.
    /// </summary>
    [Benchmark]
    public void Tick_AwakeWithinRegisteredScaling()
    {
        Workload.Kernel.Tick();
    }
}