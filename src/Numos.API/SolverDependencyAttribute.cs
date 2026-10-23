using JetBrains.Annotations;

namespace Numos.API;

/// <summary>
///     Declares a world service or another solver that a class solver needs, as an instance partial property the
///     <c>Numos.API</c> source generator fills in.
/// </summary>
/// <remarks>
///     <para>
///         Put the attribute on an instance <see langword="partial" /> property declaration with only a
///         <see langword="get" /> accessor, in a <see langword="partial" /> class. Every type the class is nested in
///         must be <see langword="partial" /> too. The property type must be a reference type. It is the type passed to
///         <see cref="AtmosSolverDependencyResolver.Resolve{T}" />. A service must be registered under exactly
///         that type. A solver only has to be assignable to it.
///     </para>
///     <para>
///         The generator implements <see cref="IAtmosSolverDependencies" /> on the class and gives each property a
///         private backing field that injection assigns. The getter returns the field without checks. Reading a
///         dependency before the solver's first tick (or before <see cref="AtmosWorldSolverPipeline.ResolveDependencies" />)
///         returns <see langword="null" />. Injection goes through an <c>InjectSolverDependencies</c> method. That
///         method is <see langword="protected" /> <see langword="virtual" /> on an unsealed class and is overridden in
///         derived classes, so a base class and its subclasses can each declare dependencies.
///     </para>
///     <para>
///         Mark a dependency <see cref="Optional" /> and annotate its type as nullable to have it resolve to
///         <see langword="null" /> when nothing matches. A missing required dependency makes the tick throw before it
///         changes any state.
///     </para>
/// </remarks>
/// <example>
///     <code>
///     public sealed partial class FireSolver : IAtmosWorldSolver
///     {
///         // A world service registered as world.Services.Register&lt;IFireEvents&gt;(bus).
///         [SolverDependency]
///         private partial IFireEvents Events { get; }
///
///         // Another class solver, if one is registered.
///         [SolverDependency(Optional = true)]
///         private partial ExposureSolver? Exposure { get; }
///
///         public void Solve(AtmosWorldSolverContext context)
///         {
///             Events.Publish(context.TickCount);
///             Exposure?.MarkBurning();
///         }
///     }
///
///     world.Services.Register&lt;IFireEvents&gt;(bus);
///     world.Solvers.Register("my-mod/exposure", new ExposureSolver());
///     world.Solvers.Register("my-mod/fire", new FireSolver());
///     </code>
/// </example>
[PublicAPI]
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class SolverDependencyAttribute : Attribute
{
    /// <summary>
    ///     Gets whether the dependency may be absent.
    /// </summary>
    /// <remarks>
    ///     When <see langword="true" />, the property is <see langword="null" /> if nothing matches instead of the
    ///     tick throwing. The property type should then be nullable; the generator warns when the two disagree.
    /// </remarks>
    [PublicAPI]
    public bool Optional { get; init; }
}