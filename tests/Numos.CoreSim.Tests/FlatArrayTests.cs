using Numos.CoreSim.Collections;
using Numos.Maths;

namespace Numos.CoreSim.Tests;

[TestFixture]
public sealed class FlatArrayTests
{
    [Test]
    public void DefaultValue_IsNotInitialized()
    {
        var array = default(FlatArray<int>);

        Assert.Multiple(() =>
        {
            Assert.That(array.IsInitialized, Is.False);
            Assert.That(array.Length, Is.Zero);
            Assert.That(array.Dimensions, Is.EqualTo(default(Int3)));
        });
    }

    [Test]
    public void IntegerIndexer_ReadsAndWritesBackingArray()
    {
        int[] data = [1, 2, 3, 4];
        var array = new FlatArray<int>(data);

        array[2] = 30;

        Assert.Multiple(() =>
        {
            Assert.That(array[2], Is.EqualTo(30));
            Assert.That(data[2], Is.EqualTo(30));
        });
    }

    [TestCase(0, 0, 0, 0)]
    [TestCase(1, 0, 0, 1)]
    [TestCase(0, 1, 0, 2)]
    [TestCase(1, 2, 0, 5)]
    [TestCase(0, 0, 1, 6)]
    [TestCase(1, 2, 3, 23)]
    public void Int3Indexer_UsesXThenYThenZStorageOrder(int x, int y, int z, int expected)
    {
        int[] data = Enumerable.Range(0, 24).ToArray();
        var array = new FlatArray<int>(data, new Int3(2, 3, 4));

        Assert.That(array[new Int3(x, y, z)], Is.EqualTo(expected));
    }

    [Test]
    public void Int3Indexer_WritesBackingArray()
    {
        var data = new int[24];
        var array = new FlatArray<int>(data, new Int3(2, 3, 4));

        array[new Int3(1, 2, 3)] = 42;

        Assert.That(data[23], Is.EqualTo(42));
    }

    [Test]
    public void IndexConversions_RoundTripEveryElement()
    {
        var array = new FlatArray<int>(new int[24], new Int3(2, 3, 4));

        for (var z = 0; z < 4; z++)
        for (var y = 0; y < 3; y++)
        for (var x = 0; x < 2; x++)
        {
            var position = new Int3(x, y, z);
            Assert.That(array.GetPosition(array.GetIndex(position)), Is.EqualTo(position));
        }
    }

    [Test]
    public void BulkOperations_ModifyAndCopyWrappedStorage()
    {
        var array = new FlatArray<int>(new int[4]);
        array.Fill(7);
        array.CopyFrom([1, 2]);
        var destination = new int[4];
        array.CopyTo(destination);

        Assert.That(destination, Is.EqualTo(new[] { 1, 2, 7, 7 }));

        array.Clear();

        Assert.That(array.ToArray(), Is.All.Zero);
    }

    [TestCase(-1, 0, 0)]
    [TestCase(2, 0, 0)]
    [TestCase(0, -1, 0)]
    [TestCase(0, 3, 0)]
    [TestCase(0, 0, -1)]
    [TestCase(0, 0, 4)]
    public void Int3Indexer_RejectsCoordinatesOutsideDimensions(int x, int y, int z)
    {
        var array = new FlatArray<int>(new int[24], new Int3(2, 3, 4));

        Assert.That(() => _ = array[new Int3(x, y, z)], Throws.TypeOf<IndexOutOfRangeException>());
    }
}