using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.Maths;

namespace Numos.API.Dangerous.Tests;

[TestFixture]
public sealed class AtmosSolverChunkEditTests
{
    private const string StageName = "tests/solver-edit/stage";
    private const ushort GasVoxel = 3;
    private const ushort SolidVoxel = 7;

    [Test]
    public void ChunkReads_AreTheDangerousViewSpans()
    {
        using var world = CreateWorld(out var simulation, out _);
        var failures = new List<string>();
        world.Solvers.Register(
            StageName,
            context =>
            {
                var chunk = context.Chunks(simulation)[0];
                var live = chunk.Dangerous();
                ExpectSame(failures, "Temperature", chunk.Temperature, live.Temperature);
                ExpectSame(failures, "Pressure", chunk.Pressure, live.TotalPressure);
                ExpectSame(failures, "HeatCapacity", chunk.HeatCapacity, live.TotalHeatCapacity);
                ExpectSame(failures, "Classification", chunk.Classification, live.VoxelRoomMap);
                ExpectSame(failures, "MomentumX", chunk.MomentumX, live.MomentumX);
                ExpectSame(failures, "MomentumY", chunk.MomentumY, live.MomentumY);
                ExpectSame(failures, "MomentumZ", chunk.MomentumZ, live.MomentumZ);
                ExpectSame(failures, "Moles(0)", chunk.Moles(0), live.GetGasChannel(0).Moles);
                if (!chunk.HasGas(0))
                    failures.Add("HasGas(0) is false");

                if (chunk.HasGas(1) || !chunk.Moles(1).IsEmpty || !chunk.Moles(-1).IsEmpty)
                    failures.Add("an absent gas has a channel");
            });

        world.Tick();

        Assert.That(failures, Is.Empty);
    }

    [Test]
    public void PageReads_AreTheDangerousPageSpans()
    {
        using var world = new AtmosWorld(new TestAtmosConfig());
        using var mixture = world.CreateGasMixture(2f, 350f);
        mixture.AddGas(0, 3f, 350f);
        DisableBuiltIns(world);
        var failures = new List<string>();
        world.Solvers.Register(
            StageName,
            context =>
            {
                var page = context.GasMixtures[0];
                var live = page.Dangerous().Chunk;
                ExpectSame(failures, "Temperature", page.Temperature, live.Temperature);
                ExpectSame(failures, "Pressure", page.Pressure, live.TotalPressure);
                ExpectSame(failures, "HeatCapacity", page.HeatCapacity, live.TotalHeatCapacity);
                ExpectSame(failures, "Moles(0)", page.Moles(0), live.GetGasChannel(0).Moles);
                if (!page.HasGas(0) || page.HasGas(1) || !page.Moles(1).IsEmpty)
                    failures.Add("HasGas disagrees with the channels");
            });

        world.Tick();

        Assert.That(failures, Is.Empty);
    }

    [Test]
    public void TemperatureEdit_LeavesTheCachesAFacadeWriteLeaves()
    {
        using var edited = CreateWorld(out var editedSimulation, out var editedHandle);
        using var reference = CreateWorld(out var referenceSimulation, out var referenceHandle);
        float target = editedSimulation.Dangerous().GetChunk(editedHandle).Temperature[GasVoxel] + 150f;
        edited.Solvers.Register(
            StageName,
            context => context.Chunks(editedSimulation)[0].Edit().Temperature[GasVoxel] = target);
        reference.Solvers.Register(StageName, static _ => { });
        referenceSimulation.SetVoxelTemperature(referenceHandle, GasVoxel, target);

        edited.Tick();
        reference.Tick();

        var actual = editedSimulation.Dangerous().GetChunk(editedHandle);
        var expected = referenceSimulation.Dangerous().GetChunk(referenceHandle);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual.TotalPressure[GasVoxel], Is.GreaterThan(0f));
            Assert.That(actual.TotalPressure.ToArray(), Is.EqualTo(expected.TotalPressure.ToArray()));
            Assert.That(actual.TotalHeatCapacity.ToArray(), Is.EqualTo(expected.TotalHeatCapacity.ToArray()));
            Assert.That(edited.ComputeStateHash(), Is.EqualTo(reference.ComputeStateHash()));
        }
    }

    [Test]
    public void Edit_OnASleepingChunk_WakesItAndAdvancesTheRevision()
    {
        using var world = CreateWorld(out var simulation, out var handle);
        simulation.SleepChunk(handle);
        var before = simulation.GetChunkSnapshot(handle).Version;
        world.Solvers.Register(StageName, context => context.Chunks(simulation)[0].Edit());

        world.Tick();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(simulation.Dangerous().GetChunk(handle).IsAwake, Is.True);
            Assert.That(simulation.GetChunkSnapshot(handle).Version, Is.Not.EqualTo(before));
        }
    }

    [Test]
    public void EditThatEmptiesAVoxel_LeavesItVacuumWithZeroMomentum()
    {
        using var world = CreateWorld(out var simulation, out var handle);
        simulation.SetVoxelMomentum(handle, GasVoxel, 0.1f, 0.2f, 0.3f);
        world.Solvers.Register(
            StageName,
            context =>
            {
                var editor = context.Chunks(simulation)[0].Edit();
                editor.Moles(0)[GasVoxel] = 0f;

                // Gas and momentum written into a solid voxel do not survive the repair.
                editor.Moles(0)[SolidVoxel] = 5f;
                editor.MomentumZ[SolidVoxel] = 1f;
            });

        world.Tick();

        var chunk = simulation.Dangerous().GetChunk(handle);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chunk.TotalPressure[GasVoxel], Is.Zero);
            Assert.That(chunk.TotalHeatCapacity[GasVoxel], Is.Zero);
            Assert.That(chunk.MomentumX[GasVoxel], Is.Zero);
            Assert.That(chunk.MomentumY[GasVoxel], Is.Zero);
            Assert.That(chunk.MomentumZ[GasVoxel], Is.Zero);
            Assert.That(chunk.GetGasChannel(0).Moles[SolidVoxel], Is.Zero);
            Assert.That(chunk.MomentumZ[SolidVoxel], Is.Zero);
            Assert.That(chunk.TotalPressure[SolidVoxel], Is.Zero);
        }
    }

    [Test]
    public void EditorMoles_CreatesTheChannelAndRejectsUnknownGas()
    {
        using var world = CreateWorld(out var simulation, out _);
        bool created = false;
        Exception? rejected = null;
        world.Solvers.Register(
            StageName,
            context =>
            {
                var chunk = context.Chunks(simulation)[0];
                var editor = chunk.Edit();
                editor.Moles(2)[GasVoxel] = 0.5f;
                created = chunk.HasGas(2) && chunk.Moles(2)[GasVoxel] == 0.5f;
                try
                {
                    editor.Moles(world.Config.GasRegistry.Count);
                }
                catch (ArgumentOutOfRangeException exception)
                {
                    rejected = exception;
                }
            });

        world.Tick();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(created, Is.True);
            Assert.That(rejected, Is.Not.Null);
        }
    }

    [Test]
    public void DefaultEditors_Throw()
    {
        Assert.Multiple(() =>
        {
            Assert.That(() => default(AtmosSolverChunkEditor).Temperature.Length, Throws.InvalidOperationException);
            Assert.That(() => default(AtmosSolverChunkEditor).Moles(0).Length, Throws.InvalidOperationException);
            Assert.That(() => default(AtmosSolverGasMixturePageEditor).CellCount, Throws.InvalidOperationException);
            Assert.That(() => default(AtmosSolverChunk).Edit(), Throws.InvalidOperationException);
        });
    }

    [Test]
    public void ParallelEdits_GiveTheSerialStateHash()
    {
        using var serial = CreateRowWorld(parallel: false);
        using var parallel = CreateRowWorld(parallel: true);
        using var unedited = CreateRowWorld(parallel: false, edit: false);

        serial.Tick(60);
        parallel.Tick(60);
        unedited.Tick(60);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(parallel.ComputeStateHash(), Is.EqualTo(serial.ComputeStateHash()));
            Assert.That(parallel.ComputeStateHash(), Is.Not.EqualTo(unedited.ComputeStateHash()), "the stage edited");
        }
    }

    [Test]
    public void StageThatOnlyReads_LeavesTheStateHashUnchanged()
    {
        using var reading = CreateRowWorld(parallel: false, edit: false);
        using var idle = CreateRowWorld(parallel: false, edit: false, read: false);

        reading.Tick(60);
        idle.Tick(60);

        Assert.That(reading.ComputeStateHash(), Is.EqualTo(idle.ComputeStateHash()));
    }

    [Test]
    public void RecordedRunWithAnEditingStage_ReplaysToTheSameHash()
    {
        using var world = CreateRowWorld(parallel: true);
        var simulation = world.Simulations[0];
        var checkpoint = world.CaptureCheckpoint();
        world.StartRecording();
        for (int tick = 1; tick <= 50; tick++)
        {
            if (tick == 20)
                simulation.AddGasToVoxel(new AtmosChunkHandle(Int3.PosX), 2, 0, 4f, 500f);

            world.Tick();
        }

        var recording = world.StopRecording();
        var expected = world.ComputeStateHash();

        world.ReplayTo(checkpoint, recording.Operations, recording.Head);

        Assert.That(world.ComputeStateHash(), Is.EqualTo(expected));
    }

    [Test]
    public void PageTemperatureEdit_LeavesTheCachesAFacadeWriteLeaves()
    {
        using var edited = new AtmosWorld(new TestAtmosConfig());
        using var reference = new AtmosWorld(new TestAtmosConfig());
        using var editedMixture = edited.CreateGasMixture(2f, 300f);
        using var referenceMixture = reference.CreateGasMixture(2f, 300f);
        editedMixture.AddGas(0, 3f, 300f);
        referenceMixture.AddGas(0, 3f, 300f);
        editedMixture.AddGas(1, 1f, 300f);
        referenceMixture.AddGas(1, 1f, 300f);
        DisableBuiltIns(edited);
        DisableBuiltIns(reference);

        ushort cell = editedMixture.CellRef.LocalVoxelIndex;
        float[] editedCaches = [];
        float[] referenceCaches = [];
        edited.Solvers.Register(
            StageName,
            context => context.GasMixtures[0].Edit().Temperature[cell] = 640f);
        edited.Solvers.Register(
            "tests/solver-edit/observe",
            context => editedCaches = ReadCaches(context.GasMixtures[0], cell),
            after: StageName);
        reference.Solvers.Register(StageName, static _ => { });
        reference.Solvers.Register(
            "tests/solver-edit/observe",
            context => referenceCaches = ReadCaches(context.GasMixtures[0], cell),
            after: StageName);
        referenceMixture.Temperature = 640f;

        edited.Tick();
        reference.Tick();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(editedCaches, Is.EqualTo(referenceCaches));
            Assert.That(editedCaches[0], Is.EqualTo(editedMixture.Pressure));
            Assert.That(edited.ComputeStateHash(), Is.EqualTo(reference.ComputeStateHash()));
        }
    }

    private static float[] ReadCaches(AtmosSolverGasMixturePage page, ushort cell)
    {
        return [page.Pressure[cell], page.HeatCapacity[cell]];
    }

    /// <summary>
    ///     Builds a one-chunk world with gas in <see cref="GasVoxel" />, a solid <see cref="SolidVoxel" />, and every
    ///     built-in stage disabled, so only the test's own stages touch the chunk.
    /// </summary>
    private static AtmosWorld CreateWorld(out AtmosSimulation simulation, out AtmosChunkHandle handle)
    {
        var world = new AtmosWorld(new TestAtmosConfig());
        simulation = world.CreateSimulation(2, 2, 2);
        handle = SimSetup.CreateOpenChunk(simulation, Int3.Zero);
        simulation.SetVoxelClassification(handle, SolidVoxel, VoxelClassification.RoomSolid);
        simulation.AddGasToVoxel(handle, GasVoxel, 0, 2f, 320f);
        DisableBuiltIns(world);
        return world;
    }

    /// <summary>
    ///     Builds four chunks in a row with the default pipeline and a stage that heats each chunk and feeds it a second
    ///     gas through <see cref="AtmosSolverChunk.Edit" />.
    /// </summary>
    private static AtmosWorld CreateRowWorld(bool parallel, bool edit = true, bool read = true)
    {
        var world = new AtmosWorld(new TestAtmosConfig());
        var simulation = world.CreateSimulation(4, 4, 1);
        for (int x = 0; x < 4; x++)
        {
            var handle = SimSetup.CreateOpenChunk(simulation, new Int3(x, 0, 0));
            for (ushort voxel = 0; voxel < 16; voxel++)
                simulation.AddGasToVoxel(handle, voxel, 0, 1f + x, 290f + voxel);
        }

        world.Solvers.Register(
            StageName,
            context =>
            {
                if (!read)
                    return;

                var chunks = context.Chunks(simulation);
                if (!edit)
                {
                    foreach (var chunk in chunks)
                    {
                        _ = chunk.Temperature[0] + chunk.Pressure[0] + chunk.HeatCapacity[0] + chunk.Moles(0)[0] +
                            chunk.MomentumX[0] + chunk.Classification[0];
                        _ = chunk.HasGas(1);
                    }

                    return;
                }

                if (context.TickCount % 5 != 0)
                    return;

                if (parallel)
                    Parallel.For(0, chunks.Count, index => Heat(chunks[index], index));
                else
                    for (int index = 0; index < chunks.Count; index++)
                        Heat(chunks[index], index);
            });

        return world;

        static void Heat(AtmosSolverChunk chunk, int index)
        {
            var editor = chunk.Edit();
            Span<float> temperature = editor.Temperature;
            Span<float> second = editor.Moles(1);
            for (int voxel = 0; voxel < temperature.Length; voxel += 3)
            {
                temperature[voxel] += 2f + index;
                second[voxel] += 0.01f * (index + 1);
            }
        }
    }

    private static void DisableBuiltIns(AtmosWorld world)
    {
        foreach (string name in AtmosWorldSolverPipeline.BuiltInStageNames)
            world.Solvers.SetEnabled(name, false);
    }

    private static void ExpectSame<T>(List<string> failures, string name, ReadOnlySpan<T> view, ReadOnlySpan<T> live)
    {
        if (view.Length != live.Length || !view.Overlaps(live, out int offset) || offset != 0)
            failures.Add($"{name} is not the live span");
    }
}
