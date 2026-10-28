using System.Collections.ObjectModel;
using JetBrains.Annotations;
using Numos.CoreSim;
using Numos.CoreSim.State.Advanced;

namespace Numos.API;

/// <summary>
///     Owns the ordered solver stages executed for every fixed world tick.
/// </summary>
/// <remarks>
///     The enabled stage list is captured before each tick. Pipeline edits made by a running stage take effect on the
///     next tick. Solver delegates and <see cref="IAtmosWorldSolver" /> instances remain caller-owned.
///     Numos can recreate a built-in stage from its name, so the order and enabled state of the built-in stages are
///     recorded world state. <see cref="SetLayout" /> and <see cref="SetEnabled" /> work during recording, and a
///     checkpoint restore adopts the checkpoint's layout. Numos cannot recreate a custom stage, because it has no
///     access to the host delegate. Registering, removing, or resetting stages is therefore rejected while the world
///     is recording or replaying, and a restore requires the same custom stages.
/// </remarks>
public sealed class AtmosWorldSolverPipeline
{
    private readonly List<WorldSolverRegistration> _steps = [];
    private readonly AtmosWorld _world;
    private WorldSolverRegistration[] _enabledSteps = [];
    private WorldSolverRegistration[] _statefulSteps = [];

    // The world dependency epoch every registration in _enabledSteps was injected at, or -1. Republishing the steps
    // clears it, so enabling a solver that sat out an epoch change gets it injected before it runs.
    private int _injectedEpoch = -1;

    // The one source of truth for built-in stages. The order is the canonical order that BuiltInStageNames reports.
    private static readonly (string Name, Action<AtmosSolverTickScope> Factory)[] BuiltInStages =
    [
        (AtmosBuiltInSolvers.EulerFlow, static scope => scope.World.SolveEulerFlowStage(scope)),
        (AtmosBuiltInSolvers.Advection, static scope => scope.World.SolveAdvectionStage(scope)),
        (AtmosBuiltInSolvers.ExplicitGasTransport, static scope => scope.World.SolveExplicitGasTransportStage(scope)),
        (AtmosBuiltInSolvers.BoundaryFlow, static scope => scope.World.SolveBoundaryFlowStage(scope)),
        (AtmosBuiltInSolvers.Thermodynamics, static scope => scope.World.SolveThermodynamicsStage(scope)),
        (AtmosBuiltInSolvers.ExplicitThermalTransport, static scope => scope.World.SolveExplicitThermalTransportStage(scope)),
        (AtmosBuiltInSolvers.ThermalBoundary, static scope => scope.World.SolveThermalBoundaryStage(scope)),
        (AtmosBuiltInSolvers.GasReactions, static scope => scope.World.SolveGasReactionsStage(scope))
    ];

    private static readonly Dictionary<string, Action<AtmosSolverTickScope>> BuiltInFactories = CreateBuiltInFactories();

    private static readonly ReadOnlyCollection<string> BuiltInNames = CreateBuiltInNames();

    private static readonly ReadOnlyCollection<string> EulerLayout = Array.AsReadOnly(
    [
        AtmosBuiltInSolvers.EulerFlow,
        AtmosBuiltInSolvers.ExplicitGasTransport,
        AtmosBuiltInSolvers.Thermodynamics,
        AtmosBuiltInSolvers.ExplicitThermalTransport,
        AtmosBuiltInSolvers.ThermalBoundary,
        AtmosBuiltInSolvers.GasReactions
    ]);

    private static readonly ReadOnlyCollection<string> LegacyLayout = Array.AsReadOnly(
    [
        AtmosBuiltInSolvers.Advection,
        AtmosBuiltInSolvers.ExplicitGasTransport,
        AtmosBuiltInSolvers.BoundaryFlow,
        AtmosBuiltInSolvers.Thermodynamics,
        AtmosBuiltInSolvers.ExplicitThermalTransport,
        AtmosBuiltInSolvers.ThermalBoundary,
        AtmosBuiltInSolvers.GasReactions
    ]);

    internal AtmosWorldSolverPipeline(AtmosWorld world, AtmosTransportModel model)
    {
        _world = world;
        AddDefaults(model);
        PublishSteps();
    }

    /// <summary>
    ///     Gets the name of every built-in stage, in canonical order.
    /// </summary>
    /// <remarks>
    ///     The list covers both transport models, so it includes <c>euler-flow</c> and the legacy-only
    ///     <c>advection</c> and <c>boundary-flow</c> stages. Numos can recreate any of these by name, which is why
    ///     <see cref="SetLayout" /> accepts them without a registration.
    /// </remarks>
    [PublicAPI]
    public static IReadOnlyList<string> BuiltInStageNames => BuiltInNames;

    /// <summary>
    ///     Returns the stage names that <see cref="ResetToDefaults(AtmosTransportModel)" /> installs for a model.
    /// </summary>
    /// <param name="model">Which built-in stages carry gas between voxels.</param>
    /// <returns>The built-in stage names in execution order. Every stage is enabled by default.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="model" /> is not a defined model.</exception>
    /// <example>
    ///     <code>
    ///     var layout = AtmosWorldSolverPipeline.GetDefaultLayout(AtmosTransportModel.Legacy)
    ///         .Select(static name => new AtmosWorldSolverLayoutEntry(name, true))
    ///         .ToArray();
    ///     world.Solvers.SetLayout(layout);
    ///     </code>
    /// </example>
    [PublicAPI]
    public static IReadOnlyList<string> GetDefaultLayout(AtmosTransportModel model)
    {
        return model switch
        {
            AtmosTransportModel.Euler => EulerLayout,
            AtmosTransportModel.Legacy => LegacyLayout,
            _ => throw new ArgumentOutOfRangeException(nameof(model), model, "Unknown transport model.")
        };
    }

    /// <summary>
    ///     Returns a layout that runs the given transport model, leaving every other stage where it is.
    /// </summary>
    /// <param name="layout">The layout to convert, typically from <see cref="Steps" />.</param>
    /// <param name="model">The transport model the result runs.</param>
    /// <returns>A new layout. Stages outside the transport set keep their order and enabled flags.</returns>
    /// <remarks>
    ///     <para>
    ///         Converting to <see cref="AtmosTransportModel.Legacy" /> replaces <c>euler-flow</c> with
    ///         <c>advection</c> at the same position. It adds <c>boundary-flow</c> after
    ///         <c>explicit-gas-transport</c>, or after <c>advection</c> when that stage is absent. The new stages
    ///         inherit the enabled flag of the stage they replace.
    ///     </para>
    ///     <para>
    ///         Converting to <see cref="AtmosTransportModel.Euler" /> replaces <c>advection</c> and
    ///         <c>boundary-flow</c> with one <c>euler-flow</c> at the position of the first of them. A transport stage
    ///         that the target model already has stays as it is. A layout with no transport stage at all gains the
    ///         target model's stages at the front.
    ///     </para>
    ///     <para>
    ///         This method only builds a list. Pass the result to <see cref="SetLayout" /> to apply it.
    ///     </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="layout" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="model" /> is not a defined model.</exception>
    [PublicAPI]
    public static IReadOnlyList<AtmosWorldSolverLayoutEntry> ConvertTransportModel(
        IReadOnlyList<AtmosWorldSolverLayoutEntry> layout,
        AtmosTransportModel model)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (!Enum.IsDefined(model))
            throw new ArgumentOutOfRangeException(nameof(model), model, "Unknown transport model.");

        var result = new List<AtmosWorldSolverLayoutEntry>(layout.Count + 2);
        bool hasEuler = layout.Any(static e => e.Name == AtmosBuiltInSolvers.EulerFlow);
        bool hasAdvection = layout.Any(static e => e.Name == AtmosBuiltInSolvers.Advection);
        bool hasBoundary = layout.Any(static e => e.Name == AtmosBuiltInSolvers.BoundaryFlow);
        bool anchorEnabled = true;
        if (model == AtmosTransportModel.Legacy)
        {
            bool placed = hasAdvection;
            foreach (var entry in layout)
            {
                if (entry.Name != AtmosBuiltInSolvers.EulerFlow)
                {
                    result.Add(entry);
                    continue;
                }

                anchorEnabled = entry.Enabled;
                if (!placed)
                {
                    result.Add(new AtmosWorldSolverLayoutEntry(AtmosBuiltInSolvers.Advection, entry.Enabled));
                    placed = true;
                }
            }

            if (!placed)
                result.Insert(0, new AtmosWorldSolverLayoutEntry(AtmosBuiltInSolvers.Advection, true));

            if (!hasBoundary)
            {
                int after = result.FindIndex(static e => e.Name == AtmosBuiltInSolvers.ExplicitGasTransport);
                if (after < 0)
                    after = result.FindIndex(static e => e.Name == AtmosBuiltInSolvers.Advection);

                result.Insert(after + 1, new AtmosWorldSolverLayoutEntry(AtmosBuiltInSolvers.BoundaryFlow, anchorEnabled));
            }

            return result;
        }

        bool emitted = hasEuler;
        foreach (var entry in layout)
        {
            if (entry.Name is not (AtmosBuiltInSolvers.Advection or AtmosBuiltInSolvers.BoundaryFlow))
            {
                result.Add(entry);
                continue;
            }

            if (!emitted)
            {
                result.Add(new AtmosWorldSolverLayoutEntry(AtmosBuiltInSolvers.EulerFlow, entry.Enabled));
                emitted = true;
            }
        }

        if (!emitted)
            result.Insert(0, new AtmosWorldSolverLayoutEntry(AtmosBuiltInSolvers.EulerFlow, true));

        return result;
    }

    /// <summary>
    ///     Reads the transport model from the stages that move gas between voxels.
    /// </summary>
    /// <param name="steps">The stages to inspect, typically from <see cref="Steps" />.</param>
    /// <returns>
    ///     <see cref="AtmosTransportModel.Euler" /> when only <c>euler-flow</c> is present and
    ///     <see cref="AtmosTransportModel.Legacy" /> when only <c>advection</c> is present. <see langword="null" />
    ///     when both or neither is present.
    /// </returns>
    /// <remarks>
    ///     The enabled flag does not matter, because <see cref="ConvertTransportModel" /> keeps it. Callers that need
    ///     a default write <c>DetectTransportModel(steps) ?? AtmosTransportModel.Euler</c>.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="steps" /> is <see langword="null" />.</exception>
    [PublicAPI]
    public static AtmosTransportModel? DetectTransportModel(IReadOnlyList<AtmosWorldSolverStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        bool hasEuler = steps.Any(static step => step.Name == AtmosBuiltInSolvers.EulerFlow);
        bool hasAdvection = steps.Any(static step => step.Name == AtmosBuiltInSolvers.Advection);
        if (hasEuler == hasAdvection)
            return null;

        return hasEuler ? AtmosTransportModel.Euler : AtmosTransportModel.Legacy;
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
    ///     Gets the class solvers with <c>[NumosSerialize]</c> members, enabled or not, in pipeline order.
    /// </summary>
    /// <remarks>
    ///     Usually empty, which is what lets a world without stateful solvers skip every state hash. Replaced, never
    ///     mutated, like <see cref="EnabledSteps" />.
    /// </remarks>
    internal WorldSolverRegistration[] StatefulSteps => _statefulSteps;

    /// <summary>
    ///     Finds a registration by name. The caller holds <see cref="AtmosWorld.Gate" />.
    /// </summary>
    internal WorldSolverRegistration? Find(string name)
    {
        int index = IndexOf(name);
        return index < 0 ? null : _steps[index];
    }

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
    /// <exception cref="InvalidOperationException">
    ///     The world is recording or replaying, because Numos cannot recreate a custom stage on replay.
    /// </exception>
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
    ///     Removing a built-in stage drops that physics from every subsequent tick. <see cref="ResetToDefaults()" />
    ///     or <see cref="SetLayout" /> brings it back.
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
    ///     Replaces the order and enabled flags of the stage list in one step.
    /// </summary>
    /// <param name="stages">Every stage in the new execution order, with its enabled flag.</param>
    /// <returns><see langword="true" /> when the layout changed, <see langword="false" /> when it already matched.</returns>
    /// <remarks>
    ///     <para>
    ///         Each name is a built-in stage name (see <see cref="BuiltInStageNames" />) or a registered custom stage.
    ///         Numos creates a built-in stage that is not registered and drops a built-in stage that is not listed.
    ///         Every registered custom stage must appear, because <see cref="Unregister" /> removes custom stages.
    ///         Custom stages keep their solver instance, topology, and injected dependencies.
    ///     </para>
    ///     <para>
    ///         Unlike registration, this works during recording. A change records one operation, and a call that
    ///         changes nothing records nothing. A call from inside a solver tick is not recorded, because replay
    ///         reproduces it. The call is rejected while a replay applies history.
    ///     </para>
    ///     <para>
    ///         A rejected layout changes nothing.
    ///     </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="stages" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException">
    ///     A name is blank or listed twice, a name is neither a built-in stage nor a registered custom stage, or a
    ///     registered custom stage is missing.
    /// </exception>
    /// <exception cref="InvalidOperationException">The world is replaying history.</exception>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    /// <example>
    ///     <code>
    ///     world.Solvers.SetLayout(
    ///     [
    ///         new(AtmosBuiltInSolvers.Advection, true),
    ///         new(AtmosBuiltInSolvers.ExplicitGasTransport, true),
    ///         new(AtmosBuiltInSolvers.BoundaryFlow, true),
    ///         new(AtmosBuiltInSolvers.Thermodynamics, true),
    ///         new(AtmosBuiltInSolvers.ExplicitThermalTransport, true),
    ///         new(AtmosBuiltInSolvers.ThermalBoundary, true),
    ///         new(AtmosBuiltInSolvers.GasReactions, false)
    ///     ]);
    ///     </code>
    /// </example>
    [PublicAPI]
    public bool SetLayout(IReadOnlyList<AtmosWorldSolverLayoutEntry> stages)
    {
        ArgumentNullException.ThrowIfNull(stages);
        AtmosWorldSolverLayoutEntry[] copy = stages.ToArray();
        lock (_world.Gate)
        {
            _world.ThrowIfDisposedForPipeline();
            _world.EnsureCanChangeSolverLayout();
            if (!SetLayoutCore(copy))
                return false;

            _world.RecordWorldSolverLayout(copy);
            return true;
        }
    }

    /// <summary>
    ///     Installs a layout without recording it or checking the recording state.
    /// </summary>
    /// <remarks>
    ///     Replay and checkpoint restore call this. The caller holds <see cref="AtmosWorld.Gate" />. Validation
    ///     finishes before any change, so a rejected layout leaves the pipeline as it was.
    /// </remarks>
    /// <returns><see langword="true" /> when the order or an enabled flag changed.</returns>
    internal bool SetLayoutCore(IReadOnlyList<AtmosWorldSolverLayoutEntry> stages)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var next = new WorldSolverRegistration[stages.Count];
        for (int index = 0; index < stages.Count; index++)
        {
            string name = stages[index].Name;
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException($"Layout entry {index} has a blank stage name.", nameof(stages));

            if (!seen.Add(name))
                throw new ArgumentException($"The layout lists stage '{name}' more than once.", nameof(stages));

            int existing = IndexOf(name);
            if (existing >= 0)
            {
                next[index] = _steps[existing];
            }
            else if (BuiltInFactories.TryGetValue(name, out var factory))
            {
                next[index] = WorldSolverRegistration.CreateBuiltIn(name, factory);
            }
            else
            {
                throw new ArgumentException(
                    $"The layout names '{name}', which is neither a built-in stage nor a registered custom stage.",
                    nameof(stages));
            }
        }

        foreach (var step in _steps)
        {
            if (step.Kind == AtmosWorldSolverKind.Custom && !seen.Contains(step.Name))
            {
                throw new ArgumentException(
                    $"The layout omits custom stage '{step.Name}'. Call Unregister to remove a custom stage.",
                    nameof(stages));
            }
        }

        bool changed = next.Length != _steps.Count;
        for (int index = 0; index < next.Length && !changed; index++)
        {
            changed = !ReferenceEquals(next[index], _steps[index]) ||
                      next[index].IsEnabled != stages[index].Enabled;
        }

        if (!changed)
            return false;

        for (int index = 0; index < next.Length; index++)
            next[index].IsEnabled = stages[index].Enabled;

        _steps.Clear();
        _steps.AddRange(next);
        _world.InvalidateSolverDependencies();
        PublishSteps();
        return true;
    }

    /// <summary>
    ///     Restores the built-in stages in their default order and enabled state, discarding any custom
    ///     registrations.
    /// </summary>
    /// <remarks>
    ///     Installs the <see cref="AtmosTransportModel.Euler" /> stages, the same set a new world starts with. Use
    ///     <see cref="ResetToDefaults(AtmosTransportModel)" /> to install the legacy pressure-relaxation stages
    ///     instead.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The world is recording or replaying.</exception>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    [PublicAPI]
    public void ResetToDefaults()
    {
        ResetToDefaults(AtmosTransportModel.Euler);
    }

    /// <summary>
    ///     Restores the built-in stages of one transport model in their default order and enabled state,
    ///     discarding any custom registrations.
    /// </summary>
    /// <param name="model">Which built-in stages carry gas between voxels.</param>
    /// <remarks>
    ///     <para>
    ///         <see cref="AtmosTransportModel.Euler" />, the default, installs <c>euler-flow</c>,
    ///         <c>explicit-gas-transport</c>, <c>thermodynamics</c>, <c>explicit-thermal-transport</c>,
    ///         <c>thermal-boundary</c>, and <c>gas-reactions</c>. <see cref="AtmosTransportModel.Legacy" /> replaces
    ///         <c>euler-flow</c> with <c>advection</c> ahead of <c>explicit-gas-transport</c> and
    ///         <c>boundary-flow</c> after it.
    ///     </para>
    ///     <para>
    ///         A restore adopts the checkpoint's stage layout, so restoring a legacy archive needs no call to this
    ///         method. To change the model during recording, call <see cref="SetLayout" /> with
    ///         <see cref="GetDefaultLayout" /> instead. This method discards custom stages, so it is rejected then.
    ///     </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="model" /> is not a defined model.</exception>
    /// <exception cref="InvalidOperationException">The world is recording or replaying.</exception>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    /// <example>
    ///     <code>
    /// simulation.World.Solvers.ResetToDefaults(AtmosTransportModel.Legacy);
    ///     </code>
    /// </example>
    [PublicAPI]
    public void ResetToDefaults(AtmosTransportModel model)
    {
        if (!Enum.IsDefined(model))
            throw new ArgumentOutOfRangeException(nameof(model), model, "Unknown transport model.");

        lock (_world.Gate)
        {
            _world.ThrowIfDisposedForPipeline();
            _world.EnsureCanChangeWorldSolverDefinition();
            _steps.Clear();
            AddDefaults(model);
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

    private static Dictionary<string, Action<AtmosSolverTickScope>> CreateBuiltInFactories()
    {
        var factories = new Dictionary<string, Action<AtmosSolverTickScope>>(BuiltInStages.Length, StringComparer.Ordinal);
        foreach (var (name, factory) in BuiltInStages)
        {
            factories[name] = factory;
        }

        return factories;
    }

    private static ReadOnlyCollection<string> CreateBuiltInNames()
    {
        var names = new string[BuiltInStages.Length];
        for (int i = 0; i < names.Length; i++)
        {
            names[i] = BuiltInStages[i].Name;
        }

        return Array.AsReadOnly(names);
    }

    private int IndexOf(string name)
    {
        return _steps.FindIndex(step => string.Equals(step.Name, name, StringComparison.Ordinal));
    }

    private void AddDefaults(AtmosTransportModel model)
    {
        foreach (string name in GetDefaultLayout(model))
            _steps.Add(WorldSolverRegistration.CreateBuiltIn(name, BuiltInFactories[name]));
    }

    private void PublishSteps()
    {
        _enabledSteps = _steps.Where(static step => step.IsEnabled).ToArray();
        _statefulSteps = _steps.Where(static step => step.State is not null).ToArray();
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
        State = customSolver as INumosStateTarget;
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

    /// <summary>
    ///     Gets the class solver's generated state, or <see langword="null" /> when it has no
    ///     <c>[NumosSerialize]</c> members.
    /// </summary>
    internal INumosStateTarget? State { get; }

    /// <summary>
    ///     Gets or sets the hash of <see cref="State" /> when Numos last saw it, at the end of a tick or at the last
    ///     boundary. A different hash at the next boundary means the host edited the solver in between.
    /// </summary>
    internal ulong StateHash { get; set; }

    /// <summary>
    ///     Hashes <see cref="State" /> without buffering it. Only valid for a stateful registration.
    /// </summary>
    internal ulong ComputeStateHash()
    {
        var sink = new StateHashSink();
        State!.WriteState(ref sink);
        return sink.Value;
    }

    /// <summary>
    ///     Writes <see cref="State" /> into a fresh array. Only valid for a stateful registration.
    /// </summary>
    internal byte[] CaptureState(ref StateBinarySink sink)
    {
        sink.Reset();
        State!.WriteState(ref sink);
        return sink.WrittenSpan.ToArray();
    }

    /// <summary>
    ///     Reads a payload from <see cref="CaptureState" /> back into the solver.
    /// </summary>
    internal void RestoreState(ReadOnlySpan<byte> payload, IGasRegistry gases)
    {
        var reader = new StateReader(payload);
        State!.ReadStateInto(ref reader, gases);
        if (!reader.IsAtEnd)
            throw new InvalidDataException($"The state for world solver '{Name}' has trailing data.");
    }

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