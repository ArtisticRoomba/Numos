using Numos.CoreSim.Replay;
using Numos.CoreSim.Solvers;
using Numos.CoreSim.State;
using Numos.CoreSim.State.Advanced;
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

    private static readonly ChunkFieldDescriptor<int> VacuumCleared =
        ChunkFieldRegistry.Register<int>(Key("vacuum/cleared"), true, null, clearOnVacuum: true);

    private static readonly ChunkFieldDescriptor<int> VacuumKept =
        ChunkFieldRegistry.Register<int>(Key("vacuum/kept"), true, null);

    private static readonly ChunkFieldDescriptor<long> VacuumFixed =
        ChunkFieldRegistry.Register<long>(Key("vacuum/fixed"), true, 3, clearOnVacuum: true);

    private static readonly ChunkFieldDescriptor<int> SleepOnly =
        ChunkFieldRegistry.Register<int>(Key("sleep/only"), true, null, clearOnSleep: true);

    private static readonly ChunkFieldDescriptor<long> SleepFixed =
        ChunkFieldRegistry.Register<long>(Key("sleep/fixed"), true, 3, clearOnSleep: true);

    // Fixed length, no clear flags: must survive every clearing operation.
    private static readonly ChunkFieldDescriptor<long> FixedKept =
        ChunkFieldRegistry.Register<long>(Key("fixed/kept"), true, 3);

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
            Assert.That(chunk.HasCapturedSolverArrays, Is.False);
            Assert.That(chunk.CaptureSolverArrays(), Is.Empty);
        });

        int[] values = chunk.GetOrCreateField(Captured);

        Assert.Multiple(() =>
        {
            Assert.That(values, Has.Length.EqualTo(6));
            Assert.That(chunk.GetOrCreateField(Captured), Is.SameAs(values));
            Assert.That(chunk.HasCapturedSolverArrays, Is.True);
            Assert.That(
                chunk.CaptureSolverArrays().Select(static array => array.Key),
                Is.EqualTo(new[] { Captured.Key }));
        });
    }

    [Test]
    public void GetOrCreateField_UsesFixedLength()
    {
        var chunk = new AtmosChunk(2, 3, 1);

        Assert.That(chunk.GetOrCreateField(FixedLength), Has.Length.EqualTo(3));
    }

    [Test]
    public void TryGetField_ReturnsFalseWithoutAllocating()
    {
        var chunk = new AtmosChunk(2, 3, 1);

        Assert.Multiple(() =>
        {
            Assert.That(chunk.TryGetField(Captured, out int[]? array), Is.False);
            Assert.That(array, Is.Null);
            Assert.That(chunk.HasCapturedSolverArrays, Is.False);
            Assert.That(chunk.CaptureSolverArrays(), Is.Empty);
        });
    }

    [Test]
    public void TryGetField_ReturnsAllocatedStorage()
    {
        var chunk = new AtmosChunk(2, 3, 1);
        int[] values = chunk.GetOrCreateField(Captured);

        Assert.Multiple(() =>
        {
            Assert.That(chunk.TryGetField(Captured, out int[]? array), Is.True);
            Assert.That(array, Is.SameAs(values));
        });
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
                Assert.That(results[worker], Is.SameAs(chunk.GetOrCreateField(fields[worker % fields.Length])));
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

        Assert.That(
            keys,
            Is.EqualTo(
                new[]
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
            Assert.That(chunk.HasCapturedSolverArrays, Is.False);
            Assert.That(chunk.CaptureSolverArrays(), Is.Empty);
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

        Assert.That(target.HasCapturedSolverArrays, Is.True);
        int[] restored = target.GetOrCreateField(Captured);

        Assert.Multiple(() =>
        {
            Assert.That(restored, Is.EqualTo(new[] { 0, 7 }));
            Assert.That(restored, Is.Not.SameAs(source.GetOrCreateField(Captured)));
            Assert.That(target.GetOrCreateField(Captured), Is.SameAs(restored));
            Assert.That(target.GetOrCreateField(Transient), Is.EqualTo(new[] { 0f, 0f }));
        });
    }

    [Test]
    public void SolverArrayHash_MatchesObjectKeyedFormat()
    {
        // Pins the solver-array section of the state hash as of checkpoint format 8, where strings hash as UTF-8 like
        // the wire. It first came from the object-keyed GetOrCreateSolverArray path this storage replaced. A change here
        // breaks every saved state hash that includes rollback fields.
        const ulong expected = 0x72BB19B0E7A60E0DUL;
        var chunk = new AtmosChunk(2, 1, 1);
        chunk.GetOrCreateField(HashValueField)[0] = new HashValue { Flag = 3, Amount = 1.5f };
        chunk.GetOrCreateField(HashTotals)[1] = -3;
        chunk.GetOrCreateField(HashFloats)[0] = -0f;
        chunk.GetOrCreateField(HashFloats)[1] = BitConverter.Int32BitsToSingle(0x7FC00123);
        chunk.GetOrCreateField(HashCounts)[1] = 5;
        chunk.GetOrCreateField(HashScratch)[0] = 9;

        var hash = new StateHashSink();
        SolverArraysCodec.Write(ref hash, chunk.CaptureSolverArrays());

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
        var hash = checkpoint.ComputeStateHash();

        captured[0] = 12;
        kernel.RestoreCheckpoint(checkpoint, kernel.TickCount);

        Assert.Multiple(() =>
        {
            Assert.That(kernel.GetOrCreateChunkField(Int3.Zero, Captured)[0], Is.EqualTo(11));
            Assert.That(kernel.GetOrCreateChunkField(Int3.Zero, Transient)[0], Is.Zero);
            Assert.That(kernel.CaptureCheckpoint().ComputeStateHash(), Is.EqualTo(hash));
        });
    }

    private static AtmosKernel CreateKernel()
    {
        var kernel = new AtmosKernel(2, 2, 1);
        kernel.CreateAndRegisterChunk(Int3.Zero, 2, 2, 1);
        return kernel;
    }

    [Test]
    public void Register_SameDefinition_ReturnsSameDescriptor()
    {
        var first = ChunkFieldRegistry.Register<int>(Key("repeat"), true, null, true, true);
        var second = ChunkFieldRegistry.Register<int>(Key("repeat"), true, null, true, true);

        Assert.That(second, Is.SameAs(first));
    }

    [Test]
    public void Register_DifferentClearOnSleep_Throws()
    {
        ChunkFieldRegistry.Register<int>(Key("conflict/sleep"), clearOnSleep: true);

        var ex = Assert.Throws<InvalidOperationException>(() => ChunkFieldRegistry.Register<int>(
            Key("conflict/sleep"),
            clearOnSleep: false));

        Assert.That(ex!.Message, Does.Contain("cleared on sleep").And.Contain("kept on sleep"));
    }

    [Test]
    public void Register_DifferentClearOnVacuum_Throws()
    {
        ChunkFieldRegistry.Register<int>(Key("conflict/vacuum"), clearOnVacuum: true);

        var ex = Assert.Throws<InvalidOperationException>(() => ChunkFieldRegistry.Register<int>(
            Key("conflict/vacuum"),
            clearOnVacuum: false));

        Assert.That(ex!.Message, Does.Contain("cleared on vacuum").And.Contain("kept on vacuum"));
    }

    [Test]
    public void Register_DifferentElementType_Throws()
    {
        ChunkFieldRegistry.Register<int>(Key("conflict/type"));

        Assert.Throws<InvalidOperationException>(() => ChunkFieldRegistry.Register<float>(Key("conflict/type")));
    }

    [Test]
    public void SetVoxelToVacuum_ClearsOnlyClearOnVacuumFieldsAtThatVoxel()
    {
        var chunk = new AtmosChunk(3, 1, 1);
        int[] cleared = chunk.GetOrCreateField(VacuumCleared);
        int[] kept = chunk.GetOrCreateField(VacuumKept);
        Array.Fill(cleared, 5);
        Array.Fill(kept, 5);

        chunk.SetVoxelToVacuum(1);

        Assert.Multiple(() =>
        {
            Assert.That(cleared, Is.EqualTo(new[] { 5, 0, 5 }));
            Assert.That(kept, Is.EqualTo(new[] { 5, 5, 5 }));
        });
    }

    [Test]
    public void SetVoxelToVacuum_IgnoresClearOnSleepFields()
    {
        var chunk = new AtmosChunk(2, 1, 1);
        int[] values = chunk.GetOrCreateField(SleepOnly);
        Array.Fill(values, 5);

        chunk.SetVoxelToVacuum(0);

        Assert.That(values, Is.EqualTo(new[] { 5, 5 }));
    }

    [Test]
    public void SetVoxelToVacuum_LeavesFixedLengthFieldsAlone()
    {
        var chunk = new AtmosChunk(2, 1, 1);
        long[] values = chunk.GetOrCreateField(VacuumFixed);
        Array.Fill(values, 7L);

        chunk.SetVoxelToVacuum(1);

        Assert.That(values, Is.EqualTo(new[] { 7L, 7L, 7L }));
    }

    [Test]
    public void SetVoxelToVacuum_DoesNotAllocateMissingFields()
    {
        var chunk = new AtmosChunk(2, 1, 1);

        chunk.SetVoxelToVacuum(0);

        Assert.Multiple(() =>
        {
            Assert.That(chunk.HasCapturedSolverArrays, Is.False);
            Assert.That(chunk.IsVacuum[0], Is.True);
        });
    }

    [Test]
    public void SetChunkToVacuum_ClearsEveryVoxelOfClearOnVacuumFields()
    {
        var chunk = new AtmosChunk(3, 1, 1);
        int[] cleared = chunk.GetOrCreateField(VacuumCleared);
        int[] kept = chunk.GetOrCreateField(VacuumKept);
        Array.Fill(cleared, 5);
        Array.Fill(kept, 5);
        chunk.TotalPressure.AsSpan().Fill(2f);

        chunk.SetChunkToVacuum();

        Assert.Multiple(() =>
        {
            Assert.That(cleared, Is.EqualTo(new[] { 0, 0, 0 }));
            Assert.That(kept, Is.EqualTo(new[] { 5, 5, 5 }));
            Assert.That(chunk.TotalPressure.ToArray(), Is.All.Zero);
            Assert.That(chunk.IsVacuum.ToArray(), Is.All.True);
        });
    }

    [Test]
    public void SetChunkToVacuum_ClearsFixedLengthClearOnVacuumFields()
    {
        var chunk = new AtmosChunk(2, 1, 1);
        long[] values = chunk.GetOrCreateField(VacuumFixed);
        Array.Fill(values, 7L);

        chunk.SetChunkToVacuum();

        Assert.That(values, Is.EqualTo(new[] { 0L, 0L, 0L }));
    }

    [Test]
    public void SetChunkToVacuum_LeavesFixedLengthFieldsWithoutFlagAlone()
    {
        var chunk = new AtmosChunk(2, 1, 1);
        long[] values = chunk.GetOrCreateField(FixedKept);
        Array.Fill(values, 7L);

        chunk.SetChunkToVacuum();

        Assert.That(values, Is.EqualTo(new[] { 7L, 7L, 7L }));
    }

    [Test]
    public void SetChunkToVacuum_IgnoresClearOnSleepFields()
    {
        var chunk = new AtmosChunk(2, 1, 1);
        int[] perVoxel = chunk.GetOrCreateField(SleepOnly);
        long[] fixedLength = chunk.GetOrCreateField(SleepFixed);
        Array.Fill(perVoxel, 5);
        Array.Fill(fixedLength, 7L);

        chunk.SetChunkToVacuum();

        Assert.Multiple(() =>
        {
            Assert.That(perVoxel, Is.EqualTo(new[] { 5, 5 }));
            Assert.That(fixedLength, Is.EqualTo(new[] { 7L, 7L, 7L }));
        });
    }

    [Test]
    public void SetChunkToVacuum_DoesNotAllocateMissingFields()
    {
        var chunk = new AtmosChunk(2, 1, 1);

        chunk.SetChunkToVacuum();

        Assert.That(chunk.HasCapturedSolverArrays, Is.False);
    }

    [Test]
    public void Sleep_ClearsEntireClearOnSleepFields()
    {
        var chunk = new AtmosChunk(3, 1, 1);
        int[] cleared = chunk.GetOrCreateField(SleepOnly);
        int[] kept = chunk.GetOrCreateField(VacuumKept);
        Array.Fill(cleared, 5);
        Array.Fill(kept, 5);

        chunk.Sleep();

        Assert.Multiple(() =>
        {
            Assert.That(chunk.IsAwake, Is.False);
            Assert.That(cleared, Is.EqualTo(new[] { 0, 0, 0 }));
            Assert.That(kept, Is.EqualTo(new[] { 5, 5, 5 }));
        });
    }

    [Test]
    public void Sleep_ClearsFixedLengthClearOnSleepFields()
    {
        var chunk = new AtmosChunk(2, 1, 1);
        long[] values = chunk.GetOrCreateField(SleepFixed);
        Array.Fill(values, 7L);

        chunk.Sleep();

        Assert.That(values, Is.EqualTo(new[] { 0L, 0L, 0L }));
    }

    [Test]
    public void Sleep_LeavesFixedLengthFieldsWithoutFlagAlone()
    {
        var chunk = new AtmosChunk(2, 1, 1);
        long[] values = chunk.GetOrCreateField(FixedKept);
        Array.Fill(values, 7L);

        chunk.Sleep();

        Assert.That(values, Is.EqualTo(new[] { 7L, 7L, 7L }));
    }

    [Test]
    public void Sleep_IgnoresClearOnVacuumFields()
    {
        var chunk = new AtmosChunk(2, 1, 1);
        int[] perVoxel = chunk.GetOrCreateField(VacuumCleared);
        long[] fixedLength = chunk.GetOrCreateField(VacuumFixed);
        Array.Fill(perVoxel, 5);
        Array.Fill(fixedLength, 7L);

        chunk.Sleep();

        Assert.Multiple(() =>
        {
            Assert.That(perVoxel, Is.EqualTo(new[] { 5, 5 }));
            Assert.That(fixedLength, Is.EqualTo(new[] { 7L, 7L, 7L }));
        });
    }

    [Test]
    public void Sleep_DoesNotAllocateMissingFields()
    {
        var chunk = new AtmosChunk(2, 1, 1);

        chunk.Sleep();

        Assert.That(chunk.HasCapturedSolverArrays, Is.False);
    }

    [Test]
    public void Sleep_MarksChunkChanged()
    {
        var chunk = new AtmosChunk(1, 1, 1);
        long before = chunk.Version.Revision;

        chunk.Sleep();

        Assert.That(chunk.Version.Revision, Is.GreaterThan(before));
    }

    [Test]
    public void Sleep_WhenAlreadyAsleep_StillClears()
    {
        var chunk = new AtmosChunk(2, 1, 1);
        int[] values = chunk.GetOrCreateField(SleepOnly);
        chunk.Sleep();
        Array.Fill(values, 5);

        chunk.Sleep();

        Assert.That(values, Is.EqualTo(new[] { 0, 0 }));
    }
}