using Numos.CoreSim.Datatypes.Events;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Solvers;
using Numos.Maths;

namespace Numos.CoreSim.Tests;

public class ThermalDiffusionSolverTests
{
    /// <summary>
    ///     <see cref="ThermalDiffusionSolver.Solve" /> picks between a parallel per-voxel gather and a fully sequential
    ///     scatter, based on <c>awakeChunkCount</c>. This test runs the same 4x4x4 block of voxels on each branch for several
    ///     ticks. Temperatures and heat capacities span nine orders of magnitude in every direction, and one interior solid
    ///     voxel blocks its neighbors. The two chunks must end bit-identical on the persisted float32 state
    ///     (<see cref="AtmosChunk.Temperature" />/<see cref="AtmosChunk.TotalPressure" />). The gather's neighbor visitation
    ///     order exists to reproduce the scatter form's floating-point summation order.
    /// </summary>
    [Test]
    public void Solve_ParallelGatherMatchesSequentialScatter()
    {
        // A huge global cap keeps every edge's conductance at its own heat-capacity-derived equilibrium value
        // (AtmosSolverMath.CalculateThermalConductance's MathF.Min). Otherwise every edge collapses to the same constant,
        // and summation order stops mattering.
        var config = new TestAtmosConfig { ThermalConductance = 1e9f }.CreateSnapshot();
        var tickConfig = new AtmosSolverConfigSnapshot();
        tickConfig.Capture(config);

        // Generated once and replayed into both chunks. Calling BuildChunk twice against a shared Random would give each
        // chunk different starting conditions.
        var random = new Random(20260920);
        var initialTemperatures = new float[64];
        var initialMoles = new float[64];
        for (int i = 0; i < 64; i++)
        {
            // Wide, irregular magnitudes in temperature and moles (heat capacity) expose a summation-order bug. Values that
            // differ by orders of magnitude round differently depending on which term is added first.
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

            // An interior solid voxel blocks conductance and flux from every direction. This exercises the neighbor-validity
            // short-circuit from both sides of the gather.
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
            // awakeChunkCount >= worker count always takes the sequential branch. A count of 1 always takes the parallel branch,
            // because a real chunk count is never below 1. Neither depends on the machine's core count.
            sequentialBoundaryCount = solver.Solve(sequentialChunk, tickConfig, sequentialBoundaryBuffer, awakeChunkCount: int.MaxValue);
            parallelBoundaryCount = solver.Solve(parallelChunk, tickConfig, parallelBoundaryBuffer, awakeChunkCount: 1);
        }

        // Guards against a vacuous pass. The scenario must produce boundary events and move heat, or the bit-exact
        // comparisons below would match on unchanged state.
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

    private static AtmosChunk BuildUniformChunk(float temperature, bool solidVoxel)
    {
        var chunk = new AtmosChunk();
        for (ushort index = 0; index < chunk.VoxelCount; index++)
            chunk.InjectGasToVoxel(index, 0, 1f + index % 5, temperature, 20f, 1f);

        if (solidVoxel)
        {
            var solid = chunk.GetIndex(new Int3(2, 1, 1));
            chunk.VoxelRoomMap[solid] = VoxelClassification.RoomSolid;
            chunk.Temperature[solid] = temperature * 3f;
            chunk.RebuildActiveAirIndices();
        }

        return chunk;
    }

    /// <summary>
    ///     A chunk whose valid voxels share one temperature must come out unchanged and must still report every boundary
    ///     voxel. The solid case has a stored temperature that differs, and it leaves the other voxels as they were.
    /// </summary>
    [TestCase(true, 1)]
    [TestCase(false, 1)]
    [TestCase(true, int.MaxValue)]
    [TestCase(false, int.MaxValue)]
    public void Solve_UniformTemperature_LeavesChunkUnchangedAndEmitsBoundaries(bool solidVoxel, int awakeChunkCount)
    {
        var config = new TestAtmosConfig { ThermalConductance = 0.5f }.CreateSnapshot();
        var tickConfig = new AtmosSolverConfigSnapshot();
        tickConfig.Capture(config);
        var chunk = BuildUniformChunk(300f, solidVoxel);
        var temperature = chunk.Temperature.AsSpan().ToArray();
        var pressure = chunk.TotalPressure.AsSpan().ToArray();
        var buffer = new ThermalBoundaryEvent[chunk.VoxelCount];

        int count = new ThermalDiffusionSolver().Solve(chunk, tickConfig, buffer, awakeChunkCount);

        int expectedBoundaryCount = 0;
        for (ushort index = 0; index < chunk.VoxelCount; index++)
        {
            var p = chunk.GetXyzInt3(index);
            bool onFace = p.X == 0 || p.X == chunk.Width - 1 || p.Y == 0 || p.Y == chunk.Height - 1 ||
                          p.Z == 0 || p.Z == chunk.Depth - 1;
            if (onFace && chunk.VoxelRoomMap[index] != VoxelClassification.RoomSolid)
                expectedBoundaryCount++;
        }

        Assert.That(expectedBoundaryCount, Is.GreaterThan(0));
        Assert.That(count, Is.EqualTo(expectedBoundaryCount));
        Assert.That(chunk.Temperature.AsSpan().ToArray(), Is.EqualTo(temperature));
        Assert.That(chunk.TotalPressure.AsSpan().ToArray(), Is.EqualTo(pressure));
    }

    /// <summary>
    ///     One warmer voxel is enough to defeat the skip, in the full-air span scan and in the active-list scan.
    /// </summary>
    [TestCase(true, 1)]
    [TestCase(false, 1)]
    [TestCase(true, int.MaxValue)]
    [TestCase(false, int.MaxValue)]
    public void Solve_OneWarmerVoxel_StillDiffuses(bool solidVoxel, int awakeChunkCount)
    {
        var config = new TestAtmosConfig { ThermalConductance = 0.5f }.CreateSnapshot();
        var tickConfig = new AtmosSolverConfigSnapshot();
        tickConfig.Capture(config);
        var chunk = BuildUniformChunk(300f, solidVoxel);
        var warm = chunk.GetIndex(new Int3(0, 0, 0));
        chunk.Temperature[warm] = 400f;
        var neighbor = chunk.GetIndex(new Int3(1, 0, 0));
        var buffer = new ThermalBoundaryEvent[chunk.VoxelCount];

        new ThermalDiffusionSolver().Solve(chunk, tickConfig, buffer, awakeChunkCount);

        Assert.That(chunk.Temperature[warm], Is.LessThan(400f));
        Assert.That(chunk.Temperature[neighbor], Is.GreaterThan(300f));
    }
}
