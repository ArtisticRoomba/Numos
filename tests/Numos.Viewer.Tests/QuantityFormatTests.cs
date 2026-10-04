using System.Globalization;
using Numos.Viewer.Ui;

namespace Numos.Viewer.Tests;

[TestFixture]
public sealed class QuantityFormatTests
{
    [TestCase(293.15, "K", 2, "293.15 K")]
    [TestCase(101325.0, "Pa", 2, "101325.00 Pa")]
    [TestCase(0.0, "Pa", 2, "0.00 Pa")]
    [TestCase(-12.5, "K", 1, "-12.5 K")]
    [TestCase(0.001, "mol", 3, "0.001 mol")]
    [TestCase(0.42, "", 2, "0.42")]
    public void Format_UsesFixedPointForOrdinaryMagnitudes(double value, string unit, int decimals, string expected)
    {
        Assert.That(QuantityFormat.Format(value, unit, decimals), Is.EqualTo(expected));
    }

    [TestCase(1e6, "Pa", "1E+6 Pa")]
    [TestCase(12_345_678.0, "Pa", "1.235E+7 Pa")]
    [TestCase(0.00012345, "mol", "1.235E-4 mol")]
    [TestCase(-2.5e-7, "", "-2.5E-7")]
    public void Format_UsesScientificNotationOutsideFixedRange(double value, string unit, string expected)
    {
        Assert.That(QuantityFormat.Format(value, unit), Is.EqualTo(expected));
    }

    [Test]
    public void Format_KeepsInvalidValuesDistinctFromZero()
    {
        Assert.Multiple(() =>
        {
            Assert.That(QuantityFormat.Format(double.NaN, "K"), Is.EqualTo("NaN K"));
            Assert.That(QuantityFormat.Format(double.PositiveInfinity, "Pa"), Is.EqualTo("Infinity Pa"));
            Assert.That(QuantityFormat.Format(double.NegativeInfinity, ""), Is.EqualTo("-Infinity"));
        });
    }

    [Test]
    public void Format_IgnoresCurrentCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.That(QuantityFormat.Temperature(293.15), Is.EqualTo("293.15 K"));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Test]
    public void NamedQuantities_UseTheirUnitAndPrecision()
    {
        Assert.Multiple(() =>
        {
            Assert.That(QuantityFormat.Temperature(2.7), Is.EqualTo("2.70 K"));
            Assert.That(QuantityFormat.Pressure(101_326.0), Is.EqualTo("101.33 kPa"));
            Assert.That(QuantityFormat.Amount(12.0), Is.EqualTo("12.000 mol"));
            Assert.That(QuantityFormat.Percent(0.425), Is.EqualTo("42.5 %"));
            Assert.That(QuantityFormat.Percent(0.0), Is.EqualTo("0.0 %"));
        });
    }

    [TestCase(0L, "0 ticks")]
    [TestCase(1L, "1 tick")]
    [TestCase(8192L, "8192 ticks")]
    public void Ticks_PluralizesCount(long ticks, string expected)
    {
        Assert.That(QuantityFormat.Ticks(ticks), Is.EqualTo(expected));
    }
}