using Numos.API.Dangerous;
using Numos.CoreSim.State;
using Numos.Maths;
using Numos.Serialization;

namespace Numos.API.Tests;

// The registry is process-wide and shared by every test in this assembly, so keys are namespaced to this file.
[ChunkComponent("tests/chunk-component-generated", AccessorName = "TestFire")]
internal partial struct GeneratedFire
{
    [NumosSerialize] public float Exposure;
    [NumosSerialize("burn-count"), PerChunk(Length = 2)] public int BurnCount;
    [NumosScratch] public float Heat;
}

[TestFixture]
public sealed class ChunkComponentGeneratedTests
{
    [Test]
    public void Fields_AreKeyedByComponentAndWireName()
    {
        Assert.Multiple(() =>
        {
            Assert.That(GeneratedFire.Fields.Exposure.Key, Is.EqualTo("tests/chunk-component-generated/Exposure"));
            Assert.That(GeneratedFire.Fields.Exposure.Rollback, Is.True);
            Assert.That(GeneratedFire.Fields.Exposure.Length, Is.Null);
            Assert.That(GeneratedFire.Fields.BurnCount.Key, Is.EqualTo("tests/chunk-component-generated/burn-count"));
            Assert.That(GeneratedFire.Fields.BurnCount.Rollback, Is.True);
            Assert.That(GeneratedFire.Fields.BurnCount.Length, Is.EqualTo(2));
            Assert.That(GeneratedFire.Fields.Heat.Key, Is.EqualTo("tests/chunk-component-generated/Heat"));
            Assert.That(GeneratedFire.Fields.Heat.Rollback, Is.False);
            Assert.That(GeneratedFire.Fields.Heat.ClearOnVacuum, Is.False);
            Assert.That(
                ChunkField<float>.Register("tests/chunk-component-generated/Exposure", rollback: true),
                Is.SameAs(GeneratedFire.Fields.Exposure));
        });
    }

    [Test]
    public void ViewWrites_LandInTheFieldStorage()
    {
        using var world = new AtmosWorld(new TestAtmosConfig());
        var simulation = world.CreateSimulation(2, 2, 2);
        var handle = simulation.CreateAndRegisterChunk(default);
        float seenExposure = 0f;
        int seenBurnCount = 0;
        int viewLength = 0;

        world.Solvers.Register(
            "tests/chunk-component-generated/view",
            context =>
            {
                var chunk = context.Chunks(simulation)[0];
                GeneratedFire.View fire = chunk.GetTestFire();
                viewLength = fire.Exposure.Length;
                fire.Exposure[3] += 5f;
                fire.BurnCount[1] = 7;
                fire.Heat[0] = 2f;
                seenExposure = chunk.Get(GeneratedFire.Fields.Exposure)[3];
                seenBurnCount = chunk.Get(GeneratedFire.Fields.BurnCount)[1];
            });

        world.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(viewLength, Is.EqualTo(8));
            Assert.That(seenExposure, Is.EqualTo(5f));
            Assert.That(seenBurnCount, Is.EqualTo(7));
            Assert.That(simulation.TryGetChunkField(handle, GeneratedFire.Fields.Exposure, out var exposure), Is.True);
            Assert.That(exposure[3], Is.EqualTo(5f));
            Assert.That(simulation.TryGetChunkField(handle, GeneratedFire.Fields.BurnCount, out var burnCount), Is.True);
            Assert.That(burnCount.Length, Is.EqualTo(2));
            Assert.That(simulation.TryGetChunkField(handle, GeneratedFire.Fields.Heat, out var heat), Is.True);
            Assert.That(heat[0], Is.EqualTo(2f));
        });
    }

    [Test]
    public void TryGetOnAnUntouchedChunk_CreatesNothingAndKeepsTheHash()
    {
        bool found = true;
        bool has = true;
        using var probed = CreateWorld(
            "tests/chunk-component-generated/try-get",
            chunk =>
            {
                found = chunk.TryGetTestFire(out _);
                has = chunk.HasTestFire;
            },
            out var simulation,
            out var handle);

        using var untouched = CreateWorld("tests/chunk-component-generated/try-get", _ => { }, out _, out _);
        probed.Tick();
        untouched.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(found, Is.False);
            Assert.That(has, Is.False);
            Assert.That(simulation.TryGetChunkField(handle, GeneratedFire.Fields.Exposure, out _), Is.False);
            Assert.That(simulation.TryGetChunkField(handle, GeneratedFire.Fields.BurnCount, out _), Is.False);
            Assert.That(simulation.TryGetChunkField(handle, GeneratedFire.Fields.Heat, out _), Is.False);
            Assert.That(probed.ComputeStateHash(), Is.EqualTo(untouched.ComputeStateHash()));
        });
    }

    [Test]
    public void TryGet_FindsTheComponentOnceTheFirstFieldExists()
    {
        bool found = false;
        float seen = 0f;
        using var world = CreateWorld(
            "tests/chunk-component-generated/try-get-found",
            chunk =>
            {
                found = chunk.TryGetTestFire(out GeneratedFire.View fire);
                if (found)
                    seen = fire.Exposure[1];
            },
            out var simulation,
            out var handle);

        simulation.SetChunkData(handle, GeneratedFire.Fields.Exposure, 1, 4f);
        world.Tick();

        Assert.That(found, Is.True);
        Assert.That(seen, Is.EqualTo(4f));
    }

    [Test]
    public void With_SkipsChunksWithoutTheComponent()
    {
        using var world = new AtmosWorld(new TestAtmosConfig());
        var simulation = world.CreateSimulation(1, 1, 1);
        AtmosChunkHandle[] handles =
        [
            simulation.CreateAndRegisterChunk(new Int3(0, 0, 0)),
            simulation.CreateAndRegisterChunk(new Int3(1, 0, 0)),
            simulation.CreateAndRegisterChunk(new Int3(2, 0, 0)),
        ];

        simulation.SetChunkData(handles[0], GeneratedFire.Fields.Exposure, 0, 1f);
        simulation.SetChunkData(handles[2], GeneratedFire.Fields.Exposure, 0, 3f);

        var seen = new List<(Int3 Position, float Exposure, int BurnCountLength)>();
        world.Solvers.Register(
            "tests/chunk-component-generated/with",
            context =>
            {
                foreach (var (chunk, fire) in context.Chunks(simulation).WithTestFire())
                    seen.Add((chunk.Position, fire.Exposure[0], fire.BurnCount.Length));
            });

        world.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(
                seen,
                Is.EqualTo(new[] { (new Int3(0, 0, 0), 1f, 2), (new Int3(2, 0, 0), 3f, 2) }));

            Assert.That(simulation.TryGetChunkField(handles[1], GeneratedFire.Fields.Exposure, out _), Is.False);
            Assert.That(simulation.TryGetChunkField(handles[1], GeneratedFire.Fields.BurnCount, out _), Is.False);
        });
    }

    [Test]
    public void Restore_KeepsSavedFieldsAndZeroesScratchFields()
    {
        using var simulation = new AtmosSimulation(2, 1, 1);
        var handle = simulation.CreateAndRegisterChunk(default);
        float heatAfterRestore = -1f;
        bool restored = false;
        simulation.World.Solvers.Register(
            "tests/chunk-component-generated/restore",
            context =>
            {
                GeneratedFire.View fire = context.Chunks(simulation)[0].GetTestFire();
                if (restored)
                {
                    heatAfterRestore = fire.Heat[1];
                    return;
                }

                fire.BurnCount[0]++;
                fire.Exposure[1] += fire.BurnCount[0];
                fire.Heat[1] = fire.BurnCount[0];
            });

        simulation.Tick();
        var checkpoint = simulation.World.CaptureCheckpoint();
        simulation.Tick();

        var keys = checkpoint.Simulations.Single().Checkpoint.Chunks.Single().SolverArrays.Select(static array => array.Key).ToList();
        Assert.That(keys, Does.Contain(GeneratedFire.Fields.Exposure.Key));
        Assert.That(keys, Does.Contain(GeneratedFire.Fields.BurnCount.Key));
        Assert.That(keys, Does.Not.Contain(GeneratedFire.Fields.Heat.Key));

        simulation.World.RestoreCheckpoint(checkpoint);
        restored = true;
        simulation.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(heatAfterRestore, Is.Zero);
            Assert.That(simulation.Dangerous().GetChunkField(handle, GeneratedFire.Fields.BurnCount)[0], Is.EqualTo(1));
            Assert.That(simulation.Dangerous().GetChunkField(handle, GeneratedFire.Fields.Exposure)[1], Is.EqualTo(1f));
        });
    }

    [Test]
    public void ReplayFileRoundTrip_KeepsSavedFields()
    {
        using var world = new AtmosWorld(new TestAtmosConfig());
        var simulation = world.CreateSimulation(2, 1, 1);
        var handle = simulation.CreateAndRegisterChunk(default);
        simulation.SetChunkData(handle, GeneratedFire.Fields.Exposure, 1, 2.5f);
        simulation.SetChunkData(handle, GeneratedFire.Fields.BurnCount, 1, 9);
        var timeline = new AtmosWorldReplayTimeline(world, 1);
        world.Tick();
        var archive = timeline.CaptureReplay();

        var metadata = new NumosReplayMetadata("Component", DateTimeOffset.UnixEpoch, "tests", "1", "1");
        using var stream = new MemoryStream();
        NumosWorldReplaySerializer.Serialize(stream, new NumosWorldReplayDocument(metadata, archive));
        stream.Position = 0;
        var replay = NumosWorldReplaySerializer.Deserialize(stream).Replay;

        var arrays = replay.InitialCheckpoint.Simulations.Single().Checkpoint.Chunks.Single().SolverArrays
            .ToDictionary(static array => array.Key);

        Assert.Multiple(() =>
        {
            Assert.That(replay.InitialCheckpoint.ComputeStateHash(), Is.EqualTo(archive.InitialStateHash));
            Assert.That(arrays[GeneratedFire.Fields.Exposure.Key].CopyValues<float>()[1], Is.EqualTo(2.5f));
            Assert.That(arrays[GeneratedFire.Fields.BurnCount.Key].CopyValues<int>(), Is.EqualTo(new[] { 0, 9 }));
            Assert.That(arrays, Does.Not.ContainKey(GeneratedFire.Fields.Heat.Key));
        });
    }

    [Test]
    public void MixturePage_GetsTheSameAccessors()
    {
        using var world = new AtmosWorld(new TestAtmosConfig());
        using var canister = world.CreateGasMixture(1f, 300f);
        bool before = true;
        bool after = false;
        int length = 0;

        world.Solvers.Register(
            "tests/chunk-component-generated/page",
            context =>
            {
                if (!context.GasMixtures.TryGetPage(canister.CellRef, out var page))
                    return;

                before = page.TryGetTestFire(out _);
                GeneratedFire.View fire = page.GetTestFire();
                fire.Exposure[canister.CellRef.LocalVoxelIndex] = 6f;
                length = fire.Exposure.Length;
                after = page.HasTestFire;
            });

        world.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(before, Is.False);
            Assert.That(after, Is.True);
            Assert.That(length, Is.GreaterThan(canister.CellRef.LocalVoxelIndex));
        });
    }

    private static AtmosWorld CreateWorld(
        string solverName, Action<AtmosSolverChunk> probe,
        out AtmosSimulation simulation, out AtmosChunkHandle handle)
    {
        var world = new AtmosWorld(new TestAtmosConfig());
        var created = world.CreateSimulation(2, 2, 2);
        handle = created.CreateAndRegisterChunk(default);
        world.Solvers.Register(solverName, context => probe(context.Chunks(created)[0]));
        simulation = created;
        return world;
    }
}
