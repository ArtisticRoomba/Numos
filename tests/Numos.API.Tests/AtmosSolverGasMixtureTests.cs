using Numos.CoreSim;
using Numos.Maths;

namespace Numos.API.Tests;

/// <summary>
///     The solver-side view of the gas mixture pool: <see cref="AtmosWorldSolverContext.GasMixtures" />.
/// </summary>
[TestFixture]
public sealed class AtmosSolverGasMixtureTests
{
    private const string KeyPrefix = "tests/solver-gas-mixtures/";

    private static readonly ChunkField<float> Exposure =
        ChunkField<float>.Register(KeyPrefix + "exposure", true);

    private static readonly ChunkField<float> Scratch =
        ChunkField<float>.Register(KeyPrefix + "scratch");

    [Test]
    public void Stage_SeesAllocatedMixturesWithVolumeSlotAndCellRef()
    {
        using var world = CreateWorld();
        var first = world.CreateGasMixture(2f, 300f);
        var second = world.CreateGasMixture(5f, 280f);
        var third = world.CreateGasMixture(7f, 290f);
        second.Dispose();

        var cells = new List<(bool Allocated, float Volume, int Slot, AtmosCellRef Ref)>();
        int pageCount = -1;
        int cellCount = -1;
        int pageIndex = -1;
        world.Solvers.Register(
            KeyPrefix + "inspect",
            context =>
            {
                var mixtures = context.GasMixtures;
                pageCount = mixtures.PageCount;
                var page = mixtures[0];
                pageIndex = page.Index;
                cellCount = page.CellCount;
                for (ushort cell = 0; cell < 4; cell++)
                    cells.Add((page.IsAllocated(cell), page.GetVolume(cell), page.GetSlot(cell), page.GetCellRef(cell)));
            });

        world.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(pageCount, Is.EqualTo(1));
            Assert.That(pageIndex, Is.Zero);
            Assert.That(cellCount, Is.EqualTo(256));
            Assert.That(cells.Select(static cell => cell.Allocated), Is.EqualTo(new[] { true, false, true, false }));
            Assert.That(cells.Select(static cell => cell.Volume), Is.EqualTo(new[] { 2f, 0f, 7f, 0f }));
            Assert.That(cells.Select(static cell => cell.Slot), Is.EqualTo(new[] { 0, 1, 2, 3 }));
            Assert.That(cells[0].Ref, Is.EqualTo(first.CellRef));
            Assert.That(cells[2].Ref, Is.EqualTo(third.CellRef));
            Assert.That(cells[2].Ref.IsGasMixture, Is.True);
        });
    }

    [Test]
    public void Stage_AddressesCellsOfLaterPagesBySlot()
    {
        using var world = CreateWorld();
        var mixtures = new List<GasMixture>();
        for (int index = 0; index < 300; index++)
            mixtures.Add(world.CreateGasMixture(1f + index, 300f));

        int pageCount = -1;
        var seenSlots = new List<int>();
        AtmosCellRef lastRef = default;
        float lastVolume = -1f;
        world.Solvers.Register(
            KeyPrefix + "pages",
            context =>
            {
                var pages = context.GasMixtures;
                pageCount = pages.PageCount;
                foreach (var page in pages)
                {
                    for (ushort cell = 0; cell < page.CellCount; cell++)
                    {
                        if (page.IsAllocated(cell))
                            seenSlots.Add(page.GetSlot(cell));
                    }
                }

                lastRef = pages[1].GetCellRef(43);
                lastVolume = pages[1].GetVolume(43);
            });

        world.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(pageCount, Is.EqualTo(2));
            Assert.That(seenSlots, Is.EqualTo(Enumerable.Range(0, 300)));
            Assert.That(lastRef, Is.EqualTo(mixtures[299].CellRef));
            Assert.That(lastVolume, Is.EqualTo(300f));
        });

        foreach (var mixture in mixtures)
            mixture.Dispose();
    }

    [Test]
    public void Chunks_NeverIncludePagesAndTryGetChunkRejectsMixtureCells()
    {
        using var world = CreateWorld();
        var simulation = world.CreateSimulation(2, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        using var canister = world.CreateGasMixture(1f, 300f);

        var chunkPositions = new List<Int3>();
        bool foundMixtureChunk = true;
        bool foundGridChunk = false;
        int pageCount = -1;
        world.Solvers.Register(
            KeyPrefix + "chunks",
            context =>
            {
                foreach (var visible in context.Chunks(simulation))
                    chunkPositions.Add(visible.Position);

                foundMixtureChunk = context.TryGetChunk(canister.CellRef, out _);
                foundGridChunk = context.TryGetChunk(simulation.GetCellRef(chunk, 0), out _);
                pageCount = context.GasMixtures.PageCount;
            });

        world.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(chunkPositions, Is.EqualTo(new[] { chunk.Position }));
            Assert.That(foundMixtureChunk, Is.False);
            Assert.That(foundGridChunk, Is.True);
            Assert.That(pageCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void Stage_WorksWhenTheWorldHasNoSimulation()
    {
        using var world = CreateWorld();
        using var canister = world.CreateGasMixture(4f, 300f);
        float seen = -1f;
        world.Solvers.Register(
            KeyPrefix + "empty-world",
            context =>
            {
                Assert.That(context.Simulations, Is.Empty);
                var page = context.GasMixtures[0];
                page.Get(Exposure)[0] += page.GetVolume(0);
                seen = page.Get(Exposure)[0];
            });

        world.Tick();
        world.Tick();

        Assert.That(seen, Is.EqualTo(8f));
    }

    [Test]
    public void Stage_WithoutMixturesSeesAnEmptyPool()
    {
        using var world = CreateWorld();
        int pageCount = -1;
        bool found = true;
        world.Solvers.Register(
            KeyPrefix + "no-mixtures",
            context =>
            {
                pageCount = context.GasMixtures.PageCount;
                found = context.GasMixtures.TryGetPage(new AtmosCellRef(new AtmosSimulationId(-1, 1), new AtmosChunkHandle(default), 0), out _);
            });

        world.Tick();

        Assert.That(pageCount, Is.Zero);
        Assert.That(found, Is.False);
    }

    [Test]
    public void Field_SurvivesCheckpointRoundTripAndReplaysIdentically()
    {
        using var world = CreateWorld();
        using var small = world.CreateGasMixture(2f, 300f);
        using var large = world.CreateGasMixture(5f, 300f);
        var observed = new List<(float Small, float Large)>();
        RegisterAccumulator(world, observed);

        world.Tick();
        world.Tick();
        var checkpoint = world.CaptureCheckpoint();
        world.StartRecording();
        world.Tick();
        world.Tick();
        world.Tick();
        var recording = world.StopRecording();
        var expected = world.ComputeStateHash();

        Assert.That(observed.Last(), Is.EqualTo((10f, 25f)));

        // Restoring puts the saved accumulation back, so the next tick adds to 2 ticks and not 5.
        world.RestoreCheckpoint(checkpoint);
        world.Tick();
        Assert.That(observed.Last(), Is.EqualTo((6f, 15f)));

        observed.Clear();
        world.ReplayTo(checkpoint, recording.Operations, expected.Position);
        Assert.Multiple(() =>
        {
            Assert.That(world.ComputeStateHash(), Is.EqualTo(expected));
            Assert.That(observed.Last(), Is.EqualTo((10f, 25f)));
        });

        using var fresh = new AtmosWorld(new AtmosConfig(checkpoint.Config));
        var freshObserved = new List<(float Small, float Large)>();
        RegisterAccumulator(fresh, freshObserved);
        fresh.ReplayTo(checkpoint, recording.Operations, expected.Position);
        Assert.Multiple(() =>
        {
            Assert.That(fresh.ComputeStateHash(), Is.EqualTo(expected));
            Assert.That(freshObserved.Last(), Is.EqualTo((10f, 25f)));
        });
    }

    [Test]
    public void Field_IsPartOfTheStateHashAndTheCheckpoint()
    {
        using var writer = CreateWorld();
        using var idle = CreateWorld();
        using var writerMixture = writer.CreateGasMixture(3f, 300f);
        using var idleMixture = idle.CreateGasMixture(3f, 300f);
        writer.Solvers.Register(
            KeyPrefix + "hash",
            context => context.GasMixtures[0].Get(Exposure)[0] += 1f);

        writer.Tick();
        idle.Tick();
        var written = writer.ComputeStateHash();
        var checkpoint = writer.CaptureCheckpoint();
        writer.RestoreCheckpoint(checkpoint);

        Assert.Multiple(() =>
        {
            Assert.That(written.Digest, Is.Not.EqualTo(idle.ComputeStateHash().Digest));
            Assert.That(writer.ComputeStateHash(), Is.EqualTo(written));
        });
    }

    [Test]
    public void TransientField_StartsEmptyAfterARestore()
    {
        using var world = CreateWorld();
        using var canister = world.CreateGasMixture(3f, 300f);
        float seen = -1f;
        world.Solvers.Register(
            KeyPrefix + "transient",
            context =>
            {
                var scratch = context.GasMixtures[0].Get(Scratch);
                scratch[0] += 1f;
                seen = scratch[0];
            });

        var checkpoint = world.CaptureCheckpoint();

        world.Tick();
        world.Tick();
        Assert.That(seen, Is.EqualTo(2f));

        world.RestoreCheckpoint(checkpoint);
        world.Tick();
        Assert.That(seen, Is.EqualTo(1f));
    }

    [Test]
    public void Views_ThrowOnceTheTickEnds()
    {
        using var world = CreateWorld();
        using var canister = world.CreateGasMixture(1f, 300f);
        AtmosWorldSolverContext? retainedContext = null;
        AtmosSolverGasMixtures retainedMixtures = default;
        AtmosSolverGasMixturePage retainedPage = default;
        world.Solvers.Register(
            KeyPrefix + "retain",
            context =>
            {
                retainedContext = context;
                retainedMixtures = context.GasMixtures;
                retainedPage = retainedMixtures[0];
            });

        world.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(() => retainedContext!.GasMixtures, Throws.InvalidOperationException);
            Assert.That(() => retainedMixtures.PageCount, Throws.InvalidOperationException);
            Assert.That(() => retainedMixtures[0], Throws.InvalidOperationException);
            Assert.That(() => retainedMixtures.GetEnumerator(), Throws.InvalidOperationException);
            Assert.That(() => retainedMixtures.TryGetPage(canister.CellRef, out _), Throws.InvalidOperationException);
            Assert.That(() => retainedPage.Index, Throws.InvalidOperationException);
            Assert.That(() => retainedPage.CellCount, Throws.InvalidOperationException);
            Assert.That(() => retainedPage.IsAllocated(0), Throws.InvalidOperationException);
            Assert.That(() => retainedPage.GetVolume(0), Throws.InvalidOperationException);
            Assert.That(() => retainedPage.GetSlot(0), Throws.InvalidOperationException);
            Assert.That(() => retainedPage.GetCellRef(0), Throws.InvalidOperationException);
            Assert.That(() => retainedPage.Get(Exposure), Throws.InvalidOperationException);
            Assert.That(retainedPage.ToString(), Does.Contain("expired"));
        });
    }

    [Test]
    public void DefaultViews_Throw()
    {
        Assert.Multiple(() =>
        {
            Assert.That(() => default(AtmosSolverGasMixtures).PageCount, Throws.InvalidOperationException);
            Assert.That(() => default(AtmosSolverGasMixtures).GetEnumerator(), Throws.InvalidOperationException);
            Assert.That(() => default(AtmosSolverGasMixturePage).CellCount, Throws.InvalidOperationException);
            Assert.That(() => default(AtmosSolverGasMixturePage).Get(Exposure), Throws.InvalidOperationException);
            Assert.That(default(AtmosSolverGasMixturePage).ToString(), Does.Contain("default"));
        });
    }

    [Test]
    public void TryGetPage_FindsLiveMixturesAndRejectsEverythingElse()
    {
        using var world = CreateWorld();
        var simulation = world.CreateSimulation(2, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        using var live = world.CreateGasMixture(1f, 300f);
        var gone = world.CreateGasMixture(1f, 300f);
        var goneRef = gone.CellRef;
        gone.Dispose();
        var gridCell = simulation.GetCellRef(chunk, 0);
        var liveRef = live.CellRef;
        var pool = new AtmosSimulationId(-1, 1);

        var results = new Dictionary<string, bool>();
        int livePageIndex = -1;
        world.Solvers.Register(
            KeyPrefix + "try-get",
            context =>
            {
                var mixtures = context.GasMixtures;
                results["live"] = mixtures.TryGetPage(liveRef, out var page);
                livePageIndex = results["live"] ? page.Index : -1;
                results["disposed"] = mixtures.TryGetPage(goneRef, out _);
                results["grid"] = mixtures.TryGetPage(gridCell, out _);
                results["free"] = mixtures.TryGetPage(new AtmosCellRef(pool, new AtmosChunkHandle(default), 200), out _);
                results["page"] = mixtures.TryGetPage(new AtmosCellRef(pool, new AtmosChunkHandle(new Int3(9, 0, 0)), 0), out _);
                results["negative"] = mixtures.TryGetPage(new AtmosCellRef(pool, new AtmosChunkHandle(new Int3(-1, 0, 0)), 0), out _);
                results["spatial"] = mixtures.TryGetPage(new AtmosCellRef(pool, new AtmosChunkHandle(new Int3(0, 1, 0)), 0), out _);
                results["cell"] = mixtures.TryGetPage(new AtmosCellRef(pool, new AtmosChunkHandle(default), 256), out _);
            });

        world.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(results["live"], Is.True);
            Assert.That(livePageIndex, Is.Zero);
            foreach (string key in new[] { "disposed", "grid", "free", "page", "negative", "spatial", "cell" })
                Assert.That(results[key], Is.False, key);
        });
    }

    [Test]
    public void TryGetPage_RejectsAReferenceToASlotThatAnotherMixtureReuses()
    {
        using var world = CreateWorld();
        var first = world.CreateGasMixture(1f, 300f);
        var oldRef = first.CellRef;
        first.Dispose();
        using var second = world.CreateGasMixture(2f, 300f);
        var newRef = second.CellRef;
        bool oldFound = true;
        float volume = -1f;
        AtmosCellRef pageRef = default;
        world.Solvers.Register(
            KeyPrefix + "reuse",
            context =>
            {
                oldFound = context.GasMixtures.TryGetPage(oldRef, out _);
                if (context.GasMixtures.TryGetPage(newRef, out var page))
                {
                    volume = page.GetVolume(newRef.LocalVoxelIndex);
                    pageRef = page.GetCellRef(newRef.LocalVoxelIndex);
                }
            });

        world.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(newRef.Chunk, Is.EqualTo(oldRef.Chunk), "The second mixture takes the lowest free slot.");
            Assert.That(newRef.LocalVoxelIndex, Is.EqualTo(oldRef.LocalVoxelIndex));
            Assert.That(newRef, Is.Not.EqualTo(oldRef));
            Assert.That(oldFound, Is.False);
            Assert.That(volume, Is.EqualTo(2f));
            Assert.That(pageRef, Is.EqualTo(newRef));
        });
    }

    [Test]
    public void PageMembers_RejectCellsOutsideThePage()
    {
        using var world = CreateWorld();
        using var canister = world.CreateGasMixture(1f, 300f);
        Exception?[] failures = new Exception?[5];
        world.Solvers.Register(
            KeyPrefix + "range",
            context =>
            {
                var mixtures = context.GasMixtures;
                var page = mixtures[0];
                failures[0] = Capture(() => page.IsAllocated(256));
                failures[1] = Capture(() => page.GetVolume(256));
                failures[2] = Capture(() => page.GetSlot(256));
                failures[3] = Capture(() => page.GetCellRef(256));
                failures[4] = Capture(() => _ = mixtures[1]);
            });

        world.Tick();

        Assert.That(failures, Has.All.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void Pages_UseViewsFromWorkerThreads()
    {
        using var world = CreateWorld();
        var mixtures = new List<GasMixture>();
        for (int index = 0; index < 600; index++)
            mixtures.Add(world.CreateGasMixture(1f + index % 7, 300f));

        world.Solvers.Register(
            KeyPrefix + "parallel",
            context =>
            {
                var pages = context.GasMixtures;
                Parallel.For(0, pages.PageCount, index =>
                {
                    var page = pages[index];
                    var exposure = page.Get(Exposure);
                    for (ushort cell = 0; cell < page.CellCount; cell++)
                    {
                        if (page.IsAllocated(cell))
                            exposure[cell] = page.GetVolume(cell) + page.GetSlot(cell);
                    }
                });
            });

        world.Tick();

        float total = -1f;
        world.Solvers.Register(
            KeyPrefix + "sum",
            context =>
            {
                total = 0f;
                foreach (var page in context.GasMixtures)
                    total += page.Get(Exposure).AsSpan().ToArray().Sum();
            });

        world.Tick();

        float expected = Enumerable.Range(0, 600).Sum(static index => 1f + index % 7 + index);
        Assert.That(total, Is.EqualTo(expected));

        foreach (var mixture in mixtures)
            mixture.Dispose();
    }

    private static void RegisterAccumulator(AtmosWorld world, List<(float Small, float Large)> observed)
    {
        world.Solvers.Register(
            KeyPrefix + "accumulate",
            context =>
            {
                var page = context.GasMixtures[0];
                var exposure = page.Get(Exposure);
                for (ushort cell = 0; cell < page.CellCount; cell++)
                {
                    if (page.IsAllocated(cell))
                        exposure[cell] += page.GetVolume(cell);
                }

                observed.Add((exposure[0], exposure[1]));
            });
    }

    private static Exception? Capture(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static AtmosWorld CreateWorld()
    {
        return new AtmosWorld(new AtmosConfig
        {
            GasRegistry = [SolverConfigurationArchiveTests.Air, SolverConfigurationArchiveTests.Fuel]
        });
    }
}
