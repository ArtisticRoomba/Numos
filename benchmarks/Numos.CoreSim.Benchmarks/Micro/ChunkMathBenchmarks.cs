using BenchmarkDotNet.Attributes;
using Numos.CoreSim.Benchmarks.Infrastructure;
using Numos.CoreSim.Solvers;

namespace Numos.CoreSim.Benchmarks.Micro;

/// <summary>
///     Compares scalar pressure and heat-capacity refresh with the production SoA implementation.
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory("Micro")]
public class ChunkMathBenchmarks
{
    private SimulationWorkload _workload = null!;

    /// <summary>
    ///     Gets or sets the number of contiguous active voxels in the chunk.
    /// </summary>
    [Params(64, 512)]
    public int ActiveVoxels { get; set; }

    /// <summary>
    ///     Gets or sets the number of gas channels accumulated at each voxel.
    /// </summary>
    [Params(2, 8, 32)]
    public int GasCount { get; set; }

    /// <summary>
    ///     Creates stable inputs and checks exact numerical agreement before timing.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        _workload = new SimulationWorkload(1, ActiveVoxels, GasCount);
        var chunk = _workload.Chunks[0];
        Scalar();
        float[] pressure = chunk.TotalPressure.AsSpan().ToArray();
        float[] heatCapacity = chunk.TotalHeatCapacity.AsSpan().ToArray();
        Vectorized();
        for (int voxel = 0; voxel < chunk.VoxelCount; voxel++)
        {
            if (BitConverter.SingleToInt32Bits(pressure[voxel]) != BitConverter.SingleToInt32Bits(chunk.TotalPressure[voxel]) ||
                BitConverter.SingleToInt32Bits(heatCapacity[voxel]) != BitConverter.SingleToInt32Bits(chunk.TotalHeatCapacity[voxel]))
                throw new InvalidOperationException("Chunk math must preserve the scalar result exactly.");
        }
    }

    /// <summary>
    ///     Runs the original scalar traversal over active voxels and gas channels.
    /// </summary>
    [Benchmark(Baseline = true)]
    public void Scalar()
    {
        var chunk = _workload.Chunks[0];
        var config = _workload.Config;
        chunk.TotalPressure.Clear();
        chunk.TotalHeatCapacity.Clear();
        for (int activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
        {
            ushort voxel = chunk.ActiveAirIndices[activeIndex];
            chunk.TotalPressure[voxel] = AtmosSolverMath.CalculatePressureAtVoxel(config, chunk, voxel);
        }

        for (int gas = 0; gas < chunk.ActiveGasCount; gas++)
        {
            float molarHeatCapacity = config.GetMolarHeatCapacityAtConstantVolume(chunk.ActiveGases[gas].GasId);
            for (int activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
            {
                ushort voxel = chunk.ActiveAirIndices[activeIndex];
                float moles = chunk.ActiveGases[gas].Moles[voxel];
                if (moles > 0f)
                    chunk.TotalHeatCapacity[voxel] += molarHeatCapacity * moles;
            }
        }
    }

    /// <summary>
    ///     Runs production refresh, including run detection and pooled scratch storage.
    /// </summary>
    [Benchmark]
    public void Vectorized()
    {
        AdvectionSolver.RefreshPressureAndHeatCapacity(_workload.Chunks[0], _workload.Config);
    }

    /// <summary>
    ///     Releases the simulation and pooled chunk storage after timing.
    /// </summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _workload.Dispose();
    }
}