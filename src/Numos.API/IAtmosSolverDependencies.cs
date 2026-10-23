using JetBrains.Annotations;

namespace Numos.API;

/// <summary>
///     A class solver that takes references to world services and other solvers before it runs.
/// </summary>
/// <remarks>
///     <para>
///         The <c>Numos.API</c> source generator implements this for any class with <see cref="SolverDependencyAttribute" />
///         properties, so most solvers never write it by hand. Implement it directly when the generator can't be used,
///         or when a dependency needs custom handling.
///     </para>
///     <para>
///         Numos calls <see cref="InjectDependencies" /> on the tick thread, before the first tick the solver runs in.
///         It calls it again before the next tick after any pipeline registration, removal, or reset, or any change
///         to <see cref="AtmosWorld.Services" />. Numos injects a solver that was disabled at that time when the
///         solver is enabled again. Restoring a checkpoint does not trigger injection, because services and solver
///         instances are host-owned and unaffected by it. <see cref="AtmosWorldSolverPipeline.ResolveDependencies" />
///         runs the same pass on demand.
///     </para>
///     <para>
///         Injection runs before the tick changes any state. If it throws, Numos abandons the tick with the world
///         untouched, and the next tick tries again. Only assign references here. Do not read or write simulation
///         state, because the call can repeat many times over a solver's life.
///     </para>
/// </remarks>
/// <example>
///     <code>
///     public sealed class FireSolver : IAtmosWorldSolver, IAtmosSolverDependencies
///     {
///         private IFireEvents _events = null!;
///         private ExposureSolver? _exposure;
///
///         public void InjectDependencies(AtmosSolverDependencyResolver resolver)
///         {
///             _events = resolver.Resolve&lt;IFireEvents&gt;(nameof(_events));
///             _exposure = resolver.ResolveOptional&lt;ExposureSolver&gt;();
///         }
///
///         public void Solve(AtmosWorldSolverContext context)
///         {
///             _events.Publish(context.TickCount);
///         }
///     }
///     </code>
/// </example>
[PublicAPI]
public interface IAtmosSolverDependencies
{
    /// <summary>
    ///     Replaces every dependency reference with the one currently resolved by <paramref name="resolver" />.
    /// </summary>
    /// <param name="resolver">
    ///     Looks up services and solvers for this registration. Only valid for the duration of the call.
    /// </param>
    /// <remarks>
    ///     Assign every dependency on each call, including optional ones that now resolve to <see langword="null" />,
    ///     so a removed service or solver doesn't linger.
    /// </remarks>
    void InjectDependencies(AtmosSolverDependencyResolver resolver);
}

/// <summary>
///     Looks up the services and solvers an <see cref="IAtmosSolverDependencies" /> solver depends on.
/// </summary>
/// <remarks>
///     <para>
///         A lookup for <c>T</c> first checks <see cref="AtmosWorld.Services" /> for a service registered under exactly
///         <c>T</c>. When there is none, it searches the custom class solvers registered on
///         <see cref="AtmosWorld.Solvers" />, enabled or not, for one that is a <c>T</c>. Delegate stages never match,
///         and neither does the solver being injected, so a solver can't resolve itself. One instance registered under
///         several names counts as a single match; two different instances are ambiguous and throw.
///     </para>
///     <para>
///         Depending on another solver gives direct access to its public members, which suits a producer and consumer
///         written together. Solvers that shouldn't know about each other can share a <see cref="SolverDataKey{T}" />
///         instead.
///     </para>
/// </remarks>
[PublicAPI]
public sealed class AtmosSolverDependencyResolver
{
    private readonly IAtmosWorldSolver _requester;
    private readonly IReadOnlyList<(string Name, IAtmosWorldSolver Solver)> _solvers;

    internal AtmosSolverDependencyResolver(
        AtmosWorld world,
        string solverName,
        IAtmosWorldSolver requester,
        IReadOnlyList<(string Name, IAtmosWorldSolver Solver)> solvers)
    {
        World = world;
        SolverName = solverName;
        _requester = requester;
        _solvers = solvers;
    }

    /// <summary>
    ///     Gets the world the solver is registered in.
    /// </summary>
    [PublicAPI]
    public AtmosWorld World { get; }

    /// <summary>
    ///     Gets the pipeline name of the registration being injected.
    /// </summary>
    /// <remarks>
    ///     An instance registered under several names is injected once per registration.
    /// </remarks>
    [PublicAPI]
    public string SolverName { get; }

    /// <summary>
    ///     Resolves a dependency that must exist.
    /// </summary>
    /// <typeparam name="T">The service key or solver type to look up.</typeparam>
    /// <param name="memberName">The member being assigned, used only in error messages.</param>
    /// <returns>The matching service or solver.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="memberName" /> is <see langword="null" />.</exception>
    /// <exception cref="InvalidOperationException">
    ///     Nothing matches <typeparamref name="T" />, or more than one solver does.
    /// </exception>
    [PublicAPI]
    public T Resolve<T>(string memberName) where T : class
    {
        ArgumentNullException.ThrowIfNull(memberName);
        T? resolved = ResolveCore<T>(memberName);
        if (resolved is not null)
            return resolved;

        throw new InvalidOperationException(
            $"Solver '{SolverName}' member '{memberName}' depends on '{typeof(T)}', but no world service is " +
            $"registered under that type and no other registered solver is one. Register it before the first tick " +
            $"that runs '{SolverName}', or make the dependency optional.");
    }

    /// <summary>
    ///     Resolves a dependency that may be absent.
    /// </summary>
    /// <typeparam name="T">The service key or solver type to look up.</typeparam>
    /// <returns>The matching service or solver, or <see langword="null" /> when nothing matches.</returns>
    /// <exception cref="InvalidOperationException">More than one solver matches <typeparamref name="T" />.</exception>
    [PublicAPI]
    public T? ResolveOptional<T>() where T : class
    {
        return ResolveCore<T>(null);
    }

    private T? ResolveCore<T>(string? memberName) where T : class
    {
        if (World.Services.TryGet(out T? service))
            return service;

        T? match = null;
        List<string>? names = null;
        bool ambiguous = false;
        foreach ((string name, IAtmosWorldSolver solver) in _solvers)
        {
            if (ReferenceEquals(solver, _requester) || solver is not T candidate)
                continue;

            names ??= [];
            names.Add(name);
            if (match is null)
                match = candidate;
            else if (!ReferenceEquals(match, candidate))
                ambiguous = true;
        }

        if (!ambiguous)
            return match;

        string matches = string.Join(", ", names!.Select(static name => "'" + name + "'"));
        string member = memberName is null ? "an optional member" : $"member '{memberName}'";
        throw new InvalidOperationException(
            $"Solver '{SolverName}' {member} matches multiple solvers of type '{typeof(T)}': {matches}. " +
            $"Register a world service under '{typeof(T)}' to pick one.");
    }
}