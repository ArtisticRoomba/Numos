using Numos.Collections;
using Numos.Maths;

namespace Numos.CoreSim.Tests;

[TestFixture]
public sealed class AtmosChunkSolverStorageTests
{
    [Test]
    public void EnsureInitialized_PreservesSolverStorage()
    {
        var chunk = new AtmosChunk(2, 3, 1);
        object key = new();
        int[] original = chunk.GetOrCreateSolverArray<int>(key, false);
        original[0] = 42;

        chunk.EnsureInitialized();

        Assert.That(chunk.GetOrCreateSolverArray<int>(key, false), Is.SameAs(original));
        Assert.That(original[0], Is.EqualTo(42));
    }

    [TestCase(2, 3, 1)]
    [TestCase(3, 2, 1)]
    [TestCase(3, 2, 2)]
    public void Initialize_DetachesStorageAndUsesNewDimensions(int width, int height, int depth)
    {
        var chunk = new AtmosChunk(2, 3, 1);
        object key = new();
        int[] original = chunk.GetOrCreateSolverArray<int>(key, false);
        original[0] = 42;

        chunk.Initialize(default, width, height, depth);
        FlatArray<int> replacement = chunk.GetOrCreateSolverFlatArray<int>(key, false);

        Assert.Multiple(() =>
        {
            Assert.That(chunk.GetOrCreateSolverArray<int>(key, false), Is.Not.SameAs(original));
            Assert.That(replacement.Dimensions, Is.EqualTo(new Int3(width, height, depth)));
            Assert.That(replacement.Length, Is.EqualTo(width * height * depth));
            Assert.That(replacement[0], Is.Zero);
            Assert.That(original[0], Is.EqualTo(42));
        });
    }

    [Test]
    public void Release_DetachesSolverStorage()
    {
        var chunk = new AtmosChunk(1, 1, 1);
        object key = new();
        int[] original = chunk.GetOrCreateSolverArray<int>(key, false);
        original[0] = 42;

        chunk.Release();

        Assert.That(chunk.GetOrCreateSolverArray<int>(key, false), Is.Not.SameAs(original));
        Assert.That(chunk.GetOrCreateSolverArray<int>(key, false)[0], Is.Zero);
    }
}