using Numos.Collections;
using Numos.Maths;

namespace Numos.API.Tests;

// The registry is process-wide and shared by every test in this assembly, so keys are namespaced to this file.
internal static partial class GeneratedFireData
{
    public const string KeyPrefix = "tests/chunk-data-generated/";

    [ChunkData(KeyPrefix + "exposure", Rollback = true)]
    public static partial ChunkField<float> Exposure { get; }

    [ChunkData(KeyPrefix + "burn-count", Rollback = true, Length = 2)]
    public static partial ChunkField<int> BurnCount { get; }

    [ChunkData]
    public static partial ChunkField<int> Scratch { get; }

    [ChunkData(KeyPrefix + "clears", ClearOnSleep = true, ClearOnVacuum = true)]
    public static partial ChunkField<int> Clears { get; }
}

// Nothing reads this property, so only the generated module initializer can have registered it.
internal static partial class UnreadChunkData
{
    public const string Key = "tests/chunk-data-generated/unread";

    // Runs only if something touches the declaring type itself, which early registration must not do. The flag
    // lives elsewhere because reading it from here would run this.
    static UnreadChunkData()
    {
        UnreadChunkDataProbe.TypeInitialized = true;
    }

    [ChunkData(Key, Rollback = true)]
    public static partial ChunkField<int> Value { get; }
}

internal static class UnreadChunkDataProbe
{
    public static bool TypeInitialized;
}

[TestFixture]
public sealed class ChunkDataGeneratedTests
{
    [Test]
    public void ModuleInitializer_RegistersFieldsBeforeTheirPropertyIsRead()
    {
        // A different definition under the same key only conflicts if the key is already taken.
        Assert.That(
            () => ChunkField<int>.Register(UnreadChunkData.Key, rollback: true, length: 4),
            Throws.InvalidOperationException.With.Message.Contains("already registered"));

        Assert.That(UnreadChunkDataProbe.TypeInitialized, Is.False);
    }

    [Test]
    public void GeneratedFields_MatchTheirAttributes()
    {
        Assert.Multiple(() =>
        {
            Assert.That(GeneratedFireData.Exposure.Key, Is.EqualTo("tests/chunk-data-generated/exposure"));
            Assert.That(GeneratedFireData.Exposure.Rollback, Is.True);
            Assert.That(GeneratedFireData.Exposure.Length, Is.Null);
            Assert.That(GeneratedFireData.BurnCount.Length, Is.EqualTo(2));
            Assert.That(GeneratedFireData.Scratch.Key, Is.EqualTo(typeof(GeneratedFireData).FullName + ".Scratch"));
            Assert.That(GeneratedFireData.Scratch.Rollback, Is.False);
            Assert.That(GeneratedFireData.Scratch.ClearOnSleep, Is.False);
            Assert.That(GeneratedFireData.Scratch.ClearOnVacuum, Is.False);
            Assert.That(GeneratedFireData.Clears.ClearOnSleep, Is.True);
            Assert.That(GeneratedFireData.Clears.ClearOnVacuum, Is.True);
        });
    }

    [Test]
    public void GeneratedAccessors_ReadAndWriteFromParallelSolverWork()
    {
        using var world = new AtmosWorld(new TestAtmosConfig());
        var simulation = world.CreateSimulation(2, 2, 2);
        AtmosChunkHandle[] handles =
        [
            simulation.CreateAndRegisterChunk(new Int3(0, 0, 0)),
            simulation.CreateAndRegisterChunk(new Int3(1, 0, 0)),
            simulation.CreateAndRegisterChunk(new Int3(0, 1, 0)),
        ];

        Int3 dimensions = default;
        world.Solvers.Register(
            "tests/chunk-data-generated/parallel",
            context =>
            {
                var chunks = context.Chunks(simulation);
                Parallel.For(
                    0,
                    chunks.Count,
                    index =>
                    {
                        var chunk = chunks[index];
                        FlatArray<float> exposure = chunk.Exposure;
                        for (int voxel = 0; voxel < chunk.VoxelCount; voxel++)
                            exposure[voxel] += voxel;

                        FlatArray<int> burnCount = chunk.BurnCount;
                        burnCount[0]++;
                        burnCount[1] = chunk.Position.X + 10 * chunk.Position.Y;
                    });

                foreach (var chunk in chunks)
                    dimensions = chunk.Exposure.Dimensions;
            });

        world.Tick();
        world.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(dimensions, Is.EqualTo(new Int3(2, 2, 2)));
            foreach (var handle in handles)
            {
                FlatArray<float> exposure = simulation.GetChunkField(handle, GeneratedFireData.Exposure);
                FlatArray<int> burnCount = simulation.GetChunkField(handle, GeneratedFireData.BurnCount);
                Assert.That(exposure[7], Is.EqualTo(14f));
                Assert.That(burnCount[0], Is.EqualTo(2));
                Assert.That(burnCount[1], Is.EqualTo(handle.Position.X + 10 * handle.Position.Y));
            }
        });
    }

    [Test]
    public void GeneratedRollbackFields_ReplayToTheSameHashAndTransientFieldsReset()
    {
        using var simulation = new AtmosSimulation(2, 1, 1);
        var handle = simulation.CreateAndRegisterChunk(default);
        simulation.World.Solvers.Register(
            "tests/chunk-data-generated/replay",
            context =>
            {
                foreach (var chunk in context.Chunks(simulation))
                {
                    FlatArray<int> burnCount = chunk.BurnCount;
                    burnCount[0]++;
                    chunk.Exposure[1] += burnCount[0];
                    chunk.Scratch[0] = burnCount[0];
                }
            });

        simulation.Tick();
        var checkpoint = simulation.World.CaptureCheckpoint();
        simulation.World.StartRecording();
        simulation.Tick();
        simulation.Tick();
        var recording = simulation.World.StopRecording();
        var expected = simulation.World.ComputeStateHash();

        var keys = checkpoint.Simulations.Single().Checkpoint.Chunks.Single().SolverArrays.Select(array => array.Key).ToList();
        Assert.That(keys, Does.Contain(GeneratedFireData.Exposure.Key));
        Assert.That(keys, Does.Contain(GeneratedFireData.BurnCount.Key));
        Assert.That(keys, Does.Not.Contain(GeneratedFireData.Scratch.Key));
        Assert.That(simulation.GetChunkField(handle, GeneratedFireData.Scratch)[0], Is.EqualTo(3));

        simulation.World.RestoreCheckpoint(checkpoint);
        Assert.That(simulation.GetChunkField(handle, GeneratedFireData.Scratch)[0], Is.Zero);
        Assert.That(simulation.GetChunkField(handle, GeneratedFireData.BurnCount)[0], Is.EqualTo(1));

        simulation.World.ReplayTo(checkpoint, recording.Operations, expected.Position);

        Assert.Multiple(() =>
        {
            Assert.That(simulation.World.ComputeStateHash(), Is.EqualTo(expected));
            Assert.That(simulation.GetChunkField(handle, GeneratedFireData.BurnCount)[0], Is.EqualTo(3));
            Assert.That(simulation.GetChunkField(handle, GeneratedFireData.Exposure)[1], Is.EqualTo(6f));
        });
    }

    [Test]
    public void HandRegisteredFieldWithTheSameKey_SharesGeneratedStorage()
    {
        var exposure = ChunkField<float>.Register(GeneratedFireData.KeyPrefix + "exposure", rollback: true);
        var burnCount = ChunkField<int>.Register(GeneratedFireData.KeyPrefix + "burn-count", rollback: true, length: 2);
        using var world = new AtmosWorld(new TestAtmosConfig());
        var simulation = world.CreateSimulation(1, 1, 1);
        var handle = simulation.CreateAndRegisterChunk(default);
        simulation.GetChunkField(handle, exposure)[0] = 5f;
        float seen = 0f;

        world.Solvers.Register(
            "tests/chunk-data-generated/shared",
            context =>
            {
                var chunk = context.Chunks(simulation)[0];
                seen = chunk.Exposure[0];
                chunk.Exposure[new Int3(0, 0, 0)] += 2f;
                chunk.BurnCount[1] = 8;
            });

        world.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(exposure, Is.SameAs(GeneratedFireData.Exposure));
            Assert.That(burnCount, Is.SameAs(GeneratedFireData.BurnCount));
            Assert.That(seen, Is.EqualTo(5f));
            Assert.That(simulation.GetChunkField(handle, exposure)[0], Is.EqualTo(7f));
            Assert.That(simulation.GetChunkField(handle, burnCount)[1], Is.EqualTo(8));
            Assert.That(
                () => ChunkField<float>.Register(GeneratedFireData.KeyPrefix + "exposure"),
                Throws.InvalidOperationException);
        });
    }
}