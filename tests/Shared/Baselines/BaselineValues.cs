using System.Globalization;

namespace Numos.Tests.Baselines;

/// <summary>
///     How far an actual float may drift from its baseline before the comparison fails.
/// </summary>
/// <remarks>
///     The tolerance in code is authoritative. The baseline file records it only so a reviewer reading the JSON can see
///     how strict the comparison is.
/// </remarks>
internal readonly struct BaselineTolerance
{
    private enum ToleranceKind
    {
        Exact,
        Ulps,
        Relative
    }

    private readonly ToleranceKind _kind;
    private readonly double _amount;

    private BaselineTolerance(ToleranceKind kind, double amount)
    {
        _kind = kind;
        _amount = amount;
    }

    /// <summary>
    ///     Bitwise equality, so <c>-0</c> and <c>+0</c> differ and two NaNs only match with the same payload.
    /// </summary>
    public static BaselineTolerance Exact => new(ToleranceKind.Exact, 0);

    /// <summary>
    ///     Accepts values at most <paramref name="count" /> representable floats apart.
    /// </summary>
    /// <param name="count">The largest accepted ULP distance.</param>
    public static BaselineTolerance Ulps(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return new BaselineTolerance(ToleranceKind.Ulps, count);
    }

    /// <summary>
    ///     Accepts <c>|expected - actual| &lt;= fraction * max(|expected|, |actual|)</c>.
    /// </summary>
    /// <param name="fraction">The largest accepted relative difference.</param>
    public static BaselineTolerance Relative(double fraction)
    {
        if (!(fraction >= 0) || double.IsInfinity(fraction))
            throw new ArgumentOutOfRangeException(nameof(fraction), fraction, "Must be finite and non-negative.");

        return new BaselineTolerance(ToleranceKind.Relative, fraction);
    }

    internal bool Accepts(float expected, float actual)
    {
        if (BitConverter.SingleToInt32Bits(expected) == BitConverter.SingleToInt32Bits(actual))
            return true;

        if (_kind == ToleranceKind.Exact || float.IsNaN(expected) || float.IsNaN(actual))
            return false;

        if (_kind == ToleranceKind.Ulps)
            return UlpDistance(expected, actual) <= (long)_amount;

        if (float.IsInfinity(expected) || float.IsInfinity(actual))
            return expected == actual;

        double scale = Math.Max(Math.Abs((double)expected), Math.Abs((double)actual));
        return Math.Abs((double)expected - actual) <= _amount * scale;
    }

    /// <summary>
    ///     Counts the representable floats between two values, treating <c>-0</c> and <c>+0</c> as the same point.
    /// </summary>
    /// <returns>The distance, or <see langword="null" /> when either value is NaN.</returns>
    internal static long? UlpDistance(float a, float b)
    {
        if (float.IsNaN(a) || float.IsNaN(b))
            return null;

        return Math.Abs(OrderedBits(a) - OrderedBits(b));
    }

    // Maps the sign-magnitude float encoding onto a monotonic integer line.
    private static long OrderedBits(float value)
    {
        int bits = BitConverter.SingleToInt32Bits(value);
        return bits < 0 ? int.MinValue - (long)bits : bits;
    }

    public override string ToString()
    {
        return _kind switch
        {
            ToleranceKind.Exact => "exact",
            ToleranceKind.Ulps => $"ulps({(long)_amount})",
            _ => $"relative({_amount.ToString("R", CultureInfo.InvariantCulture)})"
        };
    }
}

internal enum BaselineEntryKind
{
    Float,
    Floats,
    Digest,
    Text
}

/// <summary>
///     One value as it is stored in a baseline slot. Only the member matching the entry kind is meaningful.
/// </summary>
internal sealed record BaselineValue(float[] Floats, ulong Digest, string Text)
{
    internal static BaselineValue OfFloats(float[] values)
    {
        return new BaselineValue(values, 0, "");
    }

    internal static BaselineValue OfDigest(ulong digest)
    {
        return new BaselineValue([], digest, "");
    }

    internal static BaselineValue OfText(string text)
    {
        return new BaselineValue([], 0, text);
    }
}

internal sealed record BaselineEntry(
    string Key,
    BaselineEntryKind Kind,
    BaselineTolerance Tolerance,
    BaselineValue Value);

/// <summary>
///     The values a test wants compared against its baseline file, kept in the order they were added so the file
///     reads in the same order as the test.
/// </summary>
/// <example>
///     <code>
///     Baseline.Verify(new BaselineValues()
///         .Float("total", total, BaselineTolerance.Ulps(4))
///         .Digest("stateHash", checkpoint.ComputeStateHash()));
///     </code>
/// </example>
internal sealed class BaselineValues
{
    private readonly List<BaselineEntry> _entries = [];
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);

    internal IReadOnlyList<BaselineEntry> Entries => _entries;

    /// <summary>
    ///     Adds one float compared under <paramref name="tolerance" />.
    /// </summary>
    public BaselineValues Float(string key, float value, BaselineTolerance tolerance)
    {
        return Add(key, BaselineEntryKind.Float, tolerance, BaselineValue.OfFloats([value]));
    }

    /// <summary>
    ///     Adds a float sequence compared element by element under <paramref name="tolerance" />.
    /// </summary>
    public BaselineValues Floats(string key, ReadOnlySpan<float> values, BaselineTolerance tolerance)
    {
        return Add(key, BaselineEntryKind.Floats, tolerance, BaselineValue.OfFloats(values.ToArray()));
    }

    /// <inheritdoc cref="Floats(string, ReadOnlySpan{float}, BaselineTolerance)" />
    public BaselineValues Floats(string key, IEnumerable<float> values, BaselineTolerance tolerance)
    {
        return Add(key, BaselineEntryKind.Floats, tolerance, BaselineValue.OfFloats(values.ToArray()));
    }

    /// <summary>
    ///     Adds a state digest. Digests are always exact and always stored per architecture, since float rounding is
    ///     allowed to differ between x64 and arm64.
    /// </summary>
    public BaselineValues Digest(string key, ulong value)
    {
        return Add(key, BaselineEntryKind.Digest, BaselineTolerance.Exact, BaselineValue.OfDigest(value));
    }

    /// <summary>
    ///     Adds a string compared exactly.
    /// </summary>
    public BaselineValues Text(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Add(key, BaselineEntryKind.Text, BaselineTolerance.Exact, BaselineValue.OfText(value));
    }

    private BaselineValues Add(string key, BaselineEntryKind kind, BaselineTolerance tolerance, BaselineValue value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (!_keys.Add(key))
            throw new ArgumentException($"Baseline key '{key}' was added twice.", nameof(key));

        _entries.Add(new BaselineEntry(key, kind, tolerance, value));
        return this;
    }
}
