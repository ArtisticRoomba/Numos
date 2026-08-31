using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.Maths;

namespace Numos.API.Tests;

[TestFixture]
public sealed class AtmosChunkVersionTests
{
    [Test]
    public void TryGetChunkSnapshot_UnchangedChunk_DoesNotCreateAnotherSnapshot()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(new Int3(0, 0, 0));

        bool firstCreated = simulation.TryGetChunkSnapshot(chunk, default, out var first);
        bool secondCreated = simulation.TryGetChunkSnapshot(chunk, first.Version, out _);

        Assert.Multiple(() =>
        {
            Assert.That(firstCreated, Is.True);
            Assert.That(secondCreated, Is.False);
        });
    }

    [Test]
    public void TryGetChunkSnapshot_SelectedFields_DoesNotCopyUnusedVoxelArrays()
    {
        using var simulation = new AtmosSimulation(2, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        var fields =
            AtmosChunkSnapshotFields.Temperature | AtmosChunkSnapshotFields.VoxelClassification;

        bool created = simulation.TryGetChunkSnapshot(chunk, default, fields, out var snapshot);

        Assert.Multiple(() =>
        {
            Assert.That(created, Is.True);
            Assert.That(snapshot.IsSnapshotValid, Is.True);
            Assert.That(snapshot.Temperature, Has.Length.EqualTo(2));
            Assert.That(snapshot.VoxelRoomMap, Has.Length.EqualTo(2));
            Assert.That(snapshot.TotalPressure, Is.Empty);
            Assert.That(snapshot.Gases, Is.Empty);
            Assert.That(snapshot.HasFields(fields), Is.True);
            Assert.That(snapshot.HasFields(AtmosChunkSnapshotFields.All), Is.False);
        });
    }

    [Test]
    public void TryGetChunkSnapshot_NoFields_DoesNotClaimAnyDetachedField()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);

        simulation.TryGetChunkSnapshot(
            chunk,
            default,
            AtmosChunkSnapshotFields.None,
            out var snapshot);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.IsSnapshotValid, Is.True);
            Assert.That(snapshot.HasFields(AtmosChunkSnapshotFields.Pressure), Is.False);
            Assert.That(snapshot.HasFields(AtmosChunkSnapshotFields.Temperature), Is.False);
            Assert.That(snapshot.HasFields(AtmosChunkSnapshotFields.Gases), Is.False);
            Assert.That(snapshot.HasFields(AtmosChunkSnapshotFields.VoxelClassification), Is.False);
        });
    }

    [Test]
    public void GetChangedChunkSnapshots_CapturesChangedRequestsAsOneBatch()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        var first = simulation.CreateAndRegisterChunk(new Int3(0, 0, 0));
        var second = simulation.CreateAndRegisterChunk(new Int3(1, 0, 0));
        var fields =
            AtmosChunkSnapshotFields.Temperature | AtmosChunkSnapshotFields.VoxelClassification;

        AtmosChunkSnapshotRequest[] requests =
        [
            new(first.Position, default, fields),
            new(second.Position, default, fields)
        ];

        var firstBatch = simulation.GetChangedChunkSnapshots(requests);
        requests[0] = requests[0] with { KnownVersion = firstBatch.ChangedChunks[0].Version };
        requests[1] = requests[1] with { KnownVersion = firstBatch.ChangedChunks[1].Version };
        var unchangedBatch = simulation.GetChangedChunkSnapshots(requests);

        Assert.Multiple(() =>
        {
            Assert.That(firstBatch.TickCount, Is.EqualTo(simulation.TickCount));
            Assert.That(firstBatch.ChangedChunks, Has.Length.EqualTo(2));
            Assert.That(firstBatch.ChangedChunks.All(snapshot => snapshot.HasFields(fields)), Is.True);
            Assert.That(unchangedBatch.ChangedChunks, Is.Empty);
        });
    }

    [Test]
    public void GetVoxelSnapshot_CopiesOnlyOneVoxelsGasValues()
    {
        using var simulation = new AtmosSimulation(2, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        simulation.SetChunkClassification(chunk, new VoxelClassification(1));
        simulation.AddGasToVoxel(chunk, 1, 4, 3f, 250f);

        var first = simulation.GetVoxelSnapshot(chunk, 1);
        first.Gases[0] = new VoxelGasSnapshot(99, 99f);
        var second = simulation.GetVoxelSnapshot(chunk, 1);

        Assert.Multiple(() =>
        {
            Assert.That(second.LocalIndex, Is.EqualTo(1));
            Assert.That(second.Temperature, Is.EqualTo(250f));
            Assert.That(second.Gases, Has.Length.EqualTo(1));
            Assert.That(second.Gases[0], Is.EqualTo(new VoxelGasSnapshot(4, 3f)));
        });
    }

    [Test]
    public void TryGetVoxelSnapshot_OnlyCopiesDetailsForExactPresentedVersion()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        simulation.SetChunkClassification(chunk, new VoxelClassification(1));
        simulation.AddGasToVoxel(chunk, 0, 7, 2f, 275f);
        var presentedVersion = simulation.GetChunkSnapshot(chunk).Version;

        bool matched = simulation.TryGetVoxelSnapshot(chunk, 0, presentedVersion, out var details);
        simulation.SetVoxelTemperature(chunk, 0, 300f);
        bool stale = simulation.TryGetVoxelSnapshot(chunk, 0, presentedVersion, out var staleDetails);

        Assert.Multiple(() =>
        {
            Assert.That(matched, Is.True);
            Assert.That(details.ChunkVersion, Is.EqualTo(presentedVersion));
            Assert.That(details.Gases, Is.EqualTo(new[] { new VoxelGasSnapshot(7, 2f) }));
            Assert.That(stale, Is.False);
            Assert.That(staleDetails, Is.EqualTo(default(AtmosVoxelSnapshot)));
        });
    }

    [Test]
    public void TryGetChunkSnapshot_DirectPausedMutation_AdvancesRevision()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(new Int3(0, 0, 0));
        var before = simulation.GetChunkSnapshot(chunk);

        simulation.SetVoxelTemperature(chunk, 0, 310f);

        bool created = simulation.TryGetChunkSnapshot(chunk, before.Version, out var after);
        Assert.Multiple(() =>
        {
            Assert.That(created, Is.True);
            Assert.That(after.Version.Generation, Is.EqualTo(before.Version.Generation));
            Assert.That(after.Version.Revision, Is.GreaterThan(before.Version.Revision));
            Assert.That(after.Temperature[0], Is.EqualTo(310f));
        });
    }

    [Test]
    public void TryGetChunkSnapshot_RecreatedAtSamePosition_HasNewGeneration()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        var position = new Int3(2, 3, 4);
        var original = simulation.CreateAndRegisterChunk(position);
        var first = simulation.GetChunkSnapshot(original);
        Assert.That(simulation.UnregisterChunk(original), Is.True);
        var replacement = simulation.CreateAndRegisterChunk(position);

        bool created = simulation.TryGetChunkSnapshot(replacement, first.Version, out var second);

        Assert.Multiple(() =>
        {
            Assert.That(created, Is.True);
            Assert.That(second.Version.Generation, Is.Not.EqualTo(first.Version.Generation));
        });
    }

    [Test]
    public void GetChunkHandles_DiscoversAdditionsAndRemovalsWithoutCallerRegistry()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        var first = simulation.CreateAndRegisterChunk(new Int3(2, 0, 0));
        var second = simulation.CreateAndRegisterChunk(new Int3(-1, 0, 0));

        AtmosChunkHandle[] before = simulation.GetChunkHandles();
        simulation.UnregisterChunk(first);
        AtmosChunkHandle[] after = simulation.GetChunkHandles();

        Assert.Multiple(() =>
        {
            Assert.That(
                before.Select(handle => handle.Position),
                Is.EqualTo(new[] { second.Position, first.Position }));

            Assert.That(after.Select(handle => handle.Position), Is.EqualTo(new[] { second.Position }));
        });
    }

    [Test]
    public void TryGetChunkHandles_UnchangedCollection_DoesNotAllocateAnotherHandleList()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        simulation.CreateAndRegisterChunk(default);

        bool firstCreated = simulation.TryGetChunkHandles(-1, out long revision, out AtmosChunkHandle[] first);
        bool secondCreated = simulation.TryGetChunkHandles(revision, out long unchangedRevision, out AtmosChunkHandle[] second);

        Assert.Multiple(() =>
        {
            Assert.That(firstCreated, Is.True);
            Assert.That(first, Has.Length.EqualTo(1));
            Assert.That(secondCreated, Is.False);
            Assert.That(unchangedRevision, Is.EqualTo(revision));
            Assert.That(second, Is.Empty);
        });
    }

    [Test]
    public void TryGetChunkHandles_RemoveAndRecreateSamePosition_StillAdvancesCollectionRevision()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        var original = simulation.CreateAndRegisterChunk(default);
        simulation.TryGetChunkHandles(-1, out long firstRevision, out _);

        simulation.UnregisterChunk(original);
        simulation.CreateAndRegisterChunk(default);
        bool changed = simulation.TryGetChunkHandles(firstRevision, out long secondRevision, out AtmosChunkHandle[] handles);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(secondRevision, Is.GreaterThan(firstRevision));
            Assert.That(handles.Select(handle => handle.Position), Is.EqualTo(new[] { original.Position }));
        });
    }

    [Test]
    public void TryGetChunkSnapshot_AwakeSimulationTick_AdvancesRevision()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(new Int3(0, 0, 0));
        simulation.SetChunkClassification(chunk, new VoxelClassification(1));
        simulation.AddGasToVoxel(chunk, 0, 0, 1f, 293.15f);
        var before = simulation.GetChunkSnapshot(chunk);

        simulation.Tick();

        Assert.That(simulation.TryGetChunkSnapshot(chunk, before.Version, out _), Is.True);
    }

    [Test]
    public void TryGetChunkSnapshot_SleepingSimulationTick_DoesNotCopyUnchangedChunk()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(new Int3(0, 0, 0));
        var before = simulation.GetChunkSnapshot(chunk);

        simulation.Tick();

        Assert.That(simulation.TryGetChunkSnapshot(chunk, before.Version, out _), Is.False);
    }

    [Test]
    public void TryGetChunkSnapshot_ThermalFlowIntoSleepingNeighbor_AdvancesNeighborRevision()
    {
        var config = new AtmosConfig
        {
            ThermalConductance = 0.1f,
            VacuumThreshold = 0.1f
        };

        using var simulation = new AtmosSimulation(config, 1, 1, 1);
        var hot = simulation.CreateAndRegisterChunk(new Int3(0, 0, 0));
        var cold = simulation.CreateAndRegisterChunk(new Int3(1, 0, 0));
        simulation.SetChunkClassification(hot, new VoxelClassification(1));
        simulation.SetChunkClassification(cold, new VoxelClassification(1));

        // Equal pressure prevents advection from waking the neighbor. Different temperature
        // still produces boundary heat transfer on the second (thermodynamic) tick.
        simulation.AddGasToVoxel(hot, 0, 0, 0.00125f, 400f);
        simulation.AddGasToVoxel(cold, 0, 0, 0.0025f, 200f);
        simulation.SleepChunk(cold);
        simulation.Tick();
        var before = simulation.GetChunkSnapshot(cold);

        simulation.Tick();

        bool created = simulation.TryGetChunkSnapshot(cold, before.Version, out var after);
        Assert.Multiple(() =>
        {
            Assert.That(created, Is.True);
            Assert.That(after.IsAwake, Is.False);
            Assert.That(after.Temperature[0], Is.GreaterThan(before.Temperature[0]));
        });
    }
}