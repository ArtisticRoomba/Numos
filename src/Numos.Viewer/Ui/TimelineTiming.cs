using Numos.API;

namespace Numos.Viewer.Ui;

/// <summary>
///     Converts between simulated seconds and whole ticks for timeline windows, step buttons, and tick-rate input.
/// </summary>
/// <remarks>
///     The viewer never assumes a fixed tick rate. Every conversion takes the rate of the world it describes.
/// </remarks>
internal static class TimelineTiming
{
    /// <summary>
    ///     The fewest ticks a timeline window shows, so a high-rate project cannot collapse the track to nothing.
    /// </summary>
    public const long MinimumVisibleTicks = 10;

    /// <summary>
    ///     The step size, in milliseconds, that the Back and Forward buttons start with.
    /// </summary>
    public const int DefaultStepMilliseconds = 50;

    /// <summary>
    ///     The width, in simulated seconds, that the timeline window starts with.
    /// </summary>
    public const float DefaultVisibleSeconds = 10f;

    /// <summary>
    ///     Converts a time window to ticks at a tick rate.
    /// </summary>
    /// <param name="seconds">The window width in simulated seconds.</param>
    /// <param name="tickRate">Ticks per second.</param>
    /// <returns>The rounded tick count, at least <see cref="MinimumVisibleTicks" />.</returns>
    public static long VisibleTicks(double seconds, int tickRate)
    {
        return (long)Math.Clamp(Math.Round(seconds * tickRate), MinimumVisibleTicks, long.MaxValue / 2);
    }

    /// <summary>
    ///     Converts a step size to whole ticks at a tick rate.
    /// </summary>
    /// <param name="milliseconds">The step size in simulated milliseconds.</param>
    /// <param name="tickRate">Ticks per second.</param>
    /// <returns>The rounded tick count, at least 1.</returns>
    public static int StepTicks(int milliseconds, int tickRate)
    {
        return (int)Math.Clamp(Math.Round(milliseconds / 1000d * tickRate), 1, int.MaxValue);
    }

    /// <summary>
    ///     Reports whether a tick rate is one that the <see cref="AtmosWorld" /> constructor accepts.
    /// </summary>
    /// <param name="tickRate">The candidate rate.</param>
    /// <returns><see langword="true" /> for a multiple of 20 from 20 to 100000.</returns>
    public static bool IsValidTickRate(int tickRate)
    {
        return AtmosWorld.IsValidTickRate(tickRate);
    }

    /// <summary>
    ///     Explains why a tick rate is rejected.
    /// </summary>
    /// <param name="tickRate">The candidate rate.</param>
    /// <returns>A sentence, or <see langword="null" /> when the rate is valid.</returns>
    public static string? DescribeTickRateError(int tickRate)
    {
        return IsValidTickRate(tickRate) ? null : Locale.GetString("tick-rate-error");
    }
}
