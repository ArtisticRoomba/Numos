using Numos.API;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.EulerFlow;
using Numos.CoreSim.Solvers.Euler;
using Numos.Maths;

namespace Numos.CoreSim.IntegrationTests.Euler;

/// <summary>
///     Static chunks that no source can reach skip the sweeps. The skip must change no bit anywhere.
/// </summary>
[TestFixture]
public sealed class EulerStaticChunkTests
{
    private const int ChunkSize = 4;
    private const int LineLength = 70;
    private const int HotStart = 30;
    private const int Ticks = 12;
    private const float Temperature = 293f;
    private const float HotTemperature = 400f;
    private const float Atmosphere = AtmosPhysicalConstants.StandardAtmosphericPressure;

    [TearDown]
    public void RestoreSwitches()
    {
        EulerFlowSolver.SkipStaticChunks = true;
        EulerFlowSolver.WorkerCountOverride = null;
    }

    [Test]
    public void UniformGrid_IsNotSweptLeftBitwiseAloneAndFallsAsleepOnSchedule()
    {
        const int chunkCount = 3;
        var config = CreateConfig(48);
        config.SleepThreshold = 5;
        config.SleepEpsilon = 3.5f;
        using var simulation = SimTestHelpers.CreateEulerSimulation(config, ChunkSize, ChunkSize, ChunkSize, out var first);
        var chunks = new List<AtmosChunkHandle> { first };
        for (int index = 1; index < chunkCount; index++)
            chunks.Add(SimTestHelpers.CreateOpenChunk(simulation, index * Int3.PosX));

        float moles = SimTestHelpers.MolesFor(config, Atmosphere, Temperature);
        foreach (var chunk in chunks)
            Fill(simulation, chunk, moles, 0f, Temperature);

        ulong before = GasDigest(simulation, chunks);
        simulation.Tick();
        Assert.Multiple(() =>
        {
            Assert.That(simulation.LastFlowDiagnostics.Substeps, Is.Zero, "no chunk is swept");
            Assert.That(GasDigest(simulation, chunks), Is.EqualTo(before), "gas untouched");
            Assert.That(chunks.All(chunk => simulation.GetChunkSnapshot(chunk).IsAwake), Is.True, "awake after one tick");
        });

        for (int tick = 2; tick <= config.SleepThreshold; tick++)
            simulation.Tick();

        Assert.That(chunks.All(chunk => simulation.GetChunkSnapshot(chunk).IsAwake), Is.True, "awake at the threshold");
        simulation.Tick();
        Assert.Multiple(() =>
        {
            Assert.That(chunks.All(chunk => !simulation.GetChunkSnapshot(chunk).IsAwake), Is.True, "asleep after the threshold");
            Assert.That(GasDigest(simulation, chunks), Is.EqualTo(before), "gas untouched");
        });
    }

    /// <summary>
    ///     A pulse at one end of a long line and a hotter, faster sea at the other. The chunks far from both are
    ///     skipped. The hot sea has the highest sound speed, so the shared time step shows whether skipped chunks
    ///     still count. Diffusion is on and chunks sleep during the run, so the hash covers both.
    /// </summary>
    [TestCase(1)]
    [TestCase(1 << 16)]
    [TestCase(null)]
    public void SkippingStaticChunks_ChangesNoBitInAnyTick(int? workerCount)
    {
        EulerFlowSolver.WorkerCountOverride = workerCount;
        using var skipping = CreateLine();
        using var sweeping = CreateLine();
        int skippedTotal = 0;
        for (int tick = 1; tick <= Ticks; tick++)
        {
            EulerFlowSolver.SkipStaticChunks = true;
            skipping.Tick();
            skippedTotal += EulerFlowSolver.LastSkippedCount;
            EulerFlowSolver.SkipStaticChunks = false;
            sweeping.Tick();
            Assert.Multiple(() =>
            {
                Assert.That(skipping.LastFlowDiagnostics, Is.EqualTo(sweeping.LastFlowDiagnostics), $"diagnostics, tick {tick}");
                Assert.That(
                    skipping.CaptureCheckpoint().ComputeStateHash(),
                    Is.EqualTo(sweeping.CaptureCheckpoint().ComputeStateHash()), $"state hash, tick {tick}");
            });
        }

        TestContext.Out.WriteLine($"chunks skipped over {Ticks} ticks: {skippedTotal}");
        Assert.That(skippedTotal, Is.GreaterThan(0), "the comparison skipped nothing");
    }

    [Test]
    public void SkippedChunksOutsideEveryReach_AreExactlyTheOnesBeyondTheBound()
    {
        // 48 substeps, 4-cell chunks: D = ceil(50 / 4) = 13. The pulse chunk is 0 and the hot sea starts at 30.
        using var simulation = CreateLine();
        simulation.Tick();
        int skipped = EulerFlowSolver.LastSkippedCount;
        // Skipped: chunks 14 and 15 (more than 13 from chunk 0 and from 29) and chunks 44 to 69 (more than 13 from 30).
        Assert.That(skipped, Is.EqualTo(28));
    }

    [Test]
    public void TwoGasesAtEqualPressure_AreNotStaticAndKeepMixing()
    {
        const int width = 4;
        var config = CreateConfig(48);
        using var simulation = SimTestHelpers.CreateEulerSimulation(config, width, 2, 2, out var left);
        var right = SimTestHelpers.CreateOpenChunk(simulation, Int3.PosX);
        float moles = SimTestHelpers.MolesFor(config, Atmosphere, Temperature);
        foreach (var (chunk, gas) in new[] { (left, SimTestHelpers.EulerAirGasName), (right, SimTestHelpers.EulerUnitGasName) })
        {
            for (int z = 0; z < 2; z++)
            for (int y = 0; y < 2; y++)
            for (int x = 0; x < width; x++)
                simulation.AddGasToVoxel(chunk, x, y, z, gas, moles, Temperature);
        }

        for (int tick = 0; tick < 5; tick++)
        {
            simulation.Tick();
            Assert.That(simulation.LastFlowDiagnostics.Substeps, Is.GreaterThan(0), $"swept on tick {tick + 1}");
        }

        var rightSnapshot = simulation.GetChunkSnapshot(right);
        Assert.That(SimTestHelpers.Moles(rightSnapshot, SimTestHelpers.EulerAirGasId, 0), Is.GreaterThan(0f), "air crossed the face");
    }

    [Test]
    public void GasNextToVoid_IsNeverStatic()
    {
        const int length = 6;
        var config = CreateConfig(48);
        using var simulation = SimTestHelpers.CreateEulerSimulation(config, length, 1, 1, out var chunk);
        simulation.SetVoxelClassification(chunk, length - 1, 0, 0, VoxelClassification.RoomVoid);
        float moles = SimTestHelpers.MolesFor(config, Atmosphere, Temperature);
        for (int x = 0; x < length - 1; x++)
            simulation.AddGasToVoxel(chunk, x, 0, 0, SimTestHelpers.EulerAirGasName, moles, Temperature);

        double previous = SimTestHelpers.TotalMoles(simulation.GetChunkSnapshot(chunk));
        for (int tick = 0; tick < 5; tick++)
        {
            simulation.Tick();
            double total = SimTestHelpers.TotalMoles(simulation.GetChunkSnapshot(chunk));
            Assert.That(simulation.LastFlowDiagnostics.Substeps, Is.GreaterThan(0), $"swept on tick {tick + 1}");
            Assert.That(total, Is.LessThan(previous), $"gas drained on tick {tick + 1}");
            previous = total;
        }
    }

    private static AtmosSimulation CreateLine()
    {
        var config = CreateConfig(48);
        config.SleepThreshold = 10;
        config.SleepEpsilon = 3.5f;
        var simulation = SimTestHelpers.CreateEulerSimulation(config, ChunkSize, ChunkSize, ChunkSize, out var first);
        float ambient = SimTestHelpers.MolesFor(config, Atmosphere, Temperature);
        float hot = SimTestHelpers.MolesFor(config, Atmosphere, HotTemperature);
        for (int index = 0; index < LineLength; index++)
        {
            var chunk = index == 0 ? first : SimTestHelpers.CreateOpenChunk(simulation, index * Int3.PosX);
            if (index >= HotStart)
            {
                Fill(simulation, chunk, 0.8f * hot, 0.2f * hot, HotTemperature);
                continue;
            }

            Fill(simulation, chunk, ambient, 0f, Temperature);
            if (index == 0)
                simulation.AddGasToVoxel(chunk, 0, 1, 1, SimTestHelpers.EulerAirGasName, 0.5f * ambient, Temperature);
        }

        return simulation;
    }

    private static AtmosConfig CreateConfig(int substepCap)
    {
        var config = SimTestHelpers.CreateEulerConfig(new EulerFlowConfig { MaxSubstepsPerTick = substepCap });
        var registry = new GasRegistry();
        for (int gasId = 0; gasId < config.GasRegistry.Count; gasId++)
            registry.Add(config.GasRegistry[gasId] with { DiffusionCoefficient = 0.02f });

        config.GasRegistry = registry;
        return config;
    }

    private static void Fill(
        AtmosSimulation simulation, AtmosChunkHandle chunk, float airMoles, float unitMoles, float temperature)
    {
        for (int z = 0; z < ChunkSize; z++)
        for (int y = 0; y < ChunkSize; y++)
        for (int x = 0; x < ChunkSize; x++)
        {
            simulation.AddGasToVoxel(chunk, x, y, z, SimTestHelpers.EulerAirGasName, airMoles, temperature);
            if (unitMoles > 0f)
                simulation.AddGasToVoxel(chunk, x, y, z, SimTestHelpers.EulerUnitGasName, unitMoles, temperature);
        }
    }

    /// <summary>
    ///     FNV-1a over the gas, temperature, pressure, and velocity bits of every chunk, without the sleep state.
    /// </summary>
    private static ulong GasDigest(AtmosSimulation simulation, IEnumerable<AtmosChunkHandle> chunks)
    {
        ulong hash = 14695981039346656037UL;
        void Add(float value)
        {
            hash = (hash ^ (uint)BitConverter.SingleToInt32Bits(value)) * 1099511628211UL;
        }

        foreach (var chunk in chunks)
        {
            var snapshot = simulation.GetChunkSnapshot(chunk);
            for (int index = 0; index < snapshot.Temperature.Length; index++)
            {
                Add(snapshot.Temperature[index]);
                Add(snapshot.TotalPressure[index]);
                Add(snapshot.VelocityX[index]);
                foreach (var gas in snapshot.Gases)
                    Add(gas.Moles[index]);
            }
        }

        return hash;
    }
}
