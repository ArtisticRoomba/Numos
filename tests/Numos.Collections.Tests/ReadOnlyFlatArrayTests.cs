using Numos.Maths;

namespace Numos.Collections.Tests;

[TestFixture]
public sealed class ReadOnlyFlatArrayTests
{
    [Test]
    public void DefaultValue_IsNotInitialized()
    {
        var array = default(ReadOnlyFlatArray<int>);

        Assert.Multiple(() =>
        {
            Assert.That(array.IsInitialized, Is.False);
            Assert.That(array.Length, Is.Zero);
            Assert.That(array.Dimensions, Is.EqualTo(default(Int3)));
            Assert.That(array.AsSpan().IsEmpty, Is.True);
        });
    }

    [Test]
    public void AsReadOnly_SharesStorageAndDimensions()
    {
        int[] data = new int[24];
        var flat = new FlatArray<int>(data, new Int3(2, 3, 4));

        ReadOnlyFlatArray<int> view = flat.AsReadOnly();
        data[23] = 7;

        Assert.Multiple(() =>
        {
            Assert.That(view.IsInitialized, Is.True);
            Assert.That(view.Length, Is.EqualTo(24));
            Assert.That(view.Dimensions, Is.EqualTo(new Int3(2, 3, 4)));
            Assert.That(view[23], Is.EqualTo(7));
            Assert.That(view[new Int3(1, 2, 3)], Is.EqualTo(7));
        });
    }

    [Test]
    public void ImplicitConversion_MatchesAsReadOnly()
    {
        int[] data = [1, 2, 3];
        ReadOnlyFlatArray<int> view = new FlatArray<int>(data);

        Assert.Multiple(() =>
        {
            Assert.That(view.Length, Is.EqualTo(3));
            Assert.That(view.Dimensions, Is.EqualTo(new Int3(3, 1, 1)));
            Assert.That(view[1], Is.EqualTo(2));
        });
    }

    [Test]
    public void AsReadOnly_OfDefault_IsNotInitialized()
    {
        ReadOnlyFlatArray<int> view = default(FlatArray<int>);

        Assert.That(view.IsInitialized, Is.False);
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
        ReadOnlyFlatArray<int> view = new FlatArray<int>(data, new Int3(2, 3, 4));
        var position = new Int3(x, y, z);

        Assert.Multiple(() =>
        {
            Assert.That(view[position], Is.EqualTo(expected));
            Assert.That(view.GetIndex(position), Is.EqualTo(expected));
            Assert.That(view.GetIndexUnsafe(position), Is.EqualTo(expected));
            Assert.That(view.GetPosition(expected), Is.EqualTo(position));
        });
    }

    [Test]
    public void GetIndex_OutsideDimensions_Throws()
    {
        ReadOnlyFlatArray<int> view = new FlatArray<int>(new int[24], new Int3(2, 3, 4));

        Assert.Multiple(() =>
        {
            Assert.That(() => view.GetIndex(new Int3(2, 0, 0)), Throws.TypeOf<IndexOutOfRangeException>());
            Assert.That(() => view.GetIndex(new Int3(0, -1, 0)), Throws.TypeOf<IndexOutOfRangeException>());
            Assert.That(() => view[new Int3(0, 0, 4)], Throws.TypeOf<IndexOutOfRangeException>());
            Assert.That(() => view.GetPosition(24), Throws.TypeOf<IndexOutOfRangeException>());
            Assert.That(() => view[24], Throws.TypeOf<IndexOutOfRangeException>());
        });
    }

    [Test]
    public void AsSpan_AliasesBackingStorage()
    {
        int[] data = [1, 2, 3];
        ReadOnlyFlatArray<int> view = new FlatArray<int>(data);

        ReadOnlySpan<int> span = view.AsSpan();
        data[1] = 20;

        Assert.That(span[1], Is.EqualTo(20));
    }

    [Test]
    public void CopyTo_CopiesEveryElement()
    {
        ReadOnlyFlatArray<int> view = new FlatArray<int>([1, 2, 3]);
        int[] destination = new int[3];

        view.CopyTo(destination);

        Assert.That(destination, Is.EqualTo(new[] { 1, 2, 3 }));
    }

    [Test]
    public void CopyTo_ShortDestination_Throws()
    {
        ReadOnlyFlatArray<int> view = new FlatArray<int>([1, 2, 3]);

        Assert.That(() => view.CopyTo(new int[2]), Throws.ArgumentException);
    }

    [Test]
    public void ToArray_ReturnsIndependentCopy()
    {
        int[] data = [1, 2, 3];
        ReadOnlyFlatArray<int> view = new FlatArray<int>(data);

        int[] copy = view.ToArray();
        data[0] = 99;

        Assert.That(copy, Is.EqualTo(new[] { 1, 2, 3 }));
    }
}