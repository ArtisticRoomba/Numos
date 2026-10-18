using Numos.Collections;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.Maths;

namespace Numos.API.Tests;

[TestFixture]
public sealed class AtmosSolverChunkTests
{
    // The registry is process-wide and shared by every test in this assembly, so keys are namespaced to this fixture.
    private static readonly ChunkField<float> Exposure =
        ChunkField<float>.Register("tests/solver-chunk/exposure", rollback: true);

    private static readonly ChunkField<int> Counters =
        ChunkField<int>.Register("tests/solver-chunk/counters", rollback: true, length: 3);

    private static readonly ChunkField<int> Scratch =
        ChunkField<int>.Register("tests/solver-chunk/scratch");

    [Test]
    public void Register_SameDefinition_ReturnsSameInstance()
    {
        var again = ChunkField<float>.Register("tests/solver-chunk/exposure", rollback: true);
        var first = ChunkField<int>.CreatePrivate("tests/solver-chunk/private");
        var second = ChunkField<int>.CreatePrivate("tests/solver-chunk/private");

        Assert.Multiple(() =>
        {
            Assert.That(again, Is.SameAs(Exposure));
            Assert.That(Exposure.IsPerVoxel, Is.True);
            Assert.That(Counters.Length, Is.EqualTo(3));
            Assert.That(Counters.IsPerVoxel, Is.False);
            Assert.That(first, Is.Not.SameAs(second));
            Assert.That(first.IsPrivate, Is.True);
            Assert.That(first.Rollback, Is.False);
            Assert.That(
                () => ChunkField<float>.Register("tests/solver-chunk/exposure"),
                Throws.InvalidOperationException);
        });
    }

    [Test]
    public void Chunks_EnumerateInGridOrder()
    {
        using var world = new AtmosWorld(new TestAtmosConfig());
        var simulation = world.CreateSimulation(2, 1, 1);
        simulation.CreateAndRegisterChunk(new Int3(1, 0, 0));
        simulation.CreateAndRegisterChunk(new Int3(0, 1, 0));
        simulation.CreateAndRegisterChunk(new Int3(0, 0, 0));
        simulation.CreateAndRegisterChunk(new Int3(-1, 2, 0));
        var enumerated = new List<AtmosChunkHandle>();
        var indexed = new List<AtmosChunkHandle>();

        world.Solvers.Register("tests/solver-chunk/order", context =>
        {
            var chunks = context.Chunks(simulation);
            foreach (var chunk in chunks)
                enumerated.Add(chunk.Handle);

            for (int index = 0; index < chunks.Count; index++)
                indexed.Add(chunks[index].Handle);
        });

        world.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(enumerated, Is.EqualTo(simulation.GetChunkHandles()));
            Assert.That(indexed, Is.EqualTo(enumerated));
        });
    }

    [Test]
    public void View_ExposesChunkShapeAndFieldViews()
    {
        using var world = new AtmosWorld(new TestAtmosConfig());
        var simulation = world.CreateSimulation(2, 3, 4);
        var handle = simulation.CreateAndRegisterChunk(new Int3(0, 0, 5));
        AtmosCellRef expectedCell = simulation.GetCellRef(handle, 7);
        Int3 perVoxelDimensions = default;
        Int3 fixedDimensions = default;
        int perVoxelSpan = -1;
        int fixedSpan = -1;
        AtmosCellRef cell = default;
        bool found = false;
        bool missing = true;

        world.Solvers.Register("tests/solver-chunk/shape", context =>
        {
            var chunks = context.Chunks(simulation);
            found = chunks.TryGet(handle, out var chunk);
            missing = chunks.TryGet(new AtmosChunkHandle(Int3.PosX), out _);
            perVoxelDimensions = chunk.Get(Exposure).Dimensions;
            fixedDimensions = chunk.Get(Counters).Dimensions;
            perVoxelSpan = chunk.GetSpan(Exposure).Length;
            fixedSpan = chunk.GetSpan(Counters).Length;
            cell = chunk.GetCellRef(7);
            Assert.That(() => chunk.GetCellRef(24), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(chunk.Simulation, Is.SameAs(simulation));
            Assert.That(chunk.Position, Is.EqualTo(new Int3(0, 0, 5)));
            Assert.That(chunk.VoxelCount, Is.EqualTo(24));
        });

        world.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(found, Is.True);
            Assert.That(missing, Is.False);
            Assert.That(perVoxelDimensions, Is.EqualTo(new Int3(2, 3, 4)));
            Assert.That(fixedDimensions, Is.EqualTo(new Int3(3, 1, 1)));
            Assert.That(perVoxelSpan, Is.EqualTo(24));
            Assert.That(fixedSpan, Is.EqualTo(3));
            Assert.That(cell, Is.EqualTo(expectedCell));
        });
    }

    [Test]
    public void ParallelWorkers_ShareOneArrayPerChunk()
    {
        using var world = new AtmosWorld(new TestAtmosConfig());
        var simulation = world.CreateSimulation(4, 4, 1);
        for (int x = 0; x < 4; x++)
            simulation.CreateAndRegisterChunk(new Int3(x, 0, 0));

        // A private field guarantees that the first touch happens on the workers.
        var hits = ChunkField<int>.CreatePrivate("tests/solver-chunk/parallel-hits");
        const int workers = 256;

        world.Solvers.Register("tests/solver-chunk/parallel", context =>
        {
            var chunks = context.Chunks(simulation);
            Parallel.For(
                0,
                workers,
                new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
                index =>
                {
                    Span<int> span = chunks[index % chunks.Count].GetSpan(hits);
                    Interlocked.Increment(ref span[index % span.Length]);
                });
        });

        world.Tick();

        int total = 0;
        foreach (var handle in simulation.GetChunkHandles())
        {
            FlatArray<int> values = simulation.GetChunkField(handle, hits);
            Assert.That(values.Dimensions, Is.EqualTo(new Int3(4, 4, 1)));
            foreach (int value in values.AsSpan())
                total += value;
        }

        Assert.That(total, Is.EqualTo(workers));
    }

    [Test]
    public void GetChunkField_FromWorkerDuringTick_ThrowsInsteadOfBlocking()
    {
        using var world = new AtmosWorld(new TestAtmosConfig());
        var simulation = world.CreateSimulation(1, 1, 1);
        var handle = simulation.CreateAndRegisterChunk(default);
        Exception? workerFailure = null;
        bool tickThreadSucceeded = false;

        world.Solvers.Register("tests/solver-chunk/worker-facade", _ =>
        {
            tickThreadSucceeded = simulation.GetChunkField(handle, Scratch).Length == 1;
            workerFailure = Task.Run(() =>
            {
                try
                {
                    simulation.GetChunkField(handle, Scratch);
                    return null;
                }
                catch (Exception exception)
                {
                    return exception;
                }
            }).GetAwaiter().GetResult();
        });

        world.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(tickThreadSucceeded, Is.True);
            Assert.That(workerFailure, Is.TypeOf<InvalidOperationException>());
        });
    }

    [Test]
    public void ViewsRetainedPastTheTick_Throw()
    {
        using var world = new AtmosWorld(new TestAtmosConfig());
        var simulation = world.CreateSimulation(1, 1, 1);
        simulation.CreateAndRegisterChunk(default);
        AtmosSolverChunk retained = default;
        AtmosSolverChunks retainedList = default;
        AtmosWorldSolverContext? retainedContext = null;

        world.Solvers.Register("tests/solver-chunk/retained", context =>
        {
            retainedContext = context;
            retainedList = context.Chunks(simulation);
            retained = retainedList[0];
            Assert.That(retained.GetSpan(Exposure).Length, Is.EqualTo(1));
        });

        world.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(() => retained.Get(Exposure), Throws.InvalidOperationException);
            Assert.That(() => retained.GetSpan(Exposure).Length, Throws.InvalidOperationException);
            Assert.That(() => retained.VoxelCount, Throws.InvalidOperationException);
            Assert.That(() => retained.Simulation, Throws.InvalidOperationException);
            Assert.That(() => retainedList.Count, Throws.InvalidOperationException);
            Assert.That(() => retainedList.Simulation, Throws.InvalidOperationException);
            Assert.That(() => retainedList.GetEnumerator(), Throws.InvalidOperationException);
            Assert.That(() => retainedContext!.Chunks(simulation), Throws.InvalidOperationException);
            Assert.That(
                () => retainedContext!.TryGetChunk(simulation.GetCellRef(default, 0), out _),
                Throws.InvalidOperationException);
            Assert.That(retained.ToString(), Does.Contain("expired"));
        });
    }

    [Test]
    public void DefaultViews_Throw()
    {
        AtmosSolverChunk chunk = default;
        AtmosSolverChunks chunks = default;

        Assert.Multiple(() =>
        {
            Assert.That(() => chunk.Handle, Throws.InvalidOperationException);
            Assert.That(() => chunk.GetSpan(Exposure).Length, Throws.InvalidOperationException);
            Assert.That(() => chunks.Count, Throws.InvalidOperationException);
            Assert.That(() => chunks.TryGet(default, out _), Throws.InvalidOperationException);
        });
    }

    [Test]
    public void Chunks_ForSimulationOutsideTheTick_Throws()
    {
        using var world = new AtmosWorld(new TestAtmosConfig());
        using var otherWorld = new AtmosWorld(new TestAtmosConfig());
        world.CreateSimulation(1, 1, 1);
        var foreign = otherWorld.CreateSimulation(1, 1, 1);
        Exception? failure = null;

        world.Solvers.Register("tests/solver-chunk/foreign", context =>
        {
            try
            {
                context.Chunks(foreign);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });

        world.Tick();

        Assert.That(failure, Is.TypeOf<ArgumentException>());
    }

    [Test]
    public void TryGetChunk_ResolvesCartesianAndPortalNeighbors()
    {
        using var world = new AtmosWorld(new TestAtmosConfig());
        var station = world.CreateSimulation(1, 1, 1);
        var shuttle = world.CreateSimulation(2, 2, 1);
        var stationChunk = station.CreateAndRegisterChunk(default);
        var stationNeighbor = station.CreateAndRegisterChunk(Int3.PosX);
        var shuttleChunk = shuttle.CreateAndRegisterChunk(new Int3(3, 0, 0));
        station.SetChunkClassification(stationChunk, new VoxelClassification(1));
        station.SetChunkClassification(stationNeighbor, new VoxelClassification(1));
        shuttle.SetChunkClassification(shuttleChunk, new VoxelClassification(1));
        var source = station.GetCellRef(stationChunk, 0);
        var portalTarget = shuttle.GetCellRef(shuttleChunk, 3);
        world.CreatePortal(source, portalTarget, ExplicitLinkFlags.GasTransport);
        var resolved = new List<(AtmosSimulation Simulation, AtmosChunkHandle Handle, AtmosCellRef Cell)>();
        bool missingChunk = true;
        bool missingVoxel = true;

        world.Solvers.Register(
            "tests/solver-chunk/neighbors",
            context =>
            {
                foreach (var neighbor in context.Topology.GetNeighbors(source))
                {
                    Assert.That(context.TryGetChunk(neighbor.Cell, out var chunk), Is.True);
                    chunk.GetSpan(Exposure)[neighbor.Cell.LocalVoxelIndex] = 5f;
                    resolved.Add((chunk.Simulation, chunk.Handle, chunk.GetCellRef(neighbor.Cell.LocalVoxelIndex)));
                }

                missingChunk = context.TryGetChunk(
                    source with { Chunk = new AtmosChunkHandle(new Int3(9, 9, 9)) }, out _);
                missingVoxel = context.TryGetChunk(source with { LocalVoxelIndex = 1 }, out _);
            },
            AtmosNeighborSelection.All("tests/solver-chunk/neighbors-v1"));

        world.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(resolved, Has.Count.EqualTo(2));
            Assert.That(
                resolved.Select(static item => item.Cell),
                Is.EquivalentTo(new[] { station.GetCellRef(stationNeighbor, 0), portalTarget }));

            Assert.That(resolved.Single(static item => item.Cell.LocalVoxelIndex == 3).Simulation, Is.SameAs(shuttle));
            Assert.That(shuttle.GetChunkField(shuttleChunk, Exposure)[3], Is.EqualTo(5f));
            Assert.That(missingChunk, Is.False);
            Assert.That(missingVoxel, Is.False);
        });
    }

    [Test]
    public void GetChunkField_SharesStorageWithSolverViews()
    {
        using var world = new AtmosWorld(new TestAtmosConfig());
        var simulation = world.CreateSimulation(2, 1, 1);
        var handle = simulation.CreateAndRegisterChunk(default);
        simulation.GetChunkField(handle, Exposure)[new Int3(1, 0, 0)] = 3f;
        float seen = 0f;

        world.Solvers.Register("tests/solver-chunk/shared", context =>
        {
            Assert.That(context.Chunks(simulation).TryGet(handle, out var chunk), Is.True);
            FlatArray<float> exposure = chunk.Get(Exposure);
            seen = exposure[new Int3(1, 0, 0)];
            exposure[0] = 11f;
        });

        world.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(seen, Is.EqualTo(3f));
            Assert.That(simulation.GetChunkField(handle, Exposure)[0], Is.EqualTo(11f));
            Assert.That(
                () => simulation.GetChunkField(new AtmosChunkHandle(Int3.PosY), Exposure),
                Throws.TypeOf<KeyNotFoundException>());
        });
    }

    [Test]
    public void RollbackFieldsWrittenThroughViews_RoundTripThroughCheckpoints()
    {
        using var world = new AtmosWorld(new TestAtmosConfig());
        var simulation = world.CreateSimulation(2, 2, 1);
        var handle = simulation.CreateAndRegisterChunk(default);

        world.Solvers.Register("tests/solver-chunk/rollback", context =>
        {
            foreach (var chunk in context.Chunks(simulation))
            {
                chunk.GetSpan(Counters)[1]++;
                chunk.GetSpan(Exposure)[2] += 0.5f;
                chunk.GetSpan(Scratch)[0]++;
            }
        });

        world.Tick();
        var checkpoint = world.CaptureCheckpoint();
        world.Tick();
        world.Tick();
        Assert.That(simulation.GetChunkField(handle, Counters)[1], Is.EqualTo(3));

        world.RestoreCheckpoint(checkpoint);

        Assert.Multiple(() =>
        {
            Assert.That(simulation.GetChunkField(handle, Counters)[1], Is.EqualTo(1));
            Assert.That(simulation.GetChunkField(handle, Exposure)[2], Is.EqualTo(0.5f));
            Assert.That(simulation.GetChunkField(handle, Scratch)[0], Is.Zero);
            Assert.That(world.ComputeStateHash(), Is.EqualTo(checkpoint.ComputeStateHash()));
        });

        world.Tick();
        Assert.That(simulation.GetChunkField(handle, Counters)[1], Is.EqualTo(2));
    }
}
