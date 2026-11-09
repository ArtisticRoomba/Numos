using Numos.API;
using Numos.CoreSim;
using Numos.Maths;

namespace Numos.Workbench;

/// <summary>
///     One side of a rectangular face dock.
/// </summary>
/// <param name="Simulation">The simulation that owns the chunk.</param>
/// <param name="ChunkPosition">The chunk whose face is docked.</param>
/// <param name="Face">Which face of the chunk.</param>
public readonly record struct DockEndpoint(AtmosSimulationId Simulation, Int3 ChunkPosition, DockFace Face);

/// <summary>
///     Describes a dock between two chunk faces.
/// </summary>
/// <param name="First">The first face.</param>
/// <param name="Second">The second face.</param>
/// <param name="Rotation">Quarter turns applied to the second face. Only the two low bits count.</param>
/// <param name="FlipU">Whether to mirror the second face along its U axis after rotation.</param>
/// <param name="FlipV">Whether to mirror the second face along its V axis after rotation.</param>
/// <param name="Flags">The transport modes of each link.</param>
/// <param name="AreaFraction">
///     The open area of each link as a fraction of one voxel face, or <see langword="null" /> to follow the
///     configuration default.
/// </param>
public readonly record struct DockDefinition(
    DockEndpoint First,
    DockEndpoint Second,
    int Rotation,
    bool FlipU,
    bool FlipV,
    ExplicitLinkFlags Flags,
    float? AreaFraction = null);

/// <summary>
///     What a solver pipeline edit did, with the message the session wrote to its log.
/// </summary>
/// <param name="Applied">
///     The edit's yes-or-no outcome. Each <see cref="WorkbenchSession" /> solver method documents what it means.
/// </param>
/// <param name="Message">The log message, which a front end can show beside the control that made the edit.</param>
/// <param name="Level">
///     The log level of the message. <see cref="WorkbenchLogLevel.Warn" /> marks a partial or failed edit.
/// </param>
public readonly record struct SolverEditResult(bool Applied, string Message, WorkbenchLogLevel Level);

public sealed partial class WorkbenchSession
{
    /// <summary>
    ///     Gets a detached view of every link set, including pending ones.
    /// </summary>
    public IReadOnlyList<AtmosWorldLinkSetSnapshot> GetLinkSets()
    {
        return RequireWorld().GetLinkSets();
    }

    /// <summary>
    ///     Creates a portal between two cells. It activates at the next tick boundary.
    /// </summary>
    /// <param name="first">The first cell.</param>
    /// <param name="second">The second cell.</param>
    /// <param name="flags">The transport modes of the link.</param>
    /// <param name="areaFraction">
    ///     The open area as a fraction of one voxel face, or <see langword="null" /> for the configuration default.
    /// </param>
    /// <returns>The portal handle.</returns>
    public AtmosPortalHandle CreatePortal(
        AtmosCellRef first,
        AtmosCellRef second,
        ExplicitLinkFlags flags,
        float? areaFraction = null)
    {
        return Guard(
            "Could not create the portal",
            () =>
            {
                var world = RequireWorld();
                ThrowIfInspecting();
                var portal = world.CreatePortal(first, second, flags, areaFraction);
                Info("Created portal. It will activate on the next world tick.");
                return portal;
            });
    }

    /// <summary>
    ///     Builds the per-voxel links of a dock without creating it.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     An endpoint has no chunks, or the faces do not match after rotation.
    /// </exception>
    public ExplicitLinkDefinition[] BuildDockLinks(DockDefinition dock)
    {
        RequireWorld();
        var firstSimulation = RequireSimulation(dock.First.Simulation);
        var secondSimulation = RequireSimulation(dock.Second.Simulation);
        if (firstSimulation.ChunkCount == 0 || secondSimulation.ChunkCount == 0)
            throw new InvalidOperationException("Each dock endpoint needs a chunk.");

        var firstDimensions = firstSimulation.ChunkDimensions;
        var secondDimensions = secondSimulation.ChunkDimensions;
        var firstSize = DockGeometry.GetFaceSize(firstDimensions, dock.First.Face);
        var secondSize = DockGeometry.GetFaceSize(secondDimensions, dock.Second.Face);
        bool quarterTurn = (dock.Rotation & 1) != 0;
        if (firstSize.Width != (quarterTurn ? secondSize.Height : secondSize.Width) ||
            firstSize.Height != (quarterTurn ? secondSize.Width : secondSize.Height))
            throw new InvalidOperationException("The selected faces do not have matching dimensions after rotation.");

        var firstChunk = new AtmosChunkHandle(dock.First.ChunkPosition);
        var secondChunk = new AtmosChunkHandle(dock.Second.ChunkPosition);
        var links = new ExplicitLinkDefinition[firstSize.Width * firstSize.Height];
        int destination = 0;
        for (int v = 0; v < firstSize.Height; v++)
        for (int u = 0; u < firstSize.Width; u++)
        {
            (int secondU, int secondV) = DockGeometry.RotateFace(u, v, secondSize, dock.Rotation);
            if (dock.FlipU) secondU = secondSize.Width - 1 - secondU;
            if (dock.FlipV) secondV = secondSize.Height - 1 - secondV;
            ushort firstIndex = DockGeometry.GetFaceVoxelIndex(firstDimensions, dock.First.Face, u, v);
            ushort secondIndex = DockGeometry.GetFaceVoxelIndex(secondDimensions, dock.Second.Face, secondU, secondV);
            links[destination++] = new ExplicitLinkDefinition(
                new AtmosCellRef(firstSimulation.Id, firstChunk, firstIndex),
                new AtmosCellRef(secondSimulation.Id, secondChunk, secondIndex),
                dock.Flags,
                dock.AreaFraction);
        }

        return links;
    }

    /// <summary>
    ///     Creates a rectangular face dock. It activates at the next tick boundary.
    /// </summary>
    /// <returns>The dock handle.</returns>
    public AtmosDockHandle CreateDock(DockDefinition dock)
    {
        return Guard(
            "Could not create the dock",
            () =>
            {
                var world = RequireWorld();
                ThrowIfInspecting();
                ExplicitLinkDefinition[] links = BuildDockLinks(dock);
                var handle = world.CreateDock(links);
                Info($"Created dock with {links.Length} links. It will activate on the next world tick.");
                return handle;
            });
    }

    /// <summary>
    ///     Queues a link set (portal or dock) for removal at the next tick boundary.
    /// </summary>
    public void DestroyLinkSet(ExplicitLinkSetHandle handle)
    {
        Guard(
            "Could not remove the connection",
            () =>
            {
                var world = RequireWorld();
                ThrowIfInspecting();
                world.DestroyLinks(handle);
                Info("Queued the connection for removal on the next world tick.");
            });
    }

    /// <summary>
    ///     Lists the solver stages in execution order.
    /// </summary>
    public IReadOnlyList<AtmosWorldSolverStep> GetSolverSteps()
    {
        return RequireWorld().Solvers.Steps;
    }

    /// <summary>
    ///     Enables or disables a solver stage by name.
    /// </summary>
    /// <returns>
    ///     The logged message. <see cref="SolverEditResult.Applied" /> is <see langword="true" /> when the stage
    ///     exists.
    /// </returns>
    public SolverEditResult SetSolverEnabled(string name, bool enabled)
    {
        return Guard(
            $"Could not change solver '{name}'",
            () =>
            {
                var world = RequireWorld();
                ThrowIfInspecting();
                return world.Solvers.SetEnabled(name, enabled)
                    ? SolverEdited(true, $"{(enabled ? "Enabled" : "Disabled")} solver '{name}'.")
                    : SolverEdited(false, $"Solver '{name}' does not exist.", WorkbenchLogLevel.Warn);
            });
    }

    /// <summary>
    ///     Sets how many ticks pass between runs of a solver stage.
    /// </summary>
    /// <param name="name">The stage name.</param>
    /// <param name="interval">Ticks between runs. Must be at least 1.</param>
    /// <returns>
    ///     The logged message. <see cref="SolverEditResult.Applied" /> is <see langword="true" /> when the stage
    ///     exists.
    /// </returns>
    /// <remarks>
    ///     The world records an actual change. Keep the interval of <c>advection</c> equal to that of
    ///     <c>boundary-flow</c>, and <c>thermodynamics</c> equal to <c>thermal-boundary</c>.
    ///     <see cref="SolverPipelineEditor.GetWarnings" /> reports a mismatch.
    /// </remarks>
    /// <exception cref="InvalidOperationException">No project is open, or the timeline inspects history.</exception>
    public SolverEditResult SetSolverInterval(string name, int interval)
    {
        return Guard(
            $"Could not change the interval of solver '{name}'",
            () =>
            {
                var world = RequireWorld();
                ThrowIfInspecting();
                return world.Solvers.SetInterval(name, interval)
                    ? SolverEdited(true, $"Set solver '{name}' to run every {interval} ticks.")
                    : SolverEdited(false, $"Solver '{name}' does not exist.", WorkbenchLogLevel.Warn);
            });
    }

    /// <summary>
    ///     Replaces the order and enabled flags of the solver stages.
    /// </summary>
    /// <param name="stages">Every stage in the new order. See <see cref="AtmosWorldSolverPipeline.SetLayout" />.</param>
    /// <returns>
    ///     The logged message. <see cref="SolverEditResult.Applied" /> is <see langword="true" /> when the layout
    ///     changed and <see langword="false" /> when it already matched.
    /// </returns>
    /// <remarks>
    ///     The world records an actual change, so it replays at the same position.
    /// </remarks>
    /// <exception cref="ArgumentException">
    ///     The world rejected the layout. The pipeline stays unchanged and the log holds the reason.
    /// </exception>
    /// <exception cref="InvalidOperationException">No project is open, or the timeline inspects history.</exception>
    public SolverEditResult SetSolverLayout(IReadOnlyList<AtmosWorldSolverLayoutEntry> stages)
    {
        ArgumentNullException.ThrowIfNull(stages);
        return Guard("Could not change the solver layout", () => ApplySolverLayout(stages));
    }

    /// <summary>
    ///     Switches the transport stages to one model and keeps every other stage as it is.
    /// </summary>
    /// <param name="model">The transport model to run.</param>
    /// <returns>
    ///     The logged message. <see cref="SolverEditResult.Applied" /> is <see langword="true" /> when the layout
    ///     changed and <see langword="false" /> when it already matched.
    /// </returns>
    /// <remarks>
    ///     See <see cref="AtmosWorldSolverPipeline.ConvertTransportModel" /> for how the stages move.
    /// </remarks>
    /// <exception cref="ArgumentException">
    ///     <paramref name="model" /> is not a defined model, or the world rejected the resulting layout.
    /// </exception>
    /// <exception cref="InvalidOperationException">No project is open, or the timeline inspects history.</exception>
    public SolverEditResult ApplyTransportPreset(AtmosTransportModel model)
    {
        return Guard(
            "Could not apply the transport preset",
            () =>
            {
                var world = RequireWorld();
                var current = SolverPipelineEditor.ToLayout(world.Solvers.Steps);
                return ApplySolverLayout(AtmosWorldSolverPipeline.ConvertTransportModel(current, model));
            });
    }

    /// <summary>
    ///     Restores the built-in solver stages, order, and enabled flags.
    /// </summary>
    /// <returns>
    ///     The logged message. <see cref="SolverEditResult.Applied" /> is <see langword="true" /> when the pipeline
    ///     is back to its defaults and <see langword="false" /> when only the enabled flags could be restored.
    /// </returns>
    /// <param name="transportModel">
    ///     The built-in transport stages to install, or <see langword="null" /> to keep the model the stage list runs now.
    /// </param>
    /// <remarks>
    ///     A world without custom stages gets the default layout through <see cref="AtmosWorldSolverPipeline.SetLayout" />,
    ///     which also works while recording. The world cannot discard custom stages while it records, and a project
    ///     timeline always records. In that case the session enables every stage and leaves the order and the custom
    ///     stages alone.
    /// </remarks>
    public SolverEditResult ResetSolvers(AtmosTransportModel? transportModel = null)
    {
        return Guard(
            "Could not reset the solvers",
            () =>
            {
                var world = RequireWorld();
                var model = transportModel ??
                            AtmosWorldSolverPipeline.DetectTransportModel(world.Solvers.Steps) ??
                            AtmosTransportModel.Euler;
                if (world.Solvers.Steps.All(static step => step.Kind == AtmosWorldSolverKind.BuiltIn))
                {
                    ThrowIfInspecting();
                    world.Solvers.SetLayout(AtmosWorldSolverPipeline.GetDefaultLayout(model)
                        .Select(static name => new AtmosWorldSolverLayoutEntry(name, true))
                        .ToArray());

                    return SolverEdited(true, "Reset the solver pipeline to its defaults.");
                }

                try
                {
                    world.Solvers.ResetToDefaults(model);
                    return SolverEdited(true, "Reset the solver pipeline to its defaults.");
                }
                catch (InvalidOperationException)
                {
                    foreach (var step in world.Solvers.Steps)
                    {
                        if (!step.IsEnabled)
                            world.Solvers.SetEnabled(step.Name, true);
                    }

                    return SolverEdited(
                        false,
                        "The world records, so the session enabled every solver stage and kept the stage order.",
                        WorkbenchLogLevel.Warn);
                }
            });
    }

    private SolverEditResult ApplySolverLayout(IReadOnlyList<AtmosWorldSolverLayoutEntry> stages)
    {
        var world = RequireWorld();
        ThrowIfInspecting();
        bool changed = world.Solvers.SetLayout(stages);
        return SolverEdited(changed, changed ? "Changed the solver layout." : "The solver layout already matches.");
    }

    private SolverEditResult SolverEdited(
        bool applied,
        string message,
        WorkbenchLogLevel level = WorkbenchLogLevel.Info)
    {
        DefinitionRevision++;
        Log.Write(level, LogSource, message, null, World?.TickCount);
        return new SolverEditResult(applied, message, level);
    }
}
