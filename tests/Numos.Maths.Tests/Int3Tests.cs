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

    [Test]
    public void Addition_AddsEachCoordinate()
    {
        var left = new Int3(2, -3, 4);
        var right = new Int3(-5, 7, 11);

        Assert.That(left + right, Is.EqualTo(new Int3(-3, 4, 15)));
    }

    [Test]
    public void Subtraction_SubtractsEachCoordinate()
    {
        var left = new Int3(2, -3, 4);
        var right = new Int3(-5, 7, 11);

        Assert.That(left - right, Is.EqualTo(new Int3(7, -10, -7)));
    }

    [Test]
    public void UnaryNegation_NegatesEachCoordinate()
    {
        Assert.That(-new Int3(2, -3, 0), Is.EqualTo(new Int3(-2, 3, 0)));
    }

    [TestCase(-3)]
    [TestCase(0)]
    [TestCase(4)]
    public void Multiplication_ScalesEachCoordinateInEitherOperandOrder(int scalar)
    {
        var value = new Int3(2, -3, 5);
        var expected = new Int3(2 * scalar, -3 * scalar, 5 * scalar);

        Assert.Multiple(() =>
        {
            Assert.That(value * scalar, Is.EqualTo(expected));
            Assert.That(scalar * value, Is.EqualTo(expected));
        });
    }

    [Test]
    public void Division_DividesEachCoordinateUsingIntegerDivision()
    {
        Assert.That(new Int3(7, -8, 2) / 3, Is.EqualTo(new Int3(2, -2, 0)));
    }

    [Test]
    public void Division_ByZeroThrows()
    {
        Assert.That(() => _ = new Int3(1, 2, 3) / 0, Throws.TypeOf<DivideByZeroException>());
    }

    [Test]
    public void Remainder_AppliesToEachCoordinate()
    {
        Assert.That(new Int3(7, -8, 11) % new Int3(3, 5, 4), Is.EqualTo(new Int3(1, -3, 3)));
    }

    [TestCase(0, 1, 1)]
    [TestCase(1, 0, 1)]
    [TestCase(1, 1, 0)]
    public void Remainder_WithZeroCoordinateThrows(int x, int y, int z)
    {
        Assert.That(() => _ = new Int3(1, 2, 3) % new Int3(x, y, z),
            Throws.TypeOf<DivideByZeroException>());
    }

    [TestCase(0, 0, 0, true)]
    [TestCase(2, 3, 4, true)]
    [TestCase(-1, 0, 0, false)]
    [TestCase(0, -1, 0, false)]
    [TestCase(0, 0, -1, false)]
    [TestCase(3, 0, 0, false)]
    [TestCase(0, 4, 0, false)]
    [TestCase(0, 0, 5, false)]
    public void IsWithin_UsesInclusiveMinimumAndExclusiveMaximum(int x, int y, int z, bool expected)
    {
        var value = new Int3(x, y, z);

        Assert.That(value.IsWithin(default, new Int3(3, 4, 5)), Is.EqualTo(expected));
    }

    [Test]
    public void IsWithin_UsesProvidedMinimum()
    {
        var min = new Int3(-3, -2, -1);
        var max = new Int3(3, 2, 1);

        Assert.Multiple(() =>
        {
            Assert.That(min.IsWithin(min, max), Is.True);
            Assert.That(new Int3(-4, 0, 0).IsWithin(min, max), Is.False);
        });
    }
}