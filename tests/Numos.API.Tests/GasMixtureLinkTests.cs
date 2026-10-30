using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.Maths;
using Numos.Serialization;

namespace Numos.API.Tests;

/// <summary>
///     Explicit links that end at a standalone <see cref="GasMixture" />: transport between unequal volumes, release,
///     validation, replay, and checkpoints.
/// </summary>
[TestFixture]
public sealed class GasMixtureLinkTests
{
    private const string Air = "Air";
    private const float Kelvin = 300f;

    [Test]
    public void CanisterAndVoxelEqualizePressureAndConserveMolesAndEnergy()
    {
        using var world = CreateWorld(CreateConfig());
        var simulation = world.CreateSimulation(1, 1, 1);
        var chunk = CreateOpenChunk(simulation);
        var room = simulation.GetVoxelGasMixture(chunk, 0);
        room.AddGas(Air, 40f, Kelvin);
        using var canister = world.CreateGasMixture(0.1f, 400f);
        canister.AddGas(Air, 10f, 400f);

        float startMoles = canister.TotalMoles + room.TotalMoles;
        float startEnergy = canister.TotalMoles * canister.Temperature + room.TotalMoles * room.Temperature;
        float startGap = canister.Pressure - room.Pressure;
        world.CreatePortal(canister.CellRef, simulation.GetCellRef(chunk, 0), ExplicitLinkFlags.All, 1f);

        for (int tick = 0; tick < 600; tick++)
            world.Tick();

        float endMoles = canister.TotalMoles + room.TotalMoles;
        float endEnergy = canister.TotalMoles * canister.Temperature + room.TotalMoles * room.Temperature;
        Assert.Multiple(() =>
        {
            Assert.That(startGap, Is.GreaterThan(100_000f));
            Assert.That(canister.Pressure, Is.EqualTo(room.Pressure).Within(0.005f).Percent);
            Assert.That(endMoles, Is.EqualTo(startMoles).Within(0.001f).Percent);
            Assert.That(endEnergy, Is.EqualTo(startEnergy).Within(0.1f).Percent);
            Assert.That(canister.Temperature, Is.EqualTo(room.Temperature).Within(1f));
        });
    }

    [Test]
    public void LargeCanisterFillsAVoxelOfEqualPressure()
    {
        using var world = CreateWorld(CreateConfig());
        var simulation = world.CreateSimulation(1, 1, 1);
        var chunk = CreateOpenChunk(simulation);
        var room = simulation.GetVoxelGasMixture(chunk, 0);
        using var canister = world.CreateGasMixture(5f, Kelvin);
        canister.AddGas(Air, 100f, Kelvin);
        world.CreatePortal(canister.CellRef, simulation.GetCellRef(chunk, 0), ExplicitLinkFlags.GasTransport, 1f);

        for (int tick = 0; tick < 600; tick++)
            world.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(canister.Pressure, Is.EqualTo(room.Pressure).Within(0.005f).Percent);
            Assert.That(canister.TotalMoles + room.TotalMoles, Is.EqualTo(100f).Within(0.001f).Percent);

            // Pressure equalization splits the gas in proportion to the volumes.
            Assert.That(room.TotalMoles, Is.EqualTo(100f / 6f).Within(0.1f).Percent);
        });
    }

    [Test]
    public void CanisterToCanisterLinkWorksWithoutAnySimulation()
    {
        using var world = CreateWorld(CreateConfig());
        using var small = world.CreateGasMixture(0.5f, 350f);
        using var large = world.CreateGasMixture(2f, Kelvin);
        small.AddGas(Air, 20f, 350f);
        large.AddGas(Air, 1f, Kelvin);
        float startMoles = small.TotalMoles + large.TotalMoles;
        float startEnergy = small.TotalMoles * small.Temperature + large.TotalMoles * large.Temperature;
        float startGap = small.Pressure - large.Pressure;

        world.CreatePortal(small.CellRef, large.CellRef, ExplicitLinkFlags.All, 1f);
        for (int tick = 0; tick < 600; tick++)
            world.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(world.Simulations, Is.Empty);
            Assert.That(world.ActiveLinkCount, Is.EqualTo(1));
            Assert.That(startGap, Is.GreaterThan(100_000f));
            Assert.That(small.Pressure, Is.EqualTo(large.Pressure).Within(0.005f).Percent);
            Assert.That(small.TotalMoles + large.TotalMoles, Is.EqualTo(startMoles).Within(0.001f).Percent);
            Assert.That(
                small.TotalMoles * small.Temperature + large.TotalMoles * large.Temperature,
                Is.EqualTo(startEnergy).Within(0.1f).Percent);
        });
    }

    [Test]
    public void LinkedCanisterCachesMatchWhatTheVolumeImplies()
    {
        using var world = CreateWorld(CreateConfig());
        using var first = world.CreateGasMixture(0.25f, Kelvin);
        using var second = world.CreateGasMixture(3f, Kelvin);
        first.AddGas(Air, 12f, Kelvin);
        world.CreatePortal(first.CellRef, second.CellRef, ExplicitLinkFlags.GasTransport, 1f);

        for (int tick = 0; tick < 40; tick++)
            world.Tick();

        // Writing the temperature back recomputes the cached pressure, heat capacity, and vacuum flag from the cell's
        // own volume. The state hash covers the caches, so it only holds if the transport pass wrote the same values.
        var before = world.ComputeStateHash();
        first.Temperature = first.Temperature;
        second.Temperature = second.Temperature;

        Assert.That(world.ComputeStateHash(), Is.EqualTo(before));
    }

    [Test]
    public void DisposingAMixtureDestroysItsLinkSetsAtTheNextTickBoundary()
    {
        using var world = CreateWorld(CreateConfig());
        var simulation = world.CreateSimulation(1, 1, 1);
        var chunk = CreateOpenChunk(simulation);
        var canister = world.CreateGasMixture(1f, Kelvin);
        using var other = world.CreateGasMixture(1f, Kelvin);
        var toVoxel = world.CreatePortal(canister.CellRef, simulation.GetCellRef(chunk, 0));
        var toCanister = world.CreatePortal(canister.CellRef, other.CellRef);
        var unrelated = world.CreatePortal(other.CellRef, simulation.GetCellRef(chunk, 0));
        world.Tick();
        Assert.That(world.ActiveLinkCount, Is.EqualTo(3));

        canister.Dispose();

        var sets = world.GetLinkSets().ToDictionary(static set => set.Handle);
        Assert.Multiple(() =>
        {
            Assert.That(sets[toVoxel.Links].State, Is.EqualTo(AtmosWorldLinkSetState.PendingRemoval));
            Assert.That(sets[toCanister.Links].State, Is.EqualTo(AtmosWorldLinkSetState.PendingRemoval));
            Assert.That(sets[unrelated.Links].State, Is.EqualTo(AtmosWorldLinkSetState.Active));
        });

        world.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(world.ActiveLinkCount, Is.EqualTo(1));
            Assert.That(world.GetLinkSets().Select(static set => set.Handle), Is.EqualTo(new[] { unrelated.Links }));
            Assert.That(() => world.DestroyPortal(toVoxel), Throws.ArgumentException);
        });
    }

    [Test]
    public void DisposingAMixtureCancelsLinkSetsThatHaveNotActivated()
    {
        using var world = CreateWorld(CreateConfig());
        using var other = world.CreateGasMixture(1f, Kelvin);
        var canister = world.CreateGasMixture(1f, Kelvin);
        var portal = world.CreatePortal(canister.CellRef, other.CellRef);

        canister.Dispose();
        world.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(world.GetLinkSets(), Is.Empty);
            Assert.That(world.ActiveLinkCount, Is.Zero);
            Assert.That(() => world.DestroyPortal(portal), Throws.ArgumentException);
        });
    }

    [Test]
    public void ReusedSlotDoesNotInheritTheReleasedMixturesLinks()
    {
        using var world = CreateWorld(CreateConfig());
        var simulation = world.CreateSimulation(1, 1, 1);
        var chunk = CreateOpenChunk(simulation);
        var room = simulation.GetVoxelGasMixture(chunk, 0);
        room.AddGas(Air, 5f, Kelvin);
        var old = world.CreateGasMixture(1f, Kelvin);
        var oldRef = old.CellRef;
        world.CreatePortal(oldRef, simulation.GetCellRef(chunk, 0));
        world.Tick();

        old.Dispose();
        float roomMoles = room.TotalMoles;
        using var replacement = world.CreateGasMixture(1f, Kelvin);
        Assert.That(replacement.CellRef.LocalVoxelIndex, Is.EqualTo(oldRef.LocalVoxelIndex), "The replacement takes the lowest free slot.");
        Assert.That(replacement.CellRef, Is.Not.EqualTo(oldRef));

        for (int tick = 0; tick < 10; tick++)
            world.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(replacement.TotalMoles, Is.Zero);
            Assert.That(room.TotalMoles, Is.EqualTo(roomMoles));
        });
    }

    [Test]
    public void LinkingToAStaleOrMissingMixtureIsRejected()
    {
        using var world = CreateWorld(CreateConfig());
        var simulation = world.CreateSimulation(1, 1, 1);
        var chunk = CreateOpenChunk(simulation);
        var voxel = simulation.GetCellRef(chunk, 0);
        var canister = world.CreateGasMixture(1f, Kelvin);
        var released = canister.CellRef;
        canister.Dispose();
        using var live = world.CreateGasMixture(1f, Kelvin);
        live.Dispose();

        Assert.Multiple(() =>
        {
            Assert.That(() => _ = canister.CellRef, Throws.TypeOf<ObjectDisposedException>());
            Assert.That(() => world.CreatePortal(released, voxel), Throws.ArgumentException);
            Assert.That(() => world.CreatePortal(voxel, released), Throws.ArgumentException);
            Assert.That(() => world.CreatePortal(MixtureRef(40, 0), voxel), Throws.ArgumentException);
            Assert.That(() => world.CreatePortal(MixtureRef(0, 255), voxel), Throws.ArgumentException);
            Assert.That(() => world.CreatePortal(MixtureRef(-1, 0), voxel), Throws.ArgumentException);
            Assert.That(
                () => world.CreatePortal(
                    new AtmosCellRef(new AtmosSimulationId(-1, 1), new AtmosChunkHandle(new Int3(0, 1, 0)), 0),
                    voxel),
                Throws.ArgumentException);
            Assert.That(world.GetLinkSets(), Is.Empty);
        });
    }

    [Test]
    public void LinkingThroughAReferenceToAReusedSlotIsRejected()
    {
        using var world = CreateWorld(CreateConfig());
        var simulation = world.CreateSimulation(1, 1, 1);
        var chunk = CreateOpenChunk(simulation);
        var old = world.CreateGasMixture(1f, Kelvin);
        var staleRef = old.CellRef;
        old.Dispose();
        using var replacement = world.CreateGasMixture(1f, Kelvin);
        replacement.AddGas(Air, 10f, Kelvin);
        var voxel = simulation.GetCellRef(chunk, 0);

        Assert.Multiple(() =>
        {
            Assert.That(() => world.CreatePortal(staleRef, voxel), Throws.ArgumentException);
            Assert.That(() => world.CreateLinks([new ExplicitLinkDefinition(staleRef, voxel)]), Throws.ArgumentException);
            Assert.That(world.GetLinkSets(), Is.Empty);
        });

        world.Tick();
        Assert.That(replacement.TotalMoles, Is.EqualTo(10f));
    }

    [Test]
    public void MixtureCellRefIsDistinctFromEveryGridCell()
    {
        using var world = CreateWorld(CreateConfig());
        var simulation = world.CreateSimulation(1, 1, 1);
        var chunk = CreateOpenChunk(simulation);
        using var first = world.CreateGasMixture(1f, Kelvin);
        using var second = world.CreateGasMixture(1f, Kelvin);

        Assert.Multiple(() =>
        {
            Assert.That(first.CellRef.IsGasMixture, Is.True);
            Assert.That(simulation.GetCellRef(chunk, 0).IsGasMixture, Is.False);
            Assert.That(first.CellRef.Simulation.IsGasMixturePool, Is.True);
            Assert.That(first.CellRef.Simulation.IsValid, Is.False);
            Assert.That(new AtmosSimulationId(-1, 0).IsGasMixturePool, Is.False);
            Assert.That(first.CellRef, Is.Not.EqualTo(second.CellRef));
            Assert.That(first.CellRef.CompareTo(simulation.GetCellRef(chunk, 0)), Is.LessThan(0));
            Assert.That(first.CellRef.CompareTo(second.CellRef), Is.LessThan(0));
            Assert.That(() => world.CreatePortal(first.CellRef, first.CellRef), Throws.ArgumentException);
        });

        world.CreatePortal(first.CellRef, second.CellRef);
        Assert.That(() => world.CreatePortal(second.CellRef, first.CellRef), Throws.ArgumentException);
    }

    [Test]
    public void ReplayReproducesLinkedCanisterThatIsLaterDisposed()
    {
        using var world = CreateWorld(CreateConfig());
        var simulation = world.CreateSimulation(1, 1, 1);
        var chunk = CreateOpenChunk(simulation);
        var room = simulation.GetVoxelGasMixture(chunk, 0);
        room.AddGas(Air, 3f, Kelvin);
        var start = world.CaptureCheckpoint();
        world.StartRecording();
        var marks = new List<AtmosWorldStateHash>();

        var canister = world.CreateGasMixture(0.2f, 380f);
        canister.AddGas(Air, 8f, 380f);
        marks.Add(world.ComputeStateHash());
        var portal = world.CreatePortal(canister.CellRef, simulation.GetCellRef(chunk, 0));
        marks.Add(world.ComputeStateHash());
        for (int tick = 0; tick < 5; tick++)
            world.Tick();

        marks.Add(world.ComputeStateHash());
        canister.Dispose();
        marks.Add(world.ComputeStateHash());
        world.Tick();
        marks.Add(world.ComputeStateHash());
        world.Tick();
        var recording = world.StopRecording();
        var liveHash = world.ComputeStateHash();

        Assert.That(portal.Links.IsValid, Is.True);
        foreach (var mark in marks.AsEnumerable().Reverse().Concat(marks))
        {
            world.ReplayTo(start, recording.Operations, mark.Position);
            Assert.That(world.ComputeStateHash(), Is.EqualTo(mark), $"Replay at {mark.Position}");
        }

        world.ReplayTo(start, recording.Operations, recording.Head);
        Assert.Multiple(() =>
        {
            Assert.That(world.ComputeStateHash(), Is.EqualTo(liveHash));
            Assert.That(world.GetLinkSets(), Is.Empty);
        });

        using var fresh = CreateWorld(new AtmosConfig(start.Config));
        fresh.ReplayTo(start, recording.Operations, recording.Head);
        Assert.That(fresh.ComputeStateHash(), Is.EqualTo(liveHash));
    }

    [Test]
    public void CheckpointWithALiveMixtureLinkRestoresAndContinuesIdentically()
    {
        using var world = CreateWorld(CreateConfig());
        var simulation = world.CreateSimulation(1, 1, 1);
        var chunk = CreateOpenChunk(simulation);
        simulation.GetVoxelGasMixture(chunk, 0).AddGas(Air, 20f, Kelvin);
        using var canister = world.CreateGasMixture(0.3f, 420f);
        canister.AddGas(Air, 6f, 420f);
        using var second = world.CreateGasMixture(2f, Kelvin);
        world.CreatePortal(canister.CellRef, simulation.GetCellRef(chunk, 0));
        world.CreatePortal(canister.CellRef, second.CellRef);
        for (int tick = 0; tick < 7; tick++)
            world.Tick();

        var checkpoint = world.CaptureCheckpoint();
        for (int tick = 0; tick < 30; tick++)
            world.Tick();

        var expected = world.ComputeStateHash();

        world.RestoreCheckpoint(checkpoint);
        Assert.That(world.ComputeStateHash(), Is.EqualTo(checkpoint.ComputeStateHash()));
        for (int tick = 0; tick < 30; tick++)
            world.Tick();

        Assert.That(world.ComputeStateHash(), Is.EqualTo(expected), "Same world");

        using var other = CreateWorld(new AtmosConfig(checkpoint.Config));
        other.RestoreCheckpoint(checkpoint);
        for (int tick = 0; tick < 30; tick++)
            other.Tick();

        Assert.That(other.ComputeStateHash(), Is.EqualTo(expected), "Fresh world");
    }

    [Test]
    public void ArchiveRoundTripKeepsAMixtureLink()
    {
        using var world = CreateWorld(CreateConfig());
        var simulation = world.CreateSimulation(1, 1, 1);
        var chunk = CreateOpenChunk(simulation);
        using var canister = world.CreateGasMixture(0.5f, 350f);
        canister.AddGas(Air, 5f, 350f);
        var timeline = new AtmosWorldReplayTimeline(world, 1);
        world.CreatePortal(canister.CellRef, simulation.GetCellRef(chunk, 0));
        world.Tick();
        world.Tick();
        var archive = timeline.CaptureReplay();

        var metadata = new NumosReplayMetadata("Link", DateTimeOffset.UnixEpoch, "tests", "1", "1");
        using var stream = new MemoryStream();
        NumosWorldReplaySerializer.Serialize(stream, new NumosWorldReplayDocument(metadata, archive));
        stream.Position = 0;
        var restored = NumosWorldReplaySerializer.Deserialize(stream).Replay;

        Assert.Multiple(() =>
        {
            Assert.That(restored.InitialStateHash, Is.EqualTo(archive.InitialStateHash));
            Assert.That(restored.HeadStateHash, Is.EqualTo(archive.HeadStateHash));
        });

        using var imported = CreateWorld(new AtmosConfig(restored.InitialCheckpoint.Config));
        AtmosWorldReplayTimeline.Import(imported, restored, 1).ReturnToHead();
        Assert.Multiple(() =>
        {
            Assert.That(imported.ComputeStateHash(), Is.EqualTo(archive.HeadStateHash));
            Assert.That(imported.ActiveLinkCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void RestoreThatDropsALinkedMixtureLeavesNoLinkToTheFreeSlot()
    {
        using var world = CreateWorld(CreateConfig());
        var simulation = world.CreateSimulation(1, 1, 1);
        var chunk = CreateOpenChunk(simulation);
        var empty = world.CaptureCheckpoint();
        using var canister = world.CreateGasMixture(1f, Kelvin);
        world.CreatePortal(canister.CellRef, simulation.GetCellRef(chunk, 0));
        world.Tick();
        var linked = world.CaptureCheckpoint();

        world.RestoreCheckpoint(empty);
        Assert.Multiple(() =>
        {
            Assert.That(world.GetLinkSets(), Is.Empty);
            Assert.That(world.ActiveLinkCount, Is.Zero);
        });

        world.RestoreCheckpoint(linked);
        Assert.That(world.ActiveLinkCount, Is.EqualTo(1));
    }

    [Test]
    public void NeighborTopologyListsTheMixtureOnTheVoxelSideWithoutFailing()
    {
        using var world = CreateWorld(CreateConfig());
        var simulation = world.CreateSimulation(1, 1, 1);
        var chunk = CreateOpenChunk(simulation);
        using var canister = world.CreateGasMixture(1f, Kelvin);
        var voxel = simulation.GetCellRef(chunk, 0);
        AtmosNeighbor[] seen = [];
        AtmosNeighborEdge[] edges = [];
        bool mixtureHasChunk = true;
        bool mixtureThrows = false;
        world.Solvers.Register(
            "probe",
            context =>
            {
                seen = context.Topology.GetNeighbors(voxel).ToArray();
                edges = context.Topology.GetOwnedEdges().ToArray();
                mixtureHasChunk = context.TryGetChunk(canister.CellRef, out _);
                try
                {
                    _ = context.Topology.GetNeighborCount(canister.CellRef);
                }
                catch (ArgumentException)
                {
                    mixtureThrows = true;
                }
            },
            new AtmosNeighborSelection("probe", false, static _ => true));

        world.CreatePortal(canister.CellRef, voxel);
        world.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(seen.Select(static neighbor => neighbor.Cell), Is.EqualTo(new[] { canister.CellRef }));
            Assert.That(edges, Has.Length.EqualTo(1));
            Assert.That(edges[0].First, Is.EqualTo(canister.CellRef));
            Assert.That(mixtureHasChunk, Is.False);
            Assert.That(mixtureThrows, Is.True);
        });
    }

    [TestCase(0.001f, 1f)]
    [TestCase(0.005f, 2.5f)]
    public void TinyCanisterAtRoomPressureStaysAtRoomPressure(float canisterVolume, float voxelVolume)
    {
        using var world = CreateWorld(CreateConfig(voxelVolume));
        var simulation = world.CreateSimulation(1, 1, 1);
        var chunk = CreateOpenChunk(simulation);
        var room = simulation.GetVoxelGasMixture(chunk, 0);
        room.AddGas(Air, 40f * voxelVolume, Kelvin);
        using var canister = world.CreateGasMixture(canisterVolume, Kelvin);
        canister.AddGas(Air, 40f * canisterVolume, Kelvin);
        Assert.That(canister.Pressure, Is.EqualTo(room.Pressure).Within(0.01f).Percent);
        world.CreatePortal(canister.CellRef, simulation.GetCellRef(chunk, 0), ExplicitLinkFlags.GasTransport, 1f);

        for (int tick = 0; tick < 2000; tick++)
            world.Tick();

        Assert.That(canister.Pressure, Is.EqualTo(room.Pressure).Within(0.01f).Percent);
    }

    [TestCase(0.001f, 1f)]
    [TestCase(0.005f, 2.5f)]
    public void TinyCanisterSettlesOnTheRoomPressureAndComposition(float canisterVolume, float voxelVolume)
    {
        using var world = CreateWorld(CreateConfig(voxelVolume));
        var simulation = world.CreateSimulation(1, 1, 1);
        var chunk = CreateOpenChunk(simulation);
        var room = simulation.GetVoxelGasMixture(chunk, 0);
        room.AddGas(Air, 40f * voxelVolume, Kelvin);

        // Pure fuel at twice the room's pressure.
        using var canister = world.CreateGasMixture(canisterVolume, Kelvin);
        canister.AddGas("Fuel", 80f * canisterVolume, Kelvin);
        float startMoles = canister.TotalMoles + room.TotalMoles;
        world.CreatePortal(canister.CellRef, simulation.GetCellRef(chunk, 0), ExplicitLinkFlags.GasTransport, 1f);

        for (int tick = 0; tick < 8000; tick++)
            world.Tick();

        float canisterFuelFraction = canister.GetMoles("Fuel") / canister.TotalMoles;
        float roomFuelFraction = room.GetMoles("Fuel") / room.TotalMoles;
        Assert.Multiple(() =>
        {
            Assert.That(canister.Pressure, Is.EqualTo(room.Pressure).Within(0.01f).Percent);
            Assert.That(canisterFuelFraction, Is.EqualTo(roomFuelFraction).Within(0.1f).Percent);
            Assert.That(canister.TotalMoles + room.TotalMoles, Is.EqualTo(startMoles).Within(0.001f).Percent);
        });
    }

    private static AtmosCellRef MixtureRef(int page, ushort cell)
    {
        return new AtmosCellRef(new AtmosSimulationId(-1, 1), new AtmosChunkHandle(new Int3(page, 0, 0)), cell);
    }

    private static AtmosChunkHandle CreateOpenChunk(AtmosSimulation simulation)
    {
        var chunk = simulation.CreateAndRegisterChunk(default);
        simulation.SetChunkClassification(chunk, new VoxelClassification(1));
        simulation.SetVoxelTemperature(chunk, 0, Kelvin);
        return chunk;
    }

    // The tests count ticks at 20 Hz, where explicit gas transport runs every tick.
    private static AtmosWorld CreateWorld(AtmosConfig config)
    {
        return new AtmosWorld(config, AtmosTransportModel.Euler, 20);
    }

    private static AtmosConfig CreateConfig(float voxelVolume = 1f)
    {
        return new AtmosConfig
        {
            GasRegistry = [SolverConfigurationArchiveTests.Air, SolverConfigurationArchiveTests.Fuel],
            VoxelVolume = voxelVolume
        };
    }
}
