using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.Maths;

namespace Numos.API.Tests;

[TestFixture]
public sealed class AtmosWorldIdleTickTests
{
    private const int HighRate = 960;

    [Test]
    public void IdleTicks_AdvanceWorldAndEverySimulation()
    {
        using var world = new AtmosWorld(CreateConfig(), AtmosTransportModel.Legacy, HighRate);
        var first = world.CreateSimulation(1, 1, 1);
        var second = world.CreateSimulation(1, 1, 1);
        int runs = 0;
        world.Solvers.Register("counter", _ => runs++, interval: 5);
        foreach (var step in world.Solvers.Steps.Where(static step => step.Name != "counter").ToArray())
            world.Solvers.SetEnabled(step.Name, false);


        for (int index = 0; index < 11; index++)
            world.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(runs, Is.EqualTo(2));
            Assert.That(world.TickCount, Is.EqualTo(11));
            Assert.That(first.TickCount, Is.EqualTo(11));
            Assert.That(second.TickCount, Is.EqualTo(11));
        });
    }

    [Test]
    public void LegacyWorldAt960Hz_MatchesTheFullPath()
    {
        using var idle = CreateLegacyScene(false, out var idleSimulation, out var idleChunks);
        using var full = CreateLegacyScene(true, out var fullSimulation, out var fullChunks);

        for (int tick = 0; tick < 96; tick++)
        {
            idle.Tick();
            full.Tick();
        }

        Assert.That(idle.TickCount, Is.EqualTo(96));
        Assert.That(idleSimulation.GetChunkSnapshot(idleChunks[0]).Temperature[0], Is.Not.EqualTo(400f), "The scene must evolve.");
        for (int index = 0; index < idleChunks.Length; index++)
        {
            var expected = fullSimulation.GetChunkSnapshot(fullChunks[index]);
            var actual = idleSimulation.GetChunkSnapshot(idleChunks[index]);
            Assert.Multiple(() =>
            {
                Assert.That(actual.Temperature, Is.EqualTo(expected.Temperature));
                Assert.That(actual.TotalPressure, Is.EqualTo(expected.TotalPressure));
                Assert.That(actual.TotalHeatCapacity, Is.EqualTo(expected.TotalHeatCapacity));
                Assert.That(actual.IsAwake, Is.EqualTo(expected.IsAwake));
                Assert.That(actual.SleepTimer, Is.EqualTo(expected.SleepTimer));
            });
        }
    }

    [Test]
    public void TopologyChangeQueuedBeforeAnIdleTick_AppliesAtThatBoundary()
    {
        using var world = new AtmosWorld(CreateConfig(), AtmosTransportModel.Legacy, HighRate);
        var simulation = world.CreateSimulation(1, 1, 1);
        var first = simulation.CreateAndRegisterChunk(default);
        var second = simulation.CreateAndRegisterChunk(new Int3(2, 0, 0));
        simulation.SetChunkClassification(first, new VoxelClassification(1));
        simulation.SetChunkClassification(second, new VoxelClassification(1));
        world.Tick();
        world.Tick();
        world.Tick();
        Assert.That(world.TopologyVersion, Is.Zero);

        world.CreatePortal(simulation.GetCellRef(first, 0), simulation.GetCellRef(second, 0));
        world.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(world.TickCount, Is.EqualTo(4));
            Assert.That(simulation.TickCount, Is.EqualTo(4));
            Assert.That(world.TopologyVersion, Is.EqualTo(1));
            Assert.That(world.ActiveLinkCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void Update_At960Hz_RunsExactly32TicksPerThirtiethOfASecond()
    {
        using var world = new AtmosWorld(CreateConfig(), AtmosTransportModel.Legacy, HighRate);
        world.CreateSimulation(1, 1, 1);

        for (int frame = 1; frame <= 600; frame++)
        {
            world.Update(1f / 30f);
            Assert.That(world.TickCount, Is.EqualTo(frame * 32), $"frame {frame}");
        }
    }

    [Test]
    public void Update_At960Hz_RunsOneTickPerStep()
    {
        using var world = new AtmosWorld(CreateConfig(), AtmosTransportModel.Legacy, HighRate);
        var simulation = world.CreateSimulation(1, 1, 1);

        world.Update(0.05f);

        Assert.Multiple(() =>
        {
            Assert.That(world.TickCount, Is.EqualTo(48));
            Assert.That(simulation.TickCount, Is.EqualTo(48));
        });
    }

    [Test]
    public void Update_At960Hz_ClampsBacklogByTime()
    {
        using var world = new AtmosWorld(CreateConfig(), AtmosTransportModel.Legacy, HighRate);
        world.CreateSimulation(1, 1, 1);

        world.Update(10f);
        Assert.That(world.TickCount, Is.EqualTo(240));

        world.Update(0f);
        Assert.That(world.TickCount, Is.EqualTo(240));
    }

    private static AtmosWorld CreateLegacyScene(
        bool noOpStage,
        out AtmosSimulation simulation,
        out AtmosChunkHandle[] chunks)
    {
        var world = new AtmosWorld(CreateConfig(), AtmosTransportModel.Legacy, HighRate);
        if (noOpStage)
            world.Solvers.Register("no-op", static _ => { });

        simulation = world.CreateSimulation(2, 1, 1);
        chunks =
        [
            simulation.CreateAndRegisterChunk(default),
            simulation.CreateAndRegisterChunk(new Int3(1, 0, 0))
        ];
        foreach (var chunk in chunks)
            simulation.SetChunkClassification(chunk, new VoxelClassification(1));

        simulation.AddGasToVoxel(chunks[0], 0, "TestGas", 3f, 400f);
        simulation.AddGasToVoxel(chunks[0], 1, "TestGas", 1f, 300f);
        simulation.AddGasToVoxel(chunks[1], 0, "TestGas", 0.5f, 250f);
        return world;
    }

    private static AtmosConfig CreateConfig()
    {
        return new AtmosConfig
        {
            BulkFlowCoefficient = 0.25f,
            ThermalConductance = 0.5f,
            GasRegistry = [new GasProperties { Name = "TestGas", MolarHeatCapacityAtConstantVolume = 20f }]
        };
    }
}
