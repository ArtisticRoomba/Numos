using System.Diagnostics.CodeAnalysis;
using Numos.CoreSim;
using Numos.CoreSim.Solvers;

namespace Numos.API;

/// <summary>
///     Runs one caller-provided stage during an <see cref="AtmosWorld" /> tick.
/// </summary>
/// <param name="context">The tick-wide simulations, configuration, and compiled neighborhood view.</param>
public delegate void AtmosWorldSolver(AtmosWorldSolverContext context);

/// <summary>
///     A caller-provided world stage written as a class, for solvers that keep state between ticks or need to react
///     to checkpoint restores.
/// </summary>
/// <remarks>
///     <para>
///         Mark the class with <see cref="AtmosSolverAttribute" /> and add it with
///         <see cref="AtmosWorldSolverPipeline.Add{T}" />. The world then creates and owns the instance. Numos can also
///         create the stage by name for a replay, a checkpoint restore, or an imported replay file.
///     </para>
///     <para>
///         The manual form registers an instance with
///         <see cref="AtmosWorldSolverPipeline.Register(string, IAtmosWorldSolver, AtmosNeighborSelection?, string?, string?, int)" />.
///         The caller owns that instance. Numos never clones or disposes it, and a restore needs it registered already.
///         The pipeline's checkpoint metadata is the same as for a delegate registered under the same name.
///     </para>
///     <para>
///         Fields marked <c>[NumosSerialize]</c> (the class must be <see langword="partial" />) are the solver's state.
///         Numos checkpoints, hashes, and saves them. On restore it writes them back into this instance. It records a
///         change the host makes between ticks as a <see cref="SetAtmosWorldSolverStateOperation" /> at the next tick
///         or checkpoint, so replay sees it too. Changes <see cref="Solve" /> makes are derived, like any other solver
///         work, and Numos does not record them. While recording, Numos hashes the marked members twice a tick. That
///         costs nothing for settings and counters but adds up for a large marked array.
///     </para>
///     <para>
///         A <see langword="partial" /> method marked <see cref="SolverCommandAttribute" /> is a recorded input. A host
///         call between ticks runs the method's <c>Apply{Name}</c> body and records one
///         <see cref="InvokeAtmosSolverCommandOperation" />, which replay runs again. The host calls inside the body
///         are not recorded on their own.
///     </para>
///     <para>
///         Fields marked <c>[NumosScratch]</c> are scratch work. Numos never saves them, and it resets each one after a
///         checkpoint restore, before <see cref="OnRestored" />. The class must be <see langword="partial" /> for
///         that too. A class with scratch members and no <c>[NumosSerialize]</c> member has no checkpoint entry.
///     </para>
///     <para>
///         Unmarked fields are invisible to replay. Keep them to references to services, other solvers, and chunk
///         fields, plus scratch that <see cref="OnRestored" /> resets by hand. A mutable one without a mark is a
///         build warning (<c>NUMOSSTATE016</c>), even when the class marks nothing. So is a <see langword="readonly" />
///         array or collection. Per-voxel state belongs in a rollback <see cref="ChunkField{T}" />. Caches rebuilt from
///         authoritative inputs can live under a <see cref="SolverDataKey{T}" />.
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
///         [ChunkData("my-mod/fire/exposure")]
///         public static partial ChunkField&lt;float&gt; Exposure { get; }
///     }
/// 
///     [AtmosSolver("my-mod/exposure-decay", After = AtmosBuiltInSolvers.Thermodynamics, RunsPerSecond = 10)]
///     public sealed partial class ExposureDecaySolver : IAtmosWorldSolver
///     {
///         [NumosScratch] private int _chunksVisited; // transient, reset on restore
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
///     }
/// 
///     world.Solvers.Add&lt;ExposureDecaySolver&gt;();
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
    ///         Numos calls this on every custom class solver, enabled or not, in pipeline order. The call happens once
    ///         the restored state is fully installed (including every solver's <c>[NumosSerialize]</c> members) and
    ///         before any replayed tick. The solver's <c>[NumosScratch]</c> members are already reset. That covers
    ///         <see cref="AtmosWorld.RestoreCheckpoint(AtmosWorldCheckpoint)" />, <see cref="AtmosSimulation.RestoreCheckpoint" />, both
    ///         <c>ReplayTo</c> methods, and the restore that puts the previous state back after a failed
    ///         <c>ReplayTo</c>. An instance that a restore created from <see cref="AtmosSolverCatalog" /> gets the call
///         too. A single host call can trigger it more than once, so make it idempotent.
    ///         A timeline seek that continues forward from the current inspection position restores nothing and does
    ///         not call it.
    ///     </para>
    ///     <para>
    ///         Services and other solvers are still registered when this runs. An exception propagates to the caller
    ///         of the restore. The restored state stays installed, but solvers later in the pipeline are not notified.
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
///     portals, docks, or other explicit links. The compiled view then stays limited to the sparse explicit edge
///     set. Numos does not re-derive all six ordinary neighbors of every voxel in every chunk. Use
///     <see langword="true" /> only when the same stage must also traverse ordinary walls, such as fire or sound
///     propagating through both open doorways and portals.
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
    ///     Numos checkpoints this string with the solver's registration and folds it into world state hashing. A
    ///     restored checkpoint therefore compiles topology using the key it was captured with. Give a selection a new
    ///     key when its <see cref="AtmosExplicitLinkSelector" /> changes what it matches. If you reuse a key for a
    ///     different selection, a restored checkpoint silently compiles the wrong edges for it.
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
    ///     Runs the vacuum cleanup pass over one simulation of this tick.
    /// </summary>
    /// <param name="simulation">A simulation from <see cref="Simulations" />.</param>
    /// <exception cref="ArgumentNullException"><paramref name="simulation" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException"><paramref name="simulation" /> is not part of this tick.</exception>
    /// <exception cref="InvalidOperationException">The tick has ended.</exception>
    internal void ApplyVacuumCleanup(AtmosSimulation simulation)
    {
        ArgumentNullException.ThrowIfNull(simulation);
        _solverScope.ThrowIfClosed();
        int index = _solverScope.IndexOf(simulation);
        if (index < 0)
            throw new ArgumentException("The simulation is not registered in the world running this tick.", nameof(simulation));

        VacuumCleanup.Apply(_solverScope.Contexts[index]);
    }

    /// <summary>
    ///     Gets the world executing this callback.
    /// </summary>
    public AtmosWorld World => _solverScope.World;

    /// <summary>
    ///     Gets the one-based world tick currently being executed.
    /// </summary>
    public long TickCount => checked(World.TickCount + 1);

    /// <summary>
    ///     Gets how many ticks pass between runs of this stage.
    /// </summary>
    public int Interval => _solverScope.Interval;

    /// <summary>
    ///     Gets the atmospheric time this run covers, in seconds.
    /// </summary>
    /// <remarks>
    ///     The time since the stage last ran, which is <see cref="Interval" /> divided by
    ///     <see cref="AtmosWorld.TickRate" /> while neither changes. Scale rates by this value instead of assuming a
    ///     fixed step.
    /// </remarks>
    public float DeltaTime => _solverScope.DeltaTime;

    /// <summary>
    ///     Gets the immutable configuration captured for this tick.
    /// </summary>
    /// <remarks>
    ///     The read never locks, so worker threads can use it.
    /// </remarks>
    public AtmosConfigSnapshot Config => _solverScope.Config;

    /// <summary>
    ///     Gets simulations in stable registration order.
    /// </summary>
    public IReadOnlyList<AtmosSimulation> Simulations => _solverScope.PublicSimulations;

    /// <summary>
    ///     Gets the solver-specific compiled neighborhood view.
    /// </summary>
    /// <remarks>
    ///     A stage registered without a neighbor selection receives an empty view here. The view has no Cartesian
    ///     neighbors and no explicit edges, and access does not throw. Pass <c>neighbors</c> to
    ///     <see cref="AtmosWorldSolverPipeline.Register(string, AtmosWorldSolver, AtmosNeighborSelection?, string?, string?, int)" />
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
    ///     Gets one simulation's <typeparamref name="T" /> component, and creates it on first use.
    /// </summary>
    /// <param name="simulation">A simulation from <see cref="Simulations" />.</param>
    /// <typeparam name="T">A <see cref="SimulationComponentAttribute" /> class.</typeparam>
    /// <returns>The one instance of <typeparamref name="T" /> the simulation holds.</returns>
    /// <remarks>
    ///     <para>
    ///         What a stage does to a component is derived work, like its writes to chunk fields. Numos does not record
    ///         it, because replay runs the stage again. A component the stage creates is in the next checkpoint.
    ///     </para>
    ///     <para>
    ///         Worker threads can call this. The lookup takes a short lock of the simulation's own, so call it once per
    ///         simulation outside a voxel loop.
    ///     </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="simulation" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException"><paramref name="simulation" /> is not part of this tick.</exception>
    /// <exception cref="InvalidOperationException">
    ///     The tick has ended, or another class holds the key of <typeparamref name="T" />.
    /// </exception>
    /// <example>
    ///     <code>
    ///     foreach (var simulation in context.Simulations)
    ///     {
    ///         PipeNetwork pipes = context.Get&lt;PipeNetwork&gt;(simulation);
    ///         pipes.Nodes.Add(new PipeNode(pipes.NextId++));
    ///     }
    ///     </code>
    /// </example>
    public T Get<T>(AtmosSimulation simulation) where T : class, ISimulationComponentDefinition<T>
    {
        ThrowIfNotInTick(simulation);
        return simulation.GetOrCreateComponent<T>();
    }

    /// <summary>
    ///     Gets one simulation's <typeparamref name="T" /> component when it has one.
    /// </summary>
    /// <param name="simulation">A simulation from <see cref="Simulations" />.</param>
    /// <param name="component">The component, or <see langword="null" /> when the simulation has none.</param>
    /// <typeparam name="T">A <see cref="SimulationComponentAttribute" /> class.</typeparam>
    /// <returns><see langword="true" /> when the simulation holds the component.</returns>
    /// <remarks>
    ///     The call never creates a component. Worker threads can call it, as they can <see cref="Get{T}" />.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="simulation" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException"><paramref name="simulation" /> is not part of this tick.</exception>
    /// <exception cref="InvalidOperationException">
    ///     The tick has ended, or another class holds the key of <typeparamref name="T" />.
    /// </exception>
    public bool TryGet<T>(AtmosSimulation simulation, [NotNullWhen(true)] out T? component)
        where T : class, ISimulationComponentDefinition<T>
    {
        ThrowIfNotInTick(simulation);
        return simulation.TryGetComponentCore(out component);
    }

    private void ThrowIfNotInTick(AtmosSimulation simulation)
    {
        ArgumentNullException.ThrowIfNull(simulation);
        _solverScope.ThrowIfClosed();
        if (_solverScope.IndexOf(simulation) < 0)
            throw new ArgumentException("The simulation is not registered in the world running this tick.", nameof(simulation));
    }

    /// <summary>
    ///     Gets lock-free views of the pages that hold the world's standalone <see cref="GasMixture" /> instances, in
    ///     index order and valid until the tick ends.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Pages never appear in <see cref="Chunks" />, because cells of a page never neighbor each other and a page
    ///         index is not a position. A transport solver that walks chunks can ignore mixtures. A solver that keeps
    ///         per-mixture data reads it here. The view works when the world has no simulation.
    ///     </para>
    ///     <para>
    ///         Like <see cref="Chunks" />, the views never take a lock, so call this once on the callback thread and
    ///         hand the result to <c>Parallel.For</c> workers.
    ///     </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The tick has ended.</exception>
    /// <example>
    ///     <code>
    ///     foreach (var page in context.GasMixtures)
    ///     {
    ///         Span&lt;float&gt; exposure = page.Get(HeatData.Exposure).AsSpan();
    ///         for (ushort cell = 0; cell &lt; page.CellCount; cell++)
    ///         {
    ///             if (page.IsAllocated(cell))
    ///                 exposure[cell] += 1f;
    ///         }
    ///     }
    ///     </code>
    /// </example>
    public AtmosSolverGasMixtures GasMixtures
    {
        get
        {
            _solverScope.ThrowIfClosed();
            return new AtmosSolverGasMixtures(_solverScope);
        }
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
    ///     <see cref="Chunks" />, this is safe to call from worker threads during the callback. A reference to a
    ///     standalone <see cref="GasMixture" /> has no chunk view, so this returns <see langword="false" /> for it.
    ///     Use <see cref="AtmosSolverGasMixtures.TryGetPage" /> on <see cref="GasMixtures" /> to reach a mixture.
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

    /// <summary>
    ///     Buffers an event for the host. Numos delivers it through <see cref="AtmosWorld.Events" /> after the tick.
    /// </summary>
    /// <typeparam name="T">The event type. Subscriptions match the exact type.</typeparam>
    /// <param name="value">The event. Numos copies it into the buffer.</param>
    /// <remarks>
    ///     <para>
    ///         Use this instead of calling a host service and checking <see cref="AtmosWorld.IsReplaying" />. Numos
    ///         delivers to each subscription only when its <see cref="AtmosEventDelivery" /> allows it, so a replay does
    ///         not repeat sounds or damage. An event that no subscription would receive costs one lookup and allocates
    ///         nothing.
    ///     </para>
    ///     <para>
    ///         Call it on the callback thread. Handlers see events in emit order, and a worker thread would make that
    ///         order differ from run to run. A parallel stage collects its events per chunk inside the loop. After the
    ///         loop it emits them in grid order.
    ///     </para>
    ///     <para>
    ///         Events are not simulation state. A stage must not rely on a handler to change the world, because a
    ///         <see cref="AtmosEventDelivery.LiveOnly" /> handler does not run during a replay.
    ///     </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    ///     Called from a thread other than the callback thread, or after the tick has ended.
    /// </exception>
    /// <example>
    ///     <code>
    ///     var chunks = context.Chunks(simulation);
    ///     var spread = new List&lt;FireSpread&gt;[chunks.Count];
    ///     Parallel.For(0, chunks.Count, index => spread[index] = FindSpread(chunks[index]));
    ///     foreach (var list in spread)
    ///     {
    ///         foreach (var fire in list)
    ///             context.Emit(fire);
    ///     }
    ///     </code>
    /// </example>
    public void Emit<T>(in T value) where T : struct
    {
        _solverScope.ThrowIfClosed();
        if (Environment.CurrentManagedThreadId != _solverScope.ThreadId)
        {
            throw new InvalidOperationException(
                "Emit events on the solver callback thread. A parallel stage collects them per chunk and emits them " +
                "after the loop, in grid order.");
        }

        _solverScope.World.Events.Emit(in value);
    }

    /// <summary>
    ///     Creates a random generator for this tick, this stage, and one stream.
    /// </summary>
    /// <param name="stream">
    ///     A number that tells this draw apart from the stage's other draws in the same tick, such as a voxel index or
    ///     a decision number.
    /// </param>
    /// <returns>
    ///     A generator that starts from <see cref="AtmosConfigSnapshot.RandomSeed" />, <see cref="TickCount" />, the
    ///     stage's name, and <paramref name="stream" />.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         The same inputs always give the same sequence, so a replay draws what the original run drew. Numos stores
    ///         nothing, and the generator needs no checkpoint entry. Two calls with the same stream in one tick and
    ///         stage give the same numbers. Use a different stream for each voxel or decision that must be independent.
    ///     </para>
    ///     <para>
    ///         The call never locks, so worker threads can make it. A parallel stage can then draw per voxel without
    ///         making the result depend on thread timing.
    ///     </para>
    ///     <para>
    ///         Use this instead of <see cref="System.Random" />. A <see cref="System.Random" /> keeps state outside the
    ///         checkpoint, so a replay draws different numbers (<c>NUMOSSOLVER003</c>).
    ///     </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The tick has ended.</exception>
    /// <example>
    ///     <code>
    ///     AtmosRandom random = context.Random(stream: (ulong)cell.LocalVoxelIndex);
    ///     if (random.NextSingle() &lt; chance)
    ///         Ignite(cell);
    ///     </code>
    /// </example>
    public AtmosRandom Random(ulong stream)
    {
        _solverScope.ThrowIfClosed();
        return AtmosRandom.Create(_solverScope.Config.RandomSeed, _solverScope.Tick, _solverScope.StageNameHash, stream);
    }
}

/// <summary>
///     Detached metadata for one registered world solver.
/// </summary>
/// <param name="Name">The unique stage name.</param>
/// <param name="Kind">Whether Numos or the caller supplied the implementation.</param>
/// <param name="IsEnabled">Whether the stage participates in subsequent ticks.</param>
/// <param name="NeighborSelectionKey">The compiled-neighborhood policy key, or <see langword="null" />.</param>
/// <param name="Interval">How many ticks pass between runs of the stage.</param>
public readonly record struct AtmosWorldSolverStep(
    string Name,
    AtmosWorldSolverKind Kind,
    bool IsEnabled,
    string? NeighborSelectionKey,
    int Interval);

/// <summary>
///     One stage of a requested solver layout.
/// </summary>
/// <param name="Name">A built-in stage name or the name of a registered custom stage.</param>
/// <param name="Enabled">Whether the stage participates in subsequent ticks.</param>
/// <param name="Interval">
///     How many ticks pass between runs of the stage. Zero keeps the current interval of a registered stage and gives
///     a new built-in stage its default.
/// </param>
public readonly record struct AtmosWorldSolverLayoutEntry(string Name, bool Enabled, int Interval = 0);
