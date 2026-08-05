using NUnit.Framework;

namespace Numos.Maths.Tests;

[TestFixture]
public sealed class Int3Tests
{
    [TestCase(0, 0, 0)]
    [TestCase(-12, 34, -56)]
    [TestCase(int.MinValue, int.MaxValue, -1)]
    public void Constructor_AssignsEveryCoordinate(int x, int y, int z)
    {
        var value = new Int3(x, y, z);

        Assert.Multiple(() =>
        {
            Assert.That(value.X, Is.EqualTo(x));
            Assert.That(value.Y, Is.EqualTo(y));
            Assert.That(value.Z, Is.EqualTo(z));
        });
    }

    [Test]
    public void DefaultValue_HasZeroCoordinates()
    {
        var value = default(Int3);

        Assert.Multiple(() =>
        {
            Assert.That(value.X, Is.Zero);
            Assert.That(value.Y, Is.Zero);
            Assert.That(value.Z, Is.Zero);
        });
    }

    [TestCase(0, 0, 0)]
    [TestCase(-4, 0, 9)]
    [TestCase(int.MinValue, int.MaxValue, int.MinValue)]
    public void EqualValues_AreEqualThroughEveryEqualitySurface(int x, int y, int z)
    {
        var left = new Int3(x, y, z);
        var right = new Int3(x, y, z);

        Assert.Multiple(() =>
        {
            Assert.That(left.Equals(right), Is.True);
            Assert.That(left.Equals((object)right), Is.True);
            Assert.That(left == right, Is.True);
            Assert.That(right == left, Is.True);
            Assert.That(left != right, Is.False);
            Assert.That(left, Is.EqualTo(right));
        });
    }

    [TestCase(2, 3, 4, 1, 3, 4)]
    [TestCase(2, 3, 4, 2, 1, 4)]
    [TestCase(2, 3, 4, 2, 3, 9)]
    public void Equality_UsesAllThreeCoordinates(
        int leftX, int leftY, int leftZ,
        int rightX, int rightY, int rightZ)
    {
        var left = new Int3(leftX, leftY, leftZ);
        var right = new Int3(rightX, rightY, rightZ);

        Assert.Multiple(() =>
        {
            Assert.That(left.Equals(right), Is.False);
            Assert.That(left.Equals((object)right), Is.False);
            Assert.That(left == right, Is.False);
            Assert.That(left != right, Is.True);
            Assert.That(left, Is.Not.EqualTo(right));
        });
    }

    [Test]
    public void EqualsObject_RejectsNullAndOtherTypes()
    {
        var value = new Int3(1, 2, 3);

        Assert.Multiple(() =>
        {
            Assert.That(value.Equals(null), Is.False);
            Assert.That(value.Equals("1,2,3"), Is.False);
            Assert.That(value.Equals((1, 2, 3)), Is.False);
        });
    }

    [TestCase(0, 0, 0)]
    [TestCase(-4, 0, 9)]
    [TestCase(int.MinValue, int.MaxValue, -17)]
    public void EqualValues_ProduceEqualHashCodes(int x, int y, int z)
    {
        var left = new Int3(x, y, z);
        var right = new Int3(x, y, z);

        Assert.That(left.GetHashCode(), Is.EqualTo(right.GetHashCode()));
    }

    [Test]
    public void Value_CanBeUsedAsAHashCollectionKey()
    {
        var key = new Int3(int.MinValue, -20, int.MaxValue);
        var values = new Dictionary<Int3, string>
        {
            [key] = "chunk"
        };

        Assert.That(values[new Int3(int.MinValue, -20, int.MaxValue)], Is.EqualTo("chunk"));
    }
}