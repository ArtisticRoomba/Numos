using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using JetBrains.Annotations;
using Numos.CoreSim;
using Numos.CoreSim.State;
using Numos.CoreSim.State.Advanced;
using Numos.Maths;

namespace Numos.API;

/// <summary>
///     Owns the ordered solver stages executed for every fixed world tick.
/// </summary>
/// <remarks>
///     <para>
///         The enabled stage list is captured before each tick. Pipeline edits made by a running stage take effect on
///         the next tick.
///     </para>
///     <para>
///         Numos can recreate a built-in stage from its name, and a catalog stage from its
///         <see cref="AtmosSolverCatalog" /> entry. The order and enabled state of these stages are recorded world
///         state. <see cref="SetLayout" />, <see cref="SetEnabled" />, <see cref="Add{T}" />, and removing a catalog
///         stage work during recording. A checkpoint restore adopts the checkpoint's layout. It creates the catalog
///         stages the checkpoint names and removes the ones it lacks. The world owns a catalog stage's instance.
///     </para>
///     <para>
///         Delegates and instances passed to <c>Register</c> stay caller-owned. Numos cannot recreate them, because it
///         has no access to the host object. Registering or removing such a stage, or resetting the pipeline, is
///         therefore rejected while the world is recording or replaying. A restore requires the same host-registered
///         stages.
///     </para>
/// </remarks>
public sealed class AtmosWorldSolverPipeline
{
    // The world that each owned instance was last registered in. An owned instance has [NumosSerialize] state or
    // solver commands. A stateful instance in two live worlds would mix the state of both into each world's
    // checkpoints. A command finds its world here, so it can only have one. The gate is a leaf lock: holders read
    // other worlds' published stage arrays and take no other lock.
    private static readonly Lock OwnerGate = new();
    private static readonly ConditionalWeakTable<IAtmosWorldSolver, WeakReference<AtmosWorld>> Owners = new();

    private readonly List<WorldSolverRegistration> _steps = [];
    private readonly AtmosWorld _world;
    private WorldSolverRegistration[] _enabledSteps = [];
    private WorldSolverRegistration[] _statefulSteps = [];
    private WorldSolverRegistration[] _ownedSteps = [];

    // The world dependency epoch every registration in _enabledSteps was injected at, or -1. Republishing the steps
    // clears it, so enabling a solver that sat out an epoch change gets it injected before it runs.
    private int _injectedEpoch = -1;

    // While a tick, restore, or replay runs, removed catalog instances wait here instead of being disposed. The tick may
    // still call a removed stage, and a rollback may put it back. Guarded by the world's gate.
    private int _disposalDeferrals;
    private List<IAtmosWorldSolver>? _deferredDisposals;

    // BuiltInStageTable lists the built-in stages. These lookups add the world method that runs each one.
    private static readonly Dictionary<string, BuiltInWorldStage> BuiltInStages = CreateBuiltInStages();

    private static readonly ReadOnlyCollection<string> BuiltInNames =
        Array.AsReadOnly(BuiltInStageTable.All.Select(static stage => stage.Name).ToArray());

    private static readonly ReadOnlyCollection<string> EulerLayout = CreateDefaultLayout(AtmosTransportModel.Euler);

    private static readonly ReadOnlyCollection<string> LegacyLayout = CreateDefaultLayout(AtmosTransportModel.Legacy);

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
    ///     <c>derived-refresh</c>, <c>advection</c>, and <c>boundary-flow</c> stages. Numos can recreate any of these
    ///     by name, which is why <see cref="SetLayout" /> accepts them without a registration.
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
    ///         <c>explicit-gas-transport</c>. When that stage is absent, <c>boundary-flow</c> goes after the later of
    ///         <c>advection</c> and <c>vacuum-cleanup</c>. The new stages inherit the enabled flag of the stage they
    ///         replace. A layout without <c>derived-refresh</c> gains it right before <c>advection</c>, with the
    ///         enabled flag and interval of the <c>advection</c> entry.
    ///     </para>
    ///     <para>
    ///         Converting to <see cref="AtmosTransportModel.Euler" /> removes <c>derived-refresh</c>. It replaces
    ///         <c>advection</c> and <c>boundary-flow</c> with one <c>euler-flow</c> at the position of the first of
    ///         them. A transport stage that the target model already has stays as it is. A layout with no transport
    ///         stage at all gains the target model's stages at the front.
    ///     </para>
    ///     <para>
    ///         Both models run <c>vacuum-cleanup</c>, so a conversion keeps it in place and never adds it.
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

            if (!result.Any(static e => e.Name == AtmosBuiltInSolvers.DerivedRefresh))
            {
                // The two stages hand data over within a tick, so the new stage takes the interval of advection too.
                int advection = result.FindIndex(static e => e.Name == AtmosBuiltInSolvers.Advection);
                result.Insert(advection, result[advection] with { Name = AtmosBuiltInSolvers.DerivedRefresh });
            }

            if (!hasBoundary)
            {
                int after = result.FindIndex(static e => e.Name == AtmosBuiltInSolvers.ExplicitGasTransport);
                if (after < 0)
                {
                    after = Math.Max(
                        result.FindIndex(static e => e.Name == AtmosBuiltInSolvers.Advection),
                        result.FindIndex(static e => e.Name == AtmosBuiltInSolvers.VacuumCleanup));
                }

                result.Insert(after + 1, new AtmosWorldSolverLayoutEntry(AtmosBuiltInSolvers.BoundaryFlow, anchorEnabled));
            }

            return result;
        }

        bool emitted = hasEuler;
        foreach (var entry in layout)
        {
            if (entry.Name == AtmosBuiltInSolvers.DerivedRefresh)
                continue;

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
    ///     Gets the name of the first stage that holds <paramref name="solver" />, or <see langword="null" /> when none
    ///     does. The caller holds <see cref="AtmosWorld.Gate" />.
    /// </summary>
    /// <remarks>
    ///     An instance registered under two names records its commands under the first one in pipeline order. Replay
    ///     reaches the same instance through that name.
    /// </remarks>
    internal string? FindStageName(IAtmosWorldSolver solver)
    {
        foreach (var step in _steps)
        {
            if (ReferenceEquals(step.CustomSolver, solver))
                return step.Name;
        }

        return null;
    }

    /// <summary>
    ///     Gets the world that last registered an instance with solver commands, or <see langword="null" />.
    /// </summary>
    /// <remarks>
    ///     The world may have removed the stage since, so the caller checks <see cref="FindStageName" /> under that
    ///     world's gate.
    /// </remarks>
    internal static AtmosWorld? FindOwner(IAtmosWorldSolver solver)
    {
        return Owners.TryGetValue(solver, out WeakReference<AtmosWorld>? reference) &&
               reference.TryGetTarget(out AtmosWorld? owner)
            ? owner
            : null;
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
    /// <param name="interval">
    ///     How many ticks pass between runs of the stage. One runs it every tick. See
    ///     <see cref="AtmosWorldSolverContext.DeltaTime" /> for the time each run covers.
    /// </param>
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
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="interval" /> is less than one.</exception>
    /// <exception cref="KeyNotFoundException">The <paramref name="before" /> or <paramref name="after" /> stage is not registered.</exception>
    /// <exception cref="InvalidOperationException">
    ///     The world is recording or replaying, because Numos cannot recreate a host-registered stage on replay.
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
        string? after = null,
        int interval = 1)
    {
        ArgumentNullException.ThrowIfNull(solver);
        RegisterCore(name, new DelegateWorldSolver(solver), neighbors, before, after, interval);
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
    /// <param name="interval">How many ticks pass between runs of the stage. One runs it every tick.</param>
    /// <remarks>
    ///     <para>
    ///         Identical to the <see cref="AtmosWorldSolver" /> overload in placement, topology, and checkpoint metadata.
    ///         The instance is caller-owned and also receives <see cref="IAtmosWorldSolver.OnRestored" />.
    ///     </para>
    ///     <para>
    ///         An instance with <c>[NumosSerialize]</c> members can be in one live world at a time, because each
    ///         world's checkpoints would capture and overwrite the same fields. So can an instance with
    ///         <see cref="SolverCommandAttribute" /> methods, because a command records into the world that holds it.
    ///         Either can still be registered under two names in one world. Any other instance can be shared between
    ///         worlds.
    ///     </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="name" /> or <paramref name="solver" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException">
    ///     A name is blank, <paramref name="name" /> is already registered, or both <paramref name="before" /> and
    ///     <paramref name="after" /> are given.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="interval" /> is less than one.</exception>
    /// <exception cref="KeyNotFoundException">The <paramref name="before" /> or <paramref name="after" /> stage is not registered.</exception>
    /// <exception cref="InvalidOperationException">
    ///     The world is recording or replaying. Or <paramref name="solver" /> has <c>[NumosSerialize]</c> members or
    ///     solver commands and is registered in another world that is not disposed.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    [PublicAPI]
    public void Register(
        string name,
        IAtmosWorldSolver solver,
        AtmosNeighborSelection? neighbors = null,
        string? before = null,
        string? after = null,
        int interval = 1)
    {
        RegisterCore(name, solver, neighbors, before, after, interval);
    }

    /// <summary>
    ///     Creates the world's instance of a catalog solver and adds it as a stage.
    /// </summary>
    /// <typeparam name="T">A class marked <see cref="AtmosSolverAttribute" />.</typeparam>
    /// <param name="before">
    ///     The registered stage the new stage runs immediately before, or <see langword="null" /> for the declared
    ///     placement.
    /// </param>
    /// <param name="after">
    ///     The registered stage the new stage runs immediately after, or <see langword="null" /> for the declared
    ///     placement.
    /// </param>
    /// <param name="interval">The interval in ticks, or zero for the declared rate.</param>
    /// <returns>The new instance. The world owns it.</returns>
    /// <remarks>
    ///     <para>
    ///         The stage takes its name and neighbor selection from <typeparamref name="T" />'s
    ///         <see cref="AtmosSolverDescriptor" />. Passing <paramref name="before" /> or <paramref name="after" />
    ///         replaces the declared placement. A stage with no placement is appended to the end of the pipeline. A
    ///         declared <see cref="AtmosSolverDescriptor.RunsPerSecond" /> converts to ticks at the world's
    ///         <see cref="AtmosWorld.TickRate" />. A declaration with no rate runs every tick.
    ///     </para>
    ///     <para>
    ///         The call also adds the descriptor to <see cref="AtmosSolverCatalog" />, so replay and
    ///         <see cref="SetLayout" /> can find it by name.
    ///     </para>
    ///     <para>
    ///         This works during recording. Numos records one solver layout operation, and replay creates the stage
    ///         from the catalog at the same position. A new instance starts from its constructor state on replay too.
    ///         The call is rejected while a replay applies history.
    ///     </para>
    ///     <para>
    ///         The world disposes the instance, when it is <see cref="IDisposable" />, once the stage is removed or the
    ///         world is disposed. A stage removed during a tick, a checkpoint restore, or a replay is disposed when that
    ///         call ends, so the running tick can still call it and a failed restore or replay can put it back.
    ///     </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    ///     The stage name is a built-in stage name or already registered, or both <paramref name="before" /> and
    ///     <paramref name="after" /> are given, or one of them is blank.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="interval" /> is negative.</exception>
    /// <exception cref="KeyNotFoundException">The placement anchor is not registered.</exception>
    /// <exception cref="InvalidOperationException">
    ///     The world is replaying history, or the catalog holds a different class under the same name.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    /// <example>
    ///     <code>
    ///     FireSolver fire = world.Solvers.Add&lt;FireSolver&gt;();
    ///     world.Solvers.Add&lt;VentSolver&gt;(before: AtmosBuiltInSolvers.GasReactions, interval: 96);
    ///     </code>
    /// </example>
    [PublicAPI]
    public T Add<T>(string? before = null, string? after = null, int interval = 0)
        where T : class, IAtmosWorldSolver, IAtmosSolverDefinition<T>
    {
        AtmosSolverDescriptor descriptor = T.Descriptor;
        AtmosSolverCatalog.Register(descriptor);
        return (T)AddCore(descriptor, before, after, interval);
    }

    /// <summary>
    ///     Creates the world's instance of the catalog solver named <paramref name="name" /> and adds it as a stage.
    /// </summary>
    /// <param name="name">The stage name of an <see cref="AtmosSolverCatalog" /> entry.</param>
    /// <param name="before">
    ///     The registered stage the new stage runs immediately before, or <see langword="null" /> for the declared
    ///     placement.
    /// </param>
    /// <param name="after">
    ///     The registered stage the new stage runs immediately after, or <see langword="null" /> for the declared
    ///     placement.
    /// </param>
    /// <param name="interval">The interval in ticks, or zero for the declared rate.</param>
    /// <returns>The new instance. The world owns it.</returns>
    /// <remarks>
    ///     Behaves like <see cref="Add{T}" /> with the descriptor the catalog holds under <paramref name="name" />.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="name" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException">
    ///     <paramref name="name" /> is blank, a built-in stage name, or already registered. Or both
    ///     <paramref name="before" /> and <paramref name="after" /> are given, or one of them is blank.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="interval" /> is negative.</exception>
    /// <exception cref="KeyNotFoundException">
    ///     The catalog has no solver named <paramref name="name" />, or the placement anchor is not registered.
    /// </exception>
    /// <exception cref="InvalidOperationException">The world is replaying history.</exception>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    [PublicAPI]
    public IAtmosWorldSolver Add(string name, string? before = null, string? after = null, int interval = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!AtmosSolverCatalog.TryGet(name, out AtmosSolverDescriptor? descriptor))
        {
            throw new KeyNotFoundException(
                $"The solver catalog has no solver named '{name}'. Mark a solver class with [AtmosSolver(\"{name}\")] " +
                "so its assembly registers it when it loads, or call AtmosSolverCatalog.Register.");
        }

        return AddCore(descriptor, before, after, interval);
    }

    /// <summary>
    ///     Gets the registered solver instance of exactly type <typeparamref name="T" />.
    /// </summary>
    /// <typeparam name="T">The solver class. A derived class does not match.</typeparam>
    /// <returns>The instance.</returns>
    /// <remarks>
    ///     One instance registered under two names counts once.
    /// </remarks>
    /// <exception cref="KeyNotFoundException">No stage holds an instance of <typeparamref name="T" />.</exception>
    /// <exception cref="InvalidOperationException">Two or more instances of <typeparamref name="T" /> are registered.</exception>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    [PublicAPI]
    public T Get<T>() where T : class, IAtmosWorldSolver
    {
        if (TryGet(out T? solver))
            return solver;

        throw new KeyNotFoundException($"No world solver of type {typeof(T).FullName} is registered.");
    }

    /// <summary>
    ///     Finds the registered solver instance of exactly type <typeparamref name="T" />.
    /// </summary>
    /// <typeparam name="T">The solver class. A derived class does not match.</typeparam>
    /// <param name="solver">The instance, or <see langword="null" /> when none is registered.</param>
    /// <returns><see langword="true" /> when a stage holds an instance of <typeparamref name="T" />.</returns>
    /// <remarks>
    ///     One instance registered under two names counts once.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Two or more instances of <typeparamref name="T" /> are registered.</exception>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    [PublicAPI]
    public bool TryGet<T>([NotNullWhen(true)] out T? solver) where T : class, IAtmosWorldSolver
    {
        lock (_world.Gate)
        {
            _world.ThrowIfDisposedForPipeline();
            List<int> indices = IndicesOfType(typeof(T));
            solver = indices.Count == 0 ? null : (T)_steps[indices[0]].CustomSolver!;
            return solver is not null;
        }
    }

    /// <summary>
    ///     Finds the class solver a stage holds, with descriptors of its <c>[NumosSerialize]</c> members.
    /// </summary>
    /// <param name="name">The stage name.</param>
    /// <param name="solver">The solver instance, or <see langword="null" /> when the method returns false.</param>
    /// <param name="members">
    ///     One descriptor per marked member, in declaration order. The list is empty for a solver that marks nothing,
    ///     and when the method returns false.
    /// </param>
    /// <returns>
    ///     <see langword="true" /> when the stage exists and holds a class solver. A built-in stage or a delegate stage
    ///     returns <see langword="false" />.
    /// </returns>
    /// <remarks>
    ///     Read and write the members from host code between ticks. Numos records a write at the next recording
    ///     boundary, as it does for any other host edit to the solver.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="name" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException"><paramref name="name" /> is empty or whitespace.</exception>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    /// <example>
    ///     <code>
    ///     if (world.Solvers.TryGetState("my-mod/fire", out object? fire, out var members))
    ///     {
    ///         foreach (AtmosStateMember member in members)
    ///             Console.WriteLine($"{member.Name} = {member.GetValue(fire)} {member.Unit}");
    ///     }
    ///     </code>
    /// </example>
    [PublicAPI]
    public bool TryGetState(
        string name, [NotNullWhen(true)] out object? solver, out IReadOnlyList<AtmosStateMember> members)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_world.Gate)
        {
            _world.ThrowIfDisposedForPipeline();
            if (Find(name)?.CustomSolver is not { } custom || custom is DelegateWorldSolver)
            {
                solver = null;
                members = [];
                return false;
            }

            solver = custom;
            members = (custom as INumosStateDescribed)?.StateMembers ?? [];
            return true;
        }
    }

    /// <summary>
    ///     Removes the stage that holds the solver instance of exactly type <typeparamref name="T" />.
    /// </summary>
    /// <typeparam name="T">The solver class. A derived class does not match.</typeparam>
    /// <returns><see langword="true" /> when a stage was removed.</returns>
    /// <remarks>
    ///     <para>
    ///         Every stage that holds the instance is removed. A catalog stage from <see cref="Add{T}" /> can be
    ///         removed while recording, like <see cref="Unregister" />. The world then disposes its instance.
    ///     </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    ///     Two or more instances of <typeparamref name="T" /> are registered. Or the world is replaying history. Or the
    ///     stage is a host-registered stage and the world is recording.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    [PublicAPI]
    public bool Remove<T>() where T : class, IAtmosWorldSolver
    {
        lock (_world.Gate)
        {
            _world.ThrowIfDisposedForPipeline();
            List<int> indices = IndicesOfType(typeof(T));
            if (indices.Count == 0)
                return false;

            if (_steps[indices[0]].Descriptor is not null)
                return RemoveCatalogStage(indices[0]);

            _world.EnsureCanChangeWorldSolverDefinition();
            for (int index = indices.Count - 1; index >= 0; index--)
                _steps.RemoveAt(indices[index]);

            _world.InvalidateSolverDependencies();
            PublishSteps();
            return true;
        }
    }

    /// <summary>
    ///     Removes a stage by name, including a built-in stage.
    /// </summary>
    /// <param name="name">The registered stage name.</param>
    /// <returns>
    ///     <see langword="true" /> when a stage was removed. <see langword="false" /> when no stage has the name,
    ///     whether or not the world records or replays.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         Removing a built-in stage drops that physics from every subsequent tick. <see cref="ResetToDefaults()" />
    ///         or <see cref="SetLayout" /> brings it back.
    ///     </para>
    ///     <para>
    ///         Removing a catalog stage from <see cref="Add{T}" /> works during recording. Numos records one solver
    ///         layout operation and disposes the instance. Every other removal is rejected while the world records.
    ///     </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="name" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException"><paramref name="name" /> is empty or whitespace.</exception>
    /// <exception cref="InvalidOperationException">
    ///     The world is replaying history, or it is recording and the stage is not a catalog stage.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    [PublicAPI]
    public bool Unregister(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_world.Gate)
        {
            _world.ThrowIfDisposedForPipeline();
            int index = IndexOf(name);
            if (index < 0)
                return false;

            if (_steps[index].Descriptor is not null)
                return RemoveCatalogStage(index);

            _world.EnsureCanChangeWorldSolverDefinition();
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
            if (enabled)
                AlignSchedule(_steps[index]);

            PublishSteps();
            _world.RecordWorldSolverEnablement(name, enabled);
            return true;
        }
    }

    /// <summary>
    ///     Sets how many ticks pass between runs of a registered stage.
    /// </summary>
    /// <param name="name">The registered stage name.</param>
    /// <param name="interval">The new interval in ticks. One runs the stage every tick.</param>
    /// <returns><see langword="true" /> when the named stage exists.</returns>
    /// <remarks>
    ///     <para>
    ///         The stage runs on multiples of <paramref name="interval" />, the same ticks as a stage that always had
    ///         that interval. The next run is the first such tick after the current tick. The first run after the
    ///         change covers the time since the last run, so <see cref="AtmosWorldSolverContext.DeltaTime" /> stays
    ///         the real elapsed time.
    ///     </para>
    ///     <para>
    ///         Producer and consumer stages must share an interval. <c>derived-refresh</c> and <c>advection</c> hand
    ///         data over within a tick. So do <c>advection</c> and <c>boundary-flow</c>, and <c>thermodynamics</c>
    ///         and <c>thermal-boundary</c>. Numos does not
    ///         check this, because a host can replace the producer.
    ///     </para>
    ///     <para>
    ///         Unlike registration, this works during recording. A change records one solver layout operation that
    ///         carries every interval, and a call that changes nothing records nothing. A call from inside a solver
    ///         tick is not recorded, because replay reproduces it. The call is rejected while a replay applies history.
    ///     </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="name" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException"><paramref name="name" /> is empty or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="interval" /> is less than one.</exception>
    /// <exception cref="InvalidOperationException">The world is replaying history.</exception>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    /// <example>
    ///     <code>
    ///     world.Solvers.SetInterval(AtmosBuiltInSolvers.Thermodynamics, 4);
    ///     world.Solvers.SetInterval(AtmosBuiltInSolvers.ThermalBoundary, 4);
    ///     </code>
    /// </example>
    [PublicAPI]
    public bool SetInterval(string name, int interval)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfLessThan(interval, 1);
        lock (_world.Gate)
        {
            _world.ThrowIfDisposedForPipeline();
            _world.EnsureCanChangeSolverLayout();
            int index = IndexOf(name);
            if (index < 0)
                return false;

            if (_steps[index].Interval == interval)
                return true;

            ApplyInterval(_steps[index], interval);
            _world.RecordWorldSolverLayout(ResolveLayout());
            return true;
        }
    }

    /// <summary>
    ///     Replaces the order and enabled flags of the stage list in one step.
    /// </summary>
    /// <param name="stages">
    ///     Every stage in the new execution order, with its enabled flag and interval. An interval of zero keeps the
    ///     current interval of a registered stage and gives a new built-in or catalog stage its default.
    /// </param>
    /// <returns><see langword="true" /> when the layout changed, <see langword="false" /> when it already matched.</returns>
    /// <remarks>
    ///     <para>
    ///         Numos resolves each name in this order: a registered stage, a built-in stage name (see
    ///         <see cref="BuiltInStageNames" />), then an <see cref="AtmosSolverCatalog" /> entry. Numos creates a
    ///         built-in or catalog stage that is not registered. It drops a built-in stage that is not listed, and it
    ///         removes and disposes a catalog stage that is not listed. Every stage registered with <c>Register</c>
    ///         must appear, because only <see cref="Unregister" /> removes it. Registered custom stages keep their
    ///         solver instance, topology, and injected dependencies.
    ///     </para>
    ///     <para>
    ///         Unlike registration, this works during recording. A change records one operation, and a call that
    ///         changes nothing records nothing. A call from inside a solver tick is not recorded, because replay
    ///         reproduces it. The call is rejected while a replay applies history.
    ///     </para>
    ///     <para>
    ///         The recorded operation lists the resolved interval of every stage, so replay never depends on a
    ///         default. A rejected layout changes nothing.
    ///     </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="stages" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException">
    ///     A name is blank or listed twice, a name is neither a registered stage, a built-in stage, nor a catalog entry,
    ///     a stage registered with <c>Register</c> is missing, or an interval is negative.
    /// </exception>
    /// <exception cref="InvalidOperationException">The world is replaying history.</exception>
    /// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
    /// <example>
    ///     <code>
    ///     world.Solvers.SetLayout(
    ///     [
    ///         new(AtmosBuiltInSolvers.DerivedRefresh, true),
    ///         new(AtmosBuiltInSolvers.Advection, true),
    ///         new(AtmosBuiltInSolvers.VacuumCleanup, true),
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
            return ApplyAndRecordLayout(copy);
        }
    }

    /// <summary>
    ///     Installs a layout without recording it or checking the recording state, then releases the catalog stages it
    ///     removed (see <see cref="Release" />).
    /// </summary>
    /// <remarks>
    ///     Replay and checkpoint restore call this. The caller holds <see cref="AtmosWorld.Gate" />. Validation and the
    ///     creation of catalog stages finish before any change, so a rejected layout leaves the pipeline as it was.
    /// </remarks>
    /// <returns><see langword="true" /> when the order or an enabled flag changed.</returns>
    internal bool SetLayoutCore(IReadOnlyList<AtmosWorldSolverLayoutEntry> stages)
    {
        bool changed = ApplyLayout(stages, out List<IAtmosWorldSolver>? removed);
        Release(removed);
        return changed;
    }

    /// <summary>
    ///     Installs a layout, records it, then releases the catalog stages it removed (see <see cref="Release" />).
    /// </summary>
    /// <remarks>
    ///     The caller holds <see cref="AtmosWorld.Gate" /> and has checked that the layout can change. Disposal comes
    ///     after the record, so a throwing <see cref="IDisposable.Dispose" /> cannot leave a change unrecorded.
    /// </remarks>
    private bool ApplyAndRecordLayout(IReadOnlyList<AtmosWorldSolverLayoutEntry> stages)
    {
        if (!ApplyLayout(stages, out List<IAtmosWorldSolver>? removed))
            return false;

        _world.RecordWorldSolverLayout(ResolveLayout());
        Release(removed);
        return true;
    }

    /// <summary>
    ///     Installs a layout and returns the instances of the catalog stages it removed, which the caller disposes.
    /// </summary>
    private bool ApplyLayout(IReadOnlyList<AtmosWorldSolverLayoutEntry> stages, out List<IAtmosWorldSolver>? removed)
    {
        removed = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var next = new WorldSolverRegistration[stages.Count];
        var intervals = new int[stages.Count];
        var created = new bool[stages.Count];
        AtmosSolverDescriptor?[]? catalog = null;
        for (int index = 0; index < stages.Count; index++)
        {
            string name = stages[index].Name;
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException($"Layout entry {index} has a blank stage name.", nameof(stages));

            if (!seen.Add(name))
                throw new ArgumentException($"The layout lists stage '{name}' more than once.", nameof(stages));

            if (stages[index].Interval < 0)
                throw new ArgumentException($"Layout entry '{name}' has a negative interval.", nameof(stages));

            int existing = IndexOf(name);
            if (existing >= 0)
            {
                next[index] = _steps[existing];
                intervals[index] = stages[index].Interval == 0 ? next[index].Interval : stages[index].Interval;
            }
            else if (BuiltInStages.TryGetValue(name, out var builtIn))
            {
                intervals[index] = stages[index].Interval == 0
                    ? BuiltInStageSchedule.GetInterval(name, _world.TickRate)
                    : stages[index].Interval;
                next[index] = WorldSolverRegistration.CreateBuiltIn(builtIn, intervals[index]);
                created[index] = true;
            }
            else if (AtmosSolverCatalog.TryGet(name, out AtmosSolverDescriptor? descriptor))
            {
                // Created below, once the whole layout has passed validation.
                (catalog ??= new AtmosSolverDescriptor?[stages.Count])[index] = descriptor;
                intervals[index] = stages[index].Interval == 0 ? DefaultInterval(descriptor) : stages[index].Interval;
                created[index] = true;
            }
            else
            {
                throw new ArgumentException(
                    $"The layout names '{name}', which is neither a registered stage, a built-in stage, nor a solver " +
                    "catalog entry.",
                    nameof(stages));
            }
        }

        foreach (var step in _steps)
        {
            if (step.Kind == AtmosWorldSolverKind.Custom && step.Descriptor is null && !seen.Contains(step.Name))
            {
                throw new ArgumentException(
                    $"The layout omits custom stage '{step.Name}'. Call Unregister to remove a custom stage.",
                    nameof(stages));
            }
        }

        bool changed = catalog is not null || next.Length != _steps.Count;
        for (int index = 0; index < next.Length && !changed; index++)
        {
            changed = !ReferenceEquals(next[index], _steps[index]) ||
                      next[index].IsEnabled != stages[index].Enabled ||
                      next[index].Interval != intervals[index];
        }

        if (!changed)
            return false;

        if (catalog is not null)
            CreateCatalogStages(catalog, intervals, next);

        for (int index = 0; index < next.Length; index++)
        {
            var step = next[index];
            bool enabling = !step.IsEnabled && stages[index].Enabled;
            step.IsEnabled = stages[index].Enabled;
            if (created[index])
                AlignSchedule(step);
            else if (step.Interval != intervals[index])
                ApplyInterval(step, intervals[index]);

            if (enabling && !created[index])
                AlignSchedule(step);
        }

        foreach (var step in _steps)
        {
            if (step.Descriptor is not null && !seen.Contains(step.Name))
                (removed ??= []).Add(step.CustomSolver!);
        }

        _steps.Clear();
        _steps.AddRange(next);
        _world.InvalidateSolverDependencies();
        PublishSteps();
        return true;
    }

    /// <summary>
    ///     Creates the instance, registration, and topology of every catalog entry a layout names.
    /// </summary>
    /// <remarks>
    ///     Runs host code (the constructor and the neighbor selector) before the layout changes anything. A failure
    ///     disposes the instances already created and leaves <paramref name="next" /> unused.
    /// </remarks>
    private void CreateCatalogStages(AtmosSolverDescriptor?[] catalog, int[] intervals, WorldSolverRegistration[] next)
    {
        IReadOnlyList<ExplicitLinkDefinition>? links = null;
        IReadOnlyDictionary<AtmosSimulationId, Int3>? chunkDimensions = null;
        List<IAtmosWorldSolver>? instances = null;
        try
        {
            for (int index = 0; index < catalog.Length; index++)
            {
                if (catalog[index] is not { } descriptor)
                    continue;

                IAtmosWorldSolver solver = descriptor.Create() ??
                                           throw new InvalidOperationException(
                                               $"The factory of solver '{descriptor.Name}' returned null.");

                (instances ??= []).Add(solver);
                var registration = WorldSolverRegistration.CreateCustom(
                    descriptor.Name, solver, descriptor.Neighbors, intervals[index], descriptor);

                links ??= _world.GetActiveLinksCore();
                chunkDimensions ??= _world.GetChunkDimensionsCore();
                registration.InstallTopology(registration.BuildTopology(_world, links, chunkDimensions));

                // The baseline of the solver-state boundary. A stage created while recording starts from its
                // constructor state on replay too, so that state is not a host edit.
                if (registration.State is not null)
                {
                    registration.StateHash = registration.ComputeStateHash();
                    StateEditBaseline.RefreshBulk(registration.State, ref registration.BulkHashes);
                }

                // A new instance, so no other world holds it. Its commands find this world through the table.
                if (solver is IAtmosSolverCommands)
                {
                    lock (OwnerGate)
                    {
                        Owners.AddOrUpdate(solver, new WeakReference<AtmosWorld>(_world));
                    }
                }

                next[index] = registration;
            }
        }
        catch (Exception exception)
        {
            try
            {
                DisposeAll(instances);
            }
            catch (Exception disposeException)
            {
                throw new AggregateException(exception, disposeException);
            }

            throw;
        }
    }

    /// <summary>
    ///     Gets the interval of a catalog stage whose layout entry or <c>Add</c> call gives none.
    /// </summary>
    private int DefaultInterval(AtmosSolverDescriptor descriptor)
    {
        if (descriptor.Interval > 0)
            return descriptor.Interval;

        return descriptor.RunsPerSecond > 0 ? BuiltInStageSchedule.IntervalFor(descriptor.RunsPerSecond, _world.TickRate) : 1;
    }

    /// <summary>
    ///     Inserts a catalog stage through the layout path, records the layout, and returns the new instance.
    /// </summary>
    private IAtmosWorldSolver AddCore(AtmosSolverDescriptor descriptor, string? before, string? after, int interval)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(interval);
        if (before != null && after != null)
            throw new ArgumentException("A world solver can be placed before or after another stage, not both.", nameof(after));

        if (before == null && after == null)
        {
            before = descriptor.Before;
            after = descriptor.After;
        }

        string? anchor = before ?? after;
        if (anchor != null)
            ArgumentException.ThrowIfNullOrWhiteSpace(anchor, before != null ? nameof(before) : nameof(after));

        string name = descriptor.Name;
        lock (_world.Gate)
        {
            _world.ThrowIfDisposedForPipeline();
            _world.EnsureCanChangeSolverLayout();
            if (BuiltInStages.ContainsKey(name))
                throw new ArgumentException($"'{name}' is a built-in stage name.", nameof(descriptor));

            if (IndexOf(name) >= 0)
                throw new ArgumentException($"A world solver named '{name}' is already registered.", nameof(descriptor));

            int insertionIndex = _steps.Count;
            if (anchor != null)
            {
                int anchorIndex = IndexOf(anchor);
                if (anchorIndex < 0)
                    throw new KeyNotFoundException($"No world solver named '{anchor}' is registered.");

                insertionIndex = before != null ? anchorIndex : anchorIndex + 1;
            }

            var layout = new List<AtmosWorldSolverLayoutEntry>(ResolveLayout());
            layout.Insert(
                insertionIndex,
                new AtmosWorldSolverLayoutEntry(name, true, interval == 0 ? DefaultInterval(descriptor) : interval));
            ApplyAndRecordLayout(layout);
            return Find(name)!.CustomSolver!;
        }
    }

    /// <summary>
    ///     Removes a catalog stage through the layout path, records the layout, and disposes the instance.
    /// </summary>
    /// <remarks>The caller holds <see cref="AtmosWorld.Gate" />.</remarks>
    private bool RemoveCatalogStage(int index)
    {
        _world.EnsureCanChangeSolverLayout();
        var layout = new List<AtmosWorldSolverLayoutEntry>(ResolveLayout());
        layout.RemoveAt(index);
        return ApplyAndRecordLayout(layout);
    }

    /// <summary>
    ///     Lists the indices of the stages whose solver is exactly <paramref name="type" />.
    /// </summary>
    /// <remarks>The caller holds <see cref="AtmosWorld.Gate" />.</remarks>
    /// <exception cref="InvalidOperationException">The stages hold two or more distinct instances.</exception>
    private List<int> IndicesOfType(Type type)
    {
        var indices = new List<int>();
        IAtmosWorldSolver? found = null;
        for (int index = 0; index < _steps.Count; index++)
        {
            IAtmosWorldSolver? solver = _steps[index].CustomSolver;
            if (solver is null || solver.GetType() != type)
                continue;

            if (found is not null && !ReferenceEquals(found, solver))
            {
                throw new InvalidOperationException(
                    $"More than one instance of {type.FullName} is registered, in stages '{_steps[indices[0]].Name}' " +
                    $"and '{_steps[index].Name}'.");
            }

            found = solver;
            indices.Add(index);
        }

        return indices;
    }

    /// <summary>
    ///     Disposes each instance that is <see cref="IDisposable" />, then rethrows the first failure.
    /// </summary>
    /// <remarks>
    ///     One failure does not stop the rest. Two or more failures become one <see cref="AggregateException" />.
    /// </remarks>
    internal static void DisposeAll(List<IAtmosWorldSolver>? solvers)
    {
        if (solvers is null)
            return;

        List<Exception>? errors = null;
        foreach (var solver in solvers)
        {
            if (solver is not IDisposable disposable)
                continue;

            try
            {
                disposable.Dispose();
            }
            catch (Exception exception)
            {
                (errors ??= []).Add(exception);
            }
        }

        if (errors is null)
            return;

        if (errors.Count == 1)
            ExceptionDispatchInfo.Throw(errors[0]);

        throw new AggregateException(errors);
    }

    /// <summary>
    ///     Disposes removed catalog instances now, or hands them to the open deferral.
    /// </summary>
    /// <remarks>The caller holds <see cref="AtmosWorld.Gate" />.</remarks>
    private void Release(List<IAtmosWorldSolver>? removed)
    {
        if (removed is null)
            return;

        if (_disposalDeferrals == 0)
        {
            DisposeAll(removed);
            return;
        }

        _deferredDisposals ??= [];
        foreach (var solver in removed)
        {
            if (!_deferredDisposals.Contains(solver, ReferenceEqualityComparer.Instance))
                _deferredDisposals.Add(solver);
        }
    }

    /// <summary>
    ///     Holds back the disposal of catalog instances that stages lose until the matching
    ///     <see cref="EndDeferredDisposal" />.
    /// </summary>
    /// <remarks>
    ///     A tick, a checkpoint restore, and a replay open one. Deferrals nest, and only the outermost one disposes. The
    ///     caller holds <see cref="AtmosWorld.Gate" /> until the matching end.
    /// </remarks>
    internal void BeginDeferredDisposal()
    {
        _disposalDeferrals++;
    }

    /// <summary>
    ///     Closes a deferral. The outermost one disposes every deferred instance that no stage holds any more.
    /// </summary>
    /// <param name="failure">
    ///     The exception that ended the guarded work, or <see langword="null" /> when it completed. A disposal failure
    ///     during a failure throws an <see cref="AggregateException" /> that holds both.
    /// </param>
    /// <remarks>
    ///     An instance a rollback put back into the pipeline is held again, so it survives. The caller holds
    ///     <see cref="AtmosWorld.Gate" />.
    /// </remarks>
    internal void EndDeferredDisposal(Exception? failure = null)
    {
        if (--_disposalDeferrals > 0 || _deferredDisposals is null)
            return;

        List<IAtmosWorldSolver> deferred = _deferredDisposals;
        _deferredDisposals = null;
        deferred.RemoveAll(solver => _steps.Exists(step => ReferenceEquals(step.CustomSolver, solver)));
        if (failure is null)
        {
            DisposeAll(deferred);
            return;
        }

        try
        {
            DisposeAll(deferred);
        }
        catch (Exception disposeException)
        {
            throw new AggregateException(failure, disposeException);
        }
    }

    /// <summary>
    ///     Lists the instances of every catalog stage, for the world to dispose.
    /// </summary>
    /// <remarks>The caller holds <see cref="AtmosWorld.Gate" />.</remarks>
    internal List<IAtmosWorldSolver>? CollectCatalogSolvers()
    {
        List<IAtmosWorldSolver>? owned = null;
        foreach (var step in _steps)
        {
            if (step.Descriptor is not null)
                (owned ??= []).Add(step.CustomSolver!);
        }

        return owned;
    }

    /// <summary>
    ///     Lists every stage with its enabled flag and resolved interval, in execution order.
    /// </summary>
    /// <remarks>The caller holds <see cref="AtmosWorld.Gate" />.</remarks>
    internal AtmosWorldSolverLayoutEntry[] ResolveLayout()
    {
        var layout = new AtmosWorldSolverLayoutEntry[_steps.Count];
        for (int index = 0; index < layout.Length; index++)
            layout[index] = new AtmosWorldSolverLayoutEntry(_steps[index].Name, _steps[index].IsEnabled, _steps[index].Interval);

        return layout;
    }

    /// <summary>
    ///     Captures the identity, interval, and schedule state of every stage for a world checkpoint.
    /// </summary>
    internal AtmosWorldSolverCheckpoint[] CaptureCheckpoints()
    {
        lock (_world.Gate)
        {
            var result = new AtmosWorldSolverCheckpoint[_steps.Count];
            for (int index = 0; index < result.Length; index++)
            {
                var step = _steps[index];
                result[index] = new AtmosWorldSolverCheckpoint(
                    step.Name,
                    step.Kind,
                    step.IsEnabled,
                    step.Selection?.Key,
                    step.Interval,
                    step.NextDueTick,
                    step.LastRunTick);
            }

            return result;
        }
    }

    /// <summary>
    ///     Installs the schedule state of a checkpoint on the stages <see cref="SetLayoutCore" /> just arranged.
    /// </summary>
    /// <remarks>The caller holds <see cref="AtmosWorld.Gate" /> and has validated that every name is registered.</remarks>
    internal void RestoreSchedules(IReadOnlyList<AtmosWorldSolverCheckpoint> saved)
    {
        foreach (var entry in saved)
        {
            var step = Find(entry.Name)!;
            step.Interval = entry.Interval;
            step.NextDueTick = entry.NextDueTick;
            step.LastRunTick = entry.LastRunTick;
        }
    }

    /// <summary>
    ///     Starts a stage's schedule on the first tick that is a multiple of its interval.
    /// </summary>
    /// <remarks>
    ///     A new or re-enabled stage has no meaningful last run. Starting on the interval grid gives it the nominal
    ///     <c>interval / tickRate</c> dt on its first run and keeps the cadence <c>tick % interval == 0</c> of a stage
    ///     that never changed interval.
    /// </remarks>
    private void AlignSchedule(WorldSolverRegistration step)
    {
        long due = FirstGridTick(step.Interval);
        step.NextDueTick = due;
        step.LastRunTick = due - step.Interval;
    }

    /// <summary>
    ///     Moves a stage to a new interval and puts it on the new interval grid.
    /// </summary>
    /// <remarks>
    ///     <see cref="WorldSolverRegistration.LastRunTick" /> stays the real last run, so the first
    ///     <c>DeltaTime</c> after the change covers the real gap. A stage off the grid would run on different ticks
    ///     than its paired stage, and the pair hands boundary batches over only on the tick they were made.
    /// </remarks>
    private void ApplyInterval(WorldSolverRegistration step, int interval)
    {
        step.Interval = interval;
        step.NextDueTick = FirstGridTick(interval);
    }

    /// <summary>
    ///     Returns the first multiple of <paramref name="interval" /> that the next tick pass can still run.
    /// </summary>
    private long FirstGridTick(int interval)
    {
        // A change made from inside a tick misses the current tick, because the tick runs a fixed stage list.
        long first = (long)_world.TickCount + (_world.IsTickExecuting ? 2 : 1);
        return (first + interval - 1) / interval * interval;
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
    ///         <c>vacuum-cleanup</c>, <c>explicit-gas-transport</c>, <c>thermodynamics</c>,
    ///         <c>explicit-thermal-transport</c>, <c>thermal-boundary</c>, and <c>gas-reactions</c>.
    ///         <see cref="AtmosTransportModel.Legacy" /> replaces <c>euler-flow</c> with <c>derived-refresh</c> and
    ///         <c>advection</c>. It also adds <c>boundary-flow</c> after <c>explicit-gas-transport</c>.
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
            List<IAtmosWorldSolver>? removed = CollectCatalogSolvers();
            _steps.Clear();
            AddDefaults(model);
            _world.InvalidateSolverDependencies();
            PublishSteps();
            Release(removed);
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

    /// <summary>
    ///     Reports whether any of the stages is due on the next tick.
    /// </summary>
    internal bool AnyDueOnNextTick(WorldSolverRegistration[] steps)
    {
        long tick = (long)_world.TickCount + 1;
        foreach (var step in steps)
        {
            if (tick >= step.NextDueTick)
                return true;
        }

        return false;
    }

    /// <summary>
    ///     Runs the stages that are due on the tick now executing.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A stage is due when the one-based tick has reached its next due tick. Execution advances the schedule
    ///         before the stage runs, so a stage that calls <see cref="SetInterval" /> on itself measures from this
    ///         run.
    ///     </para>
    ///     <para>
    ///         Before a due stage runs, every awake chunk of every simulation advances its revision, unless the stage
    ///         marks its own writes (<see cref="WorldSolverRegistration.MarksOwnChanges" />). Custom stages always
    ///         get the bump, because Numos cannot see which chunks they write.
    ///     </para>
    ///     <para>
    ///         After each stage, also one that throws, Numos repairs the chunks and mixture pages that the stage opened
    ///         with <see cref="AtmosSolverChunk.Edit" /> or <see cref="AtmosSolverGasMixturePage.Edit" />. A stage that
    ///         opened none pays one flag read.
    ///     </para>
    /// </remarks>
    internal void Execute(WorldSolverRegistration[] steps, AtmosSolverTickScope scope)
    {
        long tick = (long)_world.TickCount + 1;
        int tickRate = _world.TickRate;
        foreach (var step in steps)
        {
            if (tick < step.NextDueTick)
                continue;

            float deltaTime = BuiltInStageSchedule.GapTime(tick - step.LastRunTick, tickRate);
            scope.BeginStage(step.Interval, deltaTime, step.NameHash);
            step.NextDueTick = tick + step.Interval;
            step.LastRunTick = tick;
            if (!step.MarksOwnChanges)
                scope.MarkAwakeChunksChanged();

            try
            {
                step.Execute(scope);
            }
            finally
            {
                scope.RepairSolverEdits();
            }
        }
    }

    /// <summary>
    ///     Compiles one topology per stage without changing any stage.
    /// </summary>
    /// <param name="links">The explicit links that are active in the topology being built.</param>
    /// <param name="chunkDimensions">The chunk dimensions of the simulations the links can name.</param>
    /// <returns>The topologies in stage order, for <see cref="InstallTopologies" />.</returns>
    /// <remarks>
    ///     A host selector can throw, and then no stage has changed. The caller holds <see cref="AtmosWorld.Gate" />.
    /// </remarks>
    internal AtmosWorldNeighborTopology[] CompileTopologies(
        IReadOnlyList<ExplicitLinkDefinition> links,
        IReadOnlyDictionary<AtmosSimulationId, Int3> chunkDimensions)
    {
        var compiled = new AtmosWorldNeighborTopology[_steps.Count];
        for (int index = 0; index < _steps.Count; index++)
            compiled[index] = _steps[index].BuildTopology(_world, links, chunkDimensions);

        return compiled;
    }

    /// <summary>
    ///     Gives every stage the topology that <see cref="CompileTopologies" /> built for it.
    /// </summary>
    /// <remarks>
    ///     The stage list must not have changed since the compile. The caller holds <see cref="AtmosWorld.Gate" />.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The stage count differs from the compiled count.</exception>
    internal void InstallTopologies(AtmosWorldNeighborTopology[] compiled)
    {
        if (compiled.Length != _steps.Count)
            throw new InvalidOperationException("The solver stages changed after the topology was compiled.");

        for (int index = 0; index < _steps.Count; index++)
            _steps[index].InstallTopology(compiled[index]);
    }

    /// <summary>
    ///     Captures the stage list and every stage's schedule, for <see cref="RestoreLayoutSnapshot" />.
    /// </summary>
    /// <remarks>The caller holds <see cref="AtmosWorld.Gate" />.</remarks>
    internal LayoutSnapshot CaptureLayoutSnapshot()
    {
        var steps = _steps.ToArray();
        var states = new StepSchedule[steps.Length];
        for (int index = 0; index < steps.Length; index++)
        {
            var step = steps[index];
            states[index] = new StepSchedule(step.IsEnabled, step.Interval, step.NextDueTick, step.LastRunTick);
        }

        return new LayoutSnapshot(steps, states);
    }

    /// <summary>
    ///     Puts back the stage list and schedules that <see cref="CaptureLayoutSnapshot" /> took.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A layout change can enable or reschedule a stage that it keeps, and it drops the stages it does not list.
    ///         This brings the same registration objects back with their old fields. A catalog stage created since the
    ///         snapshot is released like a removed one.
    ///     </para>
    ///     <para>
    ///         The caller holds <see cref="AtmosWorld.Gate" /> and a deferral (<see cref="BeginDeferredDisposal" />)
    ///         that was open when the snapshot was taken. Without it, a stage removed in between would already be
    ///         disposed, and this would bring the disposed instance back.
    ///     </para>
    /// </remarks>
    internal void RestoreLayoutSnapshot(LayoutSnapshot snapshot)
    {
        Debug.Assert(_disposalDeferrals > 0, "A layout snapshot is restored only inside a disposal deferral.");
        List<IAtmosWorldSolver>? dropped = null;
        foreach (var step in _steps)
        {
            if (step.Descriptor is not null && Array.IndexOf(snapshot.Steps, step) < 0)
                (dropped ??= []).Add(step.CustomSolver!);
        }

        for (int index = 0; index < snapshot.Steps.Length; index++)
        {
            var step = snapshot.Steps[index];
            var state = snapshot.Schedules[index];
            step.IsEnabled = state.IsEnabled;
            step.Interval = state.Interval;
            step.NextDueTick = state.NextDueTick;
            step.LastRunTick = state.LastRunTick;
        }

        _steps.Clear();
        _steps.AddRange(snapshot.Steps);
        _world.InvalidateSolverDependencies();
        PublishSteps();
        Release(dropped);
    }

    /// <summary>
    ///     The stage registrations in execution order and the mutable fields of each.
    /// </summary>
    internal sealed class LayoutSnapshot
    {
        internal LayoutSnapshot(WorldSolverRegistration[] steps, StepSchedule[] schedules)
        {
            Steps = steps;
            Schedules = schedules;
        }

        internal WorldSolverRegistration[] Steps { get; }

        internal StepSchedule[] Schedules { get; }
    }

    internal readonly record struct StepSchedule(bool IsEnabled, int Interval, long NextDueTick, long LastRunTick);

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
        string? after,
        int interval)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(solver);
        ArgumentOutOfRangeException.ThrowIfLessThan(interval, 1);
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

            var registration = WorldSolverRegistration.CreateCustom(name, solver, selection, interval);
            AlignSchedule(registration);
            registration.InstallTopology(
                registration.BuildTopology(_world, _world.GetActiveLinksCore(), _world.GetChunkDimensionsCore()));

            if (!IsOwned(registration))
            {
                Insert(insertionIndex, registration);
                return;
            }

            // Under the owner gate from the check through the publish, so two worlds cannot both take the instance.
            lock (OwnerGate)
            {
                ThrowIfOwnedElsewhere(solver);
                Insert(insertionIndex, registration);
                Owners.AddOrUpdate(solver, new WeakReference<AtmosWorld>(_world));
            }
        }
    }

    private void Insert(int index, WorldSolverRegistration registration)
    {
        _steps.Insert(index, registration);
        _world.InvalidateSolverDependencies();
        PublishSteps();
    }

    /// <summary>
    ///     Gets whether a stage's instance can be in only one live world: it has state or solver commands.
    /// </summary>
    private static bool IsOwned(WorldSolverRegistration step)
    {
        return step.State is not null || step.CustomSolver is IAtmosSolverCommands;
    }

    /// <summary>
    ///     Rejects an owned instance that a live world other than this one holds. The caller holds
    ///     <see cref="OwnerGate" />.
    /// </summary>
    private void ThrowIfOwnedElsewhere(IAtmosWorldSolver solver)
    {
        if (!Owners.TryGetValue(solver, out WeakReference<AtmosWorld>? reference) ||
            !reference.TryGetTarget(out AtmosWorld? owner) ||
            ReferenceEquals(owner, _world) ||
            owner.IsDisposed ||
            !owner.Solvers.HoldsOwnedInstance(solver))
        {
            return;
        }

        throw new InvalidOperationException(
            $"This {solver.GetType().FullName} instance has [NumosSerialize] state or solver commands and is already " +
            "registered in another world. Create one instance per world, or unregister it from the other world first.");
    }

    /// <summary>
    ///     Reports whether a published stage holds <paramref name="solver" />, without taking the world's gate.
    /// </summary>
    private bool HoldsOwnedInstance(IAtmosWorldSolver solver)
    {
        foreach (var step in Volatile.Read(ref _ownedSteps))
        {
            if (ReferenceEquals(step.CustomSolver, solver))
                return true;
        }

        return false;
    }

    private static Dictionary<string, BuiltInWorldStage> CreateBuiltInStages()
    {
        var stages = new Dictionary<string, BuiltInWorldStage>(BuiltInStageTable.All.Length, StringComparer.Ordinal);
        foreach (BuiltInStage stage in BuiltInStageTable.All)
            stages[stage.Name] = new BuiltInWorldStage(stage, GetWorldSolver(stage.Name));

        return stages;
    }

    /// <summary>
    ///     Gets the world method that runs a built-in stage across every simulation.
    /// </summary>
    /// <exception cref="UnreachableException"><paramref name="name" /> is a table entry without a world method.</exception>
    private static Action<AtmosSolverTickScope> GetWorldSolver(string name)
    {
        return name switch
        {
            AtmosBuiltInSolvers.DerivedRefresh => static scope => scope.World.SolveDerivedRefreshStage(scope),
            AtmosBuiltInSolvers.EulerFlow => static scope => scope.World.SolveEulerFlowStage(scope),
            AtmosBuiltInSolvers.Advection => static scope => scope.World.SolveAdvectionStage(scope),
            AtmosBuiltInSolvers.VacuumCleanup => static scope => scope.World.SolveVacuumCleanupStage(scope),
            AtmosBuiltInSolvers.ExplicitGasTransport => static scope => scope.World.SolveExplicitGasTransportStage(scope),
            AtmosBuiltInSolvers.BoundaryFlow => static scope => scope.World.SolveBoundaryFlowStage(scope),
            AtmosBuiltInSolvers.Thermodynamics => static scope => scope.World.SolveThermodynamicsStage(scope),
            AtmosBuiltInSolvers.ExplicitThermalTransport =>
                static scope => scope.World.SolveExplicitThermalTransportStage(scope),
            AtmosBuiltInSolvers.ThermalBoundary => static scope => scope.World.SolveThermalBoundaryStage(scope),
            AtmosBuiltInSolvers.GasReactions => static scope => scope.World.SolveGasReactionsStage(scope),
            _ => throw new UnreachableException($"Built-in stage '{name}' has no world method.")
        };
    }

    private static ReadOnlyCollection<string> CreateDefaultLayout(AtmosTransportModel model)
    {
        return Array.AsReadOnly(BuiltInStageTable.GetLayout(model).Select(static stage => stage.Name).ToArray());
    }

    private int IndexOf(string name)
    {
        return _steps.FindIndex(step => string.Equals(step.Name, name, StringComparison.Ordinal));
    }

    private void AddDefaults(AtmosTransportModel model)
    {
        foreach (string name in GetDefaultLayout(model))
        {
            var step = WorldSolverRegistration.CreateBuiltIn(
                BuiltInStages[name],
                BuiltInStageSchedule.GetInterval(name, _world.TickRate));
            AlignSchedule(step);
            _steps.Add(step);
        }
    }

    private void PublishSteps()
    {
        _enabledSteps = _steps.Where(static step => step.IsEnabled).ToArray();

        _statefulSteps = _steps.Where(static step => step.State is not null).ToArray();

        // Another world's registration reads this without the gate (HoldsOwnedInstance).
        Volatile.Write(ref _ownedSteps, _steps.Where(IsOwned).ToArray());
        _injectedEpoch = -1;
    }
}

/// <summary>
///     A <see cref="BuiltInStageTable" /> entry with the world method that runs it.
/// </summary>
/// <param name="Stage">The table entry.</param>
/// <param name="Solve">Runs the stage on every simulation of the world.</param>
internal sealed record BuiltInWorldStage(BuiltInStage Stage, Action<AtmosSolverTickScope> Solve);

internal sealed class WorldSolverRegistration
{
    private readonly Action<AtmosSolverTickScope>? _builtInSolver;
    private AtmosWorldNeighborTopology _topology = AtmosWorldNeighborTopology.Empty;

    private WorldSolverRegistration(
        string name,
        AtmosWorldSolverKind kind,
        Action<AtmosSolverTickScope>? builtInSolver,
        IAtmosWorldSolver? customSolver,
        AtmosNeighborSelection? selection,
        int interval,
        AtmosSolverDescriptor? descriptor,
        bool marksOwnChanges)
    {
        Interval = interval;
        Name = name;
        NameHash = AtmosRandom.HashStageName(name);
        Kind = kind;
        Descriptor = descriptor;
        MarksOwnChanges = marksOwnChanges;
        _builtInSolver = builtInSolver;
        CustomSolver = customSolver;
        Selection = selection;
        State = customSolver as INumosStateTarget;
    }

    internal string Name { get; }

    /// <summary>
    ///     Gets <see cref="AtmosRandom.HashStageName" /> of <see cref="Name" />, computed once so that
    ///     <see cref="AtmosWorldSolverContext.Random" /> does not hash the name on every call.
    /// </summary>
    internal ulong NameHash { get; }

    internal AtmosWorldSolverKind Kind { get; }
    internal AtmosNeighborSelection? Selection { get; }
    internal bool IsEnabled { get; set; } = true;

    /// <summary>
    ///     Gets the catalog descriptor the stage was created from, or <see langword="null" /> for a built-in stage or a
    ///     host registration.
    /// </summary>
    /// <remarks>
    ///     A stage with a descriptor is a catalog stage. The world owns its instance, and replay can recreate it by name.
    /// </remarks>
    internal AtmosSolverDescriptor? Descriptor { get; }

    /// <summary>
    ///     Gets whether the stage advances the revision of every chunk it writes, so the pipeline can skip the
    ///     revision bump of every awake chunk before it runs.
    /// </summary>
    /// <remarks>
    ///     <see cref="BuiltInStage.MarksOwnChanges" /> sets it, so only <c>euler-flow</c> and <c>vacuum-cleanup</c>
    ///     have it. Custom stages never do. A chunk that
    ///     <c>euler-flow</c> skips as static keeps its revision, so a renderer does not copy it again. That stage
    ///     also reuses its static test of a chunk while the revision holds, so a blanket bump would force a rescan of
    ///     every awake chunk on every call. <c>vacuum-cleanup</c> advances the revision of each chunk it empties
    ///     voxels in and of each sleeping chunk whose timer it clears. It leaves the rest alone for the same reason.
    /// </remarks>
    internal bool MarksOwnChanges { get; }

    /// <summary>
    ///     Gets or sets how many ticks pass between runs.
    /// </summary>
    internal int Interval { get; set; }

    /// <summary>
    ///     Gets or sets the first one-based tick on which the stage runs again.
    /// </summary>
    internal long NextDueTick { get; set; }

    /// <summary>
    ///     Gets or sets the one-based tick of the latest run, or the grid tick before the first run.
    /// </summary>
    internal long LastRunTick { get; set; }

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
    ///     The Debug-only baseline of the members marked <c>[NumosSerialize(HostEditable = false)]</c>, or
    ///     <see langword="null" />.
    /// </summary>
    internal ulong[]? BulkHashes;

    /// <summary>
    ///     Hashes the host-editable members of <see cref="State" /> without buffering them. Only valid for a stateful
    ///     registration.
    /// </summary>
    internal ulong ComputeStateHash()
    {
        return StateEditBaseline.HashHostEditable(State!);
    }

    /// <summary>
    ///     Writes <see cref="State" /> into a fresh array. Only valid for a stateful registration.
    /// </summary>
    internal byte[] CaptureState(ref StateBinarySink sink)
    {
        return StateEditBaseline.Capture(State!, ref sink);
    }

    /// <summary>
    ///     Reads a payload from <see cref="CaptureState" /> back into the solver.
    /// </summary>
    internal void RestoreState(ReadOnlySpan<byte> payload, IGasRegistry gases)
    {
        StateEditBaseline.Restore(State!, payload, gases, $"world solver '{Name}'");
    }

    internal static WorldSolverRegistration CreateBuiltIn(BuiltInWorldStage stage, int interval)
    {
        return new WorldSolverRegistration(
            stage.Stage.Name, AtmosWorldSolverKind.BuiltIn, stage.Solve, null, null, interval, null,
            stage.Stage.MarksOwnChanges);
    }

    internal static WorldSolverRegistration CreateCustom(
        string name,
        IAtmosWorldSolver solver,
        AtmosNeighborSelection? selection,
        int interval,
        AtmosSolverDescriptor? descriptor = null)
    {
        return new WorldSolverRegistration(
            name, AtmosWorldSolverKind.Custom, null, solver, selection, interval, descriptor, false);
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
        if (CustomSolver is null or DelegateWorldSolver)
            return;

        // Scratch resets first, so OnRestored sees each scratch member as a new instance holds it.
        if (CustomSolver is INumosScratchTarget scratch)
            scratch.ResetScratch();

        CustomSolver.OnRestored();
    }

    internal AtmosWorldNeighborTopology BuildTopology(
        AtmosWorld world,
        IReadOnlyList<ExplicitLinkDefinition> links,
        IReadOnlyDictionary<AtmosSimulationId, Int3> chunkDimensions)
    {
        return Selection == null
            ? AtmosWorldNeighborTopology.EmptyFor(world)
            : AtmosWorldNeighborTopology.Compile(world, Selection, links, chunkDimensions);
    }

    internal void InstallTopology(AtmosWorldNeighborTopology topology)
    {
        _topology = topology;
    }

    internal AtmosWorldSolverStep ToPublic()
    {
        return new AtmosWorldSolverStep(Name, Kind, IsEnabled, Selection?.Key, Interval);
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