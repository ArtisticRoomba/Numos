namespace Numos.API.Dangerous;

/// <summary>
///     Entry point for low-level <see cref="AtmosSimulation" /> APIs.
/// </summary>
/// <remarks>
///     <para>
///         These APIs generally grant you access to internal state and allow you to forego validation checks.
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
    /// <remarks>
    ///     The caller is responsible for preventing concurrent simulation or chunk-lifecycle operations while using
    ///     the returned view. A custom solver callback provides that synchronization automatically.
    /// </remarks>
    /// <exception cref="KeyNotFoundException">No chunk is registered at the handle's position.</exception>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    public AtmosDangerousChunk GetChunk(AtmosChunkHandle chunk)
    {
        return new AtmosDangerousChunk(_simulation.Kernel.GetChunkForDangerousAccess(chunk.Position));
    }
}
