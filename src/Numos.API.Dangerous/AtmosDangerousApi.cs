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
    ///     Gets the dangerous registration surface for the simulation's shared solver pipeline.
    /// </summary>
    public AtmosDangerousSolverPipeline Solvers => new(_simulation);
}