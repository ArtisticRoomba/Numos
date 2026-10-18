using Numos.CoreSim.Datatypes.Snapshots;
using Numos.CoreSim.Replay;
using Numos.CoreSim.Solvers;
using Numos.Maths;

namespace Numos.CoreSim.Tests;

[TestFixture]
public sealed class AtmosChunkFieldStorageTests
{
    private static readonly ChunkFieldDescriptor<int> Captured =
        ChunkFieldRegistry.Register<int>(Key("captured"), true, null);

    private static readonly ChunkFieldDescriptor<float> Transient =
        ChunkFieldRegistry.Register<float>(Key("transient"), false, null);

    private static readonly ChunkFieldDescriptor<long> FixedLength =
        ChunkFieldRegistry.Register<long>(Key("fixed-length"), true, 3);

    // Registered in reverse ordinal order so slot order and key order disagree.
    private static readonly ChunkFieldDescriptor<int> Zeta = ChunkFieldRegistry.Register<int>(Key("order/zeta"), true, null);
    private static readonly ChunkFieldDescriptor<int> Lower = ChunkFieldRegistry.Register<int>(Key("order/alpha"), true, null);
    private static readonly ChunkFieldDescriptor<int> Upper = ChunkFieldRegistry.Register<int>(Key("order/Alpha"), true, null);

    // Registered out of ordinal order. Keys, types and lengths are pinned by the hash format test, so leave them be.
    private static readonly ChunkFieldDescriptor<HashValue> HashValueField =
        ChunkFieldRegistry.Register<HashValue>(Key("hash/value"), true, 1);

    private static readonly ChunkFieldDescriptor<long> HashTotals =
        ChunkFieldRegistry.Register<long>(Key("hash/Totals"), true, 3);

    private static readonly ChunkFieldDescriptor<float> HashFloats =
        ChunkFieldRegistry.Register<float>(Key("hash/floats"), true, null);

    private static readonly ChunkFieldDescriptor<int> HashCounts =
        ChunkFieldRegistry.Register<int>(Key("hash/counts"), true, null);

    private static readonly ChunkFieldDescriptor<int> HashScratch =
        ChunkFieldRegistry.Register<int>(Key("hash/scratch"), false, null);

    private struct HashValue
    {
        public int Flag;
        public float Amount;
    }

    // The registry is process-wide, so every key here is namespaced to this fixture.
    private static string Key(string name)
    {
        return $"{typeof(AtmosChunkFieldStorageTests).FullName}/{name}";
    }

    [Test]
    public void GetOrCreateField_AllocatesLazily()
    {
        var chunk = new AtmosChunk(2, 3, 1);

        Assert.Multiple(() =>
        {
            Assert.That(chunk.TryGetField(Captured, out _), Is.False);
            Assert.That(chunk.HasCapturedSolverArrays, Is.False);
            Assert.That(chunk.CaptureSolverArrays(), Is.Empty);
        });

        int[] values = chunk.GetOrCreateField(Captured);

        Assert.Multiple(() =>
        {
            Assert.That(values, Has.Length.EqualTo(6));
            Assert.That(chunk.GetOrCreateField(Captured), Is.SameAs(values));
            Assert.That(chunk.TryGetField(Captured, out int[]? found), Is.True);
            Assert.That(found, Is.SameAs(values));
            Assert.That(chunk.HasCapturedSolverArrays, Is.True);
            Assert.That(chunk.TryGetField(Transient, out _), Is.False);
        });
    }

    [Test]
    public void GetOrCreateField_UsesFixedLength()
    {
        var chunk = new AtmosChunk(2, 3, 1);

        Assert.That(chunk.GetOrCreateField(FixedLength), Has.Length.EqualTo(3));
    }

    [Test]
    public void GetOrCreateField_ParallelFirstTouchSharesStorage()
    {
        const int workerCount = 64;
        ChunkFieldDescriptor<int>[] fields = [Captured, Zeta, Lower, Upper];

        for (int round = 0; round < 8; round++)
        {
            var chunk = new AtmosChunk(4, 4, 1);
            var results = new int[workerCount][];
            using var start = new ManualResetEventSlim();
            var workers = new Thread[workerCount];
            for (int worker = 0; worker < workerCount; worker++)
            {
                int index = worker;
                workers[index] = new Thread(() =>
                {
                    start.Wait();
                    results[index] = chunk.GetOrCreateField(fields[index % fields.Length]);
                });
                workers[index].Start();
            }

            start.Set();
            foreach (var thread in workers)
                thread.Join();

            for (int worker = 0; worker < workerCount; worker++)
            {
                Assert.That(chunk.TryGetField(fields[worker % fields.Length], out int[]? stored), Is.True);
                Assert.That(results[worker], Is.SameAs(stored));
            }

            Assert.That(chunk.CaptureSolverArrays(), Has.Length.EqualTo(fields.Length));
        }
    }

    [Test]
    public void CaptureSolverArrays_OrdersByOrdinalKey()
    {
        var chunk = new AtmosChunk(1, 1, 1);
        chunk.GetOrCreateField(Zeta);
        chunk.GetOrCreateField(Transient);
        chunk.GetOrCreateField(Upper);
        chunk.GetOrCreateField(Lower);

        string[] keys = chunk.CaptureSolverArrays().Select(static array => array.Key).ToArray();

        Assert.That(keys, Is.EqualTo(new[]
        {
            Key("order/Alpha"),
            Key("order/alpha"),
            Key("order/zeta")
        }));
    }

    [Test]
    public void GetOrCreateField_MarksChangedOnlyForCapturedCreation()
    {
        var chunk = new AtmosChunk(1, 1, 1);
        long initial = chunk.Version.Revision;

        chunk.GetOrCreateField(Transient);
        long afterTransient = chunk.Version.Revision;
        chunk.GetOrCreateField(Captured);
        long afterCaptured = chunk.Version.Revision;
        chunk.GetOrCreateField(Captured);

        Assert.Multiple(() =>
        {
            Assert.That(afterTransient, Is.EqualTo(initial));
            Assert.That(afterCaptured, Is.EqualTo(initial + 1));
            Assert.That(chunk.Version.Revision, Is.EqualTo(afterCaptured));
        });
    }

    [Test]
    public void Initialize_DetachesFields()
    {
        var chunk = new AtmosChunk(2, 1, 1);
        int[] original = chunk.GetOrCreateField(Captured);
        original[0] = 42;

        chunk.Initialize(default, 3, 1, 1);
        int[] replacement = chunk.GetOrCreateField(Captured);

        Assert.Multiple(() =>
        {
            Assert.That(replacement, Is.Not.SameAs(original));
            Assert.That(replacement, Has.Length.EqualTo(3));
            Assert.That(replacement[0], Is.Zero);
            Assert.That(original[0], Is.EqualTo(42));
        });
    }

    [Test]
    public void Release_DetachesFields()
    {
        var chunk = new AtmosChunk(1, 1, 1);
        int[] original = chunk.GetOrCreateField(Captured);

        chunk.Release();

        Assert.Multiple(() =>
        {
            Assert.That(chunk.TryGetField(Captured, out _), Is.False);
            Assert.That(chunk.HasCapturedSolverArrays, Is.False);
            Assert.That(chunk.GetOrCreateField(Captured), Is.Not.SameAs(original));
        });
    }

    [Test]
    public void RestoreSolverArrays_RoutesFieldsIntoSlots()
    {
        var source = new AtmosChunk(2, 1, 1);
        source.GetOrCreateField(Captured)[1] = 7;
        source.GetOrCreateField(Transient)[1] = 1f;
        var snapshots = source.CaptureSolverArrays();

        var target = new AtmosChunk(2, 1, 1);
        target.RestoreSolverArrays(snapshots);

        Assert.Multiple(() =>
        {
            Assert.That(target.TryGetField(Captured, out int[]? restored), Is.True);
            Assert.That(restored, Is.EqualTo(new[] { 0, 7 }));
            Assert.That(restored, Is.Not.SameAs(source.GetOrCreateField(Captured)));
            Assert.That(target.GetOrCreateField(Captured), Is.SameAs(restored));
            Assert.That(target.TryGetField(Transient, out _), Is.False);
            Assert.That(target.HasCapturedSolverArrays, Is.True);
        });
    }

    [Test]
    public void RestoreSolverArrays_BindsSnapshotsWithoutDescriptorByKey()
    {
        var snapshot = new AtmosSolverArraySnapshot(Captured.Key, new SolverArrayStorage<int>([4, 6], true));
        var chunk = new AtmosChunk(2, 1, 1);

        chunk.RestoreSolverArrays([snapshot]);

        Assert.Multiple(() =>
        {
            Assert.That(chunk.TryGetField(Captured, out int[]? restored), Is.True);
            Assert.That(restored, Is.EqualTo(new[] { 4, 6 }));
            Assert.That(
                chunk.CaptureSolverArrays().Select(static array => array.Key),
                Is.EqualTo(new[] { Captured.Key }));
        });
    }

    [Test]
    public void RestoreSolverArrays_RejectsSnapshotsWithoutMatchingField()
    {
        var chunk = new AtmosChunk(2, 1, 1);

        Assert.Multiple(() =>
        {
            Assert.That(
                () => chunk.RestoreSolverArrays([Detached(Key("restore/unregistered"), new int[2])]),
                Throws.InvalidOperationException);
            Assert.That(
                () => chunk.RestoreSolverArrays([Detached(Captured.Key, new long[2])]),
                Throws.InvalidOperationException);
            Assert.That(
                () => chunk.RestoreSolverArrays([Detached(Captured.Key, new int[3])]),
                Throws.InvalidOperationException);
            Assert.That(
                () => chunk.RestoreSolverArrays([Detached(Transient.Key, new float[2])]),
                Throws.InvalidOperationException);
        });
    }

    [Test]
    public void SolverArrayHash_MatchesObjectKeyedFormat()
    {
        // The expected value was computed with the object-keyed GetOrCreateSolverArray path this storage replaced,
        // using the same keys, element types, lengths and values. A change here breaks every saved state hash that
        // includes rollback fields.
        const ulong expected = 0x357B98807B35979FUL;
        var chunk = new AtmosChunk(2, 1, 1);
        chunk.GetOrCreateField(HashValueField)[0] = new HashValue { Flag = 3, Amount = 1.5f };
        chunk.GetOrCreateField(HashTotals)[1] = -3;
        chunk.GetOrCreateField(HashFloats)[0] = -0f;
        chunk.GetOrCreateField(HashFloats)[1] = BitConverter.Int32BitsToSingle(0x7FC00123);
        chunk.GetOrCreateField(HashCounts)[1] = 5;
        chunk.GetOrCreateField(HashScratch)[0] = 9;

        var snapshots = chunk.CaptureSolverArrays();
        var hash = new AtmosStateHasher();
        hash.Add(snapshots.Length);
        foreach (var snapshot in snapshots)
            snapshot.AppendHash(ref hash);

        Assert.That(hash.Value, Is.EqualTo(expected));
    }

    [Test]
    public void RestoreCheckpoint_RestoresCapturedFieldsAndDropsTransientFields()
    {
        using var kernel = CreateKernel();
        int[] captured = kernel.GetOrCreateChunkField(Int3.Zero, Captured);
        captured[0] = 11;
        kernel.GetOrCreateChunkField(Int3.Zero, Transient)[0] = 3f;
        var checkpoint = kernel.CaptureCheckpoint();
        var hash = kernel.ComputeStateHash();

        captured[0] = 12;
        kernel.RestoreCheckpoint(checkpoint);

        Assert.Multiple(() =>
        {
            Assert.That(kernel.GetOrCreateChunkField(Int3.Zero, Captured)[0], Is.EqualTo(11));
            Assert.That(kernel.GetOrCreateChunkField(Int3.Zero, Transient)[0], Is.Zero);
            Assert.That(kernel.ComputeStateHash(), Is.EqualTo(hash));
        });
    }

    private static AtmosSolverArraySnapshot Detached<T>(string key, T[] values)
    {
        return new AtmosSolverArraySnapshot(key, new SolverArrayStorage<T>(values, true));
    }

    private static AtmosKernel CreateKernel()
    {
        var kernel = new AtmosKernel(2, 2, 1);
        kernel.CreateAndRegisterChunk(Int3.Zero, 2, 2, 1);
        return kernel;
    }
}