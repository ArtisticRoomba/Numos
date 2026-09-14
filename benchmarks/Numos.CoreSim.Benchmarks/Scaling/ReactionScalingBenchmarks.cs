using BenchmarkDotNet.Attributes;
using Numos.CoreSim.Benchmarks.Infrastructure;

namespace Numos.CoreSim.Benchmarks.Scaling;

/// <summary>
///     Varies reaction count independently for inactive, sparse, and dense definitions.
/// </summary>
[BenchmarkCategory("PR", "ReactionScaling")]
public class ReactionScalingBenchmarks : ScalingBenchmarkBase
{
    /// <summary>
    ///     Gets or sets configured reactions.
    /// </summary>
    [ParamsSource(nameof(ReactionCounts))]
    public int Reactions { get; set; }

    /// <summary>
    ///     Gets routine or full reaction-count values.
    /// </summary>
    public IEnumerable<int> ReactionCounts => BenchmarkDimensions.Choose([1, 8, 32], [1, 2, 4, 8, 16, 32, 64]);

    /// <summary>
    ///     Gets or sets reaction sparsity and activity.
    /// </summary>
    [ParamsSource(nameof(ReactionStates))]
    public ReactionWorkload ReactionState { get; set; }

    /// <summary>
    ///     Gets inactive, sparse, and dense reaction states.
    /// </summary>
    public IEnumerable<ReactionWorkload> ReactionStates => BenchmarkDimensions.Smoke(
        ReactionWorkload.ActiveSparse,
        ReactionWorkload.Inactive,
        ReactionWorkload.ActiveDense);

    internal override ScalingWorkloadOptions Options => new()
    {
        RegisteredChunkCount = 2, AwakeChunkCount = 2, ActiveVoxelFraction = 0.125d,
        GasCount = 32, ReactionCount = Reactions, ReactionWorkload = ReactionState
    };

    /// <summary>
    ///     Restores reactants before every measured solve.
    /// </summary>
    [IterationSetup(Target = nameof(Reaction_ReactionCountScaling_Transient))]
    public void ResetTransient()
    {
        Workload.Reset();
    }

    /// <summary>
    ///     Measures reaction dispatch, limiting, and gas-vector writeback.
    /// </summary>
    [Benchmark]
    public void Reaction_ReactionCountScaling_Transient()
    {
        Workload.Steps[4].Solver(Workload.Context);
    }
}