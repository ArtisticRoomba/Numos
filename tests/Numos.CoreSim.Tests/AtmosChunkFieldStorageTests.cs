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
        chunk.GetOrCreateSolverArray<int>(Key("order/legacy"), true);
        chunk.GetOrCreateField(Lower);

        string[] keys = chunk.CaptureSolverArrays().Select(static array => array.Key).ToArray();

        Assert.That(keys, Is.EqualTo(new[]
        {
            Key("order/Alpha"),
            Key("order/alpha"),
            Key("order/legacy"),
            Key("order/zeta")
        }));
    }

    [Test]
    public void CaptureSolverArrays_RejectsKeyUsedByBothApis()
    {
        var chunk = new AtmosChunk(1, 1, 1);
        chunk.GetOrCreateField(Captured);
        chunk.GetOrCreateSolverArray<int>(Captured.Key, true);

        Assert.Throws<InvalidOperationException>(() => chunk.CaptureSolverArrays());
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
        source.GetOrCreateSolverArray<int>(Key("restore/legacy"), true)[0] = 9;
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
            Assert.That(target.GetOrCreateSolverArray<int>(Key("restore/legacy"), true)[0], Is.EqualTo(9));
            Assert.That(target.HasCapturedSolverArrays, Is.True);
        });
    }

    [Test]
    public void ComputeStateHash_MatchesObjectKeyedStorage()
    {
        using var legacy = CreateKernel();
        using var slotted = CreateKernel();
        legacy.GetOrCreateChunkSolverArray<int>(Int3.Zero, Captured.Key, true, null)[2] = 5;
        legacy.GetOrCreateChunkSolverArray<long>(Int3.Zero, FixedLength.Key, true, 3)[1] = -3;
        slotted.GetOrCreateChunkField(Int3.Zero, FixedLength)[1] = -3;
        slotted.GetOrCreateChunkField(Int3.Zero, Captured)[2] = 5;
        slotted.GetOrCreateChunkField(Int3.Zero, Transient)[0] = 2f;

        Assert.That(slotted.ComputeStateHash(), Is.EqualTo(legacy.ComputeStateHash()));
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

    private static AtmosKernel CreateKernel()
    {
        var kernel = new AtmosKernel(2, 2, 1);
        kernel.CreateAndRegisterChunk(Int3.Zero, 2, 2, 1);
        return kernel;
    }
}