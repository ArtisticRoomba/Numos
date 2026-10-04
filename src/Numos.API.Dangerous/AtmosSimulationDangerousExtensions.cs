using JetBrains.Annotations;

namespace Numos.API.Dangerous;

/// <summary>
///     Opt-in entry point for the low-level <see cref="AtmosSimulation" /> APIs.
/// </summary>
public static class AtmosSimulationDangerousExtensions
{
    /// <summary>
    ///     Opens the low-level API for <paramref name="simulation" />.
    /// </summary>
    /// <param name="simulation">The simulation to access.</param>
    /// <returns>A lightweight wrapper; creating it does not touch simulation state.</returns>
    /// <remarks>
    ///     See <see cref="AtmosDangerousApi" /> for the recording and replay consequences of using it.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="simulation" /> is <see langword="null" />.</exception>
    [PublicAPI]
    public static AtmosDangerousApi Dangerous(this AtmosSimulation simulation)
    {
        ArgumentNullException.ThrowIfNull(simulation);
        return new AtmosDangerousApi(simulation);
    }
}