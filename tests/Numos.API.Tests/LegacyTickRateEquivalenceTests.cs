using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.GasReactions;
using Numos.Maths;

namespace Numos.API.Tests;

/// <summary>
///     The legacy pipeline must give bit-identical results at 20 Hz and 960 Hz once each 20 Hz step has elapsed.
/// </summary>
[TestFixture]
public sealed class LegacyTickRateEquivalenceTests
{
    private const int LowRate = 20;
    private const int HighRate = 960;
    private const int Ratio = HighRate / LowRate;
    private const int Steps = 60;
    private const int IgnitionStep = 5;

    private sealed class Scene : IDisposable
    {
        public required AtmosWorld World;
        public required List<(AtmosSimulation Simulation, AtmosChunkHandle Chunk)> Chunks;
        public required AtmosChunkHandle ReactiveChunk;
        public required AtmosSimulation ReactiveSimulation;
        public required AtmosChunkHandle QuietChunk;
        public required AtmosSimulation QuietSimulation;
        public required AtmosChunkHandle VaporChunk;
        public required AtmosSimulation VaporSimulation;
        public required int VaporGasId;

        public void Dispose()
        {
            World.Dispose();
        }
    }

    [Test]
    public void Legacy960Hz_MatchesLegacy20HzBitwiseAtEveryTwentyHertzTick()
    {
        using var low = BuildScene(LowRate);
        using var high = BuildScene(HighRate);
        var initial = Capture(low);
        Assert.That(Compare(initial, Capture(high)), Is.Null, "The scenes must start identical.");

        var sleepSeen = false;
        for (int step = 1; step <= Steps; step++)
        {
            var before = Capture(low);
            if (step == IgnitionStep + 1)
            {
                Ignite(low);
                Ignite(high);
            }

            low.World.Tick();
            for (int tick = 1; tick <= Ratio; tick++)
            {
                high.World.Tick();
                if (tick is 1 or 24 or Ratio - 1)
                {
                    // Solver stages run once per 48 ticks, so nothing has moved yet.
                    Assert.That(
                        Compare(before, Capture(high), includeSleepTimers: false),
                        Is.Null,
                        $"step {step}, high-rate tick {tick}");
                }
            }

            var expected = Capture(low);
            Assert.That(Compare(expected, Capture(high)), Is.Null, $"step {step}");
            Assert.That(high.World.TickCount, Is.EqualTo(low.World.TickCount * Ratio));
            Assert.That(high.World.SimulatedTime, Is.EqualTo(low.World.SimulatedTime).Within(1e-9));
            sleepSeen |= !low.QuietSimulation.GetChunkSnapshot(low.QuietChunk).IsAwake;
        }

        var final = Capture(low);
        var lowQuiet = low.QuietSimulation.GetChunkSnapshot(low.QuietChunk);
        var lowReactive = low.ReactiveSimulation.GetChunkSnapshot(low.ReactiveChunk);
        var startQuiet = initial.Chunks[low.Chunks.FindIndex(c => c.Chunk.Equals(low.QuietChunk))];
        Assert.Multiple(() =>
        {
            Assert.That(Compare(initial, final), Is.Not.Null, "The scene must evolve.");
            Assert.That(startQuiet.IsAwake, Is.True);
            Assert.That(sleepSeen, Is.True, "The quiet chunk must fall asleep during the run.");
            Assert.That(lowQuiet.IsAwake, Is.False);
            Assert.That(
                low.VaporSimulation.GetChunkSnapshot(low.VaporChunk).Gases.Single(g => g.GasId == low.VaporGasId).Moles.Sum(),
                Is.LessThan(initial.Chunks[low.Chunks.FindIndex(c => c.Chunk.Equals(low.VaporChunk))]
                    .Gases[low.VaporGasId].Sum(static m => BitConverter.Int32BitsToSingle(m))),
                "Vapor must condense.");
            var products = lowReactive.Gases.Single(g => g.GasId == low.VaporGasId + 3).Moles.Sum();
            Assert.That(products, Is.GreaterThan(0f), "Ignition must start a fire.");
        });
    }

    [Test]
    public void IgnitionIssuedBetweenTwentyHertzTicks_LastsUntilTheNextReactionPassAt960Hz()
    {
        using var low = BuildScene(LowRate);
        using var high = BuildScene(HighRate);
        for (int step = 0; step < IgnitionStep; step++)
        {
            low.World.Tick();
            for (int tick = 0; tick < Ratio; tick++)
                high.World.Tick();
        }

        Ignite(low);
        Ignite(high);
        low.World.Tick();
        for (int tick = 0; tick < Ratio; tick++)
            high.World.Tick();

        Assert.That(Compare(Capture(low), Capture(high)), Is.Null);

        // Ignite part way through a high-rate step. The source must survive to the next reaction pass.
        using var mid = BuildScene(HighRate);
        for (int tick = 0; tick < IgnitionStep * Ratio + Ratio / 2; tick++)
            mid.World.Tick();
        Ignite(mid);
        for (int tick = 0; tick < Ratio * 3; tick++)
            mid.World.Tick();

        var products = mid.ReactiveSimulation.GetChunkSnapshot(mid.ReactiveChunk)
            .Gases.Single(g => g.GasId == mid.VaporGasId + 3).Moles.Sum();
        Assert.That(products, Is.GreaterThan(0f));
    }

    private static void Ignite(Scene scene)
    {
        scene.ReactiveSimulation.IgniteVoxel(scene.ReactiveChunk, 0, 1500f, 0.5f);
    }

    private static Scene BuildScene(int tickRate)
    {
        var air = new GasProperties { Name = "Air", MolarHeatCapacityAtConstantVolume = 20f, DiffusionCoefficient = 0.02f };
        var vapor = new GasProperties
        {
            Name = "Vapor",
            MolarHeatCapacityAtConstantVolume = 33f,
            BoilingPoint = 320f,
            CondensationEnabled = true,
            MolarEnthalpyOfVaporization = 40_650f,
            DiffusionCoefficient = 0.01f
        };
        var fuel = new GasProperties { Name = "Fuel", MolarHeatCapacityAtConstantVolume = 25f, DiffusionCoefficient = 0.02f };
        var oxidizer = new GasProperties { Name = "Oxidizer", MolarHeatCapacityAtConstantVolume = 25f, DiffusionCoefficient = 0.02f };
        var product = new GasProperties { Name = "Product", MolarHeatCapacityAtConstantVolume = 30f, DiffusionCoefficient = 0.02f };
        var reaction = new LinearGasReaction(
            new Dictionary<GasProperties, float> { [fuel] = 1f, [oxidizer] = 1f },
            new Dictionary<GasProperties, float> { [product] = 2f },
            60_000f,
            250f,
            1000f,
            0.5f,
            2f,
            false,
            false,
            new HashSet<LinearGasReaction.LinearSpeedFactor>(),
            true);
        var config = new AtmosConfig
        {
            BulkFlowCoefficient = 0.25f,
            ThermalConductance = 0.5f,
            SleepDelay = 0.5f,
            GasRegistry = [air, vapor, fuel, oxidizer, product],
            SolverConfigurations = [new GasReactionConfig(linearReactions: [reaction])]
        };

        var world = new AtmosWorld(config, AtmosTransportModel.Legacy, tickRate);
        var main = world.CreateSimulation(4, 2, 2);
        var other = world.CreateSimulation(4, 2, 2);
        var chunks = new List<(AtmosSimulation, AtmosChunkHandle)>();

        AtmosChunkHandle Open(AtmosSimulation simulation, Int3 position)
        {
            var chunk = simulation.CreateAndRegisterChunk(position);
            simulation.SetChunkClassification(chunk, new VoxelClassification(1));
            chunks.Add((simulation, chunk));
            return chunk;
        }

        // Three adjacent chunks with a strong pressure and temperature gradient across their boundaries.
        var vaporChunk = Open(main, new Int3(0, 0, 0));
        var middle = Open(main, new Int3(1, 0, 0));
        var cold = Open(main, new Int3(2, 0, 0));
        for (int voxel = 0; voxel < 16; voxel++)
        {
            main.AddGasToVoxel(vaporChunk, (ushort)voxel, "Air", 14f + 0.25f * voxel, 420f - 3f * voxel);
            main.AddGasToVoxel(vaporChunk, (ushort)voxel, "Vapor", 2f, 305f);
            main.AddGasToVoxel(middle, (ushort)voxel, "Air", 5f + 0.1f * voxel, 300f);
            main.AddGasToVoxel(cold, (ushort)voxel, "Air", 0.5f + 0.05f * voxel, 220f + voxel);
        }

        // Uniform and isolated, so it has no reason to stay awake.
        var quiet = Open(main, new Int3(5, 0, 0));
        for (int voxel = 0; voxel < 16; voxel++)
            main.AddGasToVoxel(quiet, (ushort)voxel, "Air", 4f, 293f);

        // Isolated reactive mixture. The test supplies the ignition.
        var reactive = Open(main, new Int3(8, 0, 0));
        for (int voxel = 0; voxel < 16; voxel++)
        {
            main.AddGasToVoxel(reactive, (ushort)voxel, "Fuel", 3f, 400f);
            main.AddGasToVoxel(reactive, (ushort)voxel, "Oxidizer", 3f, 400f);
            main.AddGasToVoxel(reactive, (ushort)voxel, "Air", 1f, 400f);
        }

        // A second grid, linked to the first by two portals.
        var remote = Open(other, new Int3(0, 0, 0));
        for (int voxel = 0; voxel < 16; voxel++)
            other.AddGasToVoxel(remote, (ushort)voxel, "Air", 1f + 0.1f * voxel, 250f);

        world.CreatePortal(main.GetCellRef(vaporChunk, 0), other.GetCellRef(remote, 0));
        world.CreatePortal(main.GetCellRef(cold, 15), other.GetCellRef(remote, 15));

        return new Scene
        {
            World = world,
            Chunks = chunks,
            ReactiveChunk = reactive,
            ReactiveSimulation = main,
            QuietChunk = quiet,
            QuietSimulation = main,
            VaporChunk = vaporChunk,
            VaporSimulation = main,
            VaporGasId = 1
        };
    }

    private sealed record ChunkState(
        int[] Temperature,
        int[][] Gases,
        int[] VoxelRoomMap,
        bool IsAwake,
        int SleepTimer,
        int ActiveAirCount);

    private sealed record WorldState(List<ChunkState> Chunks)
    {
        public ChunkState this[int index] => Chunks[index];
    }

    private static WorldState Capture(Scene scene)
    {
        var states = new List<ChunkState>();
        foreach (var (simulation, chunk) in scene.Chunks)
        {
            var snapshot = simulation.GetChunkSnapshot(chunk);
            var gases = new int[5][];
            for (int gas = 0; gas < gases.Length; gas++)
                gases[gas] = [];

            foreach (var channel in snapshot.Gases)
                gases[channel.GasId] = channel.Moles.Select(BitConverter.SingleToInt32Bits).ToArray();

            states.Add(new ChunkState(
                snapshot.Temperature.Select(BitConverter.SingleToInt32Bits).ToArray(),
                gases,
                snapshot.VoxelRoomMap.ToArray(),
                snapshot.IsAwake,
                snapshot.SleepTimer,
                snapshot.ActiveAirCount));
        }

        return new WorldState(states);
    }

    // The first difference as text, or null when the states match bit for bit. Sleep timers are scaled by the tick ratio.
    private static string? Compare(WorldState expected, WorldState actual, bool includeSleepTimers = true)
    {
        for (int chunk = 0; chunk < expected.Chunks.Count; chunk++)
        {
            var left = expected[chunk];
            var right = actual[chunk];
            if (!left.Temperature.AsSpan().SequenceEqual(right.Temperature))
                return $"chunk {chunk}: temperature";

            for (int gas = 0; gas < left.Gases.Length; gas++)
            {
                // A channel can be absent from one side if it holds only zeros.
                var a = left.Gases[gas];
                var b = right.Gases[gas];
                if (a.Length == 0 && b.All(static v => v == 0) || b.Length == 0 && a.All(static v => v == 0))
                    continue;

                if (!a.AsSpan().SequenceEqual(b))
                    return $"chunk {chunk}: gas {gas} moles";
            }

            if (!left.VoxelRoomMap.AsSpan().SequenceEqual(right.VoxelRoomMap))
                return $"chunk {chunk}: voxel classification";

            if (left.IsAwake != right.IsAwake)
                return $"chunk {chunk}: awake flag ({left.IsAwake} vs {right.IsAwake})";

            if (left.ActiveAirCount != right.ActiveAirCount)
                return $"chunk {chunk}: active air count";

            if (includeSleepTimers && left.SleepTimer * Ratio != right.SleepTimer)
                return $"chunk {chunk}: sleep timer ({left.SleepTimer} x {Ratio} vs {right.SleepTimer})";
        }

        return null;
    }
}
