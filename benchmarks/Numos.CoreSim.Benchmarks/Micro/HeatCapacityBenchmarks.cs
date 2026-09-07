using BenchmarkDotNet.Attributes;
using Numos.CoreSim.Benchmarks.Infrastructure;
using Numos.CoreSim.Solvers;

namespace Numos.CoreSim.Benchmarks.Micro;

/// <summary>
///     Compares gas-property lookup through a configuration snapshot with the tick's precomputed gas tables.
/// </summary>
/// <remarks>
///     Both paths call the production heat-capacity reduction on identical non-mutating inputs.
///     No iteration setup is needed, allowing BenchmarkDotNet to batch small operations.
/// </remarks>
[MemoryDiagnoser]
[BenchmarkCategory("Micro")]
public class HeatCapacityBenchmarks
{
    private IAtmosConfig _uncached = null!;
    private SimulationWorkload _workload = null!;

    /// <summary>
    ///     Gets or sets the number of gas channels included in the reduction.
    /// </summary>
    [ParamsSource(nameof(GasCounts))]
    public int GasCount { get; set; }

    /// <summary>
    ///     Gets the gas sweep, or a single smoke case.
    /// </summary>
    public IEnumerable<int> GasCounts => ScalingBenchmark.Smoke ? [2] : [1, 2, 8, 32, 64];

    /// <summary>
    ///     Populates one chunk and verifies that both paths produce identical results before timing.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        _workload = new SimulationWorkload(1, 64, GasCount);
        _uncached = _workload.Kernel.GetAtmosConfig();
        if (ConfigurationLookup() != CapturedGasTables())
            throw new InvalidOperationException("Heat-capacity comparison must preserve the result exactly.");
    }

    /// <summary>
    ///     Reduces heat capacity using ordinary configuration gas-property lookups.
    /// </summary>
    /// <returns>Heat capacity in joules per kelvin.</returns>
    [Benchmark(Baseline = true)]
    public float ConfigurationLookup()
    {
        return AtmosSolverMath.CalculateHeatCapacityAtVoxel(_uncached, _workload.Chunks[0], 0);
    }

    /// <summary>
    ///     Reduces heat capacity using the captured tick gas tables.
    /// </summary>
    /// <returns>Heat capacity in joules per kelvin.</returns>
    [Benchmark]
    public float CapturedGasTables()
    {
        return AtmosSolverMath.CalculateHeatCapacityAtVoxel(_workload.Config, _workload.Chunks[0], 0);
    }

    /// <summary>
    ///     Releases the fixture after measurements finish.
    /// </summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _workload.Dispose();
    }
}