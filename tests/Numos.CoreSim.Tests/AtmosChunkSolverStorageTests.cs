using Numos.CoreSim.Solvers;

namespace Numos.CoreSim.Tests;

[TestFixture]
public sealed class AtmosChunkSolverStorageTests
{
    private readonly static ChunkFieldDescriptor<int> Field =
        ChunkFieldRegistry.Register<int>($"{typeof(AtmosChunkSolverStorageTests).FullName}/scratch", false, null);

    [Test]
    public void EnsureInitialized_PreservesSolverStorage()
    {
        var chunk = new AtmosChunk(2, 3, 1);
        int[] original = chunk.GetOrCreateField(Field);
        original[0] = 42;

        chunk.EnsureInitialized();

        Assert.That(chunk.GetOrCreateField(Field), Is.SameAs(original));
        Assert.That(original[0], Is.EqualTo(42));
    }

    [TestCase(2, 3, 1)]
    [TestCase(3, 2, 1)]
    [TestCase(3, 2, 2)]
    public void Initialize_DetachesStorageAndUsesNewDimensions(int width, int height, int depth)
    {
        var chunk = new AtmosChunk(2, 3, 1);
        int[] original = chunk.GetOrCreateField(Field);
        original[0] = 42;

        chunk.Initialize(default, width, height, depth);
        int[] replacement = chunk.GetOrCreateField(Field);

        Assert.Multiple(() =>
        {
            Assert.That(replacement, Is.Not.SameAs(original));
            Assert.That(replacement, Has.Length.EqualTo(width * height * depth));
            Assert.That(replacement[0], Is.Zero);
            Assert.That(original[0], Is.EqualTo(42));
        });
    }

    [Test]
    public void Release_DetachesSolverStorage()
    {
        var chunk = new AtmosChunk(1, 1, 1);
        int[] original = chunk.GetOrCreateField(Field);
        original[0] = 42;

        chunk.Release();

        Assert.That(chunk.GetOrCreateField(Field), Is.Not.SameAs(original));
        Assert.That(chunk.GetOrCreateField(Field)[0], Is.Zero);
    }
}