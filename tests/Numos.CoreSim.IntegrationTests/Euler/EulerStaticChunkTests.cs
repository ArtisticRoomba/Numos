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
    private const int FlatSize = 16;
    private const int FlatGridSize = 6;
    private const int DoorY = 8;
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
        config.SleepDelay = 0.25f;
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

        const int sleepDelayTicks = 5;
        for (int tick = 2; tick <= sleepDelayTicks; tick++)
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
        // 48 substeps, 4-cell chunks: R = ceil(50 / 4) = 13 on every axis.
        // The pulse chunk is 0 and the hot sea starts at 30.
        using var simulation = CreateLine();
        simulation.Tick();
        int skipped = EulerFlowSolver.LastSkippedCount;
        // Skipped: chunks 14 and 15 (more than 13 from chunk 0 and from 29) and chunks 44 to 69 (more than 13 from 30).
        Assert.That(skipped, Is.EqualTo(28));
    }

    /// <summary>
    ///     An open 6x6 grid of flat 16x16x1 chunks, one disturbed. Each axis has its own reach, so the one-voxel Z
    ///     axis does not widen the box: at 20 Hz the cap is 16 substeps and <c>R = ceil(18 / 16) = 2</c>, at 960 Hz
    ///     it is 6 and <c>R = ceil(8 / 16) = 1</c>. The disturbed chunk is (1, 1), so the box holds 4x4 or 3x3 chunks.
    /// </summary>
    [TestCase(SimTestHelpers.PinnedTickRate, 20)]
    [TestCase(960, 27)]
    public void FlatOpenGrid_SkipsChunksOutsideThePerAxisBoxAndChangesNoBit(int tickRate, int expectedSkipped)
    {
        List<int> skipped = CompareSkipWithSweep(() => CreateFlatGrid(tickRate, walls: false, door: false), tickRate);
        Assert.That(skipped[0], Is.EqualTo(expectedSkipped), "skipped on the first call");
    }

    /// <summary>
    ///     The flat grid with every chunk sealed into its own room. No signal can leave the disturbed room, so the
    ///     reach search stops at its walls and every other room is skipped on every call.
    /// </summary>
    [TestCase(SimTestHelpers.PinnedTickRate)]
    [TestCase(960)]
    public void SealedRooms_SkipEveryRoomButTheDisturbedOneAndChangeNoBit(int tickRate)
    {
        List<int> skipped = CompareSkipWithSweep(() => CreateFlatGrid(tickRate, walls: true, door: false), tickRate);
        Assert.That(skipped, Is.All.EqualTo(FlatGridSize * FlatGridSize - 1));
    }

    /// <summary>
    ///     The sealed rooms with a one-voxel door from the disturbed room into chunk (2, 1). The search crosses the
    ///     door, so that room is swept from the first call.
    /// </summary>
    [TestCase(SimTestHelpers.PinnedTickRate)]
    [TestCase(960)]
    public void SealedRoomsWithADoor_SweepTheRoomBehindTheDoorAndChangeNoBit(int tickRate)
    {
        List<int> skipped = CompareSkipWithSweep(() => CreateFlatGrid(tickRate, walls: true, door: true), tickRate);
        Assert.That(skipped[0], Is.EqualTo(FlatGridSize * FlatGridSize - 2), "skipped on the first call");
    }

    /// <summary>
    ///     A 3x3x3 grid of 8^3 chunks, one corner disturbed. The cap is 6 substeps at 20 Hz and 2 at 960 Hz, so the
    ///     box is one chunk on every axis. That is the cube the 26-neighbour search used, and 8 chunks are swept.
    /// </summary>
    [TestCase(SimTestHelpers.PinnedTickRate)]
    [TestCase(960)]
    public void CubeGrid_SkipsChunksOutsideTheBoxAndChangesNoBit(int tickRate)
    {
        List<int> skipped = CompareSkipWithSweep(() => CreateCubeGrid(tickRate), tickRate);
        Assert.That(skipped[0], Is.EqualTo(19), "skipped on the first call");
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

    /// <summary>
    ///     Ticks a skipping and a sweeping copy side by side for 0.3 s and compares the diagnostics and the state
    ///     hash after every tick. That is 6 <c>euler-flow</c> calls at 20 Hz and 18 at 960 Hz.
    /// </summary>
    /// <returns>The skipped count of every call the skipping copy made, in order.</returns>
    private static List<int> CompareSkipWithSweep(Func<AtmosSimulation> create, int tickRate)
    {
        using var skipping = create();
        using var sweeping = create();
        var skipped = new List<int>();
        int ticks = 3 * tickRate / 10;
        for (int tick = 1; tick <= ticks; tick++)
        {
            EulerFlowSolver.SkipStaticChunks = true;
            EulerFlowSolver.LastSkippedCount = -1;
            skipping.Tick();
            if (EulerFlowSolver.LastSkippedCount >= 0)
                skipped.Add(EulerFlowSolver.LastSkippedCount);

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

        TestContext.Out.WriteLine($"skipped per call: {string.Join(", ", skipped)}");
        Assert.That(skipped, Is.Not.Empty, "euler-flow never ran");
        return skipped;
    }

    /// <summary>
    ///     A 6x6 grid of flat chunks of <see cref="FlatSize" /> squared voxels, filled with air at rest. Chunk (1, 1)
    ///     has a +50% pressure block in its middle that touches no chunk face.
    /// </summary>
    /// <param name="tickRate">The world tick rate. The cap is 320 substeps per second.</param>
    /// <param name="walls">Whether solid voxels on <c>x = 0</c> and <c>y = 0</c> seal every chunk into a room.</param>
    /// <param name="door">Whether one wall voxel of chunk (2, 1) is air, joining its room to the disturbed one.</param>
    private static AtmosSimulation CreateFlatGrid(int tickRate, bool walls, bool door)
    {
        var config = CreateConfig(16);
        var simulation = SimTestHelpers.CreateEulerSimulation(
            config, FlatSize, FlatSize, 1, out var first, tickRate: tickRate);
        float ambient = SimTestHelpers.MolesFor(config, Atmosphere, Temperature);
        for (int gridY = 0; gridY < FlatGridSize; gridY++)
        for (int gridX = 0; gridX < FlatGridSize; gridX++)
        {
            var position = new Int3(gridX, gridY, 0);
            var chunk = position == default ? first : SimTestHelpers.CreateOpenChunk(simulation, position);
            bool disturbed = gridX == 1 && gridY == 1;
            bool doorway = door && gridX == 2 && gridY == 1;
            for (int y = 0; y < FlatSize; y++)
            for (int x = 0; x < FlatSize; x++)
            {
                bool solid = walls && (x == 0 || y == 0) && !(doorway && x == 0 && y == DoorY);
                if (solid)
                {
                    simulation.SetVoxelClassification(chunk, x, y, 0, VoxelClassification.RoomSolid);
                    continue;
                }

                float moles = disturbed && x is >= 4 and < 12 && y is >= 4 and < 12 ? 1.5f * ambient : ambient;
                simulation.AddGasToVoxel(chunk, x, y, 0, SimTestHelpers.EulerAirGasName, moles, Temperature);
            }
        }

        return simulation;
    }

    /// <summary>
    ///     A 3x3x3 grid of 8^3 chunks filled with air at rest, with a +50% pressure block in the middle of chunk
    ///     (0, 0, 0) that touches no chunk face. The cap is 120 substeps per second.
    /// </summary>
    private static AtmosSimulation CreateCubeGrid(int tickRate)
    {
        const int size = 8;
        const int gridSize = 3;
        var config = CreateConfig(6);
        var simulation = SimTestHelpers.CreateEulerSimulation(config, size, size, size, out var first, tickRate: tickRate);
        float ambient = SimTestHelpers.MolesFor(config, Atmosphere, Temperature);
        for (int gridZ = 0; gridZ < gridSize; gridZ++)
        for (int gridY = 0; gridY < gridSize; gridY++)
        for (int gridX = 0; gridX < gridSize; gridX++)
        {
            var position = new Int3(gridX, gridY, gridZ);
            var chunk = position == default ? first : SimTestHelpers.CreateOpenChunk(simulation, position);
            for (int z = 0; z < size; z++)
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                bool block = position == default && x is >= 2 and < 6 && y is >= 2 and < 6 && z is >= 2 and < 6;
                simulation.AddGasToVoxel(
                    chunk, x, y, z, SimTestHelpers.EulerAirGasName, block ? 1.5f * ambient : ambient, Temperature);
            }
        }

        return simulation;
    }

    private static AtmosSimulation CreateLine()
    {
        var config = CreateConfig(48);
        config.SleepDelay = 0.5f;
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
        var config = SimTestHelpers.CreateEulerConfig(new EulerFlowConfig { MaxSubstepsPerSecond = substepCap * 20 });
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
