using Numos.Chunks;
using Numos.Chunks.Topology;
using Numos.CoreSim;

namespace Numos.API;

/// <summary>
///     Runs one caller-provided stage during an <see cref="AtmosWorld" /> tick.
/// </summary>
/// <param name="context">The tick-wide simulations, configuration, and compiled neighborhood view.</param>
public delegate void AtmosWorldSolver(AtmosWorldSolverContext context);

/// <summary>
///     Identifies whether a world solver stage is provided by Numos or its host.
/// </summary>
public enum AtmosWorldSolverKind : byte
{
    /// <summary>
    ///     A default Numos stage.
    /// </summary>
    BuiltIn,

    /// <summary>
    ///     A caller-provided stage.
    /// </summary>
    Custom
}

/// <summary>
///     Tick-scoped inputs supplied to a world solver callback.
/// </summary>
/// <remarks>
///     The topology view is immutable for the callback. Do not retain the context or its topology after the callback
///     returns.
/// </remarks>
public sealed class AtmosWorldSolverContext
{
    internal AtmosWorldSolverContext(
        AtmosWorld world,
        IReadOnlyList<AtmosSimulation> simulations,
        WorldNeighborTopology<AtmosLinkData> topology)
    {
        World = world;
        Simulations = simulations;
        Topology = topology;
    }

    /// <summary>
    ///     Gets the world executing this callback.
    /// </summary>
    public AtmosWorld World { get; }

    /// <summary>
    ///     Gets the one-based world tick currently being executed.
    /// </summary>
    public int TickCount => checked(World.TickCount + 1);

    /// <summary>
    ///     Gets the immutable configuration captured for this tick.
    /// </summary>
    public AtmosConfigSnapshot Config => World.Config;

    /// <summary>
    ///     Gets simulations in stable registration order.
    /// </summary>
    public IReadOnlyList<AtmosSimulation> Simulations { get; }

    /// <summary>
    ///     Gets the solver-specific compiled neighborhood view.
    /// </summary>
    /// <remarks>
    ///     A stage registered through a plain <see cref="AtmosWorldSolverPipeline.Register" /> call (rather than one
    ///     of the <c>RegisterNeighborSolver*</c> overloads) receives an empty view here: no exception, no Cartesian
    ///     neighbors, no explicit edges. Use <see cref="AtmosWorldSolverPipeline.RegisterNeighborSolver" /> whenever
    ///     the callback needs this property.
    /// </remarks>
    public WorldNeighborTopology<AtmosLinkData> Topology { get; }
}

/// <summary>
///     Detached metadata for one registered world solver.
/// </summary>
/// <param name="Name">The unique stage name.</param>
/// <param name="Kind">Whether Numos or the caller supplied the implementation.</param>
/// <param name="IsEnabled">Whether the stage participates in subsequent ticks.</param>
/// <param name="NeighborSelectionKey">The compiled-neighborhood policy key, or <see langword="null" />.</param>
public readonly record struct AtmosWorldSolverStep(
    string Name,
    AtmosWorldSolverKind Kind,
    bool IsEnabled,
    string? NeighborSelectionKey);
