using Numos.Collections;
using Numos.Maths;

namespace Numos.API.Tests;

[TestFixture]
public sealed class AtmosSolverStorageTests
{
    [Test]
    public void CustomSolver_ReusesRegularAndFlatStorageAcrossTicks()
    {
        using var simulation = new AtmosSimulation(2, 3, 4);
        var chunk = simulation.CreateAndRegisterChunk(default);
        object key = new();
        int[] original = simulation.GetOrCreateChunkSolverArray<int>(chunk, key, false);
        Assert.That(original, Is.EqualTo(new int[24]));

        simulation.Solvers.Register(
            "scratch",
            world =>
            {
                FlatArray<int> flat = world.GetOrCreateChunkSolverFlatArray<int>(chunk, key, false);
                Assert.That(flat.Dimensions, Is.EqualTo(new Int3(2, 3, 4)));
                flat[new Int3(1, 2, 3)]++;
            });

        simulation.Tick();
        simulation.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(simulation.GetOrCreateChunkSolverArray<int>(chunk, key, false), Is.SameAs(original));
            Assert.That(original[23], Is.EqualTo(2));
        });
    }

    [Test]
    public void Storage_IsIsolatedByChunkSimulationAndKeyIdentity()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        using var other = new AtmosSimulation(1, 1, 1);
        var first = simulation.CreateAndRegisterChunk(default);
        var second = simulation.CreateAndRegisterChunk(Int3.PosX);
        var otherChunk = other.CreateAndRegisterChunk(default);
        var key = new EqualKey(1);
        var equalKey = new EqualKey(1);
        simulation.GetOrCreateChunkSolverArray<int>(first, key, false)[0] = 42;

        Assert.Multiple(() =>
        {
            Assert.That(simulation.GetOrCreateChunkSolverArray<int>(first, key, false)[0], Is.EqualTo(42));
            Assert.That(simulation.GetOrCreateChunkSolverArray<int>(first, equalKey, false)[0], Is.Zero);
            Assert.That(simulation.GetOrCreateChunkSolverArray<int>(second, key, false)[0], Is.Zero);
            Assert.That(other.GetOrCreateChunkSolverArray<int>(otherChunk, key, false)[0], Is.Zero);
        });
    }

    [TestCase(0)]
    [TestCase(7)]
    public void RegularArray_AllowsNonVoxelLengths(int length)
    {
        using var simulation = new AtmosSimulation(2, 3, 4);
        var chunk = simulation.CreateAndRegisterChunk(default);
        object key = new();
        int[] data = simulation.GetOrCreateChunkSolverArray<int>(chunk, key, false, length);

        Assert.Multiple(() =>
        {
            Assert.That(data, Has.Length.EqualTo(length));
            Assert.That(simulation.GetOrCreateChunkSolverArray<int>(chunk, key, false, length), Is.SameAs(data));
            Assert.That(
                () => simulation.GetOrCreateChunkSolverFlatArray<int>(chunk, key, false),
                Throws.InvalidOperationException);
        });
    }

    [Test]
    public void ExistingKey_RejectsDifferentTypesAndLengthsWithoutReplacingData()
    {
        using var simulation = new AtmosSimulation(2, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        object key = new();
        string[] data = simulation.GetOrCreateChunkSolverArray<string>(chunk, key, false);
        data[0] = "retained";

        Assert.Multiple(() =>
        {
            Assert.That(
                () => simulation.GetOrCreateChunkSolverArray<object>(chunk, key, false),
                Throws.InvalidOperationException);

            Assert.That(
                () => simulation.GetOrCreateChunkSolverArray<int>(chunk, key, false),
                Throws.InvalidOperationException);

            Assert.That(
                () => simulation.GetOrCreateChunkSolverArray<string>(chunk, key, false, 3),
                Throws.InvalidOperationException);

            Assert.That(simulation.GetOrCreateChunkSolverArray<string>(chunk, key, false), Is.SameAs(data));
            Assert.That(data[0], Is.EqualTo("retained"));
        });
    }

    [Test]
    public void InvalidRequests_RejectNullKeysNegativeLengthsAndMissingChunks()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        object key = new();
        var missing = new AtmosChunkHandle(Int3.PosX);

        Assert.Multiple(() =>
        {
            Assert.That(
                () => simulation.GetOrCreateChunkSolverArray<int>(chunk, null!, false),
                Throws.ArgumentNullException);

            Assert.That(
                () => simulation.GetOrCreateChunkSolverFlatArray<int>(chunk, null!, false),
                Throws.ArgumentNullException);

            Assert.That(
                () => simulation.GetOrCreateChunkSolverArray<int>(chunk, key, false, -1),
                Throws.TypeOf<ArgumentOutOfRangeException>());

            Assert.That(
                () => simulation.GetOrCreateChunkSolverArray<int>(missing, key, false),
                Throws.TypeOf<KeyNotFoundException>());

            Assert.That(
                () => simulation.GetOrCreateChunkSolverFlatArray<int>(missing, key, false),
                Throws.TypeOf<KeyNotFoundException>());
        });

        Assert.That(simulation.GetOrCreateChunkSolverArray<int>(chunk, key, false), Has.Length.EqualTo(1));
    }

    [Test]
    public void ConcurrentRequests_ReturnOneArrayForTheSameSlot()
    {
        using var simulation = new AtmosSimulation(2, 3, 4);
        var chunk = simulation.CreateAndRegisterChunk(default);
        object key = new();
        int[][] arrays = new int[32][];

        Parallel.For(
            0,
            arrays.Length,
            index =>
                arrays[index] = simulation.GetOrCreateChunkSolverArray<int>(chunk, key, false));

        Assert.That(arrays.All(array => ReferenceEquals(array, arrays[0])), Is.True);
    }

    [Test]
    public void ChunkReplacement_DetachesPreviousStorage()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        object key = new();
        int[] previous = simulation.GetOrCreateChunkSolverArray<int>(chunk, key, false);
        previous[0] = 42;

        simulation.UnregisterChunk(chunk);
        simulation.CreateAndRegisterChunk(chunk.Position);
        int[] replacement = simulation.GetOrCreateChunkSolverArray<int>(chunk, key, false);
        previous[0] = 99;

        Assert.Multiple(() =>
        {
            Assert.That(replacement, Is.Not.SameAs(previous));
            Assert.That(replacement[0], Is.Zero);
        });
    }

    [Test]
    public void ScratchStorage_DoesNotChangePhysicalStateRecordingOrCheckpointContents()
    {
        using var simulation = new AtmosSimulation(2, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        object key = new();
        var version = simulation.GetChunkSnapshot(chunk).Version;
        var hash = simulation.ComputeStateHash();
        simulation.StartRecording();

        FlatArray<float> data = simulation.GetOrCreateChunkSolverFlatArray<float>(chunk, key, false);
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
        FlatArray<float> restored = simulation.GetOrCreateChunkSolverFlatArray<float>(chunk, key, false);
        Assert.That(restored[0], Is.Zero);
        data[0] = 99f;
        Assert.That(restored[0], Is.Zero);
    }

    [Test]
    public void DisposedSimulation_RejectsStorageAccess()
    {
        var simulation = new AtmosSimulation(1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        object key = new();
        simulation.GetOrCreateChunkSolverArray<int>(chunk, key, false);
        simulation.Dispose();

        Assert.Multiple(() =>
        {
            Assert.That(
                () => simulation.GetOrCreateChunkSolverArray<int>(chunk, key, false),
                Throws.TypeOf<ObjectDisposedException>());

            Assert.That(
                () => simulation.GetOrCreateChunkSolverFlatArray<int>(chunk, key, false),
                Throws.TypeOf<ObjectDisposedException>());
        });
    }

    private sealed record EqualKey(int Value);
}