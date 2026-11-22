using JetBrains.Annotations;

namespace Numos.API;

/// <summary>
///     Declares a class solver's stage name, placement, rate, and neighborhood, so the <c>Numos.API</c> source
///     generator can build its <see cref="AtmosSolverDescriptor" />.
/// </summary>
/// <param name="name">
///     The stage name. Checkpoints and recordings store it, so it must stay stable across versions. Namespace it
///     (for example <c>my-mod/fire</c>).
/// </param>
/// <remarks>
///     <para>
///         Put the attribute on a <see langword="partial" /> class that implements <see cref="IAtmosWorldSolver" />.
///         Every type the class is nested in must be <see langword="partial" /> too. The class must not be abstract,
///         static, generic, nested in a generic type, or <see langword="file" />-local. It needs a public or internal
///         parameterless constructor.
///     </para>
///     <para>
///         The generator implements <see cref="IAtmosSolverDefinition{TSelf}" /> on the class. The descriptor it
///         builds creates each instance with the parameterless constructor. The assembly's module initializer adds
///         the descriptor to <see cref="AtmosSolverCatalog" /> when the assembly loads.
///     </para>
///     <para>
///         Set at most one of <see cref="After" /> and <see cref="Before" />. Set at most one of
///         <see cref="RunsPerSecond" /> and <see cref="Interval" />. When neither rate is set, the stage runs every
///         tick and the build reports a warning (<c>NUMOSSOLVER017</c>).
///     </para>
/// </remarks>
/// <example>
///     <code>
///     [AtmosSolver("my-mod/fire", After = AtmosBuiltInSolvers.Thermodynamics, RunsPerSecond = 20,
///         Neighbors = AtmosSolverNeighbors.All)]
///     public sealed partial class FireSolver : IAtmosWorldSolver
///     {
///         public void Solve(AtmosWorldSolverContext context)
///         {
///         }
///     }
///     </code>
/// </example>
[PublicAPI]
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class AtmosSolverAttribute(string name) : Attribute
{
    /// <summary>
    ///     Gets the stage name.
    /// </summary>
    public string Name { get; } = name;

    /// <summary>
    ///     Gets or sets the stage this one runs directly after, or <see langword="null" /> for no placement.
    /// </summary>
    public string? After { get; set; }

    /// <summary>
    ///     Gets or sets the stage this one runs directly before, or <see langword="null" /> for no placement.
    /// </summary>
    public string? Before { get; set; }

    /// <summary>
    ///     Gets or sets how many times a second the stage runs, or zero to leave the rate unset.
    /// </summary>
    /// <remarks>
    ///     Numos converts the rate to a tick interval with the world's tick rate, as it does for built-in stages. A
    ///     rate above the tick rate gives an interval of one tick.
    /// </remarks>
    public int RunsPerSecond { get; set; }

    /// <summary>
    ///     Gets or sets the stage's interval in ticks, or zero to leave the interval unset.
    /// </summary>
    /// <remarks>
    ///     Use this instead of <see cref="RunsPerSecond" /> when the stage must run at a tick count that does not
    ///     change with the world's tick rate.
    /// </remarks>
    public int Interval { get; set; }

    /// <summary>
    ///     Gets or sets which neighbors the stage's <see cref="AtmosWorldSolverContext.Topology" /> view holds.
    /// </summary>
    /// <remarks>
    ///     The default, <see cref="AtmosSolverNeighbors.None" />, gives an empty view. A static method marked
    ///     <see cref="SolverNeighborSelectorAttribute" /> replaces this setting. Setting both is a build error
    ///     (<c>NUMOSSOLVER019</c>).
    /// </remarks>
    public AtmosSolverNeighbors Neighbors { get; set; }
}

/// <summary>
///     Selects the neighborhood that Numos compiles for a solver declared with <see cref="AtmosSolverAttribute" />.
/// </summary>
/// <remarks>
///     Each value other than <see cref="None" /> becomes an <see cref="AtmosNeighborSelection" /> with the key
///     <c>{name}#{mode}</c>, where the mode is the value's name in lower case (for example <c>my-mod/fire#all</c>).
/// </remarks>
public enum AtmosSolverNeighbors : byte
{
    /// <summary>
    ///     The topology view is empty.
    /// </summary>
    None,

    /// <summary>
    ///     The view holds ordinary Cartesian neighbors and no explicit links.
    /// </summary>
    Cartesian,

    /// <summary>
    ///     The view holds every active explicit link and no Cartesian neighbors.
    /// </summary>
    ExplicitLinks,

    /// <summary>
    ///     The view holds Cartesian neighbors and every active explicit link.
    /// </summary>
    All
}

/// <summary>
///     Marks the static method that selects explicit links for the enclosing <see cref="AtmosSolverAttribute" />
///     class.
/// </summary>
/// <param name="key">
///     The stable compatibility key of the selection. Numos uses it as written, as
///     <see cref="AtmosNeighborSelection.Key" />.
/// </param>
/// <remarks>
///     <para>
///         The method must have the signature <c>static bool M(AtmosExplicitLinkInfo link)</c>. A solver class can have
///         one selector, and it must not also set <see cref="AtmosSolverAttribute.Neighbors" />. Give the selection a
///         new key when the method changes what it matches.
///     </para>
///     <para>
///         The method follows the rules of <see cref="AtmosExplicitLinkSelector" />. It must be deterministic and must
///         not mutate the world.
///     </para>
/// </remarks>
/// <example>
///     <code>
///     [AtmosSolver("my-mod/portal-sound", RunsPerSecond = 20)]
///     public sealed partial class PortalSoundSolver : IAtmosWorldSolver
///     {
///         [SolverNeighborSelector("my-mod/portal-sound#gas-links", IncludeCartesian = true)]
///         private static bool SelectLink(AtmosExplicitLinkInfo link) =&gt;
///             (link.Flags &amp; ExplicitLinkFlags.GasTransport) != 0;
///
///         public void Solve(AtmosWorldSolverContext context)
///         {
///         }
///     }
///     </code>
/// </example>
[PublicAPI]
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class SolverNeighborSelectorAttribute(string key) : Attribute
{
    /// <summary>
    ///     Gets the stable compatibility key of the selection.
    /// </summary>
    public string Key { get; } = key;

    /// <summary>
    ///     Gets or sets whether ordinary Cartesian neighbors participate as well as the selected links.
    /// </summary>
    public bool IncludeCartesian { get; set; }
}
