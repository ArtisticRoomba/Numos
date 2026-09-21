using Numos.CoreSim.Datatypes.Events;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Solvers;
using Numos.Maths;

namespace Numos.CoreSim.Tests;

public class ThermalDiffusionSolverTests
{
    /// <summary>
    ///     <see cref="ThermalDiffusionSolver.Solve" /> picks between a parallel per-voxel gather and a
    ///     fully sequential scatter based on <c>awakeChunkCount</c>. Runs the same 4x4x4 block of voxels
    ///     -- temperatures and heat capacities spanning nine orders of magnitude in every direction, plus
    ///     one interior solid voxel that blocks its neighbors -- on each branch for several ticks and
    ///     requires the two chunks to end up bit-identical on the float32 state that is actually persisted
    ///     (<see cref="AtmosChunk.Temperature" />/<see cref="AtmosChunk.TotalPressure" />), since the
    ///     gather's neighbor visitation order exists specifically to reproduce the scatter form's
    ///     floating-point summation order.
    /// </summary>
    [Test]
    public void Solve_ParallelGatherMatchesSequentialScatter()
    {
        // A huge global cap keeps every edge's conductance at its own heat-capacity-derived
        // equilibrium value (AtmosSolverMath.CalculateThermalConductance's MathF.Min) instead of
        // collapsing every edge to the same constant, which would make summation order irrelevant.
        var config = new TestAtmosConfig { ThermalConductance = 1e9f }.CreateSnapshot();
        var tickConfig = new AtmosSolverConfigSnapshot();
        tickConfig.Capture(config);

        // Generated once and replayed into both chunks -- calling BuildChunk twice against a shared
        // Random would otherwise give each chunk different starting conditions.
        var random = new Random(20260920);
        var initialTemperatures = new float[64];
        var initialMoles = new float[64];
        for (int i = 0; i < 64; i++)
        {
            // Wide, irregular magnitudes in both temperature and moles (heat capacity) are what
            // actually expose a summation-order bug: values differing by orders of magnitude round
            // differently depending on which term is added first.
            initialTemperatures[i] = MathF.Pow(10f, -2f + random.NextSingle() * 9f);
            initialMoles[i] = MathF.Pow(10f, -6f + random.NextSingle() * 9f);
        }

        AtmosChunk BuildChunk()
        {
            var chunk = new AtmosChunk();
            for (int x = 0; x < 4; x++)
            for (int y = 0; y < 4; y++)
            for (int z = 0; z < 4; z++)
            {
                ushort index = chunk.GetIndex(new Int3(x, y, z));
                int flatIndex = x * 16 + y * 4 + z;
                chunk.InjectGasToVoxel(index, 0, initialMoles[flatIndex], initialTemperatures[flatIndex], 20f, 1f);
            }

            // An interior solid voxel blocks conductance/flux from every direction that reaches it,
            // exercising the same neighbor-validity short-circuit from both sides of the gather.
            chunk.VoxelRoomMap[chunk.GetIndex(new Int3(2, 1, 1))] = VoxelClassification.RoomSolid;
            chunk.RebuildActiveAirIndices();
            return chunk;
        }

        var solver = new ThermalDiffusionSolver();
        var sequentialChunk = BuildChunk();
        var parallelChunk = BuildChunk();
        float initialCornerTemperature = sequentialChunk.Temperature[sequentialChunk.GetIndex(new Int3(0, 0, 0))];
        var sequentialBoundaryBuffer = new ThermalBoundaryEvent[sequentialChunk.VoxelCount];
        var parallelBoundaryBuffer = new ThermalBoundaryEvent[parallelChunk.VoxelCount];

        int sequentialBoundaryCount = 0;
        int parallelBoundaryCount = 0;
        for (int tick = 0; tick < 20; tick++)
        {
            // awakeChunkCount >= worker count always takes the sequential branch; 1 always takes the
            // parallel one (a real chunk count can never be below 1), regardless of the machine's core count.
            sequentialBoundaryCount = solver.Solve(sequentialChunk, tickConfig, sequentialBoundaryBuffer, awakeChunkCount: int.MaxValue);
            parallelBoundaryCount = solver.Solve(parallelChunk, tickConfig, parallelBoundaryBuffer, awakeChunkCount: 1);
        }

        // Guards against a vacuous pass: this scenario must actually produce boundary events and
        // move heat, or the bit-exact comparisons below would trivially match on unchanged state.
        Assert.That(sequentialBoundaryCount, Is.GreaterThan(0));
        Assert.That(
            sequentialChunk.Temperature[sequentialChunk.GetIndex(new Int3(0, 0, 0))],
            Is.Not.EqualTo(initialCornerTemperature));

        Assert.That(parallelBoundaryCount, Is.EqualTo(sequentialBoundaryCount));

        var sequentialBoundaryVoxels = sequentialBoundaryBuffer
            .Take(sequentialBoundaryCount)
            .Select(e => e.LocalVoxelIndex)
            .OrderBy(v => v)
            .ToArray();

        var parallelBoundaryVoxels = parallelBoundaryBuffer
            .Take(parallelBoundaryCount)
            .Select(e => e.LocalVoxelIndex)
            .OrderBy(v => v)
            .ToArray();

        Assert.That(parallelBoundaryVoxels, Is.EqualTo(sequentialBoundaryVoxels));

        for (ushort i = 0; i < sequentialChunk.VoxelCount; i++)
        {
            Assert.That(parallelChunk.Temperature[i], Is.EqualTo(sequentialChunk.Temperature[i]), $"voxel {i} temperature");
            Assert.That(parallelChunk.TotalPressure[i], Is.EqualTo(sequentialChunk.TotalPressure[i]), $"voxel {i} pressure");
        }
    }
}
