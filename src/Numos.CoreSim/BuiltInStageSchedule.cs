namespace Numos.CoreSim;

/// <summary>
///     The default run rate of each built-in solver stage.
/// </summary>
/// <remarks>
///     <para>
///         A rate is runs per second of simulated time, so the stage interval (in ticks) follows from the tick rate. The
///         world pipeline and the standalone <see cref="AtmosKernel" /> tick both read this table. They therefore
///         agree on which tick runs which stage.
///     </para>
///     <para>
///         The tick rate is a multiple of 20, so the gas and thermal intervals always come out exact. The
///         <c>euler-flow</c> interval rounds down when 60 does not divide the tick rate (at 100 Hz it is 1 tick).
///         That is fine because no other stage has to share its interval.
///     </para>
/// </remarks>
internal static class BuiltInStageSchedule
{
    /// <summary>
    ///     The runs per second of the gas transport and reaction stages.
    /// </summary>
    internal const int GasRunsPerSecond = 20;

    /// <summary>
    ///     The runs per second of the thermal stages.
    /// </summary>
    internal const int ThermalRunsPerSecond = 10;

    /// <summary>
    ///     The runs per second of the <c>euler-flow</c> stage.
    /// </summary>
    /// <remarks>
    ///     Each call pays for a gather and a write-back, and it rounds its CFL substep count up.
    ///     At 960 Hz a run on every tick costs up to twice as much as a run at this rate.
    /// </remarks>
    internal const int EulerRunsPerSecond = 60;

    /// <summary>
    ///     Gets the default run rate of a built-in stage.
    /// </summary>
    /// <param name="name">The stage name.</param>
    /// <param name="runsPerSecond">The runs per second of simulated time.</param>
    /// <returns><see langword="true" /> when <paramref name="name" /> is a built-in stage.</returns>
    internal static bool TryGetRunsPerSecond(string name, out int runsPerSecond)
    {
        switch (name)
        {
            case "euler-flow":
                runsPerSecond = EulerRunsPerSecond;
                return true;
            case "derived-refresh":
            case "advection":
            case "vacuum-cleanup":
            case "explicit-gas-transport":
            case "boundary-flow":
            case "gas-reactions":
                runsPerSecond = GasRunsPerSecond;
                return true;
            case "thermodynamics":
            case "explicit-thermal-transport":
            case "thermal-boundary":
                runsPerSecond = ThermalRunsPerSecond;
                return true;
            default:
                runsPerSecond = 0;
                return false;
        }
    }

    /// <summary>
    ///     Converts a run rate to a whole number of ticks: <c>max(1, tickRate / runsPerSecond)</c>, rounding down.
    /// </summary>
    /// <param name="runsPerSecond">The runs per second. Must be positive.</param>
    /// <param name="tickRate">Ticks per second.</param>
    /// <returns>The interval, at least one tick.</returns>
    internal static int IntervalFor(int runsPerSecond, int tickRate)
    {
        return Math.Max(1, tickRate / runsPerSecond);
    }

    /// <summary>
    ///     Gets the default interval of a stage.
    /// </summary>
    /// <param name="name">The stage name. A name that is not a built-in stage gets one tick.</param>
    /// <param name="tickRate">Ticks per second.</param>
    /// <returns>The interval in ticks.</returns>
    internal static int GetInterval(string name, int tickRate)
    {
        return TryGetRunsPerSecond(name, out int runsPerSecond) ? IntervalFor(runsPerSecond, tickRate) : 1;
    }

    /// <summary>
    ///     Gets the time a stage covers when it runs after a gap of whole ticks.
    /// </summary>
    /// <param name="ticks">The gap since the stage last ran, in ticks.</param>
    /// <param name="tickRate">Ticks per second.</param>
    /// <returns>The gap in seconds. The single division rounds the same way at every rate that divides evenly.</returns>
    internal static Second GapTime(long ticks, int tickRate)
    {
        Second gap = (float)ticks / tickRate;
        return gap;
    }

    /// <summary>
    ///     Converts a length of time to ticks: <c>round(seconds · tickRate)</c>, rounding half away from zero.
    /// </summary>
    /// <param name="seconds">The time. Positive infinity is allowed.</param>
    /// <param name="tickRate">Ticks per second.</param>
    /// <returns>The tick count, clamped to [0, <see cref="int.MaxValue" />].</returns>
    internal static int SecondsToTicks(Second seconds, int tickRate)
    {
        double ticks = Math.Round((double)seconds * tickRate, MidpointRounding.AwayFromZero);
        return ticks >= int.MaxValue ? int.MaxValue : ticks <= 0d ? 0 : (int)ticks;
    }
}
