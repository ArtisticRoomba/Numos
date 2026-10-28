using Numos.API;

namespace Numos.Tests;

/// <summary>
///     Advances a world by simulated time, so a test means the same duration at any tick rate.
/// </summary>
internal static class TickHelpers
{
    /// <summary>
    ///     Ticks the world for <paramref name="seconds" /> of simulated time at its current rate.
    /// </summary>
    /// <returns>The number of ticks run.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     <paramref name="seconds" /> is negative or not a whole number of ticks.
    /// </exception>
    internal static int TickFor(AtmosWorld world, double seconds)
    {
        ArgumentNullException.ThrowIfNull(world);
        double exact = seconds * world.TickRate;
        int ticks = (int)Math.Round(exact);
        if (seconds < 0d || Math.Abs(exact - ticks) > 1e-6)
            throw new ArgumentOutOfRangeException(nameof(seconds), seconds, "Not a whole number of ticks at this rate.");

        for (int tick = 0; tick < ticks; tick++)
            world.Tick();

        return ticks;
    }

    /// <inheritdoc cref="TickFor(AtmosWorld, double)" />
    internal static int TickFor(AtmosSimulation simulation, double seconds)
    {
        ArgumentNullException.ThrowIfNull(simulation);
        return TickFor(simulation.World, seconds);
    }
}
