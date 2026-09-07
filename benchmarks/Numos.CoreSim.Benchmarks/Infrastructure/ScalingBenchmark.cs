using BenchmarkDotNet.Attributes;

namespace Numos.CoreSim.Benchmarks.Infrastructure;

/// <summary>
///     Supplies independent workload axes shared by the regular benchmarks.
/// </summary>
[MemoryDiagnoser]
public abstract class ScalingBenchmark
{
    /// <summary>
    ///     Gets or sets the number of awake, face-connected chunks.
    /// </summary>
    [ParamsSource(nameof(ChunkCounts))]
    public int ChunkCount { get; set; }

    /// <summary>
    ///     Gets or sets gas-filled active voxels per fixed 8×8×8 chunk; remaining voxels are solid.
    /// </summary>
    [ParamsSource(nameof(VoxelCounts))]
    public int ActiveVoxelsPerChunk { get; set; }

    /// <summary>
    ///     Gets or sets species with positive moles in every active voxel.
    /// </summary>
    [ParamsSource(nameof(GasCounts))]
    public int GasCount { get; set; }

    /// <summary>
    ///     Gets chunk counts, reduced to a connected pair when NUMOS_BENCHMARK_SMOKE=1.
    /// </summary>
    public IEnumerable<int> ChunkCounts => Smoke ? [2] : [1, 8, 32];

    /// <summary>
    ///     Gets active voxel counts for the occupancy sweep.
    /// </summary>
    public IEnumerable<int> VoxelCounts => Smoke ? [64] : [64, 256, 512];

    /// <summary>
    ///     Gets participating species counts for the gas sweep.
    /// </summary>
    public IEnumerable<int> GasCounts => Smoke ? [2] : [2, 8, 32];

    internal static bool Smoke => Environment.GetEnvironmentVariable("NUMOS_BENCHMARK_SMOKE") == "1";
}