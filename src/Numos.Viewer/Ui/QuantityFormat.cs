using System.Globalization;

namespace Numos.Viewer.Ui;

/// <summary>
///     Formats physical quantities as a value, a space, and a unit, so the same quantity reads identically in every
///     panel.
/// </summary>
internal static class QuantityFormat
{
    private const double FixedLowerBound = 1e-3;
    private const double FixedUpperBound = 1e6;
    private const string ScientificFormat = "0.###E+0";

    /// <summary>
    ///     Multiplier that converts pascals to kilopascals, the unit the viewer shows for pressure.
    /// </summary>
    public const float KilopascalsPerPascal = 1e-3f;

    /// <summary>
    ///     Formats <paramref name="value" /> followed by <paramref name="unit" />.
    /// </summary>
    /// <param name="value">The value to format.</param>
    /// <param name="unit">Unit symbol, or an empty string for a dimensionless value.</param>
    /// <param name="decimals">Fixed decimal places used for values between 1e-3 and 1e6 in magnitude.</param>
    /// <returns>
    ///     Fixed-point text for zero and ordinary magnitudes, or scientific notation outside that range.
    /// </returns>
    /// <remarks>
    ///     NaN and infinities are printed as such rather than clamped, so an invalid value never reads as zero.
    /// </remarks>
    public static string Format(double value, string unit, int decimals = 2)
    {
        string number;
        if (!double.IsFinite(value))
        {
            number = value.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            double magnitude = Math.Abs(value);
            number = value == 0d || magnitude is >= FixedLowerBound and < FixedUpperBound
                ? value.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)
                : value.ToString(ScientificFormat, CultureInfo.InvariantCulture);
        }

        return unit.Length == 0 ? number : number + " " + unit;
    }

    public static string Temperature(double kelvin)
    {
        return Format(kelvin, "K");
    }

    public static string Pressure(double pascals)
    {
        return Format(pascals * KilopascalsPerPascal, "kPa");
    }

    public static string Amount(double moles)
    {
        return Format(moles, "mol", 3);
    }

    /// <summary>
    ///     Formats a fraction in [0, 1] as a percentage with one decimal place.
    /// </summary>
    public static string Percent(double fraction)
    {
        return Format(fraction * 100d, "%", 1);
    }

    public static string Ticks(long ticks)
    {
        string count = ticks.ToString(CultureInfo.InvariantCulture);
        return ticks == 1 ? count + " tick" : count + " ticks";
    }
}