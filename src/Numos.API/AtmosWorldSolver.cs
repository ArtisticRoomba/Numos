using Numos.CoreSim;

namespace Numos.API;

/// <summary>
///     Runs one caller-provided stage during an <see cref="AtmosWorld" /> tick.
/// </summary>
/// <param name="context">The tick-wide simulations, configuration, and compiled neighborhood view.</param>
public delegate void AtmosWorldSolver(AtmosWorldSolverContext context);

/// <summary>
///     A caller-provided world stage written as a class, for solvers that keep host-side state between ticks or need
///     to react to checkpoint restores.
/// </summary>
/// <remarks>
///     <para>
///         Register an instance with
///         <see cref="AtmosWorldSolverPipeline.Register(string, IAtmosWorldSolver, AtmosNeighborSelection?, string?, string?)" />.
///         The instance stays caller-owned: Numos never checkpoints, clones,
///         or disposes it, and the pipeline's checkpoint metadata is the same as for a delegate registered under the
///         same name, so switching between the two forms keeps old checkpoints compatible.
///     </para>
///     <para>
///         Fields on the instance are invisible to replay. State that has to roll back with a checkpoint belongs in a
///         rollback <see cref="ChunkField{T}" />, and caches rebuilt from authoritative inputs can live under a
///         <see cref="SolverDataKey{T}" />. Fields should only hold transient work that <see cref="OnRestored" /> can
///         throw away, plus references to services, other solvers, and chunk fields.
///     </para>
///     <para>
///         Configuration is not pushed to the solver. Read <see cref="AtmosWorldSolverContext.Config" /> each tick, and
///         cache anything derived from it under a <see cref="SolverDataKey{T}" />, which is rebuilt when it changes.
///     </para>
/// </remarks>
/// <example>
///     A solver that ages per-voxel heat exposure and keeps a transient per-tick counter:
///     <code>
///     public static partial class FireData
///     {
///         [ChunkData("my-mod/fire/exposure", Rollback = true)]
///         public static partial ChunkField&lt;float&gt; Exposure { get; }
///     }
/// 
///     public sealed class ExposureDecaySolver : IAtmosWorldSolver
///     {
///         private int _chunksVisited; // transient; reset on restore
/// 
///         public void Solve(AtmosWorldSolverContext context)
///         {
///             foreach (var simulation in context.Simulations)
///             {
///                 var chunks = context.Chunks(simulation);
///                 Parallel.For(0, chunks.Count, index =>
///                 {
///                     Span&lt;float&gt; exposure = chunks[index].Get(FireData.Exposure).AsSpan();
///                     for (int voxel = 0; voxel &lt; exposure.Length; voxel++)
///                         exposure[voxel] *= 0.9f;
///                 });
///                 _chunksVisited += chunks.Count;
///             }
///         }
/// 
///         public void OnRestored() =&gt; _chunksVisited = 0;
///     }
/// 
///     world.Solvers.Register("my-mod/exposure-decay", new ExposureDecaySolver(), after: AtmosBuiltInSolvers.Thermodynamics);
///     </code>
/// </example>
public interface IAtmosWorldSolver
{
    /// <summary>
    ///     Runs this stage for one world tick.
    /// </summary>
    /// <param name="context">The tick-wide simulations, configuration, and compiled neighborhood view.</param>
    /// <remarks>
    ///     Called on the tick's thread with every simulation's state lock held. Do not retain
    ///     <paramref name="context" /> or the chunk views it hands out after returning.
    /// </remarks>
    void Solve(AtmosWorldSolverContext context);

    /// <summary>
    ///     Discards transient state after a checkpoint has been restored into the world.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Numos calls this on every custom class solver, enabled or not, in pipeline order, once the restored
    ///         state is fully installed and before any replayed tick. That covers
    ///         <see cref="AtmosWorld.RestoreCheckpoint" />, <see cref="AtmosSimulation.RestoreCheckpoint" />, both
    ///         <c>ReplayTo</c> methods, and the restore that puts the previous state back after a failed
    ///         <c>ReplayTo</c>. A single host call can therefore trigger it more than once, so make it idempotent.
    ///     </para>
    ///     <para>
    ///         Services and other solvers are still registered when this runs. An exception propagates to the caller
    ///         of the restore; the restored state stays installed, but solvers later in the pipeline are not notified.
    ///     </para>
    /// </remarks>
    void OnRestored()
    {
    }
}

/// <summary>
///     Selects an explicit link while Numos compiles a custom solver's neighborhood view.
/// </summary>
/// <param name="link">The canonical, value-only link being compiled.</param>
/// <returns><see langword="true" /> when the link participates in the solver.</returns>
/// <remarks>
///     Numos calls selectors only when topology or solver registration changes. Selectors must be deterministic and must
///     not mutate their world.
/// </remarks>
public delegate bool AtmosExplicitLinkSelector(AtmosExplicitLinkInfo link);

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
///     Describes one canonical explicit link to a topology selector.
/// </summary>
/// <param name="First">The canonical first endpoint.</param>
/// <param name="Second">The canonical second endpoint.</param>
/// <param name="Flags">The interactions enabled by the link.</param>
public readonly record struct AtmosExplicitLinkInfo(
    AtmosCellRef First,
    AtmosCellRef Second,
    ExplicitLinkFlags Flags);

/// <summary>
///     Configures the neighborhood compiled for one custom world solver.
/// </summary>
/// <param name="key">A stable identifier describing the selection policy for checkpoint compatibility.</param>
/// <param name="includeCartesian">Whether ordinary Cartesian neighbors participate.</param>
/// <param name="explicitLinks">The selector evaluated for active explicit links.</param>
/// <remarks>
///     Set <paramref name="includeCartesian" /> to <see langword="false" /> for a solver that only interacts with
///     portals, docks, or other explicit links: it keeps the compiled view limited to the sparse explicit edge set
///     instead of re-deriving all six ordinary neighbors of every voxel in every chunk. Reach for
///     <see langword="true" /> only when the same stage genuinely needs to traverse ordinary walls too, such as fire
///     or sound propagating through both open doorways and portals.
/// </remarks>
public sealed class AtmosNeighborSelection(
    string key,
    bool includeCartesian,
    AtmosExplicitLinkSelector explicitLinks)
{
    /// <summary>
    ///     Gets the stable compatibility key for this selection policy.
    /// </summary>
    /// <remarks>
    ///     This string is checkpointed alongside the solver's registration and folded into world state hashing, so a
    ///     restored checkpoint compiles topology using the key it was captured with. Give a selection a new key when
    ///     its <see cref="AtmosExplicitLinkSelector" /> changes what it matches; reusing a key for a semantically
    ///     different selection lets a restored checkpoint silently compile the wrong edges for it.
    /// </remarks>
    public string Key { get; } = string.IsNullOrWhiteSpace(key)
        ? throw new ArgumentException("A neighbor selection key cannot be empty.", nameof(key))
        : key;

    /// <summary>
    ///     Gets whether ordinary Cartesian neighbors participate.
    /// </summary>
    public bool IncludeCartesian { get; } = includeCartesian;

    internal AtmosExplicitLinkSelector ExplicitLinks { get; } =
        explicitLinks ?? throw new ArgumentNullException(nameof(explicitLinks));

    /// <summary>
    ///     Creates a selection that includes Cartesian neighbors and every active explicit link.
    /// </summary>
    /// <param name="key">The stable compatibility key for the consuming solver.</param>
    /// <returns>A selection covering the complete world topology.</returns>
    public static AtmosNeighborSelection All(string key)
    {
        return new AtmosNeighborSelection(key, true, static _ => true);
    }
}

/// <summary>
///     Tick-scoped inputs supplied to a world solver callback.
/// </summary>
/// <remarks>
///     The topology view is immutable for the callback. Do not retain the context, its topology, or its chunk views
///     after the callback returns; chunk views throw once the tick ends.
/// </remarks>
public sealed class AtmosWorldSolverContext
{
    private readonly AtmosSolverTickScope _solverScope;

    internal AtmosWorldSolverContext(AtmosSolverTickScope solverScope, AtmosWorldNeighborTopology topology)
    {
        _solverScope = solverScope;
        Topology = topology;
    }

    /// <summary>
    ///     Gets the world executing this callback.
    /// </summary>
    public AtmosWorld World => _solverScope.World;

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
    public IReadOnlyList<AtmosSimulation> Simulations => _solverScope.PublicSimulations;

    /// <summary>
    ///     Gets the solver-specific compiled neighborhood view.
    /// </summary>
    /// <remarks>
    ///     A stage registered without a neighbor selection receives an empty view here: no exception, no Cartesian
    ///     neighbors, no explicit edges. Pass <c>neighbors</c> to
    ///     <see cref="AtmosWorldSolverPipeline.Register(string, AtmosWorldSolver, AtmosNeighborSelection?, string?, string?)" />
    ///     whenever the callback needs this property.
    /// </remarks>
    public AtmosWorldNeighborTopology Topology { get; }

    /// <summary>
    ///     Gets lock-free views of one simulation's chunks for this tick.
    /// </summary>
    /// <param name="simulation">A simulation from <see cref="Simulations" />.</param>
    /// <returns>The simulation's chunks in grid order, valid until the tick ends.</returns>
    /// <remarks>
    ///     This is how solvers reach <see cref="ChunkField{T}" /> storage. The views never take the simulation's state
    ///     lock, so call this once on the callback thread and hand the result to <c>Parallel.For</c> workers.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="simulation" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException"><paramref name="simulation" /> is not part of this tick.</exception>
    /// <exception cref="InvalidOperationException">The tick has ended.</exception>
    /// <example>
    ///     <code>
    ///     foreach (var simulation in context.Simulations)
    ///     {
    ///         var chunks = context.Chunks(simulation);
    ///         Parallel.For(0, chunks.Count, index => chunks[index].Get(FireData.Exposure).Clear());
    ///     }
    ///     </code>
    /// </example>
    public AtmosSolverChunks Chunks(AtmosSimulation simulation)
    {
        ArgumentNullException.ThrowIfNull(simulation);
        _solverScope.ThrowIfClosed();
        int index = _solverScope.IndexOf(simulation);
        if (index < 0)
            throw new ArgumentException("The simulation is not registered in the world running this tick.", nameof(simulation));

        return new AtmosSolverChunks(_solverScope, index);
    }

    /// <summary>
    ///     Gets a lock-free view of the chunk that contains a world cell.
    /// </summary>
    /// <param name="cell">A cell in any simulation of this world, such as a portal or dock endpoint.</param>
    /// <param name="chunk">The chunk view when found; otherwise <see langword="default" />.</param>
    /// <returns>
    ///     <see langword="true" /> when the cell's simulation, chunk, and voxel index all exist in this tick.
    /// </returns>
    /// <remarks>
    ///     Index the chunk's fields with <see cref="AtmosCellRef.LocalVoxelIndex" /> to reach the cell itself. Like
    ///     <see cref="Chunks" />, this is safe to call from worker threads during the callback.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The tick has ended.</exception>
    /// <example>
    ///     <code>
    ///     foreach (var neighbor in context.Topology.GetNeighbors(cell))
    ///     {
    ///         if (context.TryGetChunk(neighbor.Cell, out var other))
    ///             other.Get(FireData.Exposure)[neighbor.Cell.LocalVoxelIndex] += 0.1f;
    ///     }
    ///     </code>
    /// </example>
    public bool TryGetChunk(AtmosCellRef cell, out AtmosSolverChunk chunk)
    {
        _solverScope.ThrowIfClosed();
        int index = _solverScope.IndexOf(cell.Simulation);
        if (index >= 0 &&
            new AtmosSolverChunks(_solverScope, index).TryGet(cell.Chunk, out chunk) &&
            cell.LocalVoxelIndex < chunk.VoxelCount)
        {
            return true;
        }

        chunk = default;
        return false;
    }
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