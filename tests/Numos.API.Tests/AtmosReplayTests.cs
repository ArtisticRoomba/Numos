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
        var chunk = simulation.CreateAndRegisterChunk(default);
        var initial = simulation.CaptureCheckpoint();
        simulation.StartRecording();
        var checkpoints = new ConcurrentBag<AtmosSimulationCheckpoint>();
        Parallel.For(
            0,
            32,
            index =>
            {
                simulation.SetVoxelTemperature(chunk, 0, 270f + index);
                simulation.Tick();
                checkpoints.Add(simulation.CaptureCheckpoint());
            });

        var recording = simulation.StopRecording();
        Assert.That(
            recording.Operations.Select(static operation => operation.Sequence),
            Is.EqualTo(Enumerable.Range(1, 32).Select(static sequence => (ulong)sequence)));

        foreach (var checkpoint in checkpoints)
        {
            simulation.ReplayTo(initial, recording.Operations, checkpoint.Position);
            Assert.That(simulation.ComputeStateHash(), Is.EqualTo(checkpoint.ComputeStateHash()));
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
        var first = simulation.CreateAndRegisterChunk(default);
        var second = simulation.CreateAndRegisterChunk(Int3.PosX);
        simulation.AddGasToVoxel(first, 3, 1, 0, 1, 2.25f, 420f);
        simulation.AddGasToVoxel(first, 3, 1, 0, 0, 8f, 250f);
        var start = simulation.CaptureCheckpoint();
        simulation.StartRecording();
        var references = new List<(AtmosSimulationCheckpoint Checkpoint, AtmosStateHash Hash)>();
        for (int tick = 0; tick < 24; tick++)
        {
            if (tick == 2) simulation.SetVoxelTemperature(first, 3, 1, 0, 330f);
            if (tick == 4) simulation.SleepChunk(second);
            if (tick == 6) simulation.WakeRoom(second, 0);
            if (tick == 8) simulation.SetChunkBoundaryClassification(first, VoxelClassification.RoomSolid);
            if (tick == 10) simulation.SetVoxelClassification(first, 3, 1, 0, VoxelClassification.RoomUnassigned);
            if (tick == 12) simulation.SetAtmosConfig(new AtmosConfig(simulation.Config) { ThermalConductance = 0.3f });
            if (tick == 14) simulation.UnregisterChunk(second);
            if (tick == 16) second = simulation.CreateAndRegisterChunk(Int3.PosX);
            simulation.Tick();
            references.Add((simulation.CaptureCheckpoint(), simulation.ComputeStateHash()));
        }

        var recording = simulation.StopRecording();
        Assert.That(references[1].Checkpoint.Chunks[1].Gases, Is.Not.Empty, "The corpus must exercise cross-chunk flow.");

        foreach (var reference in references.AsEnumerable().Reverse().Concat(references))
        {
            simulation.ReplayTo(start, recording.Operations, reference.Hash.Position);
            Assert.That(simulation.ComputeStateHash(), Is.EqualTo(reference.Hash), $"Replay at {reference.Hash.Position}");
            simulation.Tick();
            var continued = simulation.ComputeStateHash();
            simulation.RestoreCheckpoint(reference.Checkpoint);
            Assert.That(simulation.ComputeStateHash(), Is.EqualTo(reference.Hash));
            simulation.Tick();
            Assert.That(simulation.ComputeStateHash(), Is.EqualTo(continued), "Immediate continuation must match.");
        }
    }

    [Test]
    public void MixtureOperations_ReplayResolvedGridEffectsWithoutDetachedContainerState()
    {
        using var simulation = CreateSimulation();
        var chunk = simulation.CreateAndRegisterChunk(default);
        var initial = simulation.CaptureCheckpoint();
        var voxel = simulation.GetVoxelGasMixture(chunk, 0);
        var other = simulation.GetVoxelGasMixture(chunk, 1);
        var canister = simulation.CreateGasMixture(2f, 280f);
        canister.SetMoles(1, 3f);
        canister.SetMoles(0, 8f);
        simulation.StartRecording();
        var hashes = new List<AtmosStateHash>();
        voxel.SetMoles(1, 2f);
        hashes.Add(simulation.ComputeStateHash());
        voxel.AdjustMoles(1, -0.1f);
        hashes.Add(simulation.ComputeStateHash());
        voxel.AddGas(0, 3f, 400f);
        hashes.Add(simulation.ComputeStateHash());
        voxel.Temperature = 340f;
        hashes.Add(simulation.ComputeStateHash());
        canister.TransferRatioTo(voxel, 0.25f);
        hashes.Add(simulation.ComputeStateHash());
        voxel.TransferTo(other, 0.5f);
        hashes.Add(simulation.ComputeStateHash());
        voxel.RemoveRatio(0.1f);
        hashes.Add(simulation.ComputeStateHash());
        other.Clear();
        hashes.Add(simulation.ComputeStateHash());
        var recording = simulation.StopRecording();
        canister.Clear();

        foreach (var hash in hashes)
        {
            simulation.ReplayTo(initial, recording.Operations, hash.Position);
            Assert.That(simulation.ComputeStateHash(), Is.EqualTo(hash));
        }

        Assert.That(() => voxel.TotalMoles, Throws.InvalidOperationException);
        Assert.That(canister.TotalMoles, Is.Zero, "Detached canisters are outside the restored state domain.");
    }

    [Test]
    public void CustomSolver_UsesNormalApiWithoutRecordingAndCanSuppressHostEffects()
    {
        using var simulation = CreateSimulation();
        var chunk = simulation.CreateAndRegisterChunk(default);
        int hostEffects = 0;
        simulation.Solvers.Register(
            "host-v1",
            world =>
            {
                world.AddGasToVoxel(chunk, 0, 0, 0.25f, 300f);
                if (!world.IsReplaying) hostEffects++;
            });

        var initial = simulation.CaptureCheckpoint();
        simulation.StartRecording();
        simulation.Tick();
        simulation.Tick();
        simulation.Solvers.SetEnabled("host-v1", false);
        simulation.Solvers.SetEnabled("host-v1", false);
        simulation.Tick();
        var hash = simulation.ComputeStateHash();
        var recording = simulation.StopRecording();
        simulation.ReplayTo(initial, recording.Operations, recording.Head);
        Assert.Multiple(() =>
        {
            Assert.That(simulation.ComputeStateHash(), Is.EqualTo(hash));
            Assert.That(recording.Operations, Has.Count.EqualTo(1));
            Assert.That(hostEffects, Is.EqualTo(2));
            Assert.That(simulation.IsReplaying, Is.False);
        });
    }

    [Test]
    public void ConfigRegistryAndUpdateResidual_AreReplayedAndRestoreContinuesTheClock()
    {
        using var simulation = CreateSimulation();
        var initial = simulation.CaptureCheckpoint();
        simulation.StartRecording();
        simulation.Update(0.017f);
        simulation.Update(0.187f);
        var config = new AtmosConfig(simulation.Config);
        config.GasRegistry.Add(new GasProperties { Name = "Later" });
        simulation.SetAtmosConfig(config);
        simulation.Update(0.001f);
        var checkpoint = simulation.CaptureCheckpoint();
        var hash = simulation.ComputeStateHash();
        var recording = simulation.StopRecording();
        simulation.Update(0.075f);
        var continued = simulation.ComputeStateHash();
        simulation.ReplayTo(initial, recording.Operations, recording.Head);
        Assert.That(simulation.ComputeStateHash(), Is.EqualTo(hash));
        simulation.Update(0.075f);
        Assert.That(simulation.ComputeStateHash(), Is.EqualTo(continued));
        simulation.RestoreCheckpoint(checkpoint);
        Assert.That(simulation.Config.GasRegistry, Has.Count.EqualTo(3));
    }

    [Test]
    public void ExactSequence_ExcludesLaterOperationsAtSameTick_AndOverloadsCanonicalize()
    {
        using var simulation = CreateSimulation();
        var chunk = simulation.CreateAndRegisterChunk(default);
        var initial = simulation.CaptureCheckpoint();
        simulation.StartRecording();
        simulation.SetVoxelTemperature(chunk, 1, 2, 0, 333f);
        var firstHash = simulation.ComputeStateHash();
        simulation.SetVoxelTemperature(chunk, 9, 333f);
        Assert.That(() => simulation.AddGasToVoxel(chunk, 0, -1, 1f, 300f), Throws.TypeOf<ArgumentOutOfRangeException>());
        var recording = simulation.StopRecording();
        Assert.That(recording.Operations, Has.Count.EqualTo(2));
        Assert.That(recording.Operations[0].Operation, Is.EqualTo(recording.Operations[1].Operation));
        simulation.ReplayTo(initial, recording.Operations, firstHash.Position);
        Assert.That(simulation.ComputeStateHash(), Is.EqualTo(firstHash));
    }

    [Test]
    public void IncompatibleCheckpointAndInvalidHistory_LeaveStateUnchanged()
    {
        using var simulation = CreateSimulation();
        using var incompatible = new AtmosSimulation(new TestAtmosConfig(), 1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        var checkpoint = simulation.CaptureCheckpoint();
        simulation.AddGasToVoxel(chunk, 0, 0, 1f, 300f);
        var hash = simulation.ComputeStateHash();
        Assert.That(() => simulation.RestoreCheckpoint(incompatible.CaptureCheckpoint()), Throws.ArgumentException);
        Assert.That(simulation.ComputeStateHash(), Is.EqualTo(hash));
        AtmosRecordedOperation[] gap = [new(new AtmosTimelinePosition(0, 2), new SleepChunkOperation(default))];
        Assert.That(() => simulation.ReplayTo(checkpoint, gap, new AtmosTimelinePosition(1, 2)), Throws.ArgumentException);
        Assert.That(simulation.ComputeStateHash(), Is.EqualTo(hash));
        AtmosRecordedOperation[] invalid = [new(new AtmosTimelinePosition(0, 1), new SleepChunkOperation(Int3.PosX))];
        Assert.That(
            () => simulation.ReplayTo(checkpoint, invalid, new AtmosTimelinePosition(0, 1)),
            Throws.TypeOf<KeyNotFoundException>());

        Assert.That(simulation.ComputeStateHash(), Is.EqualTo(hash));
        simulation.Solvers.Register("incompatible", _ => { });
        hash = simulation.ComputeStateHash();
        Assert.That(() => simulation.RestoreCheckpoint(checkpoint), Throws.ArgumentException);
        Assert.That(simulation.ComputeStateHash(), Is.EqualTo(hash));
    }

    [Test]
    public void LateAuthoritativeOperation_RollsBackOnlySimulationAndCatchesUp()
    {
        using var authority = CreateSimulation();
        using var replica = CreateSimulation();
        var chunk = authority.CreateAndRegisterChunk(default);
        var initial = authority.CaptureCheckpoint();
        replica.RestoreCheckpoint(initial);
        authority.StartRecording();
        for (int tick = 0; tick < 8; tick++)
        {
            if (tick == 3) authority.AddGasToVoxel(chunk, 0, 0, 5f, 310f);
            authority.Tick();
            replica.Tick();
        }

        var recording = authority.StopRecording();
        Assert.That(replica.ComputeStateHash(), Is.Not.EqualTo(authority.ComputeStateHash()));
        replica.ReplayTo(initial, recording.Operations, recording.Head);
        Assert.That(replica.ComputeStateHash(), Is.EqualTo(authority.ComputeStateHash()));
    }

    [Test]
    public void DefinitionChangesDuringRecording_AndCaptureInsideTick_AreRejected()
    {
        using var simulation = CreateSimulation();
        simulation.Solvers.Register("probe", world => Assert.That(world.CaptureCheckpoint, Throws.InvalidOperationException));
        simulation.StartRecording();
        Assert.That(() => simulation.Solvers.Register("new", _ => { }), Throws.InvalidOperationException);
        Assert.That(() => simulation.Solvers.Unregister("probe"), Throws.InvalidOperationException);
        simulation.Tick();
        var hash = simulation.ComputeStateHash();
        var checkpoint = simulation.CaptureCheckpoint();
        simulation.StopRecording();
        simulation.Tick();
        Assert.That(simulation.ResumeRecording, Throws.InvalidOperationException);
        simulation.RestoreCheckpoint(checkpoint);
        simulation.ResumeRecording();
        Assert.That(simulation.ComputeStateHash(), Is.EqualTo(hash));
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
        Assert.That(first.ComputeStateHash(), Is.EqualTo(second.ComputeStateHash()));
        second.SetVoxelTemperature(new AtmosChunkHandle(default), 0, BitConverter.Int32BitsToSingle(unchecked((int)0x80000000)));
        Assert.That(first.ComputeStateHash(), Is.Not.EqualTo(second.ComputeStateHash()));
    }

    [Test]
    public void Restore_InvalidatesPresentationVersionsAndRetainsDisabledStageCaches()
    {
        using var simulation = CreateSimulation();
        var chunk = simulation.CreateAndRegisterChunk(default);
        simulation.AddGasToVoxel(chunk, 0, 0, 2f, 320f);
        simulation.Solvers.SetEnabled(AtmosBuiltInSolvers.Advection, false);
        simulation.GetVoxelGasMixture(chunk, 0).Temperature = 280f;
        var checkpoint = simulation.CaptureCheckpoint();
        var oldVersion = simulation.GetChunkSnapshot(chunk).Version;
        simulation.Tick();
        var hash = simulation.ComputeStateHash();
        simulation.RestoreCheckpoint(checkpoint);
        Assert.That(simulation.TryGetChunkSnapshot(chunk, oldVersion, out _), Is.True);
        simulation.Tick();
        Assert.That(simulation.ComputeStateHash(), Is.EqualTo(hash));
    }
}