using Numos.API;
using Numos.CoreSim.State;

namespace Numos.Workbench;

public sealed partial class WorkbenchSession
{
    /// <summary>
    ///     Lists every solver in <see cref="AtmosSolverCatalog" />, in ordinal name order.
    /// </summary>
    /// <remarks>
    ///     An assembly adds its <see cref="AtmosSolverAttribute" /> solvers to the catalog the first time its code runs.
    ///     A solver from an assembly that has not run yet is missing from the list.
    /// </remarks>
    /// <exception cref="InvalidOperationException">No project is open.</exception>
    public IReadOnlyList<WorkbenchCatalogSolver> GetSolverCatalog()
    {
        var steps = RequireWorld().Solvers.Steps;
        var entries = new List<WorkbenchCatalogSolver>();
        foreach (string name in AtmosSolverCatalog.Names)
        {
            if (!AtmosSolverCatalog.TryGet(name, out var descriptor))
                continue;

            entries.Add(new WorkbenchCatalogSolver(
                name,
                descriptor.Before,
                descriptor.After,
                descriptor.RunsPerSecond,
                descriptor.Interval,
                steps.Any(step => step.Name == name)));
        }

        return entries;
    }

    /// <summary>
    ///     Creates the world's instance of a catalog solver and adds it as a stage.
    /// </summary>
    /// <param name="name">The stage name of an <see cref="AtmosSolverCatalog" /> entry.</param>
    /// <param name="before">The stage the new stage runs directly before, or <see langword="null" />.</param>
    /// <param name="after">The stage the new stage runs directly after, or <see langword="null" />.</param>
    /// <param name="interval">The interval in ticks, or zero for the declared rate.</param>
    /// <returns>
    ///     The logged message. <see cref="SolverEditResult.Applied" /> is <see langword="false" /> when the catalog has
    ///     no solver named <paramref name="name" />.
    /// </returns>
    /// <remarks>
    ///     Without <paramref name="before" /> and <paramref name="after" />, the stage takes its declared placement. The
    ///     world records the new stage, so a replay creates it at the same position. See
    ///     <see cref="AtmosWorldSolverPipeline.Add(string, string?, string?, int)" />.
    /// </remarks>
    /// <exception cref="ArgumentException">
    ///     The stage is already registered, or both <paramref name="before" /> and <paramref name="after" /> are given.
    /// </exception>
    /// <exception cref="KeyNotFoundException">The placement anchor is not registered.</exception>
    /// <exception cref="InvalidOperationException">No project is open, or the timeline inspects history.</exception>
    public SolverEditResult AddSolver(string name, string? before = null, string? after = null, int interval = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Guard(
            $"Could not add solver '{name}'",
            () =>
            {
                var world = RequireWorld();
                ThrowIfInspecting();
                if (!AtmosSolverCatalog.TryGet(name, out _))
                    return SolverEdited(false, $"The solver catalog has no solver named '{name}'.", WorkbenchLogLevel.Warn);

                world.Solvers.Add(name, before, after, interval);
                return SolverEdited(true, $"Added solver '{name}'.");
            });
    }

    /// <summary>
    ///     Removes a catalog solver's stage and disposes its instance.
    /// </summary>
    /// <param name="name">The stage name of an <see cref="AtmosSolverCatalog" /> entry.</param>
    /// <returns>
    ///     The logged message. <see cref="SolverEditResult.Applied" /> is <see langword="false" /> when the name is not
    ///     in the catalog or the pipeline does not hold the stage.
    /// </returns>
    /// <remarks>
    ///     The world records the removal. Built-in stages leave the pipeline through <see cref="SetSolverLayout" />,
    ///     and stages the host registered cannot leave while the timeline records.
    /// </remarks>
    /// <exception cref="InvalidOperationException">No project is open, or the timeline inspects history.</exception>
    public SolverEditResult RemoveSolver(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Guard(
            $"Could not remove solver '{name}'",
            () =>
            {
                var world = RequireWorld();
                ThrowIfInspecting();
                if (!AtmosSolverCatalog.TryGet(name, out _))
                    return SolverEdited(false, $"'{name}' is not a catalog solver.", WorkbenchLogLevel.Warn);

                if (!world.Solvers.Unregister(name))
                    return SolverEdited(false, $"Solver '{name}' does not exist.", WorkbenchLogLevel.Warn);

                return SolverEdited(true, $"Removed solver '{name}'.");
            });
    }

    /// <summary>
    ///     Reads the <c>[NumosSerialize]</c> members of the class solver a stage holds.
    /// </summary>
    /// <param name="solver">The stage name.</param>
    /// <param name="members">The members in declaration order, or an empty list.</param>
    /// <returns>
    ///     <see langword="true" /> when the stage holds a class solver. A built-in stage or a delegate stage returns
    ///     <see langword="false" />.
    /// </returns>
    /// <exception cref="InvalidOperationException">No project is open.</exception>
    public bool TryGetSolverState(string solver, out IReadOnlyList<WorkbenchStateMember> members)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(solver);
        if (!RequireWorld().Solvers.TryGetState(solver, out object? instance, out var descriptors))
        {
            members = [];
            return false;
        }

        members = ReadMembers(instance, descriptors);
        return true;
    }

    /// <summary>
    ///     Parses a value and writes it to one member of the class solver a stage holds.
    /// </summary>
    /// <param name="solver">The stage name.</param>
    /// <param name="member">The member name as declared.</param>
    /// <param name="value">The value as invariant text, or <see langword="null" /> for a nullable member.</param>
    /// <returns>The member after the write.</returns>
    /// <remarks>
    ///     <see cref="WorkbenchStateValues.TryParse" /> describes the text each member type takes. The world records the
    ///     write at the next tick, as it does for any other host edit to the solver.
    /// </remarks>
    /// <exception cref="KeyNotFoundException">No stage holds a class solver named <paramref name="solver" />, or it has no such member.</exception>
    /// <exception cref="ArgumentException">The text does not parse for the member's type.</exception>
    /// <exception cref="InvalidOperationException">
    ///     The member is read-only, no project is open, or the timeline inspects history.
    /// </exception>
    public WorkbenchStateMember SetSolverState(string solver, string member, string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(solver);
        ArgumentException.ThrowIfNullOrWhiteSpace(member);
        return Guard(
            $"Could not set '{member}' of solver '{solver}'",
            () =>
            {
                var world = RequireWorld();
                ThrowIfInspecting();
                if (!world.Solvers.TryGetState(solver, out object? instance, out var descriptors))
                    throw new KeyNotFoundException($"No stage named '{solver}' holds a class solver.");

                var result = WriteMember(instance, descriptors, member, value, $"solver '{solver}'");
                Info($"Set '{member}' of solver '{solver}' to {result.Text}.");
                return result;
            });
    }

    /// <summary>
    ///     Reads every simulation component a simulation holds, in ordinal key order.
    /// </summary>
    /// <param name="simulation">The simulation, or <see langword="null" /> for the active one.</param>
    /// <exception cref="InvalidOperationException">No project is open, or there is no active simulation.</exception>
    /// <exception cref="ArgumentException">The simulation does not exist.</exception>
    public IReadOnlyList<WorkbenchComponentState> GetSimulationComponents(AtmosSimulationId? simulation = null)
    {
        var target = RequireSimulation(simulation);
        var components = new List<WorkbenchComponentState>();
        foreach (string key in target.ComponentKeys)
        {
            if (target.TryGetComponentState(key, out object? instance, out var descriptors))
                components.Add(new WorkbenchComponentState(key, ReadMembers(instance, descriptors)));
        }

        return components;
    }

    /// <summary>
    ///     Parses a value and writes it to one member of a simulation component.
    /// </summary>
    /// <param name="key">The component key.</param>
    /// <param name="member">The member name as declared.</param>
    /// <param name="value">The value as invariant text, or <see langword="null" /> for a nullable member.</param>
    /// <param name="simulation">The simulation, or <see langword="null" /> for the active one.</param>
    /// <returns>The member after the write.</returns>
    /// <remarks>
    ///     The call never creates a component. The world records the write at the next tick, as it does for any other
    ///     host edit to the component.
    /// </remarks>
    /// <exception cref="KeyNotFoundException">The simulation holds no component under <paramref name="key" />, or it has no such member.</exception>
    /// <exception cref="ArgumentException">The text does not parse for the member's type, or the simulation does not exist.</exception>
    /// <exception cref="InvalidOperationException">
    ///     The member is read-only, no project is open, there is no active simulation, or the timeline inspects
    ///     history.
    /// </exception>
    public WorkbenchStateMember SetSimulationComponentState(
        string key, string member, string? value, AtmosSimulationId? simulation = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(member);
        return Guard(
            $"Could not set '{member}' of simulation component '{key}'",
            () =>
            {
                ThrowIfInspecting();
                var target = RequireSimulation(simulation);
                if (!target.TryGetComponentState(key, out object? instance, out var descriptors))
                    throw new KeyNotFoundException($"The simulation holds no component '{key}'.");

                var result = WriteMember(instance, descriptors, member, value, $"simulation component '{key}'");
                Info($"Set '{member}' of simulation component '{key}' to {result.Text}.");
                return result;
            });
    }

    private static WorkbenchStateMember[] ReadMembers(object instance, IReadOnlyList<AtmosStateMember> descriptors)
    {
        var members = new WorkbenchStateMember[descriptors.Count];
        for (int index = 0; index < members.Length; index++)
            members[index] = WorkbenchStateValues.Read(descriptors[index], instance);

        return members;
    }

    private static WorkbenchStateMember WriteMember(
        object instance, IReadOnlyList<AtmosStateMember> descriptors, string member, string? text, string owner)
    {
        var descriptor = descriptors.FirstOrDefault(candidate => candidate.Name == member) ??
                         throw new KeyNotFoundException($"The {owner} has no state member '{member}'.");

        if (descriptor.SetValue is not { } setValue)
        {
            throw new InvalidOperationException(
                $"'{member}' of the {owner} is read-only to tools. Change it through a solver command.");
        }

        if (!WorkbenchStateValues.TryParse(descriptor.ValueType, text, out object? parsed, out string? error))
            throw new ArgumentException(error, nameof(text));

        setValue(instance, parsed);
        return WorkbenchStateValues.Read(descriptor, instance);
    }
}
