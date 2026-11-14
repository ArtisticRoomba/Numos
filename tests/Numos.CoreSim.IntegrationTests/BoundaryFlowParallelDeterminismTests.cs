using System.Runtime.InteropServices;
using Numos.API;
using Numos.Maths;
using Numos.Tests;

namespace Numos.CoreSim.IntegrationTests;

[TestFixture]
public sealed class BoundaryFlowParallelDeterminismTests
{
    /// <summary>
    ///     The approved x64 digest for this scenario after 0.15 s at 960 Hz (144 ticks), the default tick rate.
    ///     Equal to the 20 Hz baseline digest, because the legacy stages give the same state at both rates.
    /// </summary>
    private const ulong X64ExpectedDigest960 = 16781892038452400983UL;

    /// <summary>
    ///     Requires four runs of this scenario to produce one digest, then checks it against the baseline.
    /// </summary>
    /// <remarks>
    ///     The x64 digest came from the fully sequential <c>BoundaryFlowSolver</c> that predates <c>PendingTransferBuffer</c>.
    ///     The parallel solver reproduced it at both the default processor count and <c>DOTNET_PROCESSOR_COUNT=2</c>. Matching
    ///     it shows parallel boundary flow is bit-identical to the sequential algorithm, not merely stable across repeated
    ///     parallel runs. The value was re-captured as a grid-only
    ///     <see cref="Numos.CoreSim.Replay.AtmosSimulationCheckpoint" /> digest once the sequential-era digest had passed on
    ///     the same code. It was captured again for grid checkpoint formats 8, 9, and 10. Those formats hash state with
    ///     the wire encoding, name fields by hashed member names, and generate the checkpoint from live members. Neither
    ///     recapture changed the physics. The last one did: species diffusion started dividing by the voxel volume. That
    ///     value held at 1, 2, and 32 workers.
    /// </remarks>
    [Test]
    public void BoundaryFlow_ParallelPhasesMatchGoldenHash()
    {
        ulong? firstDigest = null;
        for (int run = 0; run < 4; run++)
        {
            using var simulation = CreateSimulation();

            for (int tick = 0; tick < 3; tick++)
                simulation.Tick();

            ulong digest = simulation.CaptureCheckpoint().ComputeStateHash();
            firstDigest ??= digest;

            Assert.That(
                digest,
                Is.EqualTo(firstDigest.Value),
                $"Run {run}; DOTNET_PROCESSOR_COUNT={Environment.ProcessorCount}");
        }

        Baseline.Verify(new BaselineValues().Digest("stateHash", firstDigest!.Value));
    }

    [Test]
    [Explicit(
        "Verifies four runs produce one deterministic digest at 960 Hz. x64 also checks the approved golden digest; " +
        "capture an Arm64 baseline before making its digest a golden value.")]
    public void BoundaryFlow_ParallelPhasesMatchGoldenHashAt960Hz()
    {
        ulong? expectedDigest = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => X64ExpectedDigest960,
            Architecture.Arm64 => null,
            var architecture => throw new PlatformNotSupportedException(
                $"Boundary flow parallel determinism has no digest baseline for {architecture}.")
        };

        for (int run = 0; run < 4; run++)
        {
            using var simulation = CreateSimulation(960);

            TickHelpers.TickFor(simulation, 0.15);

            ulong actualDigest = simulation.CaptureCheckpoint().ComputeStateHash();
            TestContext.Out.WriteLine($"boundary960 digest={actualDigest} (DOTNET_PROCESSOR_COUNT={Environment.ProcessorCount})");
            expectedDigest ??= actualDigest;

            Assert.That(
                actualDigest,
                Is.EqualTo(expectedDigest.Value),
                $"Run {run}; architecture={RuntimeInformation.ProcessArchitecture}; " +
                $"DOTNET_PROCESSOR_COUNT={Environment.ProcessorCount}");
        }
    }

    private static AtmosSimulation CreateSimulation(int tickRate = SimTestHelpers.PinnedTickRate)
    {
        var config = SimConfigs.CreateDeterministicConfig();
        string[] gasNames = DeterminismScenarios.AddSteppedGases(config, 4, "BoundaryGas", 0.003f, 12f);

        // A 3x3x3 chunk grid gives enough source-chunk batches to exceed the parallel-dispatch threshold on realistic worker
        // counts. Every chunk touches at least one face neighbor, so boundary flow has cross-chunk work on every tick.
        const int gridSize = 3;
        const int chunkExtent = 4;
        var simulation = SimTestHelpers.CreateLegacySimulation(config, chunkExtent, chunkExtent, chunkExtent, tickRate);

        for (int cz = 0; cz < gridSize; cz++)
        for (int cy = 0; cy < gridSize; cy++)
        for (int cx = 0; cx < gridSize; cx++)
        {
            int chunkOrdinal = cx + cy * gridSize + cz * gridSize * gridSize;
            var chunk = SimSetup.CreateOpenChunk(simulation, new Int3(cx, cy, cz));

            for (int z = 0; z < chunkExtent; z++)
            for (int y = 0; y < chunkExtent; y++)
            for (int x = 0; x < chunkExtent; x++)
            {
                int voxelIndex = SimTestHelpers.Index(x, y, z, chunkExtent, chunkExtent);
                for (int gas = 0; gas < gasNames.Length; gas++)
                {
                    float moles = 0.05f * (1 + (voxelIndex * 17 + gas * 7 + chunkOrdinal * 5) % 13);
                    simulation.AddGasToVoxel(chunk, x, y, z, gasNames[gas], moles, 300f);
                }

                simulation.SetVoxelTemperature(
                    chunk,
                    x,
                    y,
                    z,
                    230f + (voxelIndex * 11 + chunkOrdinal * 23) % 141);
            }

            // Sleep every third chunk so some ticks exercise the deferred neighbor-wake merge path.
            if (chunkOrdinal % 3 == 0)
                simulation.SleepChunk(chunk);
        }

        return simulation;
    }
}