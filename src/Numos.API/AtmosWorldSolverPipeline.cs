using JetBrains.Annotations;
using Numos.CoreSim.Replay;
using Numos.CoreSim.Solvers;

namespace Numos.API;

/// <summary>
///     Owns the ordered solver stages executed for every fixed world tick.
/// </summary>
/// <remarks>
///     The enabled stage list is captured before each tick. Pipeline edits made by a running stage take effect on the
///     next tick. Solver delegates and <see cref="IAtmosWorldSolver" /> instances remain caller-owned.
///     Stage names, kinds, order, and neighbor-selection keys are checkpointed and must match on restore, so adding,
///     removing, or resetting stages is rejected while the world or any of its simulations is recording or replaying.
///     Enabling and disabling stages is allowed then and is recorded.
/// </remarks>
public sealed class AtmosWorldSolverPipeline
{
    private readonly List<WorldSolverRegistration> _steps = [];
    private readonly AtmosWorld _world;
    private AtmosSolverCheckpoint[] _checkpointSteps = [];

    // Advances whenever the set of registered solvers changes, which can change what a dependency resolves to.
    // Enabling and disabling don't count: a disabled solver is skipped by injection, so it's already stale when it
    // comes back if anything changed meanwhile.
    private int _dependencyEpoch;

    internal AtmosWorldSolverPipeline(AtmosWorld world)
    {
        _world = world;
        AddDefaults();
        PublishCheckpointSteps();
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
    ///     Registers a custom stage at the end of the pipeline.
    /// </summary>
    /// <param name="name">A nonempty name unique within the pipeline.</param>
    /// <param name="solver">The caller-owned callback.</param>
    /// <remarks>
    ///     The callback receives an empty <see cref="AtmosWorldSolverContext.Topology" />. Use
    ///     <see cref="RegisterNeighborSolver(string, AtmosNeighborSelection, AtmosWorldSolver)" /> when it needs
    ///     adjacency.
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException">The name is blank or already registered.</exception>
    /// <exception cref="InvalidOperationException">The world or one of its simulations is recording or replaying.</exception>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    [PublicAPI]
    public void Register(string name, AtmosWorldSolver solver)
    {
        RegisterCore(name, WrapDelegate(solver), null, -1);
    }

    /// <summary>
    ///     Registers a custom class solver at the end of the pipeline.
    /// </summary>
    /// <param name="name">A nonempty name unique within the pipeline.</param>
    /// <param name="solver">The caller-owned solver instance.</param>
    /// <remarks>
    ///     Identical to the <see cref="AtmosWorldSolver" /> overload in pipeline order and checkpoint metadata. The
    ///     instance is caller-owned and also receives <see cref="IAtmosWorldSolver.OnRestored" />.
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException">The name is blank or already registered.</exception>
    /// <exception cref="InvalidOperationException">The world or one of its simulations is recording or replaying.</exception>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    [PublicAPI]
    public void Register(string name, IAtmosWorldSolver solver)
    {
        RegisterCore(name, solver, null, -1);
    }

    /// <summary>
    ///     Registers a custom stage with a topology view compiled for its selection policy.
    /// </summary>
    /// <param name="name">A nonempty name unique within the pipeline.</param>
    /// <param name="selection">The Cartesian and explicit adjacency included in the solver's view.</param>
    /// <param name="solver">The caller-owned callback.</param>
    /// <remarks>
    ///     Use this overload (or the <c>RegisterNeighborSolverBefore</c>/<c>RegisterNeighborSolverAfter</c> variants)
    ///     whenever the solver reads <see cref="AtmosWorldSolverContext.Topology" />. A stage registered through
    ///     <c>Register</c> instead gets an empty topology view: <c>GetOwnedEdges()</c> yields nothing and
    ///     <c>GetNeighbors</c> reports no explicit neighbors, silently, with no exception to flag the missing
    ///     selection. Set <paramref name="selection" />'s <c>includeCartesian</c> to <see langword="false" /> for a
    ///     stage that only cares about portals and docks — that keeps the compiled view limited to the sparse
    ///     explicit edge list instead of also re-deriving every ordinary chunk boundary.
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException">The name is blank or already registered.</exception>
    /// <exception cref="InvalidOperationException">The world or one of its simulations is recording or replaying.</exception>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    [PublicAPI]
    public void RegisterNeighborSolver(
        string name,
        AtmosNeighborSelection selection,
        AtmosWorldSolver solver)
    {
        ArgumentNullException.ThrowIfNull(selection);
        RegisterCore(name, WrapDelegate(solver), selection, -1);
    }

    /// <summary>
    ///     Registers a custom class solver with a topology view compiled for its selection policy.
    /// </summary>
    /// <param name="name">A nonempty name unique within the pipeline.</param>
    /// <param name="selection">The Cartesian and explicit adjacency included in the solver's view.</param>
    /// <param name="solver">The caller-owned solver instance.</param>
    /// <remarks>
    ///     Use this overload (or the <c>RegisterNeighborSolverBefore</c>/<c>RegisterNeighborSolverAfter</c> variants)
    ///     whenever the solver reads <see cref="AtmosWorldSolverContext.Topology" />. A stage registered through
    ///     <c>Register</c> instead gets an empty topology view: <c>GetOwnedEdges()</c> yields nothing and
    ///     <c>GetNeighbors</c> reports no explicit neighbors, silently, with no exception to flag the missing
    ///     selection. Set <paramref name="selection" />'s <c>includeCartesian</c> to <see langword="false" /> for a
    ///     stage that only cares about portals and docks — that keeps the compiled view limited to the sparse
    ///     explicit edge list instead of also re-deriving every ordinary chunk boundary.
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException">The name is blank or already registered.</exception>
    /// <exception cref="InvalidOperationException">The world or one of its simulations is recording or replaying.</exception>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    [PublicAPI]
    public void RegisterNeighborSolver(
        string name,
        AtmosNeighborSelection selection,
        IAtmosWorldSolver solver)
    {
        ArgumentNullException.ThrowIfNull(selection);
        RegisterCore(name, solver, selection, -1);
    }

    /// <summary>
    ///     Registers a custom stage immediately before another registered stage.
    /// </summary>
    /// <param name="existingName">The name of the stage that will follow the new stage.</param>
    /// <param name="name">A nonempty name unique within the pipeline.</param>
    /// <param name="solver">The caller-owned callback.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException">A name is blank, or <paramref name="name" /> is already registered.</exception>
    /// <exception cref="KeyNotFoundException">No stage named <paramref name="existingName" /> is registered.</exception>
    /// <exception cref="InvalidOperationException">The world or one of its simulations is recording or replaying.</exception>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    [PublicAPI]
    public void RegisterBefore(string existingName, string name, AtmosWorldSolver solver)
    {
        RegisterRelative(existingName, name, WrapDelegate(solver), null, true);
    }

    /// <summary>
    ///     Registers a custom class solver immediately before another registered stage.
    /// </summary>
    /// <param name="existingName">The name of the stage that will follow the new stage.</param>
    /// <param name="name">A nonempty name unique within the pipeline.</param>
    /// <param name="solver">The caller-owned solver instance.</param>
    /// <remarks>
    ///     Identical to the <see cref="AtmosWorldSolver" /> overload in pipeline order and checkpoint metadata. The
    ///     instance is caller-owned and also receives <see cref="IAtmosWorldSolver.OnRestored" />.
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException">A name is blank, or <paramref name="name" /> is already registered.</exception>
    /// <exception cref="KeyNotFoundException">No stage named <paramref name="existingName" /> is registered.</exception>
    /// <exception cref="InvalidOperationException">The world or one of its simulations is recording or replaying.</exception>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    [PublicAPI]
    public void RegisterBefore(string existingName, string name, IAtmosWorldSolver solver)
    {
        RegisterRelative(existingName, name, solver, null, true);
    }

    /// <summary>
    ///     Registers a neighbor-aware custom stage immediately before another registered stage.
    /// </summary>
    /// <param name="existingName">The name of the stage that will follow the new stage.</param>
    /// <param name="name">A nonempty name unique within the pipeline.</param>
    /// <param name="selection">The Cartesian and explicit adjacency included in the solver's view.</param>
    /// <param name="solver">The caller-owned callback.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException">A name is blank, or <paramref name="name" /> is already registered.</exception>
    /// <exception cref="KeyNotFoundException">No stage named <paramref name="existingName" /> is registered.</exception>
    /// <exception cref="InvalidOperationException">The world or one of its simulations is recording or replaying.</exception>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    [PublicAPI]
    public void RegisterNeighborSolverBefore(
        string existingName,
        string name,
        AtmosNeighborSelection selection,
        AtmosWorldSolver solver)
    {
        ArgumentNullException.ThrowIfNull(selection);
        RegisterRelative(existingName, name, WrapDelegate(solver), selection, true);
    }

    /// <summary>
    ///     Registers a neighbor-aware custom class solver immediately before another registered stage.
    /// </summary>
    /// <param name="existingName">The name of the stage that will follow the new stage.</param>
    /// <param name="name">A nonempty name unique within the pipeline.</param>
    /// <param name="selection">The Cartesian and explicit adjacency included in the solver's view.</param>
    /// <param name="solver">The caller-owned solver instance.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException">A name is blank, or <paramref name="name" /> is already registered.</exception>
    /// <exception cref="KeyNotFoundException">No stage named <paramref name="existingName" /> is registered.</exception>
    /// <exception cref="InvalidOperationException">The world or one of its simulations is recording or replaying.</exception>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    [PublicAPI]
    public void RegisterNeighborSolverBefore(
        string existingName,
        string name,
        AtmosNeighborSelection selection,
        IAtmosWorldSolver solver)
    {
        ArgumentNullException.ThrowIfNull(selection);
        RegisterRelative(existingName, name, solver, selection, true);
    }

    /// <summary>
    ///     Registers a custom stage immediately after another registered stage.
    /// </summary>
    /// <param name="existingName">The name of the stage that will precede the new stage.</param>
    /// <param name="name">A nonempty name unique within the pipeline.</param>
    /// <param name="solver">The caller-owned callback.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException">A name is blank, or <paramref name="name" /> is already registered.</exception>
    /// <exception cref="KeyNotFoundException">No stage named <paramref name="existingName" /> is registered.</exception>
    /// <exception cref="InvalidOperationException">The world or one of its simulations is recording or replaying.</exception>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    [PublicAPI]
    public void RegisterAfter(string existingName, string name, AtmosWorldSolver solver)
    {
        RegisterRelative(existingName, name, WrapDelegate(solver), null, false);
    }

    /// <summary>
    ///     Registers a custom class solver immediately after another registered stage.
    /// </summary>
    /// <param name="existingName">The name of the stage that will precede the new stage.</param>
    /// <param name="name">A nonempty name unique within the pipeline.</param>
    /// <param name="solver">The caller-owned solver instance.</param>
    /// <remarks>
    ///     Identical to the <see cref="AtmosWorldSolver" /> overload in pipeline order and checkpoint metadata. The
    ///     instance is caller-owned and also receives <see cref="IAtmosWorldSolver.OnRestored" />.
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException">A name is blank, or <paramref name="name" /> is already registered.</exception>
    /// <exception cref="KeyNotFoundException">No stage named <paramref name="existingName" /> is registered.</exception>
    /// <exception cref="InvalidOperationException">The world or one of its simulations is recording or replaying.</exception>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    [PublicAPI]
    public void RegisterAfter(string existingName, string name, IAtmosWorldSolver solver)
    {
        RegisterRelative(existingName, name, solver, null, false);
    }

    /// <summary>
    ///     Registers a neighbor-aware custom stage immediately after another registered stage.
    /// </summary>
    /// <param name="existingName">The name of the stage that will precede the new stage.</param>
    /// <param name="name">A nonempty name unique within the pipeline.</param>
    /// <param name="selection">The Cartesian and explicit adjacency included in the solver's view.</param>
    /// <param name="solver">The caller-owned callback.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException">A name is blank, or <paramref name="name" /> is already registered.</exception>
    /// <exception cref="KeyNotFoundException">No stage named <paramref name="existingName" /> is registered.</exception>
    /// <exception cref="InvalidOperationException">The world or one of its simulations is recording or replaying.</exception>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    [PublicAPI]
    public void RegisterNeighborSolverAfter(
        string existingName,
        string name,
        AtmosNeighborSelection selection,
        AtmosWorldSolver solver)
    {
        ArgumentNullException.ThrowIfNull(selection);
        RegisterRelative(existingName, name, WrapDelegate(solver), selection, false);
    }

    /// <summary>
    ///     Registers a neighbor-aware custom class solver immediately after another registered stage.
    /// </summary>
    /// <param name="existingName">The name of the stage that will precede the new stage.</param>
    /// <param name="name">A nonempty name unique within the pipeline.</param>
    /// <param name="selection">The Cartesian and explicit adjacency included in the solver's view.</param>
    /// <param name="solver">The caller-owned solver instance.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException">A name is blank, or <paramref name="name" /> is already registered.</exception>
    /// <exception cref="KeyNotFoundException">No stage named <paramref name="existingName" /> is registered.</exception>
    /// <exception cref="InvalidOperationException">The world or one of its simulations is recording or replaying.</exception>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    [PublicAPI]
    public void RegisterNeighborSolverAfter(
        string existingName,
        string name,
        AtmosNeighborSelection selection,
        IAtmosWorldSolver solver)
    {
        ArgumentNullException.ThrowIfNull(selection);
        RegisterRelative(existingName, name, solver, selection, false);
    }

    /// <summary>
    ///     Removes a stage by name, including a built-in stage.
    /// </summary>
    /// <param name="name">The registered stage name.</param>
    /// <returns><see langword="true" /> when a stage was removed.</returns>
    /// <remarks>
    ///     Removing a built-in stage drops that physics from every subsequent tick; <see cref="ResetToDefaults" />
    ///     brings it back.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="name" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException"><paramref name="name" /> is empty or whitespace.</exception>
    /// <exception cref="InvalidOperationException">The world or one of its simulations is recording or replaying.</exception>
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
            _dependencyEpoch++;
            PublishCheckpointSteps();
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
            PublishCheckpointSteps();
            _world.RecordWorldSolverEnablement(name, enabled);
            return true;
        }
    }

    /// <summary>
    ///     Restores the built-in stages in their default order and enabled state, discarding any custom
    ///     registrations.
    /// </summary>
    /// <exception cref="InvalidOperationException">The world or one of its simulations is recording or replaying.</exception>
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
            _dependencyEpoch++;
            PublishCheckpointSteps();
        }
    }

    /// <summary>
    ///     Injects dependencies into every enabled class solver now, without waiting for the next tick.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Ticks inject lazily, so a missing dependency normally surfaces as an exception from
    ///         <see cref="AtmosWorld.Tick" />. Call this after setting up services and solvers to get the same error up
    ///         front. Every enabled <see cref="IAtmosSolverDependencies" /> solver is injected in pipeline order, even if
    ///         it was already up to date; disabled solvers are injected when they are next enabled and ticked.
    ///     </para>
    ///     <para>
    ///         The first failure stops the pass and propagates. Solvers before it keep their new references, and the
    ///         failed one and everything after it are retried on the next tick.
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
    ///     Injects dependencies into the class solvers in <paramref name="steps" />, in order.
    /// </summary>
    /// <param name="steps">The enabled registrations about to run.</param>
    /// <param name="force">Whether to inject solvers that are already up to date.</param>
    /// <remarks>
    ///     The caller holds <see cref="AtmosWorld.Gate" /> and has marked the world as ticking, so services and the
    ///     pipeline can't change underneath the pass. A registration is stamped only after its injection returns, so
    ///     a failure leaves it stale for the next attempt.
    /// </remarks>
    internal void InjectDependencies(WorldSolverRegistration[] steps, bool force)
    {
        int epoch = _dependencyEpoch;
        int servicesVersion = _world.Services.Version;
        (string Name, IAtmosWorldSolver Solver)[]? candidates = null;
        foreach (var step in steps)
        {
            if (step.CustomSolver is not IAtmosSolverDependencies dependent)
                continue;

            if (!force && step.IsInjected(epoch, servicesVersion))
                continue;

            candidates ??= _steps
                .Where(static candidate => candidate.CustomSolver is not null and not DelegateWorldSolver)
                .Select(static candidate => (candidate.Name, candidate.CustomSolver!))
                .ToArray();

            dependent.InjectDependencies(
                new AtmosSolverDependencyResolver(_world, step.Name, step.CustomSolver, candidates));
            step.MarkInjected(epoch, servicesVersion);
        }
    }

    internal WorldSolverRegistration[] CaptureEnabled()
    {
        return _steps.Where(static step => step.IsEnabled).ToArray();
    }

    internal void Execute(WorldSolverRegistration[] steps, AtmosWorldExecutionContext context)
    {
        foreach (var step in steps)
            step.Execute(context);
    }

    internal void RecompileTopology(IReadOnlyList<ExplicitLinkDefinition> links)
    {
        var compiled = new AtmosWorldNeighborTopology[_steps.Count];
        for (int index = 0; index < _steps.Count; index++)
            compiled[index] = _steps[index].BuildTopology(_world, links);

        for (int index = 0; index < _steps.Count; index++)
            _steps[index].InstallTopology(compiled[index]);
    }

    internal AtmosSolverCheckpoint[] CaptureCheckpointSteps()
    {
        return Volatile.Read(ref _checkpointSteps);
    }

    /// <summary>
    ///     Calls <see cref="IAtmosWorldSolver.OnRestored" /> on every custom class solver, enabled or not, in pipeline
    ///     order.
    /// </summary>
    /// <remarks>
    ///     Iterates a snapshot so a solver that edits the pipeline from its hook doesn't break the loop. The first
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

    private static DelegateWorldSolver WrapDelegate(AtmosWorldSolver solver)
    {
        ArgumentNullException.ThrowIfNull(solver);
        return new DelegateWorldSolver(solver);
    }

    private void RegisterCore(
        string name,
        IAtmosWorldSolver solver,
        AtmosNeighborSelection? selection,
        int insertionIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(solver);

        lock (_world.Gate)
        {
            _world.ThrowIfDisposedForPipeline();
            _world.EnsureCanChangeWorldSolverDefinition();
            if (IndexOf(name) >= 0)
                throw new ArgumentException($"A world solver named '{name}' is already registered.", nameof(name));

            var registration = WorldSolverRegistration.CreateCustom(name, solver, selection);
            registration.InstallTopology(registration.BuildTopology(_world, _world.GetActiveLinksCore()));
            _steps.Insert(insertionIndex < 0 ? _steps.Count : insertionIndex, registration);
            _dependencyEpoch++;
            PublishCheckpointSteps();
        }
    }

    private void RegisterRelative(
        string existingName,
        string name,
        IAtmosWorldSolver solver,
        AtmosNeighborSelection? selection,
        bool before)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(existingName);
        lock (_world.Gate)
        {
            int existingIndex = IndexOf(existingName);
            if (existingIndex < 0)
                throw new KeyNotFoundException($"No world solver named '{existingName}' is registered.");

            RegisterCore(name, solver, selection, before ? existingIndex : existingIndex + 1);
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
                static context => context.World.SolveAdvectionStage(context)));

        _steps.Add(
            WorldSolverRegistration.CreateBuiltIn(
                AtmosBuiltInSolvers.ExplicitGasTransport,
                static context => context.World.SolveExplicitGasTransportStage(context)));

        _steps.Add(
            WorldSolverRegistration.CreateBuiltIn(
                AtmosBuiltInSolvers.BoundaryFlow,
                static context => context.World.SolveBoundaryFlowStage(context)));

        _steps.Add(
            WorldSolverRegistration.CreateBuiltIn(
                AtmosBuiltInSolvers.Thermodynamics,
                static context => context.World.SolveThermodynamicsStage(context)));

        _steps.Add(
            WorldSolverRegistration.CreateBuiltIn(
                AtmosBuiltInSolvers.ExplicitThermalTransport,
                static context => context.World.SolveExplicitThermalTransportStage(context)));

        _steps.Add(
            WorldSolverRegistration.CreateBuiltIn(
                AtmosBuiltInSolvers.ThermalBoundary,
                static context => context.World.SolveThermalBoundaryStage(context)));

        _steps.Add(
            WorldSolverRegistration.CreateBuiltIn(
                AtmosBuiltInSolvers.GasReactions,
                static context => context.World.SolveGasReactionsStage(context)));
    }

    private void PublishCheckpointSteps()
    {
        Volatile.Write(
            ref _checkpointSteps,
            _steps.Select(static step => new AtmosSolverCheckpoint(
                step.Name,
                step.Kind == AtmosWorldSolverKind.Custom,
                step.IsEnabled,
                step.Selection?.Key)).ToArray());
    }
}

internal sealed class AtmosWorldExecutionContext(
    AtmosWorld world,
    AtmosSimulation[] simulations,
    AtmosWorldTickExecution[] executions,
    IReadOnlyList<AtmosSimulation> publicSimulations,
    AtmosSolverTickScope solverScope)
{
    internal AtmosWorld World { get; } = world;
    internal AtmosSimulation[] Simulations { get; } = simulations;
    internal AtmosWorldTickExecution[] Executions { get; } = executions;
    internal IReadOnlyList<AtmosSimulation> PublicSimulations { get; } = publicSimulations;
    internal AtmosSolverTickScope SolverScope { get; } = solverScope;
}

internal sealed class WorldSolverRegistration
{
    private readonly Action<AtmosWorldExecutionContext>? _builtInSolver;
    private readonly IAtmosWorldSolver? _customSolver;
    private AtmosWorldNeighborTopology _topology = AtmosWorldNeighborTopology.Empty;
    private int _injectedPipelineEpoch = -1;
    private int _injectedServicesVersion = -1;

    private WorldSolverRegistration(
        string name,
        AtmosWorldSolverKind kind,
        Action<AtmosWorldExecutionContext>? builtInSolver,
        IAtmosWorldSolver? customSolver,
        AtmosNeighborSelection? selection)
    {
        Name = name;
        Kind = kind;
        _builtInSolver = builtInSolver;
        _customSolver = customSolver;
        Selection = selection;
    }

    internal string Name { get; }
    internal AtmosWorldSolverKind Kind { get; }
    internal AtmosNeighborSelection? Selection { get; }
    internal bool IsEnabled { get; set; } = true;

    /// <summary>
    ///     Gets the class solver instance, or the delegate adapter, for a custom stage.
    /// </summary>
    internal IAtmosWorldSolver? CustomSolver => _customSolver;

    internal static WorldSolverRegistration CreateBuiltIn(
        string name,
        Action<AtmosWorldExecutionContext> solver)
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

    internal void Execute(AtmosWorldExecutionContext context)
    {
        if (_customSolver != null)
        {
            _customSolver.Solve(new AtmosWorldSolverContext(
                context.World, context.PublicSimulations, _topology, context.SolverScope));
            return;
        }

        _builtInSolver!(context);
    }

    internal bool IsInjected(int pipelineEpoch, int servicesVersion)
    {
        return _injectedPipelineEpoch == pipelineEpoch && _injectedServicesVersion == servicesVersion;
    }

    internal void MarkInjected(int pipelineEpoch, int servicesVersion)
    {
        _injectedPipelineEpoch = pipelineEpoch;
        _injectedServicesVersion = servicesVersion;
    }

    internal void NotifyRestored()
    {
        // Delegates have nowhere to keep state of their own, so the default no-op is skipped without a virtual call.
        if (_customSolver is not null and not DelegateWorldSolver)
            _customSolver.OnRestored();
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
