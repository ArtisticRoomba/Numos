using System.Runtime.InteropServices;
using Numos.API;
using Numos.Maths;

namespace Numos.CoreSim.IntegrationTests;

[TestFixture]
public sealed class AdvectionParallelDeterminismTests
{
    private const ulong Arm64ExpectedDigest = 15643557825930699840UL;
    private const ulong X64ExpectedDigest = 5182992794561060750UL;

    [Test]
    [Explicit(
        "Only intended to verify that the advection solver matches behavior between parallel changes. " +
        "This can be locked down and hashed when we're ready to stop changing the sim.")]
    public void Advection_ParallelPhasesMatchGoldenHash()
    {
        ulong expectedDigest = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => Arm64ExpectedDigest,
            Architecture.X64 => X64ExpectedDigest,
            var architecture => throw new PlatformNotSupportedException($"Advection profile 2 has no golden digest for {architecture}.")
        };

        for (int run = 0; run < 4; run++)
        {
            using var simulation = CreateSimulation();

            simulation.Tick();

            Assert.That(
                simulation.ComputeStateHash().Digest,
                Is.EqualTo(expectedDigest),
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
        simulation.Solvers.SetEnabled(AtmosBuiltInSolvers.BoundaryFlow, false);
        simulation.Solvers.SetEnabled(AtmosBuiltInSolvers.Thermodynamics, false);
        simulation.Solvers.SetEnabled(AtmosBuiltInSolvers.ThermalBoundary, false);
        simulation.Solvers.SetEnabled(AtmosBuiltInSolvers.GasReactions, false);

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