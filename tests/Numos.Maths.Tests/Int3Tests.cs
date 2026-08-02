using NUnit.Framework;

namespace Maths.Tests;

[TestFixture]
public sealed class Int3Tests
{
    [Test]
    public void Equality_UsesAllThreeCoordinates()
    {
        var value = new Int3(1, 2, 3);

        Assert.Multiple(() =>
        {
            Assert.That(value, Is.EqualTo(new Int3(1, 2, 3)));
            Assert.That(value == new Int3(1, 2, 3), Is.True);
            Assert.That(value != new Int3(1, 2, 4), Is.True);
            Assert.That(value.Equals((object)new Int3(1, 2, 3)), Is.True);
        });
    }

    [Test]
    public void EqualValues_ProduceEqualHashCodes()
    {
        var left = new Int3(-4, 0, 9);
        var right = new Int3(-4, 0, 9);

        Assert.That(left.GetHashCode(), Is.EqualTo(right.GetHashCode()));
    }
}