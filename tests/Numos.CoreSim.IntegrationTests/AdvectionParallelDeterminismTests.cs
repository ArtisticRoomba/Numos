using System.Runtime.InteropServices;
using Numos.API;
using Numos.Maths;

namespace Numos.CoreSim.IntegrationTests;

[TestFixture]
public sealed class AdvectionParallelDeterminismTests
{
    /// <summary>
    ///     The approved x64 digest for advection profile 2, re-captured against the current solver.
    /// </summary>
    /// <remarks>
    ///     The previous value dated from before several physical-model changes and no longer matched
    ///     anything the solver produced. Update this only together with a deliberate physics change,
    ///     and confirm the new value is stable across worker counts before approving it.
    /// </remarks>
    private const ulong X64ExpectedDigest = 7544779294751503140UL;

    [Test]
    [Explicit(
        "Verifies four parallel runs produce one deterministic digest. x64 also checks the approved golden digest; " +
        "capture an Arm64 baseline before making its digest a golden value.")]
    public void Advection_ParallelPhasesMatchGoldenHash()
    {
        ulong? expectedDigest = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => X64ExpectedDigest,
            Architecture.Arm64 => null,
            var architecture => throw new PlatformNotSupportedException(
                $"Advection profile 2 has no digest baseline for {architecture}.")
        };

        for (int run = 0; run < 4; run++)
        {
            using var simulation = CreateSimulation();

            simulation.Tick();

            ulong actualDigest = simulation.ComputeStateHash().Digest;
            expectedDigest ??= actualDigest;

            Assert.That(
                actualDigest,
                Is.EqualTo(expectedDigest.Value),
                $"Run {run}; architecture={RuntimeInformation.ProcessArchitecture}; " +
                $"DOTNET_PROCESSOR_COUNT={Environment.ProcessorCount}");
        }
    }

    private static AtmosSimulation CreateSimulation()
    {
        var config = SimTestHelpers.CreateDeterministicConfig();
        string[] gasNames = new string[8];
        gasNames[0] = SimTestHelpers.FirstGasName;
        gasNames[1] = SimTestHelpers.SecondGasName;

        for (int gas = 0; gas < gasNames.Length; gas++)
        {
            gasNames[gas] ??= $"ParallelGas{gas}";
            if (gas >= config.GasRegistry.Count)
                config.GasRegistry.Add(TestGases.Create(gasNames[gas], 0.01f + gas * 0.002f));

            var properties = config.GasRegistry[gas];
            properties.DiffusionCoefficient = 0.01f + gas * 0.002f;
            properties.MolarHeatCapacityAtConstantVolume = 15f + gas;
            config.GasRegistry.Replace(gas, properties);
        }

        var simulation = new AtmosSimulation(config, 8, 8, 8);
        simulation.World.Solvers.SetEnabled(AtmosBuiltInSolvers.Thermodynamics, false);
        simulation.World.Solvers.SetEnabled(AtmosBuiltInSolvers.GasReactions, false);

        for (int chunkIndex = 0; chunkIndex < 4; chunkIndex++)
        {
            var chunk = SimTestHelpers.CreateOpenChunk(simulation, new Int3(chunkIndex * 2, 0, 0));
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