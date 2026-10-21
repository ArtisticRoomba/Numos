using System.Collections.Concurrent;
using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Replay;
using Numos.Maths;

namespace Numos.API.Tests;

[TestFixture]
public sealed class AtmosReplayTests
{
    [Test]
    public void ConcurrentCaptureAndMutations_HaveOneCoherentReplayOrder()
    {
        using var simulation = CreateSimulation();
        var world = simulation.World;
        var chunk = simulation.CreateAndRegisterChunk(default);
        var initial = world.CaptureCheckpoint();
        world.StartRecording();
        var checkpoints = new ConcurrentBag<AtmosWorldCheckpoint>();
        Parallel.For(
            0,
            32,
            index =>
            {
                simulation.SetVoxelTemperature(chunk, 0, 270f + index);
                world.Tick();
                checkpoints.Add(world.CaptureCheckpoint());
            });

        var recording = world.StopRecording();
        Assert.That(
            recording.Operations.Select(static operation => operation.Sequence),
            Is.EqualTo(Enumerable.Range(1, 32).Select(static sequence => (ulong)sequence)));

        foreach (var checkpoint in checkpoints)
        {
            world.ReplayTo(initial, recording.Operations, checkpoint.Position);
            Assert.That(world.ComputeStateHash(), Is.EqualTo(checkpoint.ComputeStateHash()));
        }
    }

    private static AtmosSimulation CreateSimulation()
    {
        return new AtmosSimulation(
            new AtmosConfig
            {
                GasRegistry =
                [
                    new GasProperties { Name = "A", DiffusionCoefficient = 0.02f },
                    new GasProperties { Name = "B", MolarHeatCapacityAtConstantVolume = 32f }
                ],
                SleepThreshold = 10
            },
            4,
            4,
            1);
    }

    [Test]
    public void Corpus_ReplaysEveryPositionAndContinuesFromSleepingAndAwakeCheckpoints()
    {
        using var simulation = CreateSimulation();
        var world = simulation.World;
        var first = simulation.CreateAndRegisterChunk(default);
        var second = simulation.CreateAndRegisterChunk(Int3.PosX);
        simulation.AddGasToVoxel(first, 3, 1, 0, 1, 2.25f, 420f);
        simulation.AddGasToVoxel(first, 3, 1, 0, 0, 8f, 250f);
        var start = world.CaptureCheckpoint();
        world.StartRecording();
        var references = new List<(AtmosWorldCheckpoint Checkpoint, AtmosWorldStateHash Hash)>();
        for (int tick = 0; tick < 24; tick++)
        {
            if (tick == 2) simulation.SetVoxelTemperature(first, 3, 1, 0, 330f);
            if (tick == 4) simulation.SleepChunk(second);
            if (tick == 6) simulation.WakeChunk(second);
            if (tick == 8) simulation.SetChunkBoundaryClassification(first, VoxelClassification.RoomSolid);
            if (tick == 10) simulation.SetVoxelClassification(first, 3, 1, 0, VoxelClassification.RoomUnassigned);
            if (tick == 12) world.SetAtmosConfig(new AtmosConfig(world.Config) { ThermalConductance = 0.3f });
            if (tick == 14) simulation.UnregisterChunk(second);
            if (tick == 16) second = simulation.CreateAndRegisterChunk(Int3.PosX);
            world.Tick();
            references.Add((world.CaptureCheckpoint(), world.ComputeStateHash()));
        }

        var recording = world.StopRecording();
        Assert.That(
            references[1].Checkpoint.Simulations[0].Checkpoint.Chunks[1].Gases,
            Is.Not.Empty,
            "The corpus must exercise cross-chunk flow.");

        foreach (var reference in references.AsEnumerable().Reverse().Concat(references))
        {
            world.ReplayTo(start, recording.Operations, reference.Hash.Position);
            Assert.That(world.ComputeStateHash(), Is.EqualTo(reference.Hash), $"Replay at {reference.Hash.Position}");
            world.Tick();
            var continued = world.ComputeStateHash();
            world.RestoreCheckpoint(reference.Checkpoint);
            Assert.That(world.ComputeStateHash(), Is.EqualTo(reference.Hash));
            world.Tick();
            Assert.That(world.ComputeStateHash(), Is.EqualTo(continued), "Immediate continuation must match.");
        }
    }

    [Test]
    public void MixtureOperations_ReplayResolvedGridEffectsWithoutDetachedContainerState()
    {
        using var simulation = CreateSimulation();
        var world = simulation.World;
        var chunk = simulation.CreateAndRegisterChunk(default);
        var initial = world.CaptureCheckpoint();
        var voxel = simulation.GetVoxelGasMixture(chunk, 0);
        var other = simulation.GetVoxelGasMixture(chunk, 1);
        var canister = simulation.CreateGasMixture(2f, 280f);
        canister.SetMoles(1, 3f);
        canister.SetMoles(0, 8f);
        world.StartRecording();
        var hashes = new List<AtmosWorldStateHash>();
        voxel.SetMoles(1, 2f);
        hashes.Add(world.ComputeStateHash());
        voxel.AdjustMoles(1, -0.1f);
        hashes.Add(world.ComputeStateHash());
        voxel.AddGas(0, 3f, 400f);
        hashes.Add(world.ComputeStateHash());
        voxel.Temperature = 340f;
        hashes.Add(world.ComputeStateHash());
        canister.TransferRatioTo(voxel, 0.25f);
        hashes.Add(world.ComputeStateHash());
        voxel.TransferTo(other, 0.5f);
        hashes.Add(world.ComputeStateHash());
        voxel.RemoveRatio(0.1f);
        hashes.Add(world.ComputeStateHash());
        other.Clear();
        hashes.Add(world.ComputeStateHash());
        var recording = world.StopRecording();
        canister.Clear();

        foreach (var hash in hashes)
        {
            world.ReplayTo(initial, recording.Operations, hash.Position);
            Assert.That(world.ComputeStateHash(), Is.EqualTo(hash));
        }

        Assert.That(() => voxel.TotalMoles, Throws.InvalidOperationException);
        Assert.That(canister.TotalMoles, Is.Zero, "Detached canisters are outside the restored state domain.");
    }

    [Test]
    public void MomentumOperations_ReplayAndEmptyingAVoxelZeroesItsMomentum()
    {
        using var simulation = CreateSimulation();
        var world = simulation.World;
        var chunk = simulation.CreateAndRegisterChunk(default);
        simulation.SetVoxelClassification(chunk, 2, new VoxelClassification(VoxelClassification.RoomSolid));
        simulation.AddGasToVoxel(chunk, 0, 1, 2f, 300f);
        simulation.AddGasToVoxel(chunk, 1, 0, 3f, 310f);
        var initial = world.CaptureCheckpoint();
        world.StartRecording();
        var hashes = new List<AtmosWorldStateHash>();

        simulation.SetVoxelMomentum(chunk, 0, 1.5f, -2.5f, 3.5f);
        hashes.Add(world.ComputeStateHash());
        simulation.SetVoxelMomentum(chunk, 1, 0, 0, 4f, 5f, 6f);
        hashes.Add(world.ComputeStateHash());

        // Rejected writes leave no trace: a solid voxel and a voxel with no gas both stay at zero momentum.
        var beforeRejected = world.ComputeStateHash();
        simulation.SetVoxelMomentum(chunk, 2, 9f, 9f, 9f);
        simulation.SetVoxelMomentum(chunk, 3, 9f, 9f, 9f);
        Assert.That(world.ComputeStateHash(), Is.EqualTo(beforeRejected));

        // Adding gas keeps momentum; a mixture write that removes the last mole drops it.
        simulation.AddGasToVoxel(chunk, 0, 0, 1f, 300f);
        hashes.Add(world.ComputeStateHash());
        simulation.GetVoxelGasMixture(chunk, 1).Clear();
        hashes.Add(world.ComputeStateHash());
        simulation.GetVoxelGasMixture(chunk, 0).SetMoles(1, 0f);
        simulation.GetVoxelGasMixture(chunk, 0).SetMoles(0, 0f);
        hashes.Add(world.ComputeStateHash());
        var recording = world.StopRecording();

        Assert.That(
            recording.Operations
                .Select(static op => op.Operation)
                .OfType<AtmosWorldSimulationOperation>()
                .Count(static op => op.Operation.Code == AtmosOperationCode.SetVoxelMomentum),
            Is.EqualTo(2),
            "Only accepted momentum writes are recorded.");

        var emptied = simulation.CaptureCheckpoint().Chunks.Single();
        Assert.Multiple(() =>
        {
            Assert.That(emptied.MomentumX.Take(2), Is.All.Zero);
            Assert.That(emptied.MomentumY.Take(2), Is.All.Zero);
            Assert.That(emptied.MomentumZ.Take(2), Is.All.Zero);
        });

        foreach (var hash in hashes)
        {
            world.ReplayTo(initial, recording.Operations, hash.Position);
            Assert.That(world.ComputeStateHash(), Is.EqualTo(hash));
        }

        world.ReplayTo(initial, recording.Operations, hashes[2].Position);
        var replayed = simulation.CaptureCheckpoint().Chunks.Single();
        Assert.Multiple(() =>
        {
            Assert.That(replayed.MomentumX[0], Is.EqualTo(1.5f));
            Assert.That(replayed.MomentumY[0], Is.EqualTo(-2.5f));
            Assert.That(replayed.MomentumZ[0], Is.EqualTo(3.5f));
            Assert.That(replayed.MomentumX[1], Is.EqualTo(4f));
            Assert.That(replayed.MomentumY[1], Is.EqualTo(5f));
            Assert.That(replayed.MomentumZ[1], Is.EqualTo(6f));
        });
    }

    [Test]
    public void SetVoxelMomentum_RejectsNonFiniteComponentsWithoutRecording()
    {
        using var simulation = CreateSimulation();
        var world = simulation.World;
        var chunk = simulation.CreateAndRegisterChunk(default);
        simulation.AddGasToVoxel(chunk, 0, 0, 2f, 300f);
        world.StartRecording();
        var before = world.ComputeStateHash();

        Assert.Multiple(() =>
        {
            Assert.That(() => simulation.SetVoxelMomentum(chunk, 0, float.NaN, 0f, 0f), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => simulation.SetVoxelMomentum(chunk, 0, 0f, float.PositiveInfinity, 0f), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => simulation.SetVoxelMomentum(chunk, 0, 0, 0, 0f, 0f, float.NegativeInfinity), Throws.TypeOf<ArgumentOutOfRangeException>());
        });

        var recording = world.StopRecording();
        Assert.Multiple(() =>
        {
            Assert.That(world.ComputeStateHash(), Is.EqualTo(before));
            Assert.That(recording.Operations, Is.Empty);
        });
    }

    [Test]
    public void CustomSolver_UsesNormalApiWithoutRecordingAndCanSuppressHostEffects()
    {
        using var simulation = CreateSimulation();
        var world = simulation.World;
        var chunk = simulation.CreateAndRegisterChunk(default);
        int hostEffects = 0;
        world.Solvers.Register(
            "host-v1",
            _ =>
            {
                simulation.AddGasToVoxel(chunk, 0, 0, 0.25f, 300f);
                if (!simulation.IsReplaying) hostEffects++;
            });

        var initial = world.CaptureCheckpoint();
        world.StartRecording();
        world.Tick();
        world.Tick();
        world.Solvers.SetEnabled("host-v1", false);
        world.Solvers.SetEnabled("host-v1", false);
        world.Tick();
        var hash = world.ComputeStateHash();
        var recording = world.StopRecording();
        world.ReplayTo(initial, recording.Operations, recording.Head);
        Assert.Multiple(() =>
        {
            Assert.That(world.ComputeStateHash(), Is.EqualTo(hash));
            Assert.That(recording.Operations, Has.Count.EqualTo(1));
            Assert.That(hostEffects, Is.EqualTo(2));
            Assert.That(simulation.IsReplaying, Is.False);
        });
    }

    [Test]
    public void ConfigRegistryAndUpdateResidual_AreReplayedAndRestoreContinuesTheClock()
    {
        using var simulation = CreateSimulation();
        var world = simulation.World;
        var initial = world.CaptureCheckpoint();
        world.StartRecording();
        world.Update(0.017f);
        world.Update(0.187f);
        var config = new AtmosConfig(world.Config);
        config.GasRegistry.Add(new GasProperties { Name = "Later" });
        world.SetAtmosConfig(config);
        world.Update(0.001f);
        var checkpoint = world.CaptureCheckpoint();
        var hash = world.ComputeStateHash();
        var recording = world.StopRecording();
        world.Update(0.075f);
        var continued = world.ComputeStateHash();
        world.ReplayTo(initial, recording.Operations, recording.Head);
        Assert.That(world.ComputeStateHash(), Is.EqualTo(hash));
        world.Update(0.075f);
        Assert.That(world.ComputeStateHash(), Is.EqualTo(continued));
        world.RestoreCheckpoint(checkpoint);
        Assert.That(simulation.Config.GasRegistry, Has.Count.EqualTo(3));
    }

    [Test]
    public void ExactSequence_ExcludesLaterOperationsAtSameTick_AndOverloadsCanonicalize()
    {
        using var simulation = CreateSimulation();
        var world = simulation.World;
        var chunk = simulation.CreateAndRegisterChunk(default);
        var initial = world.CaptureCheckpoint();
        world.StartRecording();
        simulation.SetVoxelTemperature(chunk, 1, 2, 0, 333f);
        var firstHash = world.ComputeStateHash();
        simulation.SetVoxelTemperature(chunk, 9, 333f);
        Assert.That(() => simulation.AddGasToVoxel(chunk, 0, -1, 1f, 300f), Throws.TypeOf<ArgumentOutOfRangeException>());
        var recording = world.StopRecording();
        Assert.That(recording.Operations, Has.Count.EqualTo(2));
        Assert.That(recording.Operations[0].Operation, Is.EqualTo(recording.Operations[1].Operation));
        world.ReplayTo(initial, recording.Operations, firstHash.Position);
        Assert.That(world.ComputeStateHash(), Is.EqualTo(firstHash));
    }

    [Test]
    public void IncompatibleCheckpointAndInvalidHistory_LeaveStateUnchanged()
    {
        using var simulation = CreateSimulation();
        using var incompatible = new AtmosSimulation(new TestAtmosConfig(), 1, 1, 1);
        var world = simulation.World;
        var chunk = simulation.CreateAndRegisterChunk(default);
        var checkpoint = world.CaptureCheckpoint();
        simulation.AddGasToVoxel(chunk, 0, 0, 1f, 300f);
        var hash = world.ComputeStateHash();
        Assert.That(() => simulation.RestoreCheckpoint(incompatible.CaptureCheckpoint()), Throws.ArgumentException);
        Assert.That(() => world.RestoreCheckpoint(incompatible.World.CaptureCheckpoint()), Throws.ArgumentException);
        Assert.That(world.ComputeStateHash(), Is.EqualTo(hash));
        AtmosWorldRecordedOperation[] gap =
        [
            new(new AtmosTimelinePosition(0, 2), new AtmosWorldSimulationOperation(simulation.Id, new SleepChunkOperation(default)))
        ];

        Assert.That(() => world.ReplayTo(checkpoint, gap, new AtmosTimelinePosition(1, 2)), Throws.ArgumentException);
        Assert.That(world.ComputeStateHash(), Is.EqualTo(hash));
        AtmosWorldRecordedOperation[] invalid =
        [
            new(new AtmosTimelinePosition(0, 1), new AtmosWorldSimulationOperation(simulation.Id, new SleepChunkOperation(Int3.PosX)))
        ];

        Assert.That(
            () => world.ReplayTo(checkpoint, invalid, new AtmosTimelinePosition(0, 1)),
            Throws.TypeOf<KeyNotFoundException>());

        Assert.That(world.ComputeStateHash(), Is.EqualTo(hash));
        world.Solvers.Register("incompatible", _ => { });
        hash = world.ComputeStateHash();
        Assert.That(() => world.RestoreCheckpoint(checkpoint), Throws.ArgumentException);
        Assert.That(world.ComputeStateHash(), Is.EqualTo(hash));
    }

    [Test]
    public void LateAuthoritativeOperation_RollsBackOnlySimulationAndCatchesUp()
    {
        using var authority = CreateSimulation();
        using var replica = CreateSimulation();
        var chunk = authority.CreateAndRegisterChunk(default);
        var initial = authority.World.CaptureCheckpoint();
        replica.World.RestoreCheckpoint(initial);
        authority.World.StartRecording();
        for (int tick = 0; tick < 8; tick++)
        {
            if (tick == 3) authority.AddGasToVoxel(chunk, 0, 0, 5f, 310f);
            authority.World.Tick();
            replica.World.Tick();
        }

        var recording = authority.World.StopRecording();
        Assert.That(replica.World.ComputeStateHash(), Is.Not.EqualTo(authority.World.ComputeStateHash()));
        replica.World.ReplayTo(initial, recording.Operations, recording.Head);
        Assert.That(replica.World.ComputeStateHash(), Is.EqualTo(authority.World.ComputeStateHash()));
    }

    [Test]
    public void DefinitionChangesDuringRecording_AndCaptureInsideTick_AreRejected()
    {
        using var simulation = CreateSimulation();
        var world = simulation.World;
        world.Solvers.Register(
            "probe",
            _ =>
            {
                Assert.That(simulation.CaptureCheckpoint, Throws.InvalidOperationException);
                Assert.That(world.CaptureCheckpoint, Throws.InvalidOperationException);
            });

        world.StartRecording();
        Assert.That(() => world.Solvers.Register("new", _ => { }), Throws.InvalidOperationException);
        Assert.That(() => world.Solvers.Unregister("probe"), Throws.InvalidOperationException);
        Assert.That(() => simulation.RestoreCheckpoint(simulation.CaptureCheckpoint()), Throws.InvalidOperationException);
        world.Tick();
        var hash = world.ComputeStateHash();
        var checkpoint = world.CaptureCheckpoint();
        world.StopRecording();
        world.Tick();
        Assert.That(world.ResumeRecording, Throws.InvalidOperationException);
        world.RestoreCheckpoint(checkpoint);
        world.ResumeRecording();
        Assert.That(world.ComputeStateHash(), Is.EqualTo(hash));
    }

    [Test]
    public void Hash_UsesCanonicalChunkOrderAndRawFloatingPointBits()
    {
        using var first = CreateSimulation();
        using var second = CreateSimulation();
        first.CreateAndRegisterChunk(default);
        first.CreateAndRegisterChunk(new Int3(2, 0, 0));
        second.CreateAndRegisterChunk(new Int3(2, 0, 0));
        second.CreateAndRegisterChunk(default);
        Assert.That(first.World.ComputeStateHash(), Is.EqualTo(second.World.ComputeStateHash()));
        Assert.That(first.CaptureCheckpoint().ComputeStateHash(), Is.EqualTo(second.CaptureCheckpoint().ComputeStateHash()));
        second.SetVoxelTemperature(new AtmosChunkHandle(default), 0, BitConverter.Int32BitsToSingle(unchecked((int)0x80000000)));
        Assert.That(first.World.ComputeStateHash(), Is.Not.EqualTo(second.World.ComputeStateHash()));
        Assert.That(first.CaptureCheckpoint().ComputeStateHash(), Is.Not.EqualTo(second.CaptureCheckpoint().ComputeStateHash()));
    }

    [Test]
    public void Restore_InvalidatesPresentationVersionsAndRetainsDisabledStageCaches()
    {
        using var simulation = CreateSimulation();
        var world = simulation.World;
        var chunk = simulation.CreateAndRegisterChunk(default);
        simulation.AddGasToVoxel(chunk, 0, 0, 2f, 320f);
        world.Solvers.SetEnabled(AtmosBuiltInSolvers.EulerFlow, false);
        simulation.GetVoxelGasMixture(chunk, 0).Temperature = 280f;
        var checkpoint = world.CaptureCheckpoint();
        var oldVersion = simulation.GetChunkSnapshot(chunk).Version;
        world.Tick();
        var hash = world.ComputeStateHash();
        world.RestoreCheckpoint(checkpoint);
        Assert.That(simulation.TryGetChunkSnapshot(chunk, oldVersion, out _), Is.True);
        world.Tick();
        Assert.That(world.ComputeStateHash(), Is.EqualTo(hash));
    }

    [Test]
    public void SimulationCheckpoint_RestoresGridOnlyAndLeavesWorldStateAlone()
    {
        using var simulation = CreateSimulation();
        var world = simulation.World;
        var chunk = simulation.CreateAndRegisterChunk(default);
        simulation.AddGasToVoxel(chunk, 0, 0, 2f, 320f);
        var grid = simulation.CaptureCheckpoint();
        world.Tick();
        world.Solvers.SetEnabled(AtmosBuiltInSolvers.EulerFlow, false);
        world.SetAtmosConfig(new AtmosConfig(world.Config) { ThermalConductance = 0.4f });
        simulation.SetVoxelTemperature(chunk, 0, 400f);

        simulation.RestoreCheckpoint(grid);

        Assert.Multiple(() =>
        {
            Assert.That(simulation.CaptureCheckpoint().ComputeStateHash(), Is.EqualTo(grid.ComputeStateHash()));
            Assert.That(world.TickCount, Is.EqualTo(1));
            Assert.That(simulation.TickCount, Is.EqualTo(1));
            Assert.That(world.Config.ThermalConductance, Is.EqualTo(0.4f));
            Assert.That(world.Solvers.Steps.Single(step => step.Name == AtmosBuiltInSolvers.EulerFlow).IsEnabled, Is.False);
        });
    }
}