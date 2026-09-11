using BenchmarkDotNet.Attributes;

namespace Numos.CoreSim.Benchmarks.Infrastructure;

/// <summary>
///     Owns a deterministic scaling fixture used by one-axis benchmark sweeps.
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory("Scaling")]
[Config(typeof(ScalingColumnsConfig))]
public abstract class ScalingBenchmarkBase
{
    internal SimulationWorkload Workload { get; private set; } = null!;

    /// <summary>
    ///     Gets the workload mode shown in exported results.
    /// </summary>
    public virtual string WorkloadMode => "SteadyState and Transient";
    /// <summary>
    ///     Gets the registered chunk count.
    /// </summary>
    public int RegisteredChunks => Options.RegisteredChunkCount;
    /// <summary>
    ///     Gets the awake chunk count.
    /// </summary>
    public int AwakeChunks => Options.AwakeChunkCount;
    /// <summary>
    ///     Gets the chunk width.
    /// </summary>
    public int ChunkWidth => Options.ChunkWidth;
    /// <summary>
    ///     Gets the chunk height.
    /// </summary>
    public int ChunkHeight => Options.ChunkHeight;
    /// <summary>
    ///     Gets the chunk depth.
    /// </summary>
    public int ChunkDepth => Options.ChunkDepth;
    /// <summary>
    ///     Gets allocated voxels per chunk.
    /// </summary>
    public int VoxelCount => Options.VoxelCount;
    /// <summary>
    ///     Gets active air voxels per awake chunk.
    /// </summary>
    public int ActiveVoxelCount => Options.ActiveVoxelCount;
    /// <summary>
    ///     Gets registered gas species.
    /// </summary>
    public int GasCount => Options.GasCount;
    /// <summary>
    ///     Gets configured reactions.
    /// </summary>
    public int ReactionCount => Options.ReactionCount;
    /// <summary>
    ///     Gets condensing gas species.
    /// </summary>
    public int CondensingGasCount => Options.CondensingGasCount;
    /// <summary>
    ///     Gets the supersaturated share of active voxels.
    /// </summary>
    public double SupersaturatedVoxelFraction => Options.SupersaturatedVoxelFraction;
    /// <summary>
    ///     Gets the chunk boundary topology.
    /// </summary>
    public BoundaryTopology BoundaryTopology => Options.BoundaryTopology;
    /// <summary>
    ///     Gets expected directed boundary events for fully occupied chunks.
    /// </summary>
    public int BoundaryEventCount => Workload.BoundaryEventCount;
    /// <summary>
    ///     Gets expected unique cross-chunk thermal edges for fully occupied chunks.
    /// </summary>
    public int ThermalBoundaryEdgeCount => Workload.ThermalBoundaryEdgeCount;

    internal abstract ScalingWorkloadOptions Options { get; }

    /// <summary>
    ///     Creates the fixture for a benchmark case.
    /// </summary>
    [GlobalSetup]
    public virtual void Setup()
    {
        Workload = new SimulationWorkload(Options);
    }

    /// <summary>
    ///     Releases pooled simulation storage.
    /// </summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        Workload.Dispose();
    }
}