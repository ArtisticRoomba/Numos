using Numos.API;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Solvers;
using Numos.CoreSim.Solvers.Euler;
using Numos.Maths;
using Numos.Tests;

namespace Numos.CoreSim.IntegrationTests.Euler;

/// <summary>
///     The Euler stage's two sweep schedules (whole chunk per worker, pencil tiles) and any worker count produce
///     the same bits.
/// </summary>
/// <remarks>
///     <see cref="ParallelPhase.WorkerCountOverride" /> picks the schedule: 1 makes every active set at least as
///     large as the pool, so chunks go whole and every phase runs on the calling thread. A count far above the chunk
///     count forces tiles. Without the override the worker count is whatever <c>DOTNET_PROCESSOR_COUNT</c> says, so
///     running this fixture at 1 and at the default covers the threading side.
/// </remarks>
[TestFixture]
[NonParallelizable]
public sealed class EulerParallelDeterminismTests
{
    private const int ChunkSize = 16;
    private const float Temperature = 293f;
    private const float Atmosphere = AtmosPhysicalConstants.StandardAtmosphericPressure;

    [TestCase("sod")]
    [TestCase("blast")]
    [TestCase("walls")]
    public void Schedules_ProduceOneDigest(string profile)
    {
        ulong whole = Run(profile, workerCount: 1);
        ulong tiled = Run(profile, workerCount: 1 << 16);
        ulong pool = Run(profile, workerCount: null);
        TestContext.Out.WriteLine($"{profile}: whole={whole} tiled={tiled} pool={pool} (DOTNET_PROCESSOR_COUNT={Environment.ProcessorCount})");
        Assert.Multiple(() =>
        {
            Assert.That(tiled, Is.EqualTo(whole), "tiled schedule differs from whole-chunk schedule");
            Assert.That(pool, Is.EqualTo(whole), "pool-sized schedule differs from whole-chunk schedule");
        });
    }

    [Test]
    public void Blast_ParallelRunsMatchGoldenHash()
    {
        ulong? firstDigest = null;
        for (int run = 0; run < 4; run++)
        {
            ulong digest = Run("blast", workerCount: null);
            firstDigest ??= digest;
            Assert.That(
                digest,
                Is.EqualTo(firstDigest.Value),
                $"Run {run}; DOTNET_PROCESSOR_COUNT={Environment.ProcessorCount}; digest={digest}");
        }

        Baseline.Verify(new BaselineValues().Digest("stateHash", firstDigest!.Value));
    }

    [Test]
    public void Blast960_ParallelRunsMatchGoldenHash()
    {
        ulong? firstDigest = null;
        for (int run = 0; run < 4; run++)
        {
            ulong digest = Run("blast", workerCount: null, tickRate: 960);
            firstDigest ??= digest;
            Assert.That(
                digest,
                Is.EqualTo(firstDigest.Value),
                $"Run {run}; DOTNET_PROCESSOR_COUNT={Environment.ProcessorCount}; digest={digest}");
        }

        Baseline.Verify(new BaselineValues().Digest("stateHash", firstDigest!.Value));
    }

    private static ulong Run(string profile, int? workerCount, int tickRate = SimTestHelpers.PinnedTickRate)
    {
        ParallelPhase.WorkerCountOverride = workerCount;
        try
        {
            using AtmosSimulation simulation = profile switch
            {
                "sod" => CreateTwoChunkSod(),
                "walls" => CreateBlast(tickRate, walls: true),
                _ => CreateBlast(tickRate)
            };
            if (profile == "sod")
            {
                for (int tick = 0; tick < 5; tick++)
                    simulation.Tick();
            }
            else
            {
                // Two ticks at 20 Hz, 0.1 s at any rate.
                TickHelpers.TickFor(simulation, 0.1);
            }

            return simulation.CaptureCheckpoint().ComputeStateHash();
        }
        finally
        {
            ParallelPhase.WorkerCountOverride = null;
        }
    }

    private static AtmosSimulation CreateTwoChunkSod()
    {
        const int half = 50;
        var config = SimTestHelpers.CreateEulerConfig();
        var simulation = SimTestHelpers.CreateEulerSimulation(config, half, 1, 1, out var left);
        var right = SimTestHelpers.CreateOpenChunk(simulation, Int3.PosX);
        float leftTemperature = 1e4f / AtmosPhysicalConstants.MolarGasConstant;
        float rightTemperature = 1e3f / (0.125f * AtmosPhysicalConstants.MolarGasConstant);
        for (int i = 0; i < 2 * half; i++)
        {
            bool high = i < half - 1;
            simulation.AddGasToVoxel(
                i < half ? left : right, i % half, 0, 0, SimTestHelpers.EulerUnitGasName,
                high ? 1f : 0.125f, high ? leftTemperature : rightTemperature);
        }

        return simulation;
    }

    /// <summary>
    ///     Eight 16³ chunks of room air in a 2×2×2 block, with a hot high-pressure sphere centred on the corner
    ///     they all share, so every chunk face carries the blast.
    /// </summary>
    /// <remarks>
    ///     With <paramref name="walls" /> each chunk also gets a solid column and scattered solid voxels, so the
    ///     active-air list has gaps inside rows and the tiled gather and write-back cut it between runs.
    /// </remarks>
    private static AtmosSimulation CreateBlast(int tickRate = SimTestHelpers.PinnedTickRate, bool walls = false)
    {
        var config = SimTestHelpers.CreateEulerConfig();
        var simulation = SimTestHelpers.CreateEulerSimulation(config, ChunkSize, ChunkSize, ChunkSize, out _, tickRate: tickRate);
        float ambient = SimTestHelpers.MolesFor(config, Atmosphere, Temperature);
        float hot = SimTestHelpers.MolesFor(config, 4f * Atmosphere, 600f);
        const float radius = 4f;
        for (int cz = 0; cz < 2; cz++)
        for (int cy = 0; cy < 2; cy++)
        for (int cx = 0; cx < 2; cx++)
        {
            var position = new Int3(cx, cy, cz);
            var chunk = position == default
                ? simulation.GetChunkHandles()[0]
                : SimTestHelpers.CreateOpenChunk(simulation, position);

            for (int z = 0; z < ChunkSize; z++)
            for (int y = 0; y < ChunkSize; y++)
            for (int x = 0; x < ChunkSize; x++)
            {
                if (walls && (x == 5 && y < 12 || (x * 7 + y * 3 + z * 5) % 23 == 0))
                {
                    simulation.SetVoxelClassification(chunk, x, y, z, VoxelClassification.RoomSolid);
                    continue;
                }

                float dx = cx * ChunkSize + x + 0.5f - ChunkSize;
                float dy = cy * ChunkSize + y + 0.5f - ChunkSize;
                float dz = cz * ChunkSize + z + 0.5f - ChunkSize;
                bool inside = dx * dx + dy * dy + dz * dz < radius * radius;
                simulation.AddGasToVoxel(
                    chunk, x, y, z, SimTestHelpers.EulerAirGasName,
                    inside ? hot : ambient, inside ? 600f : Temperature);
            }
        }

        return simulation;
    }
}
