using JetBrains.Annotations;

namespace Numos.API;

/// <summary>
///     Owns the ordered solver stages executed for every fixed world tick.
/// </summary>
/// <remarks>
///     The enabled stage list is captured before each tick. Pipeline edits made by a running stage take effect on the
///     next tick. Solver delegates and <see cref="IAtmosWorldSolver" /> instances remain caller-owned.
///     Numos checkpoints stage names, kinds, order, and neighbor-selection keys. They must match on restore, so Numos
///     rejects adding, removing, or resetting stages while the world is recording or replaying.
///     Enabling and disabling stages is allowed then, and Numos records it.
/// </remarks>
public sealed class AtmosWorldSolverPipeline
{
    private readonly List<WorldSolverRegistration> _steps = [];
    private readonly AtmosWorld _world;
    private WorldSolverRegistration[] _enabledSteps = [];

    // The world dependency epoch every registration in _enabledSteps was injected at, or -1. Republishing the steps
    // clears it, so enabling a solver that sat out an epoch change gets it injected before it runs.
    private int _injectedEpoch = -1;

    internal AtmosWorldSolverPipeline(AtmosWorld world)
    {
        _world = world;
        AddDefaults();
        PublishSteps();
    }

    /// <summary>
    ///     Returns detached metadata in execution order.
    /// </summary>
    public IReadOnlyList<AtmosWorldSolverStep> Steps
    {
        get
        {
            lock (_world.Gate)
            {
                _world.ThrowIfDisposedForPipeline();
                return _steps.Select(static step => step.ToPublic()).ToArray();
            }
        }
    }

    /// <summary>
    ///     Gets the enabled stages in execution order.
    /// </summary>
    /// <remarks>
    ///     The array is replaced, never mutated, so a tick can keep running it while a stage edits the pipeline.
    /// </remarks>
    internal WorldSolverRegistration[] EnabledSteps => _enabledSteps;

    /// <summary>
    ///     Registers a custom stage.
    /// </summary>
    /// <param name="name">A nonempty name unique within the pipeline.</param>
    /// <param name="solver">The caller-owned callback.</param>
    /// <param name="neighbors">
    ///     The Cartesian and explicit adjacency compiled into the stage's
    ///     <see cref="AtmosWorldSolverContext.Topology" />, or <see langword="null" /> for an empty view.
    /// </param>
    /// <param name="before">The registered stage the new stage runs immediately before.</param>
    /// <param name="after">The registered stage the new stage runs immediately after.</param>
    /// <remarks>
    ///     <para>
    ///         Without <paramref name="before" /> or <paramref name="after" />, the stage is appended to the end of the
    ///         pipeline. At most one of them may be given.
    ///     </para>
    ///     <para>
    ///         Pass <paramref name="neighbors" /> whenever the solver reads <see cref="AtmosWorldSolverContext.Topology" />.
    ///         Without it the view is empty. <c>GetOwnedEdges()</c> yields nothing and <c>GetNeighbors</c> reports no
    ///         explicit neighbors. Neither throws, so nothing flags the missing selection. Set the selection's
    ///         <c>includeCartesian</c> to <see langword="false" /> for a stage that only cares about portals and docks.
    ///         The compiled view then stays limited to the sparse explicit edge list. Numos does not also re-derive
    ///         every ordinary chunk boundary.
    ///     </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="name" /> or <paramref name="solver" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException">
    ///     A name is blank, <paramref name="name" /> is already registered, or both <paramref name="before" /> and
    ///     <paramref name="after" /> are given.
    /// </exception>
    /// <exception cref="KeyNotFoundException">The <paramref name="before" /> or <paramref name="after" /> stage is not registered.</exception>
    /// <exception cref="InvalidOperationException">The world is recording or replaying.</exception>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    /// <example>
    ///     <code>
    ///     world.Solvers.Register("my-mod/decay", context => Decay(context), after: AtmosBuiltInSolvers.Thermodynamics);
    ///     world.Solvers.Register(
    ///         "my-mod/vents",
    ///         context => Vent(context),
    ///         neighbors: new AtmosNeighborSelection("my-mod/vents-v1", includeCartesian: false, static _ => true),
    ///         before: AtmosBuiltInSolvers.BoundaryFlow);
    ///     </code>
    /// </example>
    [PublicAPI]
    public void Register(
        string name,
        AtmosWorldSolver solver,
        AtmosNeighborSelection? neighbors = null,
        string? before = null,
        string? after = null)
    {
        ArgumentNullException.ThrowIfNull(solver);
        RegisterCore(name, new DelegateWorldSolver(solver), neighbors, before, after);
    }

    /// <summary>
    ///     Registers a custom class solver.
    /// </summary>
    /// <param name="name">A nonempty name unique within the pipeline.</param>
    /// <param name="solver">The caller-owned solver instance.</param>
    /// <param name="neighbors">
    ///     The Cartesian and explicit adjacency compiled into the stage's
    ///     <see cref="AtmosWorldSolverContext.Topology" />, or <see langword="null" /> for an empty view.
    /// </param>
    /// <param name="before">The registered stage the new stage runs immediately before.</param>
    /// <param name="after">The registered stage the new stage runs immediately after.</param>
    /// <remarks>
    ///     Identical to the <see cref="AtmosWorldSolver" /> overload in placement, topology, and checkpoint metadata. The
    ///     instance is caller-owned and also receives <see cref="IAtmosWorldSolver.OnRestored" />.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="name" /> or <paramref name="solver" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException">
    ///     A name is blank, <paramref name="name" /> is already registered, or both <paramref name="before" /> and
    ///     <paramref name="after" /> are given.
    /// </exception>
    /// <exception cref="KeyNotFoundException">The <paramref name="before" /> or <paramref name="after" /> stage is not registered.</exception>
    /// <exception cref="InvalidOperationException">The world is recording or replaying.</exception>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    [PublicAPI]
    public void Register(
        string name,
        IAtmosWorldSolver solver,
        AtmosNeighborSelection? neighbors = null,
        string? before = null,
        string? after = null)
    {
        RegisterCore(name, solver, neighbors, before, after);
    }

    /// <summary>
    ///     Removes a stage by name, including a built-in stage.
    /// </summary>
    /// <param name="name">The registered stage name.</param>
    /// <returns><see langword="true" /> when a stage was removed.</returns>
    /// <remarks>
    ///     Removing a built-in stage drops that physics from every subsequent tick. <see cref="ResetToDefaults" />
    ///     brings it back.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="name" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException"><paramref name="name" /> is empty or whitespace.</exception>
    /// <exception cref="InvalidOperationException">The world is recording or replaying.</exception>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    [PublicAPI]
    public bool Unregister(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_world.Gate)
        {
            _world.ThrowIfDisposedForPipeline();
            _world.EnsureCanChangeWorldSolverDefinition();
            int index = IndexOf(name);
            if (index < 0)
                return false;

            _steps.RemoveAt(index);
            _world.InvalidateSolverDependencies();
            PublishSteps();
            return true;
        }
    }

    /// <summary>
    ///     Enables or disables a registered stage.
    /// </summary>
    /// <param name="name">The registered stage name.</param>
    /// <param name="enabled">Whether the stage participates in subsequent ticks.</param>
    /// <returns><see langword="true" /> when the named stage exists.</returns>
    /// <remarks>
    ///     Unlike registration, this is allowed during recording. An actual change is recorded as an external operation
    ///     so replay reproduces it at the same position.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="name" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException"><paramref name="name" /> is empty or whitespace.</exception>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    [PublicAPI]
    public bool SetEnabled(string name, bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_world.Gate)
        {
            _world.ThrowIfDisposedForPipeline();
            int index = IndexOf(name);
            if (index < 0)
                return false;

            if (_steps[index].IsEnabled == enabled)
                return true;

            _steps[index].IsEnabled = enabled;
            PublishSteps();
            _world.RecordWorldSolverEnablement(name, enabled);
            return true;
        }
    }

    /// <summary>
    ///     Restores the built-in stages in their default order and enabled state, discarding any custom
    ///     registrations.
    /// </summary>
    /// <exception cref="InvalidOperationException">The world is recording or replaying.</exception>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    [PublicAPI]
    public void ResetToDefaults()
    {
        lock (_world.Gate)
        {
            _world.ThrowIfDisposedForPipeline();
            _world.EnsureCanChangeWorldSolverDefinition();
            _steps.Clear();
            AddDefaults();
            _world.InvalidateSolverDependencies();
            PublishSteps();
        }
    }

    /// <summary>
    ///     Injects dependencies into every enabled class solver now, without waiting for the next tick.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Ticks inject lazily, so a missing dependency normally surfaces as an exception from
    ///         <see cref="AtmosWorld.Tick" />. Call this after setting up services and solvers to get the same error up
    ///         front. Numos injects every enabled <see cref="IAtmosSolverDependencies" /> solver in pipeline order, even
    ///         if it was already up to date. Numos injects disabled solvers when they are next enabled and ticked.
    ///     </para>
    ///     <para>
    ///         The first failure stops the pass and propagates. Solvers before it keep their new references. The next
    ///         tick retries the failed solver and everything after it.
    ///     </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    ///     A dependency is missing or ambiguous, or this is called from a solver during a tick.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    /// <example>
    ///     <code>
    ///     world.Services.Register&lt;IFireEvents&gt;(bus);
    ///     world.Solvers.Register("my-mod/fire", new FireSolver());
    ///     world.Solvers.ResolveDependencies(); // throws here rather than on the first tick
    ///     </code>
    /// </example>
    [PublicAPI]
    public void ResolveDependencies()
    {
        _world.ResolveSolverDependencies();
    }

    /// <summary>
    ///     Injects dependencies into the enabled class solvers, in pipeline order.
    /// </summary>
    /// <param name="force">Whether to inject solvers that are already up to date.</param>
    /// <remarks>
    ///     The caller holds <see cref="AtmosWorld.Gate" /> and has marked the world as ticking, so services cannot
    ///     change underneath the pass. Numos stamps a registration only after its injection returns. A failure leaves
    ///     it stale for the next attempt.
    /// </remarks>
    internal void InjectDependencies(bool force)
    {
        int epoch = _world.SolverDependencyEpoch;
        if (!force && _injectedEpoch == epoch)
            return;

        WorldSolverRegistration[] steps = _enabledSteps;
        (string Name, IAtmosWorldSolver Solver)[]? candidates = null;
        foreach (var step in steps)
        {
            if (step.CustomSolver is not IAtmosSolverDependencies dependent)
                continue;

            if (!force && step.InjectedEpoch == epoch)
                continue;

            candidates ??= _steps
                .Where(static candidate => candidate.CustomSolver is not null and not DelegateWorldSolver)
                .Select(static candidate => (candidate.Name, candidate.CustomSolver!))
                .ToArray();

            dependent.InjectDependencies(new AtmosSolverDependencyResolver(_world, step.Name, step.CustomSolver, candidates));
            step.InjectedEpoch = epoch;
        }

        // Pipeline edits are not blocked here, so an injection could have republished the steps. Leave the pass
        // unstamped in that case, so the next pass looks at the new set.
        if (ReferenceEquals(steps, _enabledSteps))
            _injectedEpoch = epoch;
    }

    internal void Execute(WorldSolverRegistration[] steps, AtmosSolverTickScope scope)
    {
        foreach (var step in steps)
            step.Execute(scope);
    }

    internal void RecompileTopology(IReadOnlyList<ExplicitLinkDefinition> links)
    {
        var compiled = new AtmosWorldNeighborTopology[_steps.Count];
        for (int index = 0; index < _steps.Count; index++)
            compiled[index] = _steps[index].BuildTopology(_world, links);

        for (int index = 0; index < _steps.Count; index++)
            _steps[index].InstallTopology(compiled[index]);
    }

    /// <summary>
    ///     Calls <see cref="IAtmosWorldSolver.OnRestored" /> on every custom class solver, enabled or not, in pipeline
    ///     order.
    /// </summary>
    /// <remarks>
    ///     Iterates a snapshot, so a solver that edits the pipeline from its hook does not break the loop. The first
    ///     exception stops the pass and propagates to the restore's caller.
    /// </remarks>
    internal void NotifyRestored()
    {
        WorldSolverRegistration[] steps;
        lock (_world.Gate)
        {
            steps = _steps.ToArray();
        }

        foreach (var step in steps)
            step.NotifyRestored();
    }

    private void RegisterCore(
        string name,
        IAtmosWorldSolver solver,
        AtmosNeighborSelection? selection,
        string? before,
        string? after)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(solver);
        if (before != null && after != null)
            throw new ArgumentException("A world solver can be placed before or after another stage, not both.", nameof(after));

        string? anchor = before ?? after;
        if (anchor != null)
            ArgumentException.ThrowIfNullOrWhiteSpace(anchor, before != null ? nameof(before) : nameof(after));

        lock (_world.Gate)
        {
            _world.ThrowIfDisposedForPipeline();
            _world.EnsureCanChangeWorldSolverDefinition();
            if (IndexOf(name) >= 0)
                throw new ArgumentException($"A world solver named '{name}' is already registered.", nameof(name));

            int insertionIndex = _steps.Count;
            if (anchor != null)
            {
                int anchorIndex = IndexOf(anchor);
                if (anchorIndex < 0)
                    throw new KeyNotFoundException($"No world solver named '{anchor}' is registered.");

                insertionIndex = before != null ? anchorIndex : anchorIndex + 1;
            }

            var registration = WorldSolverRegistration.CreateCustom(name, solver, selection);
            registration.InstallTopology(registration.BuildTopology(_world, _world.GetActiveLinksCore()));
            _steps.Insert(insertionIndex, registration);
            _world.InvalidateSolverDependencies();
            PublishSteps();
        }
    }

    private int IndexOf(string name)
    {
        return _steps.FindIndex(step => string.Equals(step.Name, name, StringComparison.Ordinal));
    }

    private void AddDefaults()
    {
        _steps.Add(
            WorldSolverRegistration.CreateBuiltIn(
                AtmosBuiltInSolvers.Advection,
                static scope => scope.World.SolveAdvectionStage(scope)));

        _steps.Add(
            WorldSolverRegistration.CreateBuiltIn(
                AtmosBuiltInSolvers.ExplicitGasTransport,
                static scope => scope.World.SolveExplicitGasTransportStage(scope)));

        _steps.Add(
            WorldSolverRegistration.CreateBuiltIn(
                AtmosBuiltInSolvers.BoundaryFlow,
                static scope => scope.World.SolveBoundaryFlowStage(scope)));

        _steps.Add(
            WorldSolverRegistration.CreateBuiltIn(
                AtmosBuiltInSolvers.Thermodynamics,
                static scope => scope.World.SolveThermodynamicsStage(scope)));

        _steps.Add(
            WorldSolverRegistration.CreateBuiltIn(
                AtmosBuiltInSolvers.ExplicitThermalTransport,
                static scope => scope.World.SolveExplicitThermalTransportStage(scope)));

        _steps.Add(
            WorldSolverRegistration.CreateBuiltIn(
                AtmosBuiltInSolvers.ThermalBoundary,
                static scope => scope.World.SolveThermalBoundaryStage(scope)));

        _steps.Add(
            WorldSolverRegistration.CreateBuiltIn(
                AtmosBuiltInSolvers.GasReactions,
                static scope => scope.World.SolveGasReactionsStage(scope)));
    }

    private void PublishSteps()
    {
        _enabledSteps = _steps.Where(static step => step.IsEnabled).ToArray();
        _injectedEpoch = -1;
    }
}

internal sealed class WorldSolverRegistration
{
    private readonly Action<AtmosSolverTickScope>? _builtInSolver;
    private AtmosWorldNeighborTopology _topology = AtmosWorldNeighborTopology.Empty;

    private WorldSolverRegistration(
        string name,
        AtmosWorldSolverKind kind,
        Action<AtmosSolverTickScope>? builtInSolver,
        IAtmosWorldSolver? customSolver,
        AtmosNeighborSelection? selection)
    {
        Name = name;
        Kind = kind;
        _builtInSolver = builtInSolver;
        CustomSolver = customSolver;
        Selection = selection;
    }

    internal string Name { get; }
    internal AtmosWorldSolverKind Kind { get; }
    internal AtmosNeighborSelection? Selection { get; }
    internal bool IsEnabled { get; set; } = true;

    /// <summary>
    ///     Gets or sets the world dependency epoch this solver was last injected at, or -1.
    /// </summary>
    internal int InjectedEpoch { get; set; } = -1;

    /// <summary>
    ///     Gets the class solver instance, or the delegate adapter, for a custom stage.
    /// </summary>
    internal IAtmosWorldSolver? CustomSolver { get; }

    internal static WorldSolverRegistration CreateBuiltIn(
        string name,
        Action<AtmosSolverTickScope> solver)
    {
        return new WorldSolverRegistration(name, AtmosWorldSolverKind.BuiltIn, solver, null, null);
    }

    internal static WorldSolverRegistration CreateCustom(
        string name,
        IAtmosWorldSolver solver,
        AtmosNeighborSelection? selection)
    {
        return new WorldSolverRegistration(name, AtmosWorldSolverKind.Custom, null, solver, selection);
    }

    internal void Execute(AtmosSolverTickScope scope)
    {
        if (CustomSolver != null)
        {
            CustomSolver.Solve(new AtmosWorldSolverContext(scope, _topology));
            return;
        }

        _builtInSolver!(scope);
    }

    internal void NotifyRestored()
    {
        // Delegates have nowhere to keep state of their own, so the default no-op is skipped without a virtual call.
        if (CustomSolver is not null and not DelegateWorldSolver)
            CustomSolver.OnRestored();
    }

    internal AtmosWorldNeighborTopology BuildTopology(
        AtmosWorld world,
        IReadOnlyList<ExplicitLinkDefinition> links)
    {
        return Selection == null
            ? AtmosWorldNeighborTopology.EmptyFor(world)
            : AtmosWorldNeighborTopology.Compile(world, Selection, links);
    }

    internal void InstallTopology(AtmosWorldNeighborTopology topology)
    {
        _topology = topology;
    }

    internal AtmosWorldSolverStep ToPublic()
    {
        return new AtmosWorldSolverStep(Name, Kind, IsEnabled, Selection?.Key);
    }
}

/// <summary>
///     Adapts a delegate registration to <see cref="IAtmosWorldSolver" /> so the pipeline stores one solver shape.
/// </summary>
internal sealed class DelegateWorldSolver(AtmosWorldSolver solver) : IAtmosWorldSolver
{
    public void Solve(AtmosWorldSolverContext context)
    {
        solver(context);
    }
}