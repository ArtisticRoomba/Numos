namespace Numos.API.Dangerous;

/// <summary>
///     Entry point for low-level <see cref="AtmosSimulation" /> APIs.
/// </summary>
/// <remarks>
///     <para>
///         These APIs grant access to internal state and skip validation checks.
///     </para>
///     <para>
///         Mutations made through them bypass recording. They break the replay guarantee unless they are deterministic
///         work inside a solver callback, which replay re-runs, or the host captures a fresh checkpoint afterward.
///     </para>
///     <para>
///         We trust you have received the usual lecture from the local Atmospherics Maintainer:
///         <list type="bullet">
///             <item>Respect the privacy of Numos.</item>
///             <item>Think before you write.</item>
///             <item>With great power comes great opportunity to cause another 3-year hellbug.</item>
///         </list>
///     </para>
/// </remarks>
public readonly struct AtmosDangerousApi
{
    private readonly AtmosSimulation _simulation;

    internal AtmosDangerousApi(AtmosSimulation simulation)
    {
        _simulation = simulation;
    }

    /// <summary>
    ///     Returns an unchecked live view of a registered chunk.
    /// </summary>
    /// <param name="chunk">A chunk registered with this simulation.</param>
    /// <returns>A view over the chunk's live storage arrays.</returns>
    /// <remarks>
    ///     The caller is responsible for preventing concurrent simulation or chunk-lifecycle operations while using
    ///     the returned view. A custom solver callback provides that synchronization automatically, because the tick
    ///     holds the simulation's state lock.
    /// </remarks>
    /// <exception cref="KeyNotFoundException">No chunk is registered at the handle's position.</exception>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    public AtmosDangerousChunk GetChunk(AtmosChunkHandle chunk)
    {
        return new AtmosDangerousChunk(_simulation.Kernel.GetChunkForDangerousAccess(chunk.Position));
    }
}