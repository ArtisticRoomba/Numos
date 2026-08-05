using Numos.Maths;

namespace Numos.CoreSim.Tests;

[TestFixture]
public sealed class AtmosChunkIndexingTests
{
    [Test]
    public void Dimensions_CombinesEveryAxis()
    {
        var chunk = new AtmosChunk(3, 4, 5);

        Assert.That(chunk.Dimensions, Is.EqualTo(new Int3(3, 4, 5)));
    }

    [TestCase(0, 0, 0, 0)]
    [TestCase(1, 0, 0, 1)]
    [TestCase(2, 0, 0, 2)]
    [TestCase(0, 1, 0, 3)]
    [TestCase(0, 3, 0, 9)]
    [TestCase(0, 0, 1, 12)]
    [TestCase(1, 2, 3, 43)]
    [TestCase(2, 3, 4, 59)]
    public void GetIndex_UsesXThenYThenZStorageOrder(int x, int y, int z, int expected)
    {
        var chunk = new AtmosChunk(3, 4, 5);

        Assert.That(chunk.GetIndex(x, y, z), Is.EqualTo(expected));
    }

    [TestCase(0, 0, 0)]
    [TestCase(2, 0, 0)]
    [TestCase(1, 2, 3)]
    [TestCase(2, 3, 4)]
    public void GetIndex_Int3OverloadMatchesCoordinateOverload(int x, int y, int z)
    {
        var chunk = new AtmosChunk(3, 4, 5);

        Assert.That(chunk.GetIndex(new Int3(x, y, z)), Is.EqualTo(chunk.GetIndex(x, y, z)));
    }

    [Test]
    public void IndexConversions_RoundTripEveryVoxelInRectangularChunk()
    {
        var chunk = new AtmosChunk(2, 3, 4);
        var observedIndices = new HashSet<ushort>();

        for (var z = 0; z < chunk.Depth; z++)
        for (var y = 0; y < chunk.Height; y++)
        for (var x = 0; x < chunk.Width; x++)
        {
            ushort index = chunk.GetIndex(x, y, z);
            (int actualX, int actualY, int actualZ) = chunk.GetXyz(index);

            Assert.Multiple(() =>
            {
                Assert.That(observedIndices.Add(index), Is.True, $"Index {index} was produced more than once.");
                Assert.That((actualX, actualY, actualZ), Is.EqualTo((x, y, z)));
                Assert.That(chunk.GetXyzInt3(index), Is.EqualTo(new Int3(x, y, z)));
            });
        }

        Assert.Multiple(() =>
        {
            Assert.That(observedIndices, Has.Count.EqualTo(chunk.VoxelCount));
            Assert.That(observedIndices.Order(), Is.EqualTo(Enumerable.Range(0, chunk.VoxelCount)));
        });
    }

    [TestCase(1, 1, 1, 0, 0, 0, 0)]
    [TestCase(1, 5, 1, 0, 4, 0, 4)]
    [TestCase(4, 1, 1, 3, 0, 0, 3)]
    [TestCase(1, 1, 4, 0, 0, 3, 3)]
    public void IndexConversions_HandleDegenerateAxes(
        int width, int height, int depth,
        int x, int y, int z,
        int expected)
    {
        var chunk = new AtmosChunk(width, height, depth);

        ushort index = chunk.GetIndex(x, y, z);

        Assert.Multiple(() =>
        {
            Assert.That(index, Is.EqualTo(expected));
            Assert.That(chunk.GetXyz(index), Is.EqualTo((x, y, z)));
            Assert.That(chunk.GetXyzInt3(index), Is.EqualTo(new Int3(x, y, z)));
        });
    }

    [Test]
    public void IndexConversions_HandleLargestCurrentlySafeVoxelCount()
    {
        const int width = 255;
        const int height = 257;
        var chunk = new AtmosChunk(width, height, 1);

        ushort index = chunk.GetIndex(width - 1, height - 1, 0);

        Assert.Multiple(() =>
        {
            Assert.That(chunk.VoxelCount, Is.EqualTo(ushort.MaxValue));
            Assert.That(index, Is.EqualTo(ushort.MaxValue - 1));
            Assert.That(chunk.GetXyz(index), Is.EqualTo((width - 1, height - 1, 0)));
        });
    }
}