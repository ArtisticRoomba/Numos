using JetBrains.Annotations;

namespace Numos.API.Dangerous;

/// <summary>
///     Provides extension methods for <see cref="AtmosSimulation" /> that expose low-level, dangerous APIs.
/// </summary>
public static class AtmosSimulationDangerousExtensions
{
    /// <summary>
    ///     Returns an object that allows you to access low-level
    ///     APIs for the given <see cref="AtmosSimulation" />.
    /// </summary>
    [PublicAPI]
    public static AtmosDangerousApi Dangerous(this AtmosSimulation simulation)
    {
        ArgumentNullException.ThrowIfNull(simulation);
        return new AtmosDangerousApi(simulation.Kernel);
    }
}