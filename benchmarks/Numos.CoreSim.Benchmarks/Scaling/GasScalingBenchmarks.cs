using BenchmarkDotNet.Attributes;
using Numos.CoreSim.Benchmarks.Infrastructure;

namespace Numos.CoreSim.Benchmarks.Scaling;

/// <summary>
///     Measures gas-vector scaling under idle and active physical states.
/// </summary>
[BenchmarkCategory("PR", "GasScaling")]
public class GasScalingBenchmarks : ScalingBenchmarkBase
{
    /// <summary>
    ///     Gets or sets registered gas species.
    /// </summary>
    [ParamsSource(nameof(GasCounts))]
    public int Gases { get; set; }

    /// <summary>
    ///     Gets routine or full gas-count values.
    /// </summary>
    public IEnumerable<int> GasCounts => BenchmarkDimensions.Choose([2, 8, 32, 128], [1, 2, 4, 8, 16, 32, 64, 128]);

    /// <summary>
    ///     Gets or sets pressure and temperature activity.
    /// </summary>
    [ParamsSource(nameof(Activities))]
    public GradientStrength Activity { get; set; }

    /// <summary>
    ///     Gets idle and active physical states.
    /// </summary>
    public IEnumerable<GradientStrength> Activities => BenchmarkDimensions.Smoke(GradientStrength.Large, GradientStrength.None);

    internal override ScalingWorkloadOptions Options => new()
    {
        RegisteredChunkCount = 4, AwakeChunkCount = 4, GasCount = Gases,
        PressureGradient = Activity, TemperatureGradient = Activity
    };

    /// <summary>
    ///     Measures gas-width scaling after state evolves.
    /// </summary>
    [Benchmark]
    public void Advection_GasScaling_SteadyState()
    {
        Workload.Steps[0].Solver(Workload.Context);
    }

    /// <summary>
    ///     Restores the initial gradient before transient advection.
    /// </summary>
    [IterationSetup(Target = nameof(Advection_GasScaling_Transient))]
    public void ResetTransient()
    {
        Workload.Reset();
    }

    /// <summary>
    ///     Measures gas-width scaling from a fixed gradient.
    /// </summary>
    [Benchmark]
    public void Advection_GasScaling_Transient()
    {
        Workload.Steps[0].Solver(Workload.Context);
    }
}