using BenchmarkDotNet.Attributes;
using Numos.CoreSim.Benchmarks.Infrastructure;
using Numos.CoreSim.Solvers;

namespace Numos.CoreSim.Benchmarks.Scaling;

/// <summary>
///     Separates saturation screening from condensation application work.
/// </summary>
[BenchmarkCategory("PR", "PhaseChangeScaling")]
public class PhaseChangeScalingBenchmarks : ScalingBenchmarkBase
{
    private readonly PhaseChangeSolver _solver = new();

    /// <summary>
    ///     Gets or sets phase-change-enabled gases while total gas count stays fixed.
    /// </summary>
    [ParamsSource(nameof(CondensingGasCounts))]
    public int CondensingGases { get; set; }

    /// <summary>
    ///     Gets routine or full condensing-species values.
    /// </summary>
    public IEnumerable<int> CondensingGasCounts => BenchmarkDimensions.Choose([1, 8, 32], [1, 2, 4, 8, 16, 32]);

    /// <summary>
    ///     Gets or sets the supersaturated active-voxel share.
    /// </summary>
    [ParamsSource(nameof(Supersaturations))]
    public double Supersaturation { get; set; }

    /// <summary>
    ///     Gets screening, partial, and full condensation fractions.
    /// </summary>
    public IEnumerable<double> Supersaturations => BenchmarkDimensions.Smoke(1d, 0d, 0.25d, 0.5d);

    internal override ScalingWorkloadOptions Options => new()
    {
        RegisteredChunkCount = 2, AwakeChunkCount = 2, GasCount = 32,
        CondensingGasCount = CondensingGases, SupersaturatedVoxelFraction = Supersaturation,
        TemperatureGradient = GradientStrength.None
    };

    /// <summary>
    ///     Restores vapor inventory before each measured phase-change pass.
    /// </summary>
    [IterationSetup(Target = nameof(PhaseChange_CondensingSpeciesScaling_Transient))]
    public void ResetTransient()
    {
        Workload.Reset();
    }

    /// <summary>
    ///     Measures checks and applications as condensing species and saturation grow.
    /// </summary>
    [Benchmark]
    public void PhaseChange_CondensingSpeciesScaling_Transient()
    {
        foreach (var chunk in Workload.Chunks)
            _solver.Solve(chunk, Workload.Config);
    }
}

/// <summary>
///     Varies total gas-vector width with every gas eligible for phase change.
/// </summary>
[BenchmarkCategory("Full", "PhaseChangeScaling", "GasScaling")]
public class PhaseChangeGasScalingBenchmarks : ScalingBenchmarkBase
{
    private readonly PhaseChangeSolver _solver = new();

    /// <summary>
    ///     Gets or sets total and condensing gas count.
    /// </summary>
    [ParamsSource(nameof(GasCounts))]
    public int Gases { get; set; }

    /// <summary>
    ///     Gets total gas-width values.
    /// </summary>
    public IEnumerable<int> GasCounts => BenchmarkDimensions.Smoke(1, 2, 4, 8, 16, 32, 64, 128);

    /// <summary>
    ///     Gets or sets screening-only or full-condensation state.
    /// </summary>
    [ParamsSource(nameof(Supersaturations))]
    public double Supersaturation { get; set; }

    /// <summary>
    ///     Gets screening-only and full-condensation states.
    /// </summary>
    public IEnumerable<double> Supersaturations => BenchmarkDimensions.Smoke(1d, 0d);

    internal override ScalingWorkloadOptions Options => new()
    {
        RegisteredChunkCount = 1, AwakeChunkCount = 1, ActiveVoxelFraction = 0.25d,
        GasCount = Gases, CondensingGasCount = Gases, SupersaturatedVoxelFraction = Supersaturation,
        TemperatureGradient = GradientStrength.None
    };

    /// <summary>
    ///     Restores saturation state before each measured solve.
    /// </summary>
    [IterationSetup]
    public void ResetTransient()
    {
        Workload.Reset();
    }

    /// <summary>
    ///     Measures screening and application cost as total gas width grows.
    /// </summary>
    [Benchmark]
    public void PhaseChange_GasScaling_Transient()
    {
        foreach (var chunk in Workload.Chunks)
            _solver.Solve(chunk, Workload.Config);
    }
}