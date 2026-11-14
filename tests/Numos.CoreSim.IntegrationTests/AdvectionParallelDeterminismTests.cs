using System.Runtime.InteropServices;
using Numos.API;
using Numos.Maths;
using Numos.Tests;

namespace Numos.CoreSim.IntegrationTests;

[TestFixture]
public sealed class AdvectionParallelDeterminismTests
{
    /// <summary>
    ///     The approved x64 digest for advection profile 2 after 0.05 s at 960 Hz (48 ticks), the default tick rate.
    ///     Equal to the 20 Hz baseline digest, because the legacy stages give the same state at both rates.
    /// </summary>
    private const ulong X64ExpectedDigest960 = 15048083309192399630UL;

    /// <summary>
    ///     Requires four runs of advection profile 2 to produce one digest, then checks it against the baseline.
    /// </summary>
    /// <remarks>
    ///     Regenerate the baseline only for a deliberate physics or digest-format change, and confirm the new digest is
    ///     stable across worker counts before committing it. It is a grid-only checkpoint digest.
    /// </remarks>
    [Test]
    public void Advection_ParallelPhasesMatchGoldenHash()
    {
        ulong? firstDigest = null;
        for (int run = 0; run < 4; run++)
        {
            using var simulation = CreateSimulation();

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
        "Verifies four parallel runs produce one deterministic digest at 960 Hz. x64 also checks the approved golden " +
        "digest; capture an Arm64 baseline before making its digest a golden value.")]
    public void Advection_ParallelPhasesMatchGoldenHashAt960Hz()
    {
        ulong? expectedDigest = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => X64ExpectedDigest960,
            Architecture.Arm64 => null,
            var architecture => throw new PlatformNotSupportedException(
                $"Advection profile 2 has no digest baseline for {architecture}.")
        };

        for (int run = 0; run < 4; run++)
        {
            using var simulation = CreateSimulation(960);

            TickHelpers.TickFor(simulation, 0.05);

            ulong actualDigest = simulation.CaptureCheckpoint().ComputeStateHash();
            TestContext.Out.WriteLine($"advection960 digest={actualDigest} (DOTNET_PROCESSOR_COUNT={Environment.ProcessorCount})");
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
        string[] gasNames = DeterminismScenarios.AddSteppedGases(config, 8, "ParallelGas", 0.002f, 15f);

        var simulation = SimTestHelpers.CreateLegacySimulation(config, 8, 8, 8, tickRate);
        simulation.World.Solvers.SetEnabled(AtmosBuiltInSolvers.Thermodynamics, false);
        simulation.World.Solvers.SetEnabled(AtmosBuiltInSolvers.GasReactions, false);

        for (int chunkIndex = 0; chunkIndex < 4; chunkIndex++)
        {
            var chunk = SimSetup.CreateOpenChunk(simulation, new Int3(chunkIndex * 2, 0, 0));
            for (int z = 0; z < 8; z++)
            for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                int voxelIndex = SimTestHelpers.Index(x, y, z, 8, 8);
                for (int gas = 0; gas < gasNames.Length; gas++)
                {
                    float moles = 0.05f * (1 + (voxelIndex * 17 + gas * 7 + chunkIndex * 3) % 11);
                    simulation.AddGasToVoxel(chunk, x, y, z, gasNames[gas], moles, 300f);
                }

                simulation.SetVoxelTemperature(
                    chunk,
                    x,
                    y,
                    z,
                    240f + (voxelIndex * 13 + chunkIndex * 19) % 121);
            }
        }

        return simulation;
    }
}