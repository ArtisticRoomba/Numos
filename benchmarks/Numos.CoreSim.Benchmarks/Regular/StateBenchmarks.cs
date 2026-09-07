using BenchmarkDotNet.Attributes;
using Numos.CoreSim.Benchmarks.Infrastructure;
using Numos.CoreSim.Replay;

namespace Numos.CoreSim.Benchmarks.Regular;

/// <summary>
///     Measures authoritative state capture, hashing, restoration, and grid mutation costs.
/// </summary>
[BenchmarkCategory("Regular", "State")]
public class StateBenchmarks : ScalingBenchmark
{
    private bool _alternateRoom;
    private SimulationWorkload _workload = null!;

    /// <summary>
    ///     Populates the state used by all operations.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        _workload = new SimulationWorkload(ChunkCount, ActiveVoxelsPerChunk, GasCount);
    }

    /// <summary>
    ///     Captures authoritative state, including every allocated gas channel.
    /// </summary>
    /// <returns>The captured checkpoint, kept observable by BenchmarkDotNet.</returns>
    [Benchmark]
    public AtmosSimulationCheckpoint Capture()
    {
        return _workload.Kernel.CaptureCheckpoint();
    }

    /// <summary>
    ///     Hashes authoritative state in canonical order.
    /// </summary>
    /// <returns>The computed state hash.</returns>
    [Benchmark]
    public AtmosStateHash Hash()
    {
        return _workload.Kernel.ComputeStateHash();
    }

    /// <summary>
    ///     Measures validation, state installation, and derived-state rebuilding.
    /// </summary>
    [Benchmark]
    public void Restore()
    {
        _workload.Kernel.RestoreCheckpoint(_workload.Initial);
    }

    /// <summary>
    ///     Injects gas through the kernel operation path into every active voxel.
    /// </summary>
    [Benchmark]
    public void InjectGas()
    {
        foreach (var chunk in _workload.Chunks)
            for (int active = 0; active < chunk.ActiveAirCount; active++)
                _workload.Kernel.AddGasToVoxel(chunk.GridPosition, chunk.ActiveAirIndices[active], 0, 0.1f, 300f);
    }

    /// <summary>
    ///     Reclassifies every chunk between two gas-bearing rooms through the kernel operation path.
    /// </summary>
    [Benchmark]
    public void ReclassifyChunks()
    {
        _alternateRoom = !_alternateRoom;
        int roomId = _alternateRoom ? 0 : 1;
        foreach (var chunk in _workload.Chunks)
            _workload.Kernel.SetChunkClassification(chunk.GridPosition, roomId);
    }

    /// <summary>
    ///     Releases fixture resources.
    /// </summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _workload.Dispose();
    }
}