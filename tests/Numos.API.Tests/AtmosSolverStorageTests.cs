using Numos.Collections;
using Numos.Maths;

namespace Numos.API.Tests;

[TestFixture]
public sealed class AtmosSolverStorageTests
{
    // The registry is process-wide and shared by every test in this assembly, so keys are namespaced to this fixture.
    private const string KeyPrefix = "tests/solver-storage/";

    [Test]
    public void CustomSolver_ReusesStorageAcrossTicks()
    {
        using var simulation = new AtmosSimulation(2, 3, 4);
        var chunk = simulation.CreateAndRegisterChunk(default);
        ChunkField<int> field = ChunkField<int>.Register(KeyPrefix + "reuse");
        FlatArray<int> original = simulation.GetChunkField(chunk, field);
        Assert.That(original.ToArray(), Is.EqualTo(new int[24]));

        simulation.World.Solvers.Register(
            KeyPrefix + "reuse",
            context =>
            {
                FlatArray<int> flat = context.Chunks(simulation)[0].Get(field);
                Assert.That(flat.Dimensions, Is.EqualTo(new Int3(2, 3, 4)));
                flat[new Int3(1, 2, 3)]++;
            });

        simulation.Tick();
        simulation.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(SameStorage(simulation.GetChunkField(chunk, field), original), Is.True);
            Assert.That(original[23], Is.EqualTo(2));
        });
    }

    [Test]
    public void Storage_IsIsolatedByChunkAndSimulation()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        using var other = new AtmosSimulation(1, 1, 1);
        var first = simulation.CreateAndRegisterChunk(default);
        var second = simulation.CreateAndRegisterChunk(Int3.PosX);
        var otherChunk = other.CreateAndRegisterChunk(default);
        ChunkField<int> field = ChunkField<int>.Register(KeyPrefix + "isolated");
        simulation.GetChunkField(first, field)[0] = 42;

        Assert.Multiple(() =>
        {
            Assert.That(simulation.GetChunkField(first, field)[0], Is.EqualTo(42));
            Assert.That(simulation.GetChunkField(second, field)[0], Is.Zero);
            Assert.That(other.GetChunkField(otherChunk, field)[0], Is.Zero);
        });
    }

    [TestCase(0)]
    [TestCase(7)]
    public void FixedLengthField_AllowsNonVoxelLengths(int length)
    {
        using var simulation = new AtmosSimulation(2, 3, 4);
        var chunk = simulation.CreateAndRegisterChunk(default);
        ChunkField<int> field = ChunkField<int>.Register(KeyPrefix + "fixed-length-" + length, length: length);
        FlatArray<int> data = simulation.GetChunkField(chunk, field);

        Assert.Multiple(() =>
        {
            Assert.That(data.Length, Is.EqualTo(length));
            Assert.That(SameStorage(simulation.GetChunkField(chunk, field), data), Is.True);
        });
    }

    [Test]
    public void ConflictingRegistration_ThrowsWithoutReplacingData()
    {
        using var simulation = new AtmosSimulation(2, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        const string key = KeyPrefix + "conflict";
        ChunkField<string> field = ChunkField<string>.Register(key);
        FlatArray<string> data = simulation.GetChunkField(chunk, field);
        data[0] = "retained";

        Assert.Multiple(() =>
        {
            Assert.That(() => ChunkField<object>.Register(key), Throws.InvalidOperationException);
            Assert.That(() => ChunkField<int>.Register(key), Throws.InvalidOperationException);
            Assert.That(() => ChunkField<string>.Register(key, length: 3), Throws.InvalidOperationException);
            Assert.That(ChunkField<string>.Register(key), Is.SameAs(field));
            Assert.That(SameStorage(simulation.GetChunkField(chunk, field), data), Is.True);
            Assert.That(data[0], Is.EqualTo("retained"));
        });
    }

    [Test]
    public void InvalidRequests_RejectNullFieldsBadDefinitionsAndMissingChunks()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        ChunkField<int> field = ChunkField<int>.Register(KeyPrefix + "invalid");
        var missing = new AtmosChunkHandle(Int3.PosX);

        Assert.Multiple(() =>
        {
            Assert.That(() => simulation.GetChunkField<int>(chunk, null!), Throws.ArgumentNullException);
            Assert.That(() => ChunkField<int>.Register(null!), Throws.ArgumentNullException);
            Assert.That(() => ChunkField<int>.Register("  "), Throws.ArgumentException);
            Assert.That(
                () => ChunkField<int>.Register(KeyPrefix + "negative", length: -1),
                Throws.TypeOf<ArgumentOutOfRangeException>());

            Assert.That(() => simulation.GetChunkField(missing, field), Throws.TypeOf<KeyNotFoundException>());
        });

        Assert.That(simulation.GetChunkField(chunk, field).Length, Is.EqualTo(1));
    }

    [Test]
    public void ConcurrentFirstTouch_ThroughViews_ReturnsOneArray()
    {
        using var simulation = new AtmosSimulation(2, 3, 4);
        var chunk = simulation.CreateAndRegisterChunk(default);
        ChunkField<int> field = ChunkField<int>.Register(KeyPrefix + "concurrent");
        var views = new FlatArray<int>[32];

        simulation.World.Solvers.Register(
            KeyPrefix + "concurrent",
            context =>
            {
                var chunks = context.Chunks(simulation);
                Parallel.For(0, views.Length, index => views[index] = chunks[0].Get(field));
            });

        simulation.Tick();

        FlatArray<int> stored = simulation.GetChunkField(chunk, field);
        Assert.That(views.All(view => SameStorage(view, stored)), Is.True);
    }

    [Test]
    public void ChunkReplacement_DetachesPreviousStorage()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        ChunkField<int> field = ChunkField<int>.Register(KeyPrefix + "replacement");
        FlatArray<int> previous = simulation.GetChunkField(chunk, field);
        previous[0] = 42;

        simulation.UnregisterChunk(chunk);
        simulation.CreateAndRegisterChunk(chunk.Position);
        FlatArray<int> replacement = simulation.GetChunkField(chunk, field);
        previous[0] = 99;

        Assert.Multiple(() =>
        {
            Assert.That(SameStorage(replacement, previous), Is.False);
            Assert.That(replacement[0], Is.Zero);
        });
    }

    [Test]
    public void ScratchStorage_DoesNotChangePhysicalStateRecordingOrCheckpointContents()
    {
        using var simulation = new AtmosSimulation(2, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        ChunkField<float> field = ChunkField<float>.Register(KeyPrefix + "scratch");
        var version = simulation.GetChunkSnapshot(chunk).Version;
        var hash = simulation.ComputeStateHash();
        simulation.StartRecording();

        FlatArray<float> data = simulation.GetChunkField(chunk, field);
        data.Fill(42f);
        var checkpoint = simulation.CaptureCheckpoint();

        Assert.Multiple(() =>
        {
            Assert.That(simulation.GetChunkSnapshot(chunk).Version, Is.EqualTo(version));
            Assert.That(simulation.GetChunkSnapshot(chunk).IsAwake, Is.False);
            Assert.That(simulation.GetChunkSnapshot(chunk).Temperature, Is.EqualTo(new float[2]));
            Assert.That(simulation.ComputeStateHash(), Is.EqualTo(hash));
            Assert.That(simulation.StopRecording().Operations, Is.Empty);
        });

        simulation.RestoreCheckpoint(checkpoint);
        FlatArray<float> restored = simulation.GetChunkField(chunk, field);
        Assert.That(restored[0], Is.Zero);
        data[0] = 99f;
        Assert.That(restored[0], Is.Zero);
    }

    [Test]
    public void DisposedSimulation_RejectsStorageAccess()
    {
        var simulation = new AtmosSimulation(1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        ChunkField<int> field = ChunkField<int>.Register(KeyPrefix + "disposed");
        simulation.GetChunkField(chunk, field);
        simulation.Dispose();

        Assert.That(() => simulation.GetChunkField(chunk, field), Throws.TypeOf<ObjectDisposedException>());
    }

    private static bool SameStorage<T>(FlatArray<T> left, FlatArray<T> right)
    {
        return left.AsSpan() == right.AsSpan();
    }
}