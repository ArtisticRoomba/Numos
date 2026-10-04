namespace Numos.Maths;

/// <summary>
///     Common operations for finite <see cref="float" /> values.
/// </summary>
public static class FloatMath
{
    /// <summary>
    ///     Determines whether a value is finite and strictly positive.
    /// </summary>
    /// <param name="value">The value to test.</param>
    /// <returns><see langword="false" /> for zero, negatives, NaN, and infinities.</returns>
    public static bool IsFinitePositive(float value)
    {
        return float.IsFinite(value) && value > 0f;
    }

    /// <summary>
    ///     Clamps a finite value to the inclusive unit interval, returning zero for non-finite values.
    /// </summary>
    /// <param name="value">The value to clamp.</param>
    /// <returns><paramref name="value" /> clamped to [0, 1], or zero when it is NaN or infinite.</returns>
    public static float ClampUnitInterval(float value)
    {
        return float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 0f;
    }

    /// <summary>
    ///     Returns a finite value clamped to zero or greater, returning zero for non-finite values.
    /// </summary>
    /// <param name="value">The value to clamp.</param>
    /// <returns><paramref name="value" /> when it is finite and positive; otherwise zero.</returns>
    public static float GetNonnegativeFinite(float value)
    {
        return float.IsFinite(value) ? MathF.Max(0f, value) : 0f;
    }
}