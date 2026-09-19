using System.Runtime.InteropServices;
using Numos.API;
using Numos.Maths;

namespace Numos.CoreSim.IntegrationTests;

[TestFixture]
public sealed class BoundaryFlowParallelDeterminismTests
{
    private const ulong X64ExpectedDigest = 15020874777508700793UL;

    /// <summary>
    ///     The golden digest above was captured once by diffing this exact scenario's
    ///     <c>ComputeStateHash()</c> against the pre-refactor sequential <c>BoundaryFlowSolver</c> (the version at
    ///     the commit that introduced <c>PendingTransferBuffer</c>) at both the default processor count and with
    ///     <c>DOTNET_PROCESSOR_COUNT=2</c>. All three runs produced this identical digest, which is the intended
    ///     property of the refactor: parallel boundary-flow computation is bit-identical to the sequential
    ///     algorithm it replaced, not merely stable across repeated parallel runs.
    /// </summary>
    [Test]
    [Explicit(
        "Verifies four runs at the current processor count produce one deterministic digest that also matches the " +
        "pre-parallelization golden value on x64. Capture an Arm64 baseline before making its digest a golden value.")]
    public void BoundaryFlow_ParallelPhasesMatchGoldenHash()
    {
        ulong? expectedDigest = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => X64ExpectedDigest,
            Architecture.Arm64 => null,
            var architecture => throw new PlatformNotSupportedException(
                $"Boundary flow parallel determinism has no digest baseline for {architecture}.")
        };

        for (int run = 0; run < 4; run++)
        {
            using var simulation = CreateSimulation();

            for (int tick = 0; tick < 3; tick++)
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
        string[] gasNames = new string[4];
        gasNames[0] = SimTestHelpers.FirstGasName;
        gasNames[1] = SimTestHelpers.SecondGasName;

        for (int gas = 0; gas < gasNames.Length; gas++)
        {
            gasNames[gas] ??= $"BoundaryGas{gas}";
            if (gas >= config.GasRegistry.Count)
                config.GasRegistry.Add(TestGases.Create(gasNames[gas], 0.01f + gas * 0.003f));

            var properties = config.GasRegistry[gas];
            properties.DiffusionCoefficient = 0.01f + gas * 0.003f;
            properties.MolarHeatCapacityAtConstantVolume = 12f + gas;
            config.GasRegistry.Replace(gas, properties);
        }

        // A 3x3x3 chunk grid guarantees enough source-chunk batches to exceed the parallel-dispatch
        // threshold on realistic worker counts, and every chunk touches at least one face neighbor so
        // boundary flow has cross-chunk work to compute on every tick.
        const int gridSize = 3;
        const int chunkExtent = 4;
        var simulation = new AtmosSimulation(config, chunkExtent, chunkExtent, chunkExtent);

        for (int cz = 0; cz < gridSize; cz++)
        for (int cy = 0; cy < gridSize; cy++)
        for (int cx = 0; cx < gridSize; cx++)
        {
            int chunkOrdinal = cx + cy * gridSize + cz * gridSize * gridSize;
            var chunk = SimTestHelpers.CreateOpenChunk(simulation, new Int3(cx, cy, cz));

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
