using Numos.API;
using Numos.CoreSim;

namespace Numos.Workbench;

public sealed partial class WorkbenchSession
{
    /// <summary>
    ///     Creates another simulation in the world.
    /// </summary>
    /// <param name="chunkWidth">Voxels per chunk along X.</param>
    /// <param name="chunkHeight">Voxels per chunk along Y.</param>
    /// <param name="chunkDepth">Voxels per chunk along Z.</param>
    /// <param name="name">A display name, or <see langword="null" /> for `Simulation N`.</param>
    /// <param name="activate">Whether the new simulation becomes the active one.</param>
    /// <returns>The new simulation.</returns>
    /// <exception cref="ArgumentException">The chunk dimensions are invalid.</exception>
    public AtmosSimulation AddSimulation(
        int chunkWidth,
        int chunkHeight,
        int chunkDepth,
        string? name = null,
        bool activate = true)
    {
        return Guard(
            "Could not create the simulation",
            () => AddSimulationCore(chunkWidth, chunkHeight, chunkDepth, name, activate));
    }

    private AtmosSimulation AddSimulationCore(
        int chunkWidth,
        int chunkHeight,
        int chunkDepth,
        string? name,
        bool activate)
    {
        var world = RequireWorld();
        ThrowIfInspecting();
        var simulation = world.CreateSimulation(chunkWidth, chunkHeight, chunkDepth);
        if (!string.IsNullOrWhiteSpace(name))
            _simulationNames[simulation.Id] = name.Trim();

        ReconcileSimulations();
        if (activate)
            SetActiveSimulation(simulation.Id);

        Info($"Created {GetSimulationName(simulation.Id)} with chunk size {chunkWidth} x {chunkHeight} x {chunkDepth}.");
        return simulation;
    }

    /// <summary>
    ///     Removes a simulation, its chunks, and every link connected to it.
    /// </summary>
    /// <param name="id">The simulation to remove.</param>
    /// <returns><see langword="true" /> when the simulation existed.</returns>
    public bool RemoveSimulation(AtmosSimulationId id)
    {
        return Guard("Could not remove the simulation", () => RemoveSimulationCore(id));
    }

    private bool RemoveSimulationCore(AtmosSimulationId id)
    {
        var world = RequireWorld();
        ThrowIfInspecting();
        if (!world.TryGetSimulation(id, out var simulation))
            return false;

        string name = GetSimulationName(id);
        bool removed = world.DestroySimulation(simulation!);
        ReconcileSimulations();
        if (removed)
            Info($"Removed {name}.");

        return removed;
    }

    /// <summary>
    ///     Makes a simulation the target of chunk and voxel operations that name no simulation.
    /// </summary>
    /// <param name="id">The simulation, or <see langword="null" /> to clear the selection.</param>
    /// <exception cref="ArgumentException">The simulation does not exist.</exception>
    public void SetActiveSimulation(AtmosSimulationId? id)
    {
        var world = RequireWorld();
        if (id is not { } value)
        {
            ActiveSimulationId = null;
            return;
        }

        if (!world.TryGetSimulation(value, out var simulation))
            throw new ArgumentException($"Simulation {value.Index} does not exist.", nameof(id));

        ActiveSimulationId = value;
        _chunkDimensions = simulation!.ChunkDimensions;
    }

    /// <summary>
    ///     Gets the display name of a simulation.
    /// </summary>
    /// <param name="id">The simulation id.</param>
    /// <returns>The stored name, or `Simulation N` when none was set.</returns>
    public string GetSimulationName(AtmosSimulationId id)
    {
        return _simulationNames.GetValueOrDefault(id, $"Simulation {id.Index + 1}");
    }

    /// <summary>
    ///     Sets the display name of a simulation.
    /// </summary>
    /// <param name="id">The simulation id.</param>
    /// <param name="name">The new name. A blank name resets it to `Simulation N`.</param>
    public void SetSimulationName(AtmosSimulationId id, string? name)
    {
        RequireWorld();
        if (string.IsNullOrWhiteSpace(name))
            _simulationNames.Remove(id);
        else
            _simulationNames[id] = name.Trim();
    }

    /// <summary>
    ///     Finds a simulation by id.
    /// </summary>
    public bool TryGetSimulation(AtmosSimulationId id, out AtmosSimulation? simulation)
    {
        simulation = null;
        return World != null && World.TryGetSimulation(id, out simulation);
    }

    /// <summary>
    ///     Finds a simulation by display name, ignoring case.
    /// </summary>
    /// <returns>The first match in id order, or <see langword="null" />.</returns>
    public AtmosSimulation? FindSimulation(string name)
    {
        return Simulations.FirstOrDefault(simulation =>
            string.Equals(GetSimulationName(simulation.Id), name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Drops names of simulations that no longer exist and repairs the active selection.
    /// </summary>
    /// <remarks>
    ///     The session calls this after each operation that can add or remove simulations. Call it after any other
    ///     code changes the world, such as a replay restore. A missing active simulation falls back to the first
    ///     one.
    /// </remarks>
    public void ReconcileSimulations()
    {
        if (World == null)
            return;

        var simulations = World.Simulations;
        var live = new HashSet<AtmosSimulationId>(simulations.Select(static simulation => simulation.Id));
        foreach (var id in _simulationNames.Keys.Where(id => !live.Contains(id)).ToArray())
            _simulationNames.Remove(id);

        foreach (var simulation in simulations)
            _simulationNames.TryAdd(simulation.Id, $"Simulation {simulation.Id.Index + 1}");

        if (ActiveSimulationId is not { } active || !live.Contains(active))
            SetActiveSimulation(simulations.Count > 0 ? simulations[0].Id : null);
        else if (World.TryGetSimulation(active, out var current))
            _chunkDimensions = current!.ChunkDimensions;
    }
}
