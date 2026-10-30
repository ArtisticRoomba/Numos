using Numos.API;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.GasReactions;
using Numos.CoreSim.Replay;
using Numos.CoreSim.State.Advanced;
using Numos.Tests;

namespace Numos.CoreSim.IntegrationTests;

[TestFixture]
public sealed class GasMixturePoolWorldTests
{
    private const int Hydrogen = 0;
    private const int Oxygen = 1;
    private const int Nitrogen = 2;
    private const int Water = 3;

    private static AtmosConfig CreateConfig(float hydrogenHeatCapacity = 0f)
    {
        var hydrogen = new GasProperties
        {
            Name = "Hydrogen",
            MolarHeatCapacityAtConstantVolume = hydrogenHeatCapacity > 0f
                ? hydrogenHeatCapacity
                : AtmosPhysicalConstants.IdealDiatomicMolarHeatCapacityAtConstantVolume,
            BoilingPoint = 20.4f,
            MolarEnthalpyOfVaporization = 904f,
            DiffusionCoefficient = 0.02f,
            MolarMass = 0.002016f,
            ThermalConductivity = 0.168f
        };

        var oxygen = new GasProperties
        {
            Name = "Oxygen",
            MolarHeatCapacityAtConstantVolume = AtmosPhysicalConstants.IdealDiatomicMolarHeatCapacityAtConstantVolume,
            BoilingPoint = 90.2f,
            MolarEnthalpyOfVaporization = 6_820f,
            DiffusionCoefficient = 0.02f,
            MolarMass = 0.031998f,
            ThermalConductivity = 0.024f
        };

        var nitrogen = new GasProperties
        {
            Name = "Nitrogen",
            MolarHeatCapacityAtConstantVolume = AtmosPhysicalConstants.IdealDiatomicMolarHeatCapacityAtConstantVolume,
            DiffusionCoefficient = 0.02f,
            MolarMass = 0.028014f,
            ThermalConductivity = 0.024f
        };

        var water = new GasProperties
        {
            Name = "Water Vapour",
            MolarHeatCapacityAtConstantVolume = 28f,
            BoilingPoint = 373.15f,
            CondensationEnabled = true,
            MolarEnthalpyOfVaporization = 40_657f,
            LiquidId = 0,
            DiffusionCoefficient = 0.02f,
            MolarMass = 0.018015f,
            ThermalConductivity = 0.0245f
        };

        var hydrogenFire = new StandardGasReaction(
            new Dictionary<GasProperties, float> { { hydrogen, 2 }, { oxygen, 1 } },
            new Dictionary<GasProperties, float> { { water, 2 } },
            UnitConversions.FromKilojoulePerMole(483.6f),
            1.8e13f,
            UnitConversions.FromKilojoulePerMole(256f),
            new Dictionary<GasProperties, float> { { hydrogen, 1 }, { oxygen, 0.5f } },
            true);

        return new AtmosConfig
        {
            CondensationRateFactor = 0.5f,
            GasRegistry = [hydrogen, oxygen, nitrogen, water],
            SolverConfigurations = [new GasReactionConfig(standardReactions: [hydrogenFire])]
        };
    }

    // The tests count ticks at 20 Hz, where the reaction stage runs every tick and thermodynamics every other tick.
    private static AtmosWorld CreateWorld(AtmosConfig config)
    {
        return new AtmosWorld(config, AtmosTransportModel.Euler, 20);
    }

    private static (int Slot, uint Generation) AllocateHotFuel(AtmosGasMixturePool pool)
    {
        var cell = pool.Allocate(1f, 1500f);
        pool.AddGas(cell.Slot, cell.Generation, Hydrogen, 20f, 1500f);
        pool.AddGas(cell.Slot, cell.Generation, Oxygen, 10f, 1500f);
        return cell;
    }

    // Everything observable through a handle, so two pools compare with one assertion.
    private static string Describe(AtmosGasMixturePool pool, (int Slot, uint Generation)[] handles)
    {
        var lines = new List<string> { $"allocated={pool.AllocatedCount} pages={pool.PageCount}" };
        foreach (var (slot, generation) in handles)
        {
            if (!pool.IsLive(slot, generation))
            {
                lines.Add($"{slot}/{generation}: stale");
                continue;
            }

            string gases = string.Join(
                ",",
                new[] { Hydrogen, Oxygen, Nitrogen, Water }.Select(
                    gas => ((float)pool.GetMoles(slot, generation, gas)).ToString("R")));

            lines.Add(
                $"{slot}/{generation}: v={(float)pool.GetVolume(slot, generation):R} " +
                $"t={(float)pool.GetTemperature(slot, generation):R} p={(float)pool.GetPressure(slot, generation):R} " +
                $"n={gases}");
        }

        return string.Join('\n', lines);
    }

    [Test]
    public void Tick_ReactsPoolMixturesInAWorldWithNoSimulations()
    {
        using var world = CreateWorld(CreateConfig());
        var cell = AllocateHotFuel(world.GasMixtures);
        float hydrogen = world.GasMixtures.GetMoles(cell.Slot, cell.Generation, Hydrogen);

        world.Tick();

        Assert.That(world.GasMixtures.GetMoles(cell.Slot, cell.Generation, Hydrogen), Is.LessThan(hydrogen));
        Assert.That(world.GasMixtures.GetMoles(cell.Slot, cell.Generation, Water), Is.GreaterThan(0f));
        Assert.That(world.GasMixtures.TickCount, Is.EqualTo(1));
    }

    [Test]
    public void Tick_AdvancesThePoolWithSimulationsPresent()
    {
        using var world = CreateWorld(CreateConfig());
        var simulation = world.CreateSimulation(4, 4, 4);
        simulation.CreateAndRegisterChunk(default);
        var cell = AllocateHotFuel(world.GasMixtures);

        world.Tick();
        world.Tick();

        Assert.That(world.TickCount, Is.EqualTo(2));
        Assert.That(world.GasMixtures.TickCount, Is.EqualTo(2));
        Assert.That(world.GasMixtures.GetMoles(cell.Slot, cell.Generation, Water), Is.GreaterThan(0f));
    }

    [Test]
    public void DisablingTheReactionStage_StopsMixtureReactions()
    {
        using var world = CreateWorld(CreateConfig());
        var cell = AllocateHotFuel(world.GasMixtures);
        float hydrogen = world.GasMixtures.GetMoles(cell.Slot, cell.Generation, Hydrogen);
        Assert.That(world.Solvers.SetEnabled(AtmosBuiltInSolvers.GasReactions, false), Is.True);

        for (int tick = 0; tick < 10; tick++)
            world.Tick();

        Assert.That(world.GasMixtures.GetMoles(cell.Slot, cell.Generation, Hydrogen), Is.EqualTo(hydrogen));
        Assert.That(world.GasMixtures.GetMoles(cell.Slot, cell.Generation, Water), Is.Zero);

        world.Solvers.SetEnabled(AtmosBuiltInSolvers.GasReactions, true);
        world.Tick();

        Assert.That(world.GasMixtures.GetMoles(cell.Slot, cell.Generation, Hydrogen), Is.LessThan(hydrogen));
    }

    [Test]
    public void PhaseChange_RunsOnTheThermodynamicsIntervalOnly()
    {
        using var world = CreateWorld(CreateConfig());
        Assert.That(
            BuiltInStageSchedule.IntervalFor(BuiltInStageSchedule.ThermalRunsPerSecond, world.TickRate),
            Is.EqualTo(2));
        var pool = world.GasMixtures;
        var cell = pool.Allocate(1f, 330f);
        pool.AddGas(cell.Slot, cell.Generation, Water, 30f, 330f);
        float initial = pool.GetMoles(cell.Slot, cell.Generation, Water);

        world.Tick();
        Assert.That(pool.GetMoles(cell.Slot, cell.Generation, Water), Is.EqualTo(initial), "tick 1");

        world.Tick();
        float afterTwo = pool.GetMoles(cell.Slot, cell.Generation, Water);
        Assert.That(afterTwo, Is.LessThan(initial), "tick 2");

        world.Tick();
        Assert.That(pool.GetMoles(cell.Slot, cell.Generation, Water), Is.EqualTo(afterTwo), "tick 3");

        world.Tick();
        Assert.That(pool.GetMoles(cell.Slot, cell.Generation, Water), Is.LessThan(afterTwo), "tick 4");
    }

    [Test]
    public void DisablingThermodynamics_StopsMixturePhaseChange()
    {
        using var world = CreateWorld(CreateConfig());
        var pool = world.GasMixtures;
        var cell = pool.Allocate(1f, 330f);
        pool.AddGas(cell.Slot, cell.Generation, Water, 30f, 330f);
        float initial = pool.GetMoles(cell.Slot, cell.Generation, Water);
        world.Solvers.SetEnabled(AtmosBuiltInSolvers.Thermodynamics, false);

        for (int tick = 0; tick < 8; tick++)
            world.Tick();

        Assert.That(pool.GetMoles(cell.Slot, cell.Generation, Water), Is.EqualTo(initial));
    }

    [Test]
    public void MixturesEditedAfterSittingIdle_StillReactAndCondense()
    {
        // The default tick rate, so every stage runs on its own interval and covers the real elapsed time.
        using var world = new AtmosWorld(CreateConfig());
        var simulation = world.CreateSimulation(4, 4, 4);
        simulation.CreateAndRegisterChunk(default);
        var pool = world.GasMixtures;
        var fuel = pool.Allocate(1f, 300f);
        var vapor = pool.Allocate(1f, 400f);
        pool.AddGas(vapor.Slot, vapor.Generation, Water, 30f, 400f);
        float initialWater = pool.GetMoles(vapor.Slot, vapor.Generation, Water);

        // Long enough for the grid chunk to fall asleep. Above its boiling point the vapor stays put.
        TickHelpers.TickFor(world, 2.0);
        Assert.That(pool.GetMoles(vapor.Slot, vapor.Generation, Water), Is.EqualTo(initialWater));

        pool.AddGas(fuel.Slot, fuel.Generation, Hydrogen, 20f, 1500f);
        pool.AddGas(fuel.Slot, fuel.Generation, Oxygen, 10f, 1500f);
        pool.SetTemperature(vapor.Slot, vapor.Generation, 330f);
        TickHelpers.TickFor(world, 0.2);

        Assert.Multiple(() =>
        {
            Assert.That(pool.GetMoles(fuel.Slot, fuel.Generation, Hydrogen), Is.LessThan(20f));
            Assert.That(pool.GetMoles(fuel.Slot, fuel.Generation, Water), Is.GreaterThan(0f));
            Assert.That(pool.GetMoles(vapor.Slot, vapor.Generation, Water), Is.LessThan(initialWater));
        });
    }

    [Test]
    public void ConfigQueuedDuringATick_ReachesThePool()
    {
        var config = CreateConfig();
        using var world = CreateWorld(config);
        var cell = world.GasMixtures.Allocate(1f, 300f);
        world.GasMixtures.AddGas(cell.Slot, cell.Generation, Hydrogen, 1f, 300f);

        var replacement = CreateConfig(hydrogenHeatCapacity: 50f);
        world.Solvers.Register("queue-config", _ => world.SetAtmosConfig(replacement));

        Assert.That(world.Tick, Throws.Nothing);

        // Hot hydrogen now mixes against a much larger heat capacity than the original configuration gave it.
        world.GasMixtures.AddGas(cell.Slot, cell.Generation, Hydrogen, 1f, 600f);
        float temperature = world.GasMixtures.GetTemperature(cell.Slot, cell.Generation);
        Assert.That(temperature, Is.EqualTo(450f).Within(0.01f));
    }

    [Test]
    public void CheckpointRoundTrip_RestoresPoolStateAndHandleLiveness()
    {
        using var world = CreateWorld(CreateConfig());
        var pool = world.GasMixtures;
        var first = pool.Allocate(1f, 300f);
        var second = pool.Allocate(2f, 400f);
        var third = AllocateHotFuel(pool);
        var fourth = pool.Allocate(0.5f, 330f);
        pool.Release(second.Slot, second.Generation);
        pool.AddGas(first.Slot, first.Generation, Nitrogen, 12f, 300f);
        pool.AddGas(fourth.Slot, fourth.Generation, Water, 6f, 330f);
        for (int tick = 0; tick < 3; tick++)
            world.Tick();

        var handles = new[] { first, second, third, fourth };
        string expected = Describe(pool, handles);
        var checkpoint = world.CaptureCheckpoint();
        var expectedHash = world.ComputeStateHash();
        Assert.That(checkpoint.ComputeStateHash(), Is.EqualTo(expectedHash));

        // Leave the captured state in every way a pool can: new pages, a released slot, a reused slot, changed gas.
        pool.Release(first.Slot, first.Generation);
        var reused = pool.Allocate(3f, 280f);
        pool.AddGas(reused.Slot, reused.Generation, Oxygen, 4f, 280f);
        for (int index = 0; index < 300; index++)
            pool.Allocate(1f, 290f);

        pool.AddGas(third.Slot, third.Generation, Nitrogen, 50f, 500f);
        for (int tick = 0; tick < 5; tick++)
            world.Tick();

        Assert.That(world.ComputeStateHash(), Is.Not.EqualTo(expectedHash));
        Assert.That(pool.PageCount, Is.EqualTo(2));

        world.RestoreCheckpoint(checkpoint);

        Assert.That(Describe(pool, handles), Is.EqualTo(expected));
        Assert.That(pool.IsLive(reused.Slot, reused.Generation), Is.False);
        Assert.That(pool.PageCount, Is.EqualTo(1));
        Assert.That(world.ComputeStateHash(), Is.EqualTo(expectedHash));
        Assert.That(world.CaptureCheckpoint().ComputeStateHash(), Is.EqualTo(expectedHash));

        // The lowest free slot and its generation come back exactly as they were at capture.
        var next = pool.Allocate(1f, 300f);
        Assert.That(next, Is.EqualTo((second.Slot, second.Generation + 1)));
    }

    [Test]
    public void CheckpointRestore_IntoAFreshWorldMatchesTheSource()
    {
        var config = CreateConfig();
        using var source = CreateWorld(config);
        var handles = new List<(int Slot, uint Generation)>();
        for (int index = 0; index < 260; index++)
        {
            var cell = source.GasMixtures.Allocate(1f + index % 3, 300f + index);
            source.GasMixtures.AddGas(cell.Slot, cell.Generation, Nitrogen, 1f + index, 300f + index);
            handles.Add(cell);
        }

        source.GasMixtures.Release(handles[7].Slot, handles[7].Generation);
        source.GasMixtures.Release(handles[255].Slot, handles[255].Generation);
        handles.Add(AllocateHotFuel(source.GasMixtures));
        for (int tick = 0; tick < 4; tick++)
            source.Tick();

        var checkpoint = source.CaptureCheckpoint();
        using var target = CreateWorld(config);
        target.RestoreCheckpoint(checkpoint);

        Assert.That(target.GasMixtures.PageCount, Is.EqualTo(2));
        Assert.That(Describe(target.GasMixtures, [.. handles]), Is.EqualTo(Describe(source.GasMixtures, [.. handles])));
        Assert.That(target.ComputeStateHash(), Is.EqualTo(source.ComputeStateHash()));

        // Both worlds continue identically, including the pool's own tick counter and configuration.
        for (int tick = 0; tick < 4; tick++)
        {
            source.Tick();
            target.Tick();
        }

        Assert.That(Describe(target.GasMixtures, [.. handles]), Is.EqualTo(Describe(source.GasMixtures, [.. handles])));
        Assert.That(target.ComputeStateHash(), Is.EqualTo(source.ComputeStateHash()));
        Assert.That(target.GasMixtures.Allocate(1f, 300f), Is.EqualTo(source.GasMixtures.Allocate(1f, 300f)));
    }

    [Test]
    public void CheckpointRestore_AppliesTheRestoredConfigToThePool()
    {
        using var world = CreateWorld(CreateConfig());
        var checkpoint = world.CaptureCheckpoint();
        world.SetAtmosConfig(CreateConfig(hydrogenHeatCapacity: 50f));
        var cell = world.GasMixtures.Allocate(1f, 300f);
        world.GasMixtures.Release(cell.Slot, cell.Generation);

        world.RestoreCheckpoint(checkpoint);

        var restored = world.GasMixtures.Allocate(1f, 300f);
        world.GasMixtures.AddGas(restored.Slot, restored.Generation, Hydrogen, 1f, 300f);
        world.GasMixtures.AddGas(restored.Slot, restored.Generation, Hydrogen, 1f, 600f);
        Assert.That(world.GasMixtures.GetTemperature(restored.Slot, restored.Generation), Is.EqualTo(450f).Within(0.01f));
    }

    [Test]
    public void CheckpointWireRoundTrip_KeepsTheHashAndRestores()
    {
        var config = CreateConfig();
        using var source = CreateWorld(config);
        var first = AllocateHotFuel(source.GasMixtures);
        var second = source.GasMixtures.Allocate(2f, 350f);
        source.GasMixtures.AddGas(second.Slot, second.Generation, Water, 3f, 350f);
        var third = source.GasMixtures.Allocate(1f, 300f);
        source.GasMixtures.Release(third.Slot, third.Generation);
        source.Tick();

        var checkpoint = source.CaptureCheckpoint();
        var sink = new StateBinarySink();
        NumosState.Write(ref sink, checkpoint);
        var reader = new StateReader(sink.WrittenSpan);
        var decoded = NumosState.Read<AtmosWorldCheckpoint>(ref reader);

        Assert.That(decoded.ComputeStateHash(), Is.EqualTo(checkpoint.ComputeStateHash()));

        using var target = CreateWorld(config);
        target.RestoreCheckpoint(decoded);
        var handles = new[] { first, second, third };
        Assert.That(Describe(target.GasMixtures, handles), Is.EqualTo(Describe(source.GasMixtures, handles)));
        Assert.That(target.ComputeStateHash(), Is.EqualTo(source.ComputeStateHash()));
    }

    [Test]
    public void PoolCheckpoint_RejectsInconsistentCells()
    {
        static AtmosGasMixturePoolCheckpoint CaptureAfter(Action<AtmosGasMixturePool> corrupt)
        {
            using var pool = new AtmosGasMixturePool();
            pool.Allocate(1f, 300f);
            corrupt(pool);
            return pool.CaptureCheckpoint();
        }

        var valid = CaptureAfter(static _ => { });
        Assert.That(() => AtmosGasMixturePool.ValidateCheckpoint(valid), Throws.Nothing);

        var badVolume = CaptureAfter(static pool => pool.GetPageVolumes(0)[0] = float.NaN);
        var volumeOnFreeCell = CaptureAfter(static pool => pool.GetPageVolumes(0)[1] = 1f);
        var wrongClassification = CaptureAfter(
            static pool => pool.GetPage(0).SetVoxelClassification(0, VoxelClassification.RoomSolid));

        foreach (var checkpoint in new[] { badVolume, volumeOnFreeCell, wrongClassification })
            Assert.That(() => AtmosGasMixturePool.ValidateCheckpoint(checkpoint), Throws.ArgumentException);

        Assert.That(
            () => new AtmosGasMixturePool().RestoreCheckpoint(badVolume),
            Throws.ArgumentException);
    }

    [Test]
    public void PoolCheckpoint_RejectsTruncatedData()
    {
        using var pool = new AtmosGasMixturePool();
        pool.Allocate(1f, 300f);
        var sink = new StateBinarySink();
        NumosState.Write(ref sink, pool.CaptureCheckpoint());
        byte[] bytes = sink.WrittenSpan.ToArray();

        Assert.That(
            () =>
            {
                var reader = new StateReader(bytes.AsSpan(0, bytes.Length - 40));
                NumosState.Read<AtmosGasMixturePoolCheckpoint>(ref reader);
            },
            Throws.InstanceOf<InvalidDataException>());
    }

    [Test]
    public void GridOnlyRestore_IsRejectedWhileAMixtureIsAllocated()
    {
        using var simulation = new AtmosSimulation(2, 2, 2);
        simulation.CreateAndRegisterChunk(default);
        var checkpoint = simulation.CaptureCheckpoint();
        var cell = simulation.World.GasMixtures.Allocate(1f, 300f);

        Assert.That(
            () => simulation.RestoreCheckpoint(checkpoint),
            Throws.InvalidOperationException.With.Message.Contains("gas mixtures"));

        simulation.World.GasMixtures.Release(cell.Slot, cell.Generation);
        Assert.That(() => simulation.RestoreCheckpoint(checkpoint), Throws.Nothing);
    }

    [Test]
    public void PoolCall_BlocksUntilATickOnAnotherThreadEnds()
    {
        using var world = CreateWorld(CreateConfig());
        var cell = world.GasMixtures.Allocate(1f, 300f);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        world.Solvers.Register(
            "block",
            _ =>
            {
                entered.Set();
                Assert.That(release.Wait(TimeSpan.FromSeconds(30)), Is.True);
            });

        var tick = Task.Run(world.Tick);
        Assert.That(entered.Wait(TimeSpan.FromSeconds(30)), Is.True);

        var read = Task.Run(() => (float)world.GasMixtures.GetTemperature(cell.Slot, cell.Generation));
        var allocate = Task.Run(() => world.GasMixtures.Allocate(1f, 310f));
        Assert.That(Task.WaitAny([read, allocate], TimeSpan.FromMilliseconds(300)), Is.EqualTo(-1));
        Assert.That(tick.IsCompleted, Is.False);

        release.Set();
        Assert.That(Task.WaitAll([tick, read, allocate], TimeSpan.FromSeconds(30)), Is.True);
        Assert.That(read.Result, Is.EqualTo(300f));
        Assert.That(world.GasMixtures.GetTemperature(allocate.Result.Slot, allocate.Result.Generation), Is.EqualTo(310f));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void SimulationLifecycleCallWaitingOnTheWorldGateDoesNotBlockVoxelMixtures(bool dispose)
    {
        using var world = new AtmosWorld(CreateConfig());
        var simulation = world.CreateSimulation(2, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        var voxel = simulation.GetVoxelGasMixture(chunk, 0);
        voxel.SetMoles("Nitrogen", 1f);

        Task lifecycle;
        Task<float> read;
        lock (world.Gate)
        {
            lifecycle = dispose
                ? Task.Run(simulation.Dispose)
                : Task.Run(() => simulation.SetAtmosConfig(CreateConfig(30f)));

            // The call now waits for the world gate. If it held the simulation's gate meanwhile, a voxel call (world
            // mixture gate, then simulation gate) would wait too. A world-gate holder that then wants the mixture
            // gate would close a cycle.
            Thread.Sleep(200);
            read = Task.Run(() => voxel.GetMoles("Nitrogen"));
            Assert.That(read.Wait(TimeSpan.FromSeconds(10)), Is.True, "The voxel call waited for the lifecycle call.");
        }

        Assert.That(lifecycle.Wait(TimeSpan.FromSeconds(10)), Is.True);
        Assert.That(read.Result, Is.EqualTo(1f));
    }

    [Test]
    public void ConfigChangeWaitsForAMixtureOperationInFlight()
    {
        using var world = new AtmosWorld(CreateConfig());
        var simulation = world.CreateSimulation(2, 1, 1);
        simulation.CreateAndRegisterChunk(default);
        using var canister = world.CreateGasMixture(1f, 300f);
        var before = simulation.Config;

        Task write;
        Task<bool> configure;
        lock (world.GasMixtures.StateGate)
        {
            // The write takes the world mixture gate, then waits here for the pool gate.
            write = Task.Run(() => canister.SetMoles("Nitrogen", 1f));
            Thread.Sleep(200);
            configure = Task.Run(() => world.SetAtmosConfig(CreateConfig(30f)));
            Thread.Sleep(200);

            // The write validated against the configuration it started with. No part of the world may switch to a
            // new one before the write records its result.
            Assert.That(simulation.Config, Is.SameAs(before));
        }

        Assert.That(Task.WaitAll([write, configure], TimeSpan.FromSeconds(10)), Is.True);
        Assert.That(simulation.Config, Is.Not.SameAs(before));
        Assert.That(canister.GetMoles("Nitrogen"), Is.EqualTo(1f));
    }
}
