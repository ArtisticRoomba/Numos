using JetBrains.Annotations;
using Numos.CoreSim.Replay;
using Numos.CoreSim.Solvers;

namespace Numos.API;

/// <summary>
///     Owns the ordered solver stages executed for every fixed world tick.
/// </summary>
/// <remarks>
///     The enabled stage list is captured before each tick. Pipeline edits made by a running stage take effect on the
///     next tick. Solver delegates remain caller-owned.
/// </remarks>
public sealed class AtmosWorldSolverPipeline
{
    private readonly List<WorldSolverRegistration> _steps = [];
    private readonly AtmosWorld _world;
    private AtmosSolverCheckpoint[] _checkpointSteps = [];

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
    /// <exception cref="ArgumentException">The name is empty or duplicated.</exception>
    [PublicAPI]
    public void Register(string name, AtmosWorldSolver solver)
    {
        RegisterCore(name, solver, null, -1);
    }

    /// <summary>
    ///     Registers a custom stage with a topology view compiled for its selection policy.
    /// </summary>
    /// <param name="name">A nonempty name unique within the pipeline.</param>
    /// <param name="selection">The Cartesian and explicit adjacency included in the callback's view.</param>
    /// <param name="solver">The caller-owned callback.</param>
    /// <exception cref="ArgumentException">The name is empty or duplicated.</exception>
    [PublicAPI]
    public void RegisterNeighborSolver(
        string name,
        AtmosNeighborSelection selection,
        AtmosWorldSolver solver)
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
    [PublicAPI]
    public void RegisterBefore(string existingName, string name, AtmosWorldSolver solver)
    {
        RegisterRelative(existingName, name, solver, null, true);
    }

    /// <summary>
    ///     Registers a neighbor-aware custom stage immediately before another registered stage.
    /// </summary>
    /// <param name="existingName">The name of the stage that will follow the new stage.</param>
    /// <param name="name">A nonempty name unique within the pipeline.</param>
    /// <param name="selection">The Cartesian and explicit adjacency included in the callback's view.</param>
    /// <param name="solver">The caller-owned callback.</param>
    [PublicAPI]
    public void RegisterNeighborSolverBefore(
        string existingName,
        string name,
        AtmosNeighborSelection selection,
        AtmosWorldSolver solver)
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
    [PublicAPI]
    public void RegisterAfter(string existingName, string name, AtmosWorldSolver solver)
    {
        RegisterRelative(existingName, name, solver, null, false);
    }

    /// <summary>
    ///     Registers a neighbor-aware custom stage immediately after another registered stage.
    /// </summary>
    /// <param name="existingName">The name of the stage that will precede the new stage.</param>
    /// <param name="name">A nonempty name unique within the pipeline.</param>
    /// <param name="selection">The Cartesian and explicit adjacency included in the callback's view.</param>
    /// <param name="solver">The caller-owned callback.</param>
    [PublicAPI]
    public void RegisterNeighborSolverAfter(
        string existingName,
        string name,
        AtmosNeighborSelection selection,
        AtmosWorldSolver solver)
    {
        ArgumentNullException.ThrowIfNull(selection);
        RegisterRelative(existingName, name, solver, selection, false);
    }

    /// <summary>
    ///     Removes a stage by name, including a built-in stage.
    /// </summary>
    /// <param name="name">The registered stage name.</param>
    /// <returns><see langword="true" /> when a stage was removed.</returns>
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
    ///     Restores the three built-in stages in their default order and enabled state.
    /// </summary>
    [PublicAPI]
    public void ResetToDefaults()
    {
        lock (_world.Gate)
        {
            _world.ThrowIfDisposedForPipeline();
            _world.EnsureCanChangeWorldSolverDefinition();
            _steps.Clear();
            AddDefaults();
            PublishCheckpointSteps();
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

    private void RegisterCore(
        string name,
        AtmosWorldSolver solver,
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
            PublishCheckpointSteps();
        }
    }

    private void RegisterRelative(
        string existingName,
        string name,
        AtmosWorldSolver solver,
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
    IReadOnlyList<AtmosSimulation> publicSimulations)
{
    internal AtmosWorld World { get; } = world;
    internal AtmosSimulation[] Simulations { get; } = simulations;
    internal AtmosWorldTickExecution[] Executions { get; } = executions;
    internal IReadOnlyList<AtmosSimulation> PublicSimulations { get; } = publicSimulations;
}

internal sealed class WorldSolverRegistration
{
    private readonly Action<AtmosWorldExecutionContext>? _builtInSolver;
    private readonly AtmosWorldSolver? _customSolver;
    private AtmosWorldNeighborTopology _topology = AtmosWorldNeighborTopology.Empty;

    private WorldSolverRegistration(
        string name,
        AtmosWorldSolverKind kind,
        Action<AtmosWorldExecutionContext>? builtInSolver,
        AtmosWorldSolver? customSolver,
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

    internal static WorldSolverRegistration CreateBuiltIn(
        string name,
        Action<AtmosWorldExecutionContext> solver)
    {
        return new WorldSolverRegistration(name, AtmosWorldSolverKind.BuiltIn, solver, null, null);
    }

    internal static WorldSolverRegistration CreateCustom(
        string name,
        AtmosWorldSolver solver,
        AtmosNeighborSelection? selection)
    {
        return new WorldSolverRegistration(name, AtmosWorldSolverKind.Custom, null, solver, selection);
    }

    internal void Execute(AtmosWorldExecutionContext context)
    {
        if (_customSolver != null)
        {
            _customSolver(new AtmosWorldSolverContext(context.World, context.PublicSimulations, _topology));
            return;
        }

        _builtInSolver!(context);
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