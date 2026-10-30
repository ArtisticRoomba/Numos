using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.Serialization;
using Numos.Tests;

namespace Numos.API.Tests;

/// <summary>
///     Pool-backed <see cref="GasMixture" /> handles: disposal, stale handles, cross-simulation transfers, ticking,
///     replay, archives, and threading.
/// </summary>
[TestFixture]
public sealed class GasMixturePoolApiTests
{
    // Vacuum cleanup clears this field, so it shows whether a canister write ran that cleanup.
    private static readonly ChunkField<float> EmptyCellMark =
        ChunkField<float>.Register("tests/gas-mixture-pool/empty-cell-mark", rollback: true, clearOnVacuum: true);

    // Opts out of vacuum cleanup, so only the slot reset clears it.
    private static readonly ChunkField<float> SlotData =
        ChunkField<float>.Register("tests/gas-mixture-pool/slot-data", rollback: true);

    [Test]
    public void Dispose_IsIdempotentAndRejectsFurtherUse()
    {
        using var world = new AtmosWorld(CreateConfig());
        var mixture = world.CreateGasMixture(1f, 300f);
        using var other = world.CreateGasMixture(1f, 300f);
        mixture.SetMoles("Fuel", 1f);

        mixture.Dispose();

        Assert.That(() => mixture.Dispose(), Throws.Nothing);
        Assert.Multiple(() =>
        {
            Assert.That(() => _ = mixture.TotalMoles, Throws.TypeOf<ObjectDisposedException>());
            Assert.That(() => _ = mixture.Volume, Throws.TypeOf<ObjectDisposedException>());
            Assert.That(() => mixture.Volume = 2f, Throws.TypeOf<ObjectDisposedException>());
            Assert.That(() => mixture.Temperature = 301f, Throws.TypeOf<ObjectDisposedException>());
            Assert.That(() => mixture.SetMoles("Fuel", 1f), Throws.TypeOf<ObjectDisposedException>());
            Assert.That(() => mixture.Clear(), Throws.TypeOf<ObjectDisposedException>());
            Assert.That(() => mixture.Remove(1f), Throws.TypeOf<ObjectDisposedException>());
            Assert.That(() => mixture.Clone(), Throws.TypeOf<ObjectDisposedException>());
            Assert.That(() => mixture.GetSnapshot(), Throws.TypeOf<ObjectDisposedException>());
            Assert.That(() => mixture.TransferTo(other, 1f), Throws.TypeOf<ObjectDisposedException>());
            Assert.That(() => other.TransferTo(mixture, 1f), Throws.TypeOf<ObjectDisposedException>());
        });
    }

    [Test]
    public void Dispose_FreesTheSlotAndTheOldHandleStaysDisposed()
    {
        using var world = new AtmosWorld(CreateConfig());
        var first = world.CreateGasMixture(1f, 300f);
        first.SetMoles("Fuel", 3f);
        first.Dispose();

        using var second = world.CreateGasMixture(5f, 280f);

        Assert.Multiple(() =>
        {
            Assert.That(second.TotalMoles, Is.Zero);
            Assert.That(second.Volume, Is.EqualTo(5f));
            Assert.That(() => _ = first.TotalMoles, Throws.TypeOf<ObjectDisposedException>());
        });

        second.SetMoles("Fuel", 1f);
        Assert.That(() => _ = first.TotalMoles, Throws.TypeOf<ObjectDisposedException>());
    }

    [Test]
    public void Dispose_AfterTheWorldIsDisposedDoesNothing()
    {
        var world = new AtmosWorld(CreateConfig());
        var mixture = world.CreateGasMixture(1f, 300f);
        world.Dispose();

        Assert.That(() => mixture.Dispose(), Throws.Nothing);
        Assert.That(() => _ = mixture.TotalMoles, Throws.TypeOf<ObjectDisposedException>());
        Assert.That(() => world.CreateGasMixture(1f), Throws.TypeOf<ObjectDisposedException>());
    }

    [Test]
    public void RestoreDropsHandlesAndALaterRestoreRevivesThem()
    {
        using var world = new AtmosWorld(CreateConfig());
        var empty = world.CaptureCheckpoint();
        using var mixture = world.CreateGasMixture(1f, 300f);
        mixture.SetMoles("Fuel", 2f);
        var live = world.CaptureCheckpoint();

        mixture.SetMoles("Fuel", 7f);
        world.RestoreCheckpoint(empty);
        Assert.That(() => _ = mixture.TotalMoles, Throws.TypeOf<InvalidOperationException>());
        Assert.That(() => mixture.SetMoles("Fuel", 1f), Throws.TypeOf<InvalidOperationException>());

        world.RestoreCheckpoint(live);
        Assert.That(mixture.GetMoles("Fuel"), Is.EqualTo(2f));
    }

    [Test]
    public void DisposingAStaleHandleMarksItDisposedAndLeavesThePoolAlone()
    {
        // A donor world reuses slot zero three times, so its checkpoint holds a live mixture at generation 3.
        using var donor = new AtmosWorld(CreateConfig());
        donor.CreateGasMixture(1f, 300f).Dispose();
        donor.CreateGasMixture(1f, 300f).Dispose();
        using var kept = donor.CreateGasMixture(2f, 290f);
        var occupied = donor.CaptureCheckpoint();

        using var world = new AtmosWorld(CreateConfig());
        var stale = world.CreateGasMixture(1f, 300f);
        world.RestoreCheckpoint(occupied);
        Assert.That(() => _ = stale.TotalMoles, Throws.TypeOf<InvalidOperationException>());

        var before = world.ComputeStateHash();
        Assert.That(() => stale.Dispose(), Throws.Nothing);
        Assert.Multiple(() =>
        {
            Assert.That(world.ComputeStateHash(), Is.EqualTo(before), "The slot belongs to a newer allocation.");
            Assert.That(() => _ = stale.TotalMoles, Throws.TypeOf<ObjectDisposedException>());
        });
    }

    [Test]
    public void CanisterTransfersIntoVoxelsOfEverySimulationInTheWorld()
    {
        using var world = new AtmosWorld(CreateConfig());
        var firstSimulation = world.CreateSimulation(1, 1, 1);
        var secondSimulation = world.CreateSimulation(1, 1, 1);
        var first = firstSimulation.GetVoxelGasMixture(firstSimulation.CreateAndRegisterChunk(default), 0);
        var second = secondSimulation.GetVoxelGasMixture(secondSimulation.CreateAndRegisterChunk(default), 0);
        using var canister = world.CreateGasMixture(1f, 300f);
        canister.SetMoles("Air", 4f);

        Assert.Multiple(() =>
        {
            Assert.That(canister.TransferTo(first, 1f), Is.EqualTo(1f));
            Assert.That(canister.TransferRatioTo(second, 0.5f), Is.EqualTo(1.5f));
            Assert.That(first.GetMoles("Air"), Is.EqualTo(1f));
            Assert.That(second.GetMoles("Air"), Is.EqualTo(1.5f));
            Assert.That(canister.GetMoles("Air"), Is.EqualTo(1.5f));
            Assert.That(first.World, Is.SameAs(world));
            Assert.That(canister.World, Is.SameAs(world));
        });

        using var back = second.Remove(1f);
        Assert.Multiple(() =>
        {
            Assert.That(back.GetMoles("Air"), Is.EqualTo(1f));
            Assert.That(second.TransferTo(canister, 0.5f), Is.EqualTo(0.5f));
            Assert.That(canister.GetMoles("Air"), Is.EqualTo(2f));
        });
    }

    [Test]
    public void VoxelTransfersAcrossSimulationsOfOneWorldConserveMoles()
    {
        using var world = new AtmosWorld(CreateConfig());
        var firstSimulation = world.CreateSimulation(1, 1, 1);
        var secondSimulation = world.CreateSimulation(1, 1, 1);
        var first = firstSimulation.GetVoxelGasMixture(firstSimulation.CreateAndRegisterChunk(default), 0);
        var second = secondSimulation.GetVoxelGasMixture(secondSimulation.CreateAndRegisterChunk(default), 0);
        first.AddGas("Air", 4f, 300f);
        second.AddGas("Fuel", 2f, 400f);

        float moved = first.TransferTo(second, 1f);
        float movedBack = second.TransferRatioTo(first, 0.5f);

        Assert.Multiple(() =>
        {
            Assert.That(moved, Is.EqualTo(1f));
            Assert.That(movedBack, Is.EqualTo(1.5f));
            Assert.That(first.TotalMoles + second.TotalMoles, Is.EqualTo(6f).Within(1e-5f));
            Assert.That(first.TotalMoles, Is.EqualTo(4.5f).Within(1e-5f));
        });
    }

    [Test]
    public void TransfersToAndFromAnotherWorldAreRejectedWithoutMutation()
    {
        using var world = new AtmosWorld(CreateConfig());
        using var otherWorld = new AtmosWorld(CreateConfig());
        var otherSimulation = otherWorld.CreateSimulation(1, 1, 1);
        var otherVoxel = otherSimulation.GetVoxelGasMixture(otherSimulation.CreateAndRegisterChunk(default), 0);
        using var canister = world.CreateGasMixture(1f, 300f);
        using var foreign = otherWorld.CreateGasMixture(1f, 300f);
        canister.SetMoles("Air", 2f);
        foreign.SetMoles("Air", 2f);

        Assert.Multiple(() =>
        {
            Assert.That(() => canister.TransferTo(foreign, 1f), Throws.ArgumentException);
            Assert.That(() => foreign.TransferTo(canister, 1f), Throws.ArgumentException);
            Assert.That(() => canister.TransferRatioTo(otherVoxel, 1f), Throws.ArgumentException);
            Assert.That(() => otherVoxel.TransferTo(canister, 1f), Throws.ArgumentException);
            Assert.That(canister.TotalMoles, Is.EqualTo(2f));
            Assert.That(foreign.TotalMoles, Is.EqualTo(2f));
            Assert.That(otherVoxel.TotalMoles, Is.Zero);
        });
    }

    [Test]
    public void MixturesReactWhenTheWorldTicksWithoutAnySimulation()
    {
        using var world = new AtmosWorld(CreateReactiveConfig());
        using var canister = world.CreateGasMixture(2f, 300f);
        canister.SetMoles("Fuel", 10f);

        TickHelpers.TickFor(world, 0.2);

        Assert.Multiple(() =>
        {
            Assert.That(canister.GetMoles("Fuel"), Is.LessThan(10f));
            Assert.That(canister.GetMoles("Air"), Is.GreaterThan(0f));
            Assert.That(canister.TotalMoles, Is.EqualTo(10f).Within(1e-3f));
        });
    }

    [Test]
    public void ReplayReproducesPooledMixturesAtEveryPosition()
    {
        using var world = new AtmosWorld(CreateReactiveConfig());
        var simulation = world.CreateSimulation(2, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        simulation.SetChunkClassification(chunk, new VoxelClassification(1));
        var room = simulation.GetVoxelGasMixture(chunk, 1);
        var start = world.CaptureCheckpoint();
        world.StartRecording();
        var marks = new List<AtmosWorldStateHash>();

        using var canister = world.CreateGasMixture(2f, 320f);
        marks.Add(world.ComputeStateHash());
        canister.AddGas("Fuel", 6f, 320f);
        canister.AddGas("Air", 1f, 300f);
        marks.Add(world.ComputeStateHash());
        world.Tick();
        world.Tick();
        marks.Add(world.ComputeStateHash());
        var sample = canister.Remove(1f);
        marks.Add(world.ComputeStateHash());
        canister.TransferTo(room, 1f);
        marks.Add(world.ComputeStateHash());
        canister.Volume = 3f;
        marks.Add(world.ComputeStateHash());
        sample.Dispose();
        marks.Add(world.ComputeStateHash());
        world.Tick();
        world.Tick();
        marks.Add(world.ComputeStateHash());
        var recording = world.StopRecording();
        var liveHash = world.ComputeStateHash();
        var liveSnapshot = canister.GetSnapshot();

        Assert.That(
            recording.Operations.Select(static op => op.Code),
            Does.Contain(AtmosWorldOperationCode.CreateGasMixture)
                .And.Contain(AtmosWorldOperationCode.SetGasMixture)
                .And.Contain(AtmosWorldOperationCode.SetGasMixtureVolume)
                .And.Contain(AtmosWorldOperationCode.ReleaseGasMixture));

        foreach (var mark in marks.AsEnumerable().Reverse().Concat(marks))
        {
            world.ReplayTo(start, recording.Operations, mark.Position);
            Assert.That(world.ComputeStateHash(), Is.EqualTo(mark), $"Replay at {mark.Position}");
        }

        world.ReplayTo(start, recording.Operations, recording.Head);
        Assert.That(world.ComputeStateHash(), Is.EqualTo(liveHash));
        AssertSnapshotsEqual(canister.GetSnapshot(), liveSnapshot);
        Assert.That(() => _ = sample.TotalMoles, Throws.TypeOf<ObjectDisposedException>());

        using var fresh = new AtmosWorld(new AtmosConfig(start.Config));
        fresh.ReplayTo(start, recording.Operations, recording.Head);
        Assert.That(fresh.ComputeStateHash(), Is.EqualTo(liveHash));
    }

    [Test]
    public void VoxelRemoveRatioRecordsTheNewMixtureBeforeTheVoxelWrite()
    {
        using var world = new AtmosWorld(CreateConfig());
        var simulation = world.CreateSimulation(1, 1, 1);
        var voxel = simulation.GetVoxelGasMixture(simulation.CreateAndRegisterChunk(default), 0);
        voxel.AddGas("Air", 4f, 300f);
        world.StartRecording();

        using var removed = voxel.RemoveRatio(0.5f);
        var operations = world.StopRecording().Operations;

        Assert.Multiple(() =>
        {
            Assert.That(
                operations.Select(static op => op.Code),
                Is.EqualTo(
                    new[]
                    {
                        AtmosWorldOperationCode.CreateGasMixture,
                        AtmosWorldOperationCode.SetGasMixture,
                        AtmosWorldOperationCode.SimulationOperation
                    }));

            Assert.That(removed.GetMoles("Air"), Is.EqualTo(2f));
            Assert.That(voxel.GetMoles("Air"), Is.EqualTo(2f));
        });
    }

    [Test]
    public void ArchiveRoundTripKeepsLiveMixtures()
    {
        using var world = new AtmosWorld(CreateReactiveConfig());
        world.CreateSimulation(1, 1, 1);
        using var canister = world.CreateGasMixture(3f, 310f);
        canister.AddGas("Fuel", 4f, 310f);
        var timeline = new AtmosWorldReplayTimeline(world, 1);
        canister.AddGas("Air", 1f, 300f);
        world.Tick();
        using var copy = canister.Clone();
        world.Tick();
        var archive = timeline.CaptureReplay();

        var metadata = new NumosReplayMetadata("Pool", DateTimeOffset.UnixEpoch, "tests", "1", "1");
        using var stream = new MemoryStream();
        NumosWorldReplaySerializer.Serialize(stream, new NumosWorldReplayDocument(metadata, archive));
        stream.Position = 0;
        var restored = NumosWorldReplaySerializer.Deserialize(stream).Replay;

        Assert.Multiple(() =>
        {
            Assert.That(restored.InitialStateHash, Is.EqualTo(archive.InitialStateHash));
            Assert.That(restored.HeadStateHash, Is.EqualTo(archive.HeadStateHash));
            Assert.That(restored.InitialCheckpoint.ComputeStateHash(), Is.EqualTo(archive.InitialStateHash));
        });

        using var imported = new AtmosWorld(new AtmosConfig(restored.InitialCheckpoint.Config));
        AtmosWorldReplayTimeline.Import(imported, restored, 1).ReturnToHead();
        Assert.That(imported.ComputeStateHash(), Is.EqualTo(archive.HeadStateHash));
    }

    [Test]
    public void CanisterCallOnAnotherThreadWaitsForTheTick()
    {
        using var world = new AtmosWorld(CreateConfig());
        using var canister = world.CreateGasMixture(1f, 300f);
        canister.SetMoles("Air", 1f);
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

        var read = Task.Run(() => canister.GetMoles("Air"));
        var write = Task.Run(() => canister.AddGas("Air", 2f, 300f));
        var create = Task.Run(() => world.CreateGasMixture(1f, 300f));
        Assert.That(Task.WaitAny([read, write, create], TimeSpan.FromMilliseconds(300)), Is.EqualTo(-1));
        Assert.That(tick.IsCompleted, Is.False);

        release.Set();
        Assert.That(Task.WaitAll([tick, read, write, create], TimeSpan.FromSeconds(30)), Is.True);
        Assert.That(canister.GetMoles("Air"), Is.EqualTo(3f));
        create.Result.Dispose();
    }

    [Test]
    public void SolverCanisterCallFinishesWhileAHostCanisterCallWaits()
    {
        var world = new AtmosWorld(CreateConfig());
        var canister = world.CreateGasMixture(1f, 300f);
        var other = world.CreateGasMixture(1f, 300f);
        canister.SetMoles("Air", 1f);
        other.SetMoles("Air", 2f);
        using var entered = new ManualResetEventSlim();
        using var hostStarted = new ManualResetEventSlim();
        float seen = -1f;
        world.Solvers.Register(
            "touch-canister",
            _ =>
            {
                entered.Set();
                Assert.That(hostStarted.Wait(TimeSpan.FromSeconds(10)), Is.True);

                // Gives the host call time to block behind this tick before the callback touches a canister.
                Thread.Sleep(200);
                seen = other.GetMoles("Air");
            });

        var tick = Task.Run(world.Tick);
        Assert.That(entered.Wait(TimeSpan.FromSeconds(10)), Is.True);
        var read = Task.Run(
            () =>
            {
                hostStarted.Set();
                return canister.GetMoles("Air");
            });

        // A deadlock leaves both tasks blocked, and disposing the world would then block too.
        bool finished = Task.WaitAll([tick, read], TimeSpan.FromSeconds(10));
        Assert.That(finished, Is.True, "The solver callback and the host call deadlocked.");
        Assert.Multiple(() =>
        {
            Assert.That(seen, Is.EqualTo(2f));
            Assert.That(read.Result, Is.EqualTo(1f));
        });

        canister.Dispose();
        other.Dispose();
        world.Dispose();
    }

    [Test]
    public void ReplayAcceptsAChannelForAGasTheRegistryDropped()
    {
        using var world = new AtmosWorld(CreateConfig());
        using var canister = world.CreateGasMixture(1f, 300f);
        canister.SetMoles("Fuel", 1f);
        world.SetAtmosConfig(new AtmosConfig { GasRegistry = [SolverConfigurationArchiveTests.Air] });
        var start = world.CaptureCheckpoint();
        world.StartRecording();

        // The recorded content lists the page's Fuel channel, which the registry no longer has.
        canister.SetMoles("Air", 2f);
        var recording = world.StopRecording();
        var head = world.ComputeStateHash();

        world.ReplayTo(start, recording.Operations, head.Position);
        Assert.That(world.ComputeStateHash(), Is.EqualTo(head));
    }

    [Test]
    public void TemperatureOfAnEmptyCanisterReplaysWithItsFieldsKept()
    {
        using var world = new AtmosWorld(CreateConfig());
        using var canister = world.CreateGasMixture(1f, 300f);
        canister.SetMoles("Air", 1f);
        world.Solvers.Register("mark", context => context.GasMixtures[0].Get(EmptyCellMark)[0] = 5f);
        var start = world.CaptureCheckpoint();
        world.StartRecording();

        world.Tick();
        canister.Clear();
        canister.Temperature = 280f;
        var recording = world.StopRecording();
        var head = world.ComputeStateHash();

        world.ReplayTo(start, recording.Operations, head.Position);
        Assert.That(world.ComputeStateHash(), Is.EqualTo(head));
    }

    [Test]
    public void ReusedSlotStartsWithClearFieldsAndReplaysTheSame()
    {
        using var world = new AtmosWorld(CreateConfig());
        float seen = -1f;

        // Writes on the first tick and reads on the second, so replay repeats exactly what the live run did.
        world.Solvers.Register(
            "slot-data",
            context =>
            {
                Span<float> values = context.GasMixtures[0].Get(SlotData).AsSpan();
                if (world.TickCount == 0)
                    values[0] = 42f;
                else
                    seen = values[0];
            });

        var start = world.CaptureCheckpoint();
        world.StartRecording();
        var first = world.CreateGasMixture(1f, 300f);
        world.Tick();
        first.Dispose();
        using var second = world.CreateGasMixture(1f, 300f);
        world.Tick();
        var recording = world.StopRecording();
        var head = world.ComputeStateHash();

        Assert.That(seen, Is.Zero);
        seen = -1f;
        world.ReplayTo(start, recording.Operations, head.Position);
        Assert.Multiple(() =>
        {
            Assert.That(seen, Is.Zero);
            Assert.That(world.ComputeStateHash(), Is.EqualTo(head));
        });
    }

    [Test]
    public void RestoredMixtureWhoseHandleWasDisposedCanBeReacquired()
    {
        using var world = new AtmosWorld(CreateConfig());
        var canister = world.CreateGasMixture(1f, 300f);
        canister.AddGas("Air", 5f, 300f);
        var id = canister.Id;
        var checkpoint = world.CaptureCheckpoint();
        canister.Dispose();
        Assert.That(world.GetGasMixtureIds(), Is.Empty);

        world.RestoreCheckpoint(checkpoint);

        Assert.That(world.GetGasMixtureIds(), Is.EqualTo(new[] { id }));
        Assert.That(world.TryGetGasMixture(id, out var restored), Is.True);
        using (restored)
        {
            Assert.Multiple(() =>
            {
                Assert.That(() => _ = canister.TotalMoles, Throws.TypeOf<ObjectDisposedException>());
                Assert.That(restored, Is.Not.SameAs(canister));
                Assert.That(restored!.Id, Is.EqualTo(id));
                Assert.That(restored.GetMoles("Air"), Is.EqualTo(5f));
            });
        }

        Assert.That(world.GetGasMixtureIds(), Is.Empty);
    }

    [Test]
    public void FreshWorldReplayListsAndReacquiresEveryMixture()
    {
        using var world = new AtmosWorld(CreateConfig());
        var start = world.CaptureCheckpoint();
        world.StartRecording();
        using var first = world.CreateGasMixture(1f, 300f);
        var gone = world.CreateGasMixture(2f, 300f);
        using var third = world.CreateGasMixture(3f, 300f);
        gone.Dispose();
        first.AddGas("Air", 1f, 300f);
        third.AddGas("Fuel", 2f, 300f);
        var recording = world.StopRecording();
        var head = world.ComputeStateHash();

        using var replica = new AtmosWorld(CreateConfig());
        replica.ReplayTo(start, recording.Operations, head.Position);
        var ids = replica.GetGasMixtureIds();

        Assert.That(ids, Is.EqualTo(new[] { first.Id, third.Id }));
        Assert.That(replica.TryGetGasMixture(third.Id, out var copy), Is.True);
        using (copy)
        {
            Assert.Multiple(() =>
            {
                Assert.That(copy!.World, Is.SameAs(replica));
                Assert.That(copy.Volume, Is.EqualTo(3f));
                Assert.That(copy.GetMoles("Fuel"), Is.EqualTo(2f));
                Assert.That(replica.TryGetGasMixture(gone.Id, out _), Is.False);
            });
        }
    }

    [Test]
    public void StaleOrMalformedIdsFindNothing()
    {
        using var world = new AtmosWorld(CreateConfig());
        var old = world.CreateGasMixture(1f, 300f);
        var oldId = old.Id;
        old.Dispose();
        using var reused = world.CreateGasMixture(1f, 300f);

        Assert.Multiple(() =>
        {
            Assert.That(old.Id, Is.EqualTo(oldId));
            Assert.That(reused.Id.Slot, Is.EqualTo(oldId.Slot));
            Assert.That(reused.Id, Is.Not.EqualTo(oldId));
            Assert.That(world.TryGetGasMixture(oldId, out var stale), Is.False);
            Assert.That(stale, Is.Null);
            Assert.That(world.TryGetGasMixture(default, out _), Is.False);
            Assert.That(world.TryGetGasMixture(new GasMixtureId(-1, 1), out _), Is.False);
            Assert.That(world.TryGetGasMixture(new GasMixtureId(1_000, 1), out _), Is.False);
            Assert.That(default(GasMixtureId).IsValid, Is.False);
            Assert.That(reused.Id.IsValid, Is.True);
        });
    }

    // Channels are created in the order 2, 0, 1. Summing them in that order rounds differently from gas-ID order.
    [TestCase(false)]
    [TestCase(true)]
    public void RemovingOrTransferringTotalMolesEmptiesTheMixture(bool voxel)
    {
        using var world = new AtmosWorld(CreateThreeGasConfig());
        var simulation = world.CreateSimulation(2, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        simulation.SetChunkClassification(chunk, new VoxelClassification(1));
        using var destination = world.CreateGasMixture(1f, 300f);
        using var canister = world.CreateGasMixture(1f, 300f);
        IGasMixture Fill(IGasMixture mixture)
        {
            mixture.AddGas(2, 7f, 300f);
            mixture.AddGas(0, 0.1f, 300f);
            mixture.AddGas(1, 0.2f, 300f);
            return mixture;
        }

        var removeSource = Fill(voxel ? simulation.GetVoxelGasMixture(chunk, 0) : canister);
        var snapshot = removeSource.GetSnapshot();
        Assert.Multiple(() =>
        {
            Assert.That(snapshot.TotalMoles, Is.EqualTo(removeSource.TotalMoles));
            Assert.That(snapshot.Pressure, Is.EqualTo(removeSource.Pressure));
        });

        using var removed = removeSource.Remove(removeSource.TotalMoles);
        Assert.Multiple(() =>
        {
            Assert.That(removeSource.TotalMoles, Is.Zero);
            Assert.That(removeSource.ActiveGasCount, Is.Zero);
        });

        using var second = world.CreateGasMixture(1f, 300f);
        var transferSource = Fill(voxel ? simulation.GetVoxelGasMixture(chunk, 1) : second);
        transferSource.TransferTo(destination, transferSource.TotalMoles);
        Assert.Multiple(() =>
        {
            Assert.That(transferSource.TotalMoles, Is.Zero);
            Assert.That(transferSource.ActiveGasCount, Is.Zero);
        });
    }

    [Test]
    public void TransferToItselfThrowsForADisposedOrStaleHandle()
    {
        using var world = new AtmosWorld(CreateConfig());
        var disposed = world.CreateGasMixture(1f, 300f);
        disposed.Dispose();
        using var kept = world.CreateGasMixture(1f, 300f);
        var checkpoint = world.CaptureCheckpoint();
        using var dropped = world.CreateGasMixture(1f, 300f);
        dropped.AddGas("Air", 1f, 300f);

        // The checkpoint predates "dropped", so the restore makes that handle stale.
        world.RestoreCheckpoint(checkpoint);

        Assert.Multiple(() =>
        {
            Assert.That(() => disposed.TransferTo(disposed, 1f), Throws.TypeOf<ObjectDisposedException>());
            Assert.That(() => disposed.TransferRatioTo(disposed, 1f), Throws.TypeOf<ObjectDisposedException>());
            Assert.That(() => dropped.TransferTo(dropped, 1f), Throws.InvalidOperationException);
            Assert.That(() => dropped.TransferRatioTo(dropped, 1f), Throws.InvalidOperationException);
            Assert.That(kept.TransferTo(kept, 1f), Is.Zero);
        });
    }

    private static AtmosConfig CreateThreeGasConfig()
    {
        return new AtmosConfig
        {
            GasRegistry =
            [
                new GasProperties { Name = "A" },
                new GasProperties { Name = "B" },
                new GasProperties { Name = "C" }
            ]
        };
    }

    private static void AssertSnapshotsEqual(GasMixtureSnapshot actual, GasMixtureSnapshot expected)
    {
        Assert.Multiple(() =>
        {
            Assert.That(actual.Volume, Is.EqualTo(expected.Volume));
            Assert.That(actual.Temperature, Is.EqualTo(expected.Temperature));
            Assert.That(actual.Pressure, Is.EqualTo(expected.Pressure));
            Assert.That(actual.TotalMoles, Is.EqualTo(expected.TotalMoles));
            Assert.That(actual.Gases, Is.EqualTo(expected.Gases));
        });
    }

    private static AtmosConfig CreateConfig()
    {
        return new AtmosConfig { GasRegistry = [SolverConfigurationArchiveTests.Air, SolverConfigurationArchiveTests.Fuel] };
    }

    private static AtmosConfig CreateReactiveConfig()
    {
        var config = CreateConfig();
        config.SolverConfigurations.Add(SolverConfigurationArchiveTests.Reactions(1f));
        return config;
    }
}
