namespace Numos.SimDrawer;

/// <summary>
///     Accumulates the finite extremes of float values.
/// </summary>
/// <remarks>
///     A NaN or infinite value leaves the accumulator unchanged. Start with a minimum of positive infinity and a
///     maximum of negative infinity. Both stay at those values when no finite value arrives.
/// </remarks>
internal static class FiniteRange
{
    /// <summary>
    ///     Widens the range to include one value.
    /// </summary>
    /// <param name="value">The value to include. A non-finite value is ignored.</param>
    /// <param name="minimum">The running minimum.</param>
    /// <param name="maximum">The running maximum.</param>
    public static void Include(float value, ref float minimum, ref float maximum)
    {
        if (!float.IsFinite(value))
            return;

        minimum = Math.Min(minimum, value);
        maximum = Math.Max(maximum, value);
    }

    /// <summary>
    ///     Widens the range to include every finite value of a span.
    /// </summary>
    /// <param name="values">The values to include. Non-finite values are ignored.</param>
    /// <param name="minimum">The running minimum.</param>
    /// <param name="maximum">The running maximum.</param>
    public static void Include(ReadOnlySpan<float> values, ref float minimum, ref float maximum)
    {
        foreach (float value in values)
            Include(value, ref minimum, ref maximum);
    }
}
