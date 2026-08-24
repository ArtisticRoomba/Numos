using NUnit.Framework;

namespace Numos.Maths.Tests;

[TestFixture]
public sealed class FloatMathTests
{
    [TestCase(float.NegativeInfinity, false)]
    [TestCase(-1f, false)]
    [TestCase(0f, false)]
    [TestCase(0.001f, true)]
    [TestCase(float.PositiveInfinity, false)]
    [TestCase(float.NaN, false)]
    public void IsFinitePositive_IdentifiesFinitePositiveValues(float value, bool expected)
    {
        Assert.That(FloatMath.IsFinitePositive(value), Is.EqualTo(expected));
    }

    [TestCase(float.NegativeInfinity, 0f)]
    [TestCase(-1f, 0f)]
    [TestCase(0.25f, 0.25f)]
    [TestCase(2f, 1f)]
    [TestCase(float.PositiveInfinity, 0f)]
    [TestCase(float.NaN, 0f)]
    public void ClampUnitInterval_ClampsFiniteValuesAndRejectsNonFiniteValues(float value, float expected)
    {
        Assert.That(FloatMath.ClampUnitInterval(value), Is.EqualTo(expected));
    }

    [TestCase(float.NegativeInfinity, 0f)]
    [TestCase(-1f, 0f)]
    [TestCase(0.25f, 0.25f)]
    [TestCase(float.PositiveInfinity, 0f)]
    [TestCase(float.NaN, 0f)]
    public void GetNonnegativeFinite_ClampsFiniteValuesAndRejectsNonFiniteValues(float value, float expected)
    {
        Assert.That(FloatMath.GetNonnegativeFinite(value), Is.EqualTo(expected));
    }
}