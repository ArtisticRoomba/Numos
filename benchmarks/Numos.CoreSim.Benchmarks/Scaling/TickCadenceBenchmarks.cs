using BenchmarkDotNet.Attributes;
using Numos.CoreSim.Benchmarks.Infrastructure;

namespace Numos.CoreSim.Benchmarks.Scaling;

/// <summary>
///     Makes the alternating thermodynamics cadence explicit.
/// </summary>
[BenchmarkCategory("PR", "TickCadence")]
public class TickCadenceBenchmarks : ScalingBenchmarkBase
{
    internal override ScalingWorkloadOptions Options => new()
    {
        RegisteredChunkCount = 8, AwakeChunkCount = 8, GasCount = 8,
        CondensingGasCount = 2, SupersaturatedVoxelFraction = 0.25d
    };

    /// <summary>
    ///     Prepares an odd, light tick.
    /// </summary>
    [IterationSetup(Target = nameof(Tick_NoThermodynamics))]
    public void PrepareLightTick()
    {
        Workload.Reset();
        Workload.SetTickCount(0);
    }

    /// <summary>
    ///     Measures a tick which skips thermodynamics.
    /// </summary>
    [Benchmark]
    public void Tick_NoThermodynamics()
    {
        Workload.Kernel.Tick();
    }

    /// <summary>
    ///     Prepares an even thermodynamics tick.
    /// </summary>
    [IterationSetup(Target = nameof(Tick_WithThermodynamics))]
    public void PrepareThermodynamicsTick()
    {
        Workload.Reset();
        Workload.SetTickCount(1);
    }

    /// <summary>
    ///     Measures a tick which runs thermodynamics.
    /// </summary>
    [Benchmark]
    public void Tick_WithThermodynamics()
    {
        Workload.Kernel.Tick();
    }

    /// <summary>
    ///     Prepares a normal light-plus-thermodynamics pair.
    /// </summary>
    [IterationSetup(Target = nameof(Tick_NormalCadencePair))]
    public void PrepareCadencePair()
    {
        Workload.Reset();
        Workload.SetTickCount(0);
    }

    /// <summary>
    ///     Measures two consecutive ticks; divide the result by two for average per tick.
    /// </summary>
    [Benchmark(OperationsPerInvoke = 2)]
    public void Tick_NormalCadencePair()
    {
        Workload.Kernel.Tick();
        Workload.Kernel.Tick();
    }
}