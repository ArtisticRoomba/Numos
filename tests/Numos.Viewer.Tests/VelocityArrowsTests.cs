using System.Numerics;
using Numos.Viewer.Rendering;

namespace Numos.Viewer.Tests;

[TestFixture]
public sealed class VelocityArrowsTests
{
    [Test]
    public void TryGetShaft_ScalesVelocityBySeconds()
    {
        bool drawn = VelocityArrows.TryGetShaft(new Vector3(20f, 0f, 0f), 0.05f, out var shaft);

        Assert.Multiple(() =>
        {
            Assert.That(drawn, Is.True);
            Assert.That(shaft.X, Is.EqualTo(1f).Within(1e-6f));
            Assert.That(shaft.Y, Is.Zero);
            Assert.That(shaft.Z, Is.Zero);
        });
    }

    [Test]
    public void TryGetShaft_CapsLengthButKeepsDirection()
    {
        bool drawn = VelocityArrows.TryGetShaft(new Vector3(0f, 300f, 400f), 1f, out var shaft);

        Assert.Multiple(() =>
        {
            Assert.That(drawn, Is.True);
            Assert.That(shaft.Length(), Is.EqualTo(VelocityArrows.MaxLength).Within(1e-5f));
            Assert.That(shaft.Y / shaft.Z, Is.EqualTo(0.75f).Within(1e-6f));
        });
    }

    [Test]
    public void TryGetShaft_SkipsArrowsTooShortToRead()
    {
        bool drawn = VelocityArrows.TryGetShaft(new Vector3(0.1f, 0f, 0f), 0.05f, out var shaft);

        Assert.Multiple(() =>
        {
            Assert.That(drawn, Is.False);
            Assert.That(shaft, Is.EqualTo(Vector3.Zero));
        });
    }

    [TestCase(float.NaN, 0f, 0f)]
    [TestCase(0f, float.PositiveInfinity, 0f)]
    [TestCase(0f, 0f, float.NegativeInfinity)]
    public void TryGetShaft_SkipsNonFiniteVelocity(float x, float y, float z)
    {
        Assert.That(VelocityArrows.TryGetShaft(new Vector3(x, y, z), 0.05f, out _), Is.False);
        Assert.That(VelocityArrows.TryGetShaft(new Vector2(x, y), 0.05f, out _), Is.False);
    }

    [Test]
    public void TryGetShaft_ClampsScaleBeforeUsingIt()
    {
        VelocityArrows.TryGetShaft(new Vector2(1000f, 0f), 0f, out var atFloor);
        VelocityArrows.TryGetShaft(new Vector2(1000f, 0f), VelocityArrows.MinScaleSeconds, out var atMinimum);

        Assert.That(atFloor, Is.EqualTo(atMinimum));
    }

    [TestCase(float.NaN, VelocityArrows.DefaultScaleSeconds)]
    [TestCase(-1f, VelocityArrows.MinScaleSeconds)]
    [TestCase(0.02f, 0.02f)]
    [TestCase(50f, VelocityArrows.MaxScaleSeconds)]
    public void ClampScale_StaysInsideTheAcceptedRange(float input, float expected)
    {
        Assert.That(VelocityArrows.ClampScale(input), Is.EqualTo(expected));
    }

    [Test]
    public void GetHead3D_SweepsBackFromTheTipOnBothSides()
    {
        var shaft = new Vector3(2f, 0f, 0f);
        var tip = new Vector3(5f, 1f, 1f);

        (Vector3 left, Vector3 right) = VelocityArrows.GetHead(tip, shaft);

        Assert.Multiple(() =>
        {
            Assert.That(left.X, Is.LessThan(tip.X));
            Assert.That(right.X, Is.LessThan(tip.X));
            Assert.That(left.X, Is.EqualTo(right.X).Within(1e-6f));
            Assert.That(Vector3.Distance(left, tip), Is.EqualTo(Vector3.Distance(right, tip)).Within(1e-6f));
            Assert.That(Vector3.Distance(left, right), Is.GreaterThan(0f));
        });
    }

    [Test]
    public void GetHead2D_MirrorsAcrossTheShaft()
    {
        var shaft = new Vector2(0f, -1f);
        var tip = new Vector2(0.5f, 0.5f);

        (Vector2 left, Vector2 right) = VelocityArrows.GetHead(tip, shaft);

        Assert.Multiple(() =>
        {
            Assert.That(left.Y, Is.GreaterThan(tip.Y));
            Assert.That(left.Y, Is.EqualTo(right.Y).Within(1e-6f));
            Assert.That(left.X + right.X, Is.EqualTo(2f * tip.X).Within(1e-6f));
        });
    }
}
