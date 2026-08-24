namespace Numos.Maths;

/// <summary>
///     Common operations for finite <see cref="float" /> values.
/// </summary>
public static class FloatMath
{
    /// <summary>
    ///     Determines whether a value is finite and strictly positive.
    /// </summary>
    public static bool IsFinitePositive(float value)
    {
        return float.IsFinite(value) && value > 0f;
    }

    /// <summary>
    ///     Clamps a finite value to the inclusive unit interval, returning zero for non-finite values.
    /// </summary>
    public static float ClampUnitInterval(float value)
    {
        return float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 0f;
    }

    /// <summary>
    ///     Returns a finite value clamped to zero or greater, returning zero for non-finite values.
    /// </summary>
    public static float GetNonnegativeFinite(float value)
    {
        return float.IsFinite(value) ? MathF.Max(0f, value) : 0f;
    }
}