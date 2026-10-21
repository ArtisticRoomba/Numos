namespace Numos.CoreSim.Solvers;

/// <summary>
///     Advances a chunk's sleep timer from the pressure activity a transport solver measured this tick.
/// </summary>
internal static class ChunkSleepPolicy
{
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
