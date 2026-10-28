using System.Runtime.CompilerServices;

namespace Numos.CoreSim.Solvers;

/// <summary>
///     Advances a chunk's sleep timer from the pressure activity a transport solver measured this tick.
/// </summary>
internal static class ChunkSleepPolicy
{
    /// <summary>
    ///     The pressure difference between two cells as a percentage of the higher pressure.
    /// </summary>
    /// <param name="pressure">The pressure of the first cell.</param>
    /// <param name="neighborPressure">The pressure of the second cell.</param>
    /// <returns>
    ///     A percentage from 0 to 100 when both pressures are finite and not negative. A solver takes the maximum over
    ///     a chunk's faces and passes it to <see cref="UpdateSleepState" />.
    /// </returns>
    /// <remarks>
    ///     The higher pressure must be positive, because the quotient divides by it.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Scalar RelativePressureDelta(Pascal pressure, Pascal neighborPressure)
    {
        return MathF.Abs(pressure - neighborPressure) / MathF.Max(pressure, neighborPressure) * 100f;
    }

    /// <summary>
    ///     Resets or advances a chunk's sleep timer from its greatest relative pressure difference this tick.
    /// </summary>
    /// <param name="chunk">The chunk whose sleep state will be updated.</param>
    /// <param name="config">The immutable configuration snapshot containing sleep thresholds.</param>
    /// <param name="maximumRelativePressureDelta">
    ///     The greatest neighbor pressure difference in the chunk, as a percentage of the higher pressure.
    /// </param>
    internal static void UpdateSleepState(
        AtmosChunk chunk,
        AtmosSolverConfigSnapshot config,
        Scalar maximumRelativePressureDelta)
    {
        if (maximumRelativePressureDelta >= config.SleepEpsilon)
        {
            chunk.SleepTimer = 0;
            return;
        }

        chunk.SleepTimer++;
        if (chunk.SleepTimer > config.SleepThreshold)
            chunk.Sleep();
    }
}
