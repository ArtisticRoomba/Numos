using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using CommunityToolkit.HighPerformance.Helpers;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.CoreSim.EulerFlow;
using Numos.Maths;

namespace Numos.CoreSim.Solvers.Euler;

/// <summary>
///     The <c>euler-flow</c> stage: compressible Euler gas transport across every active chunk, dimensionally split
///     into first-order Godunov sweeps with <see cref="HllcFlux" /> at every face and CFL substeps inside the tick.
/// </summary>
/// <remarks>
///     <para>
///         On every call the stage gathers each active chunk (awake, with at least one gas channel, not skipped as static, plus
///         whatever activation adds) into pooled conserved buffers, runs CFL substeps until the call's time step is used up
///         or the substep cap engages, and writes the buffers back. Trace cells are zeroed and momentum damping is
///         applied before the write-back, so the kinetic energy they remove becomes heat.
///     </para>
///     <para>
///         A settle pass runs once every <see cref="EulerFlowConfig.SettlePeriod" />, counted in ticks by
///         <see cref="AtmosSolverExecutionContext.EulerSettleTicks" />. The pass runs after the write-back, in this
///         order: the velocity floor (inside the write-back), <see cref="EulerSpeciesDiffusion" /> over the whole
///         period, <see cref="VacuumCleanup" />, <see cref="SettleMomentum" />, and last the sleep metric and
///         <see cref="ChunkSleepPolicy" />, which score the pressures diffusion and cleanup left behind. These steps
///         wait for a settle pass because their effect is per period, not per tick: at a 1 ms call the floor would
///         zero a gradient's momentum before it built up. At 20 Hz every call settles. Explicit links are handled
///         by their own stages, not this one.
///     </para>
///     <para>
///         Faces between chunks are ordinary faces. Both chunks solve a shared face from the same two read-buffer
///         cells with the lower coordinate on the left (see <see cref="EulerSweep" />), so there is no
///         reserve-and-merge pass: the two evaluations produce the same bits and each chunk applies its own side
///         of the difference. This relies on one global <c>Δt</c> per substep, sized from the fastest signal in
///         any active chunk.
///     </para>
///     <para>
///         Each substep sweeps the axes in one of the six orders of <see cref="SweepOrders" />. The list is walked
///         so every order is followed by its reverse and no axis is always in the middle. The position is
///         <see cref="AtmosSolverExecutionContext.EulerSweepPhase" />, a counter in [0, 6) that advances by one per
///         substep across ticks, so a run of one-substep ticks still walks all six orders. The kernel checkpoints
///         it, so a restored world continues the cycle.
///     </para>
///     <para>
///         The active set grows during the tick. An open face into a chunk with no workspace is a wall for the
///         sweep that meets it, and that sweep requests the chunk's activation when the two sides differ in
///         pressure by more than <see cref="EulerFlowConfig.WakePressureDelta" /> or the active side moves normal
///         to the face faster than <see cref="EulerFlowConfig.WakeSpeed" />. Requests are applied on the tick
///         thread after the barrier that ends the sweep: the chunk is woken if asleep, gathered from its live
///         fields (exact, nothing has written them this tick), and inserted into the active list in canonical
///         chunk order. A pre-pass right after the initial gather runs
///         the same test over every boundary face from the live pressures until no new chunk joins, so only a
///         front that arrives mid-tick pays the one-sweep wall latency. Activation is derived state and is never
///         recorded.
///     </para>
///     <para>
///         An awake chunk can also go without a workspace when a sweep could not change it. See
///         <see cref="EulerStaticChunk" /> for the test and <see cref="ActivateWithinReach" /> for the distance
///         rule. Such a chunk stays awake, so its sleep timer still advances at the end of a settle pass.
///         Its signal speed still counts toward the shared time step, so the substeps match an all-swept tick.
///         The skip changes no bit of any chunk, and a tick in which nothing is swept leaves
///         <see cref="LastDiagnostics" /> at its default.
///     </para>
///     <para>
///         The stage keeps nothing between ticks beyond the chunk's own momentum arrays. The stage rebuilds the buffers
///         from the chunk fields every tick. The static-test passes it keeps are a cache keyed on the chunk
///         version, so they change no result. See docs/atmospherics_technical_documentation.md section 5 for the
///         derivation.
///     </para>
/// </remarks>
internal sealed class EulerFlowSolver : IAtmosSolverStage
{
    /// <summary>
    ///     Worker count the schedule arithmetic uses instead of <see cref="Environment.ProcessorCount" />. Tests set
    ///     it to force the whole-chunk schedule (1) or the tiled one (more than the active chunk count) and compare
    ///     the results. It changes the schedule only, not which threads run it.
    /// </summary>
    internal static int? WorkerCountOverride;

    /// <summary>
    ///     The <see cref="SettleMomentum" /> floor that stops every voxel with a finite speed.
    /// </summary>
    private const MetrePerSecond StopEveryVoxel = float.PositiveInfinity;

    /// <summary>
    ///     Whether static chunks out of reach of every source skip the sweeps (see <see cref="EulerStaticChunk" />).
    ///     Tests clear it to compare against the all-swept result. The skip changes no bit, so it is not a
    ///     configuration field.
    /// </summary>
    internal static bool SkipStaticChunks = true;

    /// <summary>
    ///     Whether a workspace keeps its face masks and air map from an earlier binding when its geometry key
    ///     still matches (see <see cref="EulerChunkWorkspace.HasGeometryForBinding" />). Tests clear it to compare
    ///     against a rebuild on every call. The reuse changes no bit, so it is not a configuration field.
    /// </summary>
    internal static bool ReuseFaceGeometry = true;

    /// <summary>
    ///     Replaces the acoustic scaling factor the rule would pick, for every call. Tests set it. Null leaves the
    ///     choice to <see cref="EulerAcousticScaling.ChooseScale" />.
    /// </summary>
    internal static float? ForcedSoundSpeedReduction;

    /// <summary>
    ///     Runs a scaled call as one group over the whole active list, without building flow groups. Tests set it to
    ///     compare the group path with the single-loop path on a scene that has one group.
    /// </summary>
    internal static bool GroupingDisabled;

    /// <summary>
    ///     How many chunks the most recent tick skipped. A test hook, shared by every solver instance.
    /// </summary>
    internal static int LastSkippedCount;

    /// <summary>
    ///     How many links between flow groups the most recent call cut and restored. A test hook, shared by every
    ///     solver instance.
    /// </summary>
    internal static int LastCutLinkCount;

    /// <summary>
    ///     How many batches of flow groups the most recent call ran: 0 when it took the single loop over the whole
    ///     active list. A test hook, shared by every solver instance.
    /// </summary>
    internal static int LastBatchCount;

    /// <summary>
    ///     The sweep phase that follows <paramref name="phase" /> in the six-order cycle.
    /// </summary>
    internal static int NextSweepPhase(int phase) => phase == SweepOrderCount - 1 ? 0 : phase + 1;

    private const int SweepOrderCount = 6;

    /// <summary>
    ///     The axis orders a substep can sweep in, as consecutive triples: <c>XYZ, ZYX, YZX, XZY, ZXY, YXZ</c>, each
    ///     order followed by its reverse.
    /// </summary>
    private static ReadOnlySpan<byte> SweepOrders => [0, 1, 2, 2, 1, 0, 1, 2, 0, 0, 2, 1, 2, 0, 1, 1, 0, 2];

    private readonly Int3 _dimensions;
    private readonly List<AtmosChunk> _pendingActivation = [];
    private readonly List<EulerChunkWorkspace> _pendingGather = [];
    private readonly List<EulerChunkWorkspace> _pendingScan = [];
    private readonly List<EulerChunkWorkspace> _pool = [];
    private EulerChunkWorkspace[] _active = [];
    private int _activeCount;
    private readonly List<AtmosChunk> _frontier = [];
    private readonly List<AtmosChunk> _reached = [];
    private readonly HashSet<AtmosChunk> _rangeSet = [];
    private readonly List<AtmosChunk> _seeds = [];
    private readonly HashSet<AtmosChunk> _seedSet = [];
    private MetrePerSecond _skippedSignalSpeed;

    /// <summary>
    ///     The acoustic scaling factor of the running call, 1 outside the substep loop. <see cref="Activate" />
    ///     scales the momentum of a chunk activated mid-call by it.
    /// </summary>
    private float _scale = 1f;

    private readonly EulerFlowGroups _groups = new();
    private readonly EulerFlowGroups.Group _wholeList = new();

    /// <summary>
    ///     The batch whose loop is running, or -1 outside a batch loop and while the call runs as one list.
    ///     <see cref="Activate" /> assigns a chunk activated mid-loop to it.
    /// </summary>
    private int _runningGroup = -1;
    private int _visitGeneration;
    private int[] _visitMarks = [];
    private MetrePerSecond[] _staticSpeed = [];
    private bool[] _static = [];

    /// <summary>
    ///     The chunk version at which each chunk's interior last passed the static test, by dense id, kept across
    ///     calls (see <see cref="EulerStaticChunk.IsStatic" />). Only passes are stored.
    /// </summary>
    /// <remarks>
    ///     A recycled dense id carries the old chunk's version, whose generation no new chunk shares, so a stale
    ///     slot never matches. Restore and config changes clear it anyway. The static test runs in parallel, and
    ///     each chunk writes only its own slot.
    /// </remarks>
    private AtmosChunkVersion[] _interiorPassedAt = [];
    private EulerChunkWorkspace?[] _workspaceByDenseId = [];

    /// <summary>
    ///     The workspace each chunk was last bound to, by dense id, kept across ticks. A hint for
    ///     <see cref="TakeFromPool" /> only, so a stale entry costs a rebuild and nothing else.
    /// </summary>
    private EulerChunkWorkspace?[] _lastBoundByDenseId = [];
    private PencilWorkItem[] _workItems = [];

    // The voxel tiles of the current gather or write-back phase and one result slot per tile.
    private VoxelTile[] _voxelTiles = [];
    private MetrePerSecond[] _tileSignalSpeed = [];
    private int[] _tileCount = [];

    internal EulerFlowSolver(int chunkWidth, int chunkHeight, int chunkDepth)
    {
        _dimensions = new Int3(chunkWidth, chunkHeight, chunkDepth);
    }

    /// <summary>
    ///     What the most recent <see cref="Solve" /> did. All zeros before the first tick.
    /// </summary>
    internal AtmosFlowDiagnostics LastDiagnostics { get; private set; }

    private static int WorkerCount => Math.Max(1, WorkerCountOverride ?? Environment.ProcessorCount);

    public void Solve(AtmosSolverExecutionContext context)
    {
        AtmosSolverConfigSnapshot config = context.TickConfig;
        EulerFlowConfig settings = config.EulerFlow;
        int substepCap = SubstepCap(settings.MaxSubstepsPerSecond, context.Interval, context.TickRate);
        int elapsedTicks = (int)Math.Min((long)context.EulerSettleTicks + context.Interval, int.MaxValue);
        bool settle = elapsedTicks >= SettleThresholdTicks(settings.SettlePeriod, context.TickRate);
        var pass = new SettlePass(
            settle, elapsedTicks, BuiltInStageSchedule.GapTime(elapsedTicks, context.TickRate));
        try
        {
            _skippedSignalSpeed = 0f;
            LastSkippedCount = 0;
            if (_static.Length < context.Chunks.Length)
            {
                _static = new bool[Math.Max(context.Chunks.Length, _static.Length * 2)];
                _staticSpeed = new MetrePerSecond[_static.Length];
            }

            if (SkipStaticChunks)
            {
                EnsureInteriorSlots(context.Chunks);
                ParallelPhase.Run(
                    context.Chunks.Length, new StaticAction(context, _static, _staticSpeed, _interiorPassedAt));
            }
            else
                Array.Clear(_static, 0, context.Chunks.Length);

            int staticCount = 0;
            for (int chunkIndex = 0; chunkIndex < context.Chunks.Length; chunkIndex++)
            {
                AtmosChunk chunk = context.Chunks[chunkIndex];
                if (!chunk.IsAwake || chunk.ActiveGasCount == 0)
                    continue;

                if (_static[chunkIndex])
                    staticCount++;
                else
                    Activate(chunk, context);
            }

            GatherPending(config);
            ActivateNeighboursAtRest(context);
            if (staticCount > 0 && _activeCount > 0)
            {
                ActivateWithinReach(context, substepCap);
                for (int chunkIndex = 0; chunkIndex < context.Chunks.Length; chunkIndex++)
                {
                    AtmosChunk chunk = context.Chunks[chunkIndex];
                    if (!IsSkipped(chunk))
                        continue;

                    _skippedSignalSpeed = MathF.Max(_skippedSignalSpeed, _staticSpeed[chunkIndex]);
                    LastSkippedCount++;
                }
            }
            else
            {
                LastSkippedCount = staticCount;
            }

            if (_activeCount == 0)
            {
                LastDiagnostics = default;
                // Cleanup normally follows the sweeps, so skipped chunks still owe it when nothing is swept.
                if (settle && staticCount > 0)
                    VacuumCleanup.Apply(context);
            }
            else
            {
                LastDiagnostics = Advance(context, settings, substepCap, pass);
            }

            // An awake chunk that never got a workspace has nothing to move, but its sleep timer still has to
            // advance. Chunks the stage put to sleep are no longer awake here. Like every sleep decision, the
            // update waits for a settle pass and covers the whole period. Snapshots carry the timer, so a chunk
            // whose timer moved is marked changed. The static test does not read the timer, so a pass stored at the
            // version just before moves to the new version.
            if (settle)
            {
                for (int chunkIndex = 0; chunkIndex < context.Chunks.Length; chunkIndex++)
                {
                    AtmosChunk chunk = context.Chunks[chunkIndex];
                    if (!chunk.IsAwake || LookupWorkspace(chunk) != null)
                        continue;

                    int timer = chunk.SleepTimer;
                    AtmosChunkVersion before = chunk.Version;
                    ChunkSleepPolicy.UpdateSleepState(chunk, config, 0f, elapsedTicks, context.SleepDelayTicks);
                    if (chunk.SleepTimer == timer)
                        continue;

                    chunk.MarkChanged();
                    if (chunk.IsAwake && chunk.DenseId < _interiorPassedAt.Length &&
                        _interiorPassedAt[chunk.DenseId] == before)
                        _interiorPassedAt[chunk.DenseId] = chunk.Version;
                }
            }

            context.EulerSettleTicks = settle ? 0 : elapsedTicks;
        }
        finally
        {
            for (int index = 0; index < _activeCount; index++)
            {
                EulerChunkWorkspace workspace = _active[index];
                _workspaceByDenseId[workspace.Chunk.DenseId] = null;
                workspace.PoolSlot = _pool.Count;
                _pool.Add(workspace);
                _active[index] = null!;
            }

            _activeCount = 0;
            _scale = 1f;
            _runningGroup = -1;
            _groups.DiscardCuts();
            _skippedSignalSpeed = 0f;
            _pendingScan.Clear();
            _pendingActivation.Clear();
            _pendingGather.Clear();
        }
    }

    /// <summary>
    ///     Forgets the chunk-to-workspace lookups and every workspace's geometry key. Called when a checkpoint
    ///     replaces the kernel's chunks.
    ///     The pool stays: chunk dimensions are fixed per solver and <see cref="EulerChunkWorkspace.Bind" /> resets
    ///     every workspace, so pooled instances hold no simulation state.
    /// </summary>
    internal void ClearTransientState()
    {
        Array.Clear(_workspaceByDenseId);
        Array.Clear(_lastBoundByDenseId);
        ForgetStaticPasses();
        foreach (EulerChunkWorkspace workspace in _pool)
            workspace.InvalidateGeometry();

        for (int index = 0; index < _activeCount; index++)
            _active[index].InvalidateGeometry();
    }

    /// <summary>
    ///     Drops every stored static-test pass, so the next call scans every awake chunk's interior again. Called
    ///     on restore and when the configuration changes.
    /// </summary>
    internal void ForgetStaticPasses()
    {
        Array.Clear(_interiorPassedAt);
    }

    /// <summary>
    ///     Grows <see cref="_interiorPassedAt" /> to cover every dense id in <paramref name="chunks" />. Tick thread
    ///     only, before the parallel static test.
    /// </summary>
    private void EnsureInteriorSlots(AtmosChunk[] chunks)
    {
        int maximum = -1;
        foreach (AtmosChunk chunk in chunks)
            maximum = Math.Max(maximum, chunk.DenseId);

        if (maximum >= _interiorPassedAt.Length)
            Array.Resize(ref _interiorPassedAt, Math.Max(maximum + 1, Math.Max(4, _interiorPassedAt.Length * 2)));
    }

    /// <summary>
    ///     The substep cap of one call: <c>ceil(maxSubstepsPerSecond · interval / tickRate)</c> in integer arithmetic.
    /// </summary>
    /// <param name="maxSubstepsPerSecond">The configured cap per second of gas time. Zero or less means no cap.</param>
    /// <param name="interval">The ticks the call spans.</param>
    /// <param name="tickRate">Ticks per second.</param>
    /// <returns>The cap, at least 1, or 0 for no cap.</returns>
    internal static int SubstepCap(int maxSubstepsPerSecond, int interval, int tickRate)
    {
        if (maxSubstepsPerSecond <= 0)
            return 0;

        long cap = ((long)maxSubstepsPerSecond * interval + tickRate - 1) / tickRate;
        return (int)Math.Clamp(cap, 1L, int.MaxValue);
    }

    /// <summary>
    ///     The ticks between settle passes: <c>max(1, round(settlePeriod · tickRate))</c>, saturating at
    ///     <see cref="int.MaxValue" />.
    /// </summary>
    /// <param name="settlePeriod">The configured period in seconds, already normalized.</param>
    /// <param name="tickRate">Ticks per second.</param>
    internal static int SettleThresholdTicks(Second settlePeriod, int tickRate)
    {
        return Math.Max(1, BuiltInStageSchedule.SecondsToTicks(settlePeriod, tickRate));
    }

    /// <summary>
    ///     Whether a call runs the settle pass, and the span the pass covers.
    /// </summary>
    /// <param name="Active">Whether the settle pass runs on this call.</param>
    /// <param name="ElapsedTicks">The ticks since the last pass, including this call.</param>
    /// <param name="Time">The gas time of those ticks, from <see cref="BuiltInStageSchedule.GapTime" />.</param>
    private readonly record struct SettlePass(bool Active, int ElapsedTicks, Second Time);

    /// <summary>
    ///     Gives a chunk a workspace for the rest of the tick: wakes it if asleep, links it to its active
    ///     neighbours, and queues it for <see cref="GatherPending" />. Tick thread only.
    /// </summary>
    /// <remarks>
    ///     The workspace is not prepared yet. Its buffers, face masks, and air map are not valid until the caller
    ///     runs <see cref="GatherPending" />, which every batch of activations must do before anything reads them.
    ///     Only the pool take, the neighbour links, and the active-list insertion happen here, because they depend
    ///     on activation order.
    ///     The workspace lists only the chunk's own gases here. Rows for gases a neighbour can push across a face
    ///     are added by <see cref="ShareAxisGases" /> before each sweep, so a chunk activated mid-tick is covered
    ///     by the next sweep's merge like everyone else.
    ///     During a batch's loop the chunk joins that batch, and its links to other batches' workspaces are cut
    ///     (see <see cref="EulerFlowGroups" />).
    /// </remarks>
    private void Activate(AtmosChunk chunk, AtmosSolverExecutionContext context)
    {
        if (!chunk.IsAwake)
            chunk.Wake();

        EulerChunkWorkspace workspace = TakeFromPool(chunk);
        workspace.Attach(chunk);
        workspace.Group = Math.Max(_runningGroup, 0);
        for (int direction = 0; direction < NeighborResolution.NeighborDirections.Length; direction++)
        {
            if (!context.TryGetChunk(chunk.GridPosition + NeighborResolution.NeighborDirections[direction], out AtmosChunk neighbor))
                continue;

            workspace.NeighborChunks[direction] = neighbor;
            EulerChunkWorkspace? neighborWorkspace = LookupWorkspace(neighbor);
            if (neighborWorkspace != null && _runningGroup >= 0 && neighborWorkspace.Group != _runningGroup)
            {
                // The running batch's sweep must not read another batch's buffers. The face stays a wall for both.
                _groups.RecordCut(workspace, direction, neighborWorkspace);
                continue;
            }

            workspace.NeighborWorkspaces[direction] = neighborWorkspace;
            if (neighborWorkspace != null)
                neighborWorkspace.NeighborWorkspaces[direction ^ 1] = workspace;
        }

        // Stored momentum is real. A chunk joining a scaled call gathers it scaled.
        EulerAcousticScaling.ScaleUp(chunk, _scale);
        _pendingGather.Add(workspace);

        // Canonical order has no numerical effect. It keeps diagnostics reproducible.
        int position = _activeCount;
        while (position > 0 &&
               AtmosSolverMath.CompareChunkPositions(_active[position - 1].Chunk.GridPosition, chunk.GridPosition) > 0)
            position--;

        if (_active.Length == _activeCount)
            Array.Resize(ref _active, Math.Max(4, _activeCount * 2));

        Array.Copy(_active, position, _active, position + 1, _activeCount - position);
        _active[position] = workspace;
        _activeCount++;

        if (chunk.DenseId >= _workspaceByDenseId.Length)
        {
            int length = Math.Max(chunk.DenseId + 1, Math.Max(4, _workspaceByDenseId.Length * 2));
            Array.Resize(ref _workspaceByDenseId, length);
            Array.Resize(ref _lastBoundByDenseId, length);
        }

        _workspaceByDenseId[chunk.DenseId] = workspace;
        _lastBoundByDenseId[chunk.DenseId] = workspace;
        _pendingScan.Add(workspace);
    }

    /// <summary>
    ///     Takes a workspace out of the pool for <paramref name="chunk" />, or creates one. Tick thread only.
    /// </summary>
    /// <remarks>
    ///     The workspace the chunk had last is preferred while it is still pooled, because its face masks then
    ///     usually match and <see cref="PrepareForGather" /> skips the rebuild. The choice changes no bit.
    ///     <see cref="EulerChunkWorkspace.HasGeometryForBinding" /> alone decides whether masks are reused, and
    ///     <see cref="EulerChunkWorkspace.Prepare" /> resets everything else.
    /// </remarks>
    private EulerChunkWorkspace TakeFromPool(AtmosChunk chunk)
    {
        EulerChunkWorkspace? workspace = null;
        if (chunk.DenseId < _lastBoundByDenseId.Length)
        {
            EulerChunkWorkspace? last = _lastBoundByDenseId[chunk.DenseId];
            // Dense ids are recycled and a workspace moves on to other chunks, so check it still names this one.
            if (last is { PoolSlot: >= 0 } && ReferenceEquals(last.Chunk, chunk))
                workspace = last;
        }

        if (workspace == null)
        {
            if (_pool.Count == 0)
                return new EulerChunkWorkspace();

            workspace = _pool[^1];
        }

        // Swap-remove: the last pooled workspace moves into the freed slot.
        int slot = workspace.PoolSlot;
        EulerChunkWorkspace moved = _pool[^1];
        _pool[slot] = moved;
        moved.PoolSlot = slot;
        _pool.RemoveAt(_pool.Count - 1);
        workspace.PoolSlot = -1;
        return workspace;
    }

    /// <summary>
    ///     Prepares and gathers every workspace activated since the last call. Tick thread only.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Each workspace is prepared and gets its face geometry once, then its voxels are gathered in the
    ///         tiles of <see cref="BuildVoxelTiles" />. With at least as many workspaces as workers, one item per
    ///         workspace does both. Otherwise one phase prepares the workspaces and a second gathers the tiles.
    ///     </para>
    ///     <para>
    ///         Each item reads its chunk, the neighbour chunks' room maps, and its own neighbour slots, which are
    ///         final once <see cref="Activate" /> returns. It writes only its own workspace, and a tile writes only
    ///         its own voxels and result slot. The tick thread folds the tile results in tile order, with a max and
    ///         an integer sum. The batch result therefore does not depend on which worker runs which item.
    ///     </para>
    /// </remarks>
    private void GatherPending(AtmosSolverConfigSnapshot config)
    {
        int count = _pendingGather.Count;
        if (count == 0)
            return;

        bool whole = count >= WorkerCount;
        if (!whole)
            ParallelPhase.Run(count, new PrepareGatherAction(_pendingGather, config));

        ReadOnlySpan<EulerChunkWorkspace> batch = CollectionsMarshal.AsSpan(_pendingGather);
        int tileCount = BuildVoxelTiles(batch, whole);
        ParallelPhase.Run(
            tileCount, new GatherTileAction(_pendingGather, _voxelTiles, _tileSignalSpeed, _tileCount, config, whole));

        // Prepare zeroed both results, so a chunk with no tile keeps the values of an empty gather.
        for (int tile = 0; tile < tileCount; tile++)
        {
            EulerChunkWorkspace workspace = batch[_voxelTiles[tile].WorkspaceIndex];
            workspace.Read.MaxSignalSpeed = MathF.Max(workspace.Read.MaxSignalSpeed, _tileSignalSpeed[tile]);
            workspace.NonFiniteCellsGathered += _tileCount[tile];
        }

        _pendingGather.Clear();
    }

    /// <summary>
    ///     Prepares a workspace for its gather tiles: sizes and clears its buffers, builds its face geometry unless
    ///     the cached geometry still matches, and looks up its gather rows.
    /// </summary>
    private static void PrepareForGather(EulerChunkWorkspace workspace, AtmosSolverConfigSnapshot config)
    {
        workspace.Prepare(config);
        if (!ReuseFaceGeometry || !workspace.HasGeometryForBinding())
        {
            EulerFaceTopology.Build(workspace.Chunk, workspace.NeighborChunks, workspace.OpenMask, workspace.VoidMask);
            EulerFaceTopology.BuildAirMap(workspace.Chunk, workspace.AirMap, workspace.AirRunLength);
            workspace.RecordGeometry();
        }

        workspace.BeginGather();
    }

    /// <summary>
    ///     Splits the active-air lists of <paramref name="workspaces" /> into the tiles of one parallel phase, in
    ///     workspace order and ascending list position.
    /// </summary>
    /// <param name="workspaces">The workspaces to tile.</param>
    /// <param name="whole">Whether every workspace is one tile, the schedule for at least as many workspaces as workers.</param>
    /// <returns>The number of tiles written to <see cref="_voxelTiles" />.</returns>
    /// <remarks>
    ///     Otherwise the z-slabs of each chunk are cut into ranges of <c>ceil(slabs × workspaces / workers)</c>, as
    ///     <see cref="RunSweep" /> cuts pencils, and each range becomes the list positions of its voxels. That needs
    ///     a strictly ascending list (<see cref="EulerChunkWorkspace.ActiveAirAscending" />), so any other chunk
    ///     stays one tile. A slab range with no air voxel gets no tile.
    /// </remarks>
    private int BuildVoxelTiles(ReadOnlySpan<EulerChunkWorkspace> workspaces, bool whole)
    {
        int slabCount = _dimensions.Z;
        int tileSlabs = whole
            ? slabCount
            : Math.Max(1, ParallelPhase.DivideRoundUp(checked(slabCount * workspaces.Length), WorkerCount));
        int maxTiles = checked(workspaces.Length * ParallelPhase.DivideRoundUp(slabCount, tileSlabs));
        if (_voxelTiles.Length < maxTiles)
        {
            int length = Math.Max(maxTiles, _voxelTiles.Length * 2);
            _voxelTiles = new VoxelTile[length];
            _tileSignalSpeed = new MetrePerSecond[length];
            _tileCount = new int[length];
        }

        int tile = 0;
        for (int index = 0; index < workspaces.Length; index++)
        {
            EulerChunkWorkspace workspace = workspaces[index];
            AtmosChunk chunk = workspace.Chunk;
            int count = chunk.ActiveAirCount;
            if (tileSlabs >= slabCount || !workspace.ActiveAirAscending)
            {
                _voxelTiles[tile++] = new VoxelTile(index, 0, count);
                continue;
            }

            ReadOnlySpan<ushort> indices = chunk.ActiveAirIndices.AsSpan(0, count);
            int slabVoxels = chunk.Width * chunk.Height;
            int first = 0;
            for (int endSlab = tileSlabs; first < count; endSlab += tileSlabs)
            {
                int end = endSlab >= slabCount ? count : LowerBound(indices, endSlab * slabVoxels);
                if (end > first)
                    _voxelTiles[tile++] = new VoxelTile(index, first, end);

                first = end;
            }
        }

        return tile;
    }

    /// <summary>
    ///     The first position in an ascending list whose voxel index is at least <paramref name="voxel" />.
    /// </summary>
    private static int LowerBound(ReadOnlySpan<ushort> indices, int voxel)
    {
        // BinarySearch returns the complement of the insertion point when the value is absent.
        int found = indices.BinarySearch((ushort)voxel);
        return found >= 0 ? found : ~found;
    }

    /// <summary>
    ///     Whether every entry is greater than the one before it, so slab ranges map to list ranges and no voxel
    ///     appears twice.
    /// </summary>
    private static bool IsStrictlyAscending(ReadOnlySpan<ushort> indices)
    {
        int index = 0;
        if (Vector.IsHardwareAccelerated)
        {
            // Compares a vector of entries with the vector one position later.
            int width = Vector<ushort>.Count;
            for (; index + width < indices.Length; index += width)
            {
                var current = new Vector<ushort>(indices.Slice(index));
                var next = new Vector<ushort>(indices.Slice(index + 1));
                if (Vector.GreaterThanOrEqualAny(current, next))
                    return false;
            }
        }

        for (; index + 1 < indices.Length; index++)
        {
            if (indices[index] >= indices[index + 1])
                return false;
        }

        return true;
    }

    /// <summary>
    ///     The workspace a chunk holds this tick, or <see langword="null" />. Dense ids are recycled, so the slot
    ///     is only trusted when its workspace is bound to this very chunk.
    /// </summary>
    private EulerChunkWorkspace? LookupWorkspace(AtmosChunk chunk)
    {
        if (chunk.DenseId >= _workspaceByDenseId.Length)
            return null;

        EulerChunkWorkspace? workspace = _workspaceByDenseId[chunk.DenseId];
        return workspace != null && ReferenceEquals(workspace.Chunk, chunk) ? workspace : null;
    }

    /// <summary>
    ///     The activation pre-pass: scans every boundary face of every active chunk against its inactive
    ///     neighbours, activates what the sweep test would activate, and repeats for the newcomers until the set
    ///     is closed. Bounded by the chunk count.
    /// </summary>
    /// <remarks>
    ///     Both sides are compared through their live <see cref="AtmosChunk.TotalPressure" /> here, not through
    ///     the gathered cell pressure, so two chunks filled identically compare exactly equal even with a zero
    ///     wake delta. The gathered pressure round-trips through the energy and can differ in the last bit.
    ///     The scans run in parallel. A scan sets wake flags only on its own workspace, and no activation runs
    ///     until the scans finish. <see cref="ApplyWakeRequests" /> then reads the flags in canonical order, so the
    ///     activation order does not depend on the worker count.
    /// </remarks>
    private void ActivateNeighboursAtRest(AtmosSolverExecutionContext context)
    {
        EulerFlowConfig settings = context.TickConfig.EulerFlow;
        Pascal wakeDelta = settings.WakePressureDelta;
        MetrePerSecond wakeSpeed = settings.WakeSpeed;
        while (_pendingScan.Count > 0)
        {
            ParallelPhase.Run(_pendingScan.Count, new ScanAction(_pendingScan, wakeDelta, wakeSpeed));

            _pendingScan.Clear();
            ApplyWakeRequests(context);
        }
    }

    /// <summary>
    ///     Sets <see cref="EulerChunkWorkspace.WakeRequests" /> for each face that opens onto a chunk without a
    ///     workspace and would wake it.
    /// </summary>
    /// <remarks>
    ///     Only faces that can still wake something are walked: a registered neighbour, no workspace for it, and
    ///     no request yet. A walk stops at the first voxel that wakes. Open bits are set on air voxels only, so the
    ///     open-bit test stands in for the active-air walk.
    /// </remarks>
    private static void ScanBoundaryFaces(
        EulerChunkWorkspace workspace, Pascal wakeDelta, MetrePerSecond wakeSpeed)
    {
        for (int direction = 0; direction < NeighborResolution.NeighborDirections.Length; direction++)
        {
            if (workspace.NeighborChunks[direction] == null || workspace.NeighborWorkspaces[direction] != null ||
                workspace.WakeRequests[direction])
                continue;

            if (FaceWakesNeighbour(workspace, direction, wakeDelta, wakeSpeed))
                workspace.WakeRequests[direction] = true;
        }
    }

    private static bool FaceWakesNeighbour(
        EulerChunkWorkspace workspace, int direction, Pascal wakeDelta, MetrePerSecond wakeSpeed)
    {
        AtmosChunk chunk = workspace.Chunk;
        AtmosChunk neighbor = workspace.NeighborChunks[direction]!;
        EulerChunkState read = workspace.Read;
        int axis = direction >> 1;
        FacePlane plane = EulerFaceTopology.GetFacePlane(chunk, direction);
        for (int outer = 0; outer < plane.OuterCount; outer++)
        {
            for (int inner = 0; inner < plane.InnerCount; inner++)
            {
                ushort voxelIndex = plane.VoxelIndex(inner, outer);
                if (!EulerFaceTopology.IsOpen(workspace.OpenMask[voxelIndex], direction))
                    continue;

                ushort neighborIndex = EulerFaceTopology.NeighborIndexAcrossFace(chunk, voxelIndex, direction);
                MetrePerSecond normalVelocity = read.CellVelocity(axis)[voxelIndex];
                Pascal pressureDelta = chunk.TotalPressure[voxelIndex] - neighbor.TotalPressure[neighborIndex];
                if (EulerSweep.WakesNeighbor(normalVelocity, pressureDelta, wakeSpeed, wakeDelta))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Activates every static chunk that a source could reach this tick. The chunks left without a workspace
    ///     are exactly the ones a sweep could not change. Tick thread only.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A source is a chunk active after the pre-pass. A first-order split sweep moves information at most
    ///         one cell per substep along each axis. A call runs at most <see cref="SubstepCap" /> substeps, and the
    ///         species diffusion of a settle pass adds one cell. Along an axis of <c>n</c> voxels a signal therefore
    ///         stays within <c>R = ceil((cap + 2) / n)</c> chunks of its source. Each axis has its own
    ///         <c>R</c>, so an axis one voxel long does not widen the other two. A chunk more than <c>R</c> chunks
    ///         away along some axis has more than <c>cap + 1</c> cells between it and any cell of the source.
    ///     </para>
    ///     <para>
    ///         The search walks the six face directions from each source through every registered chunk, in any
    ///         state, and never leaves that source's box of <c>R</c> chunks per axis. It crosses a chunk face only
    ///         when <see cref="EulerFaceTopology.HasOpenFace" /> holds, because a signal crosses from chunk to chunk
    ///         only through an open air-to-air face. Every cell on the signal's path lies inside the box, so the
    ///         search finds every chunk the signal can reach. A diagonal step inside one substep passes through a
    ///         cell of a face-adjacent chunk. Without a substep cap the box has no limit. An activated static chunk
    ///         is not a source, because it changes nothing until a source reaches it. The search repeats only when
    ///         the pre-pass wakes a chunk that was not static.
    ///     </para>
    ///     <para>
    ///         A skipped chunk can sit next to an active one behind a sealed face. That face is a wall for the sweep
    ///         whether the neighbour is swept or not. The sweep, the pre-pass, the diffusion boundary pass, and the
    ///         sleep score read a neighbour only across an open face. <see cref="VacuumCleanup" /> reads every air
    ///         neighbour, but a skipped chunk keeps the pressures a sweep would give it.
    ///     </para>
    /// </remarks>
    private void ActivateWithinReach(AtmosSolverExecutionContext context, int bound)
    {
        bool unbounded = bound == 0;
        Int3 reach = unbounded
            ? default
            : new Int3(
                ParallelPhase.DivideRoundUp(bound + 2, _dimensions.X),
                ParallelPhase.DivideRoundUp(bound + 2, _dimensions.Y),
                ParallelPhase.DivideRoundUp(bound + 2, _dimensions.Z));
        _seeds.Clear();
        _seedSet.Clear();
        _rangeSet.Clear();
        for (int index = 0; index < _activeCount; index++)
        {
            AtmosChunk chunk = _active[index].Chunk;
            _seeds.Add(chunk);
            _seedSet.Add(chunk);
        }

        while (true)
        {
            _reached.Clear();
            FindChunksWithin(context, reach, unbounded);
            if (_reached.Count == 0)
                break;

            foreach (AtmosChunk chunk in _reached)
                Activate(chunk, context);

            GatherPending(context.TickConfig);
            ActivateNeighboursAtRest(context);

            bool newSource = false;
            for (int index = 0; index < _activeCount; index++)
            {
                AtmosChunk chunk = _active[index].Chunk;
                if (_seedSet.Contains(chunk) || _rangeSet.Contains(chunk))
                    continue;

                _seeds.Add(chunk);
                _seedSet.Add(chunk);
                newSource = true;
            }

            if (!newSource)
                break;
        }
    }

    private bool IsSkipped(AtmosChunk chunk)
    {
        return chunk.IsAwake && chunk.ActiveGasCount > 0 && LookupWorkspace(chunk) == null;
    }

    /// <summary>
    ///     Adds to <see cref="_reached" /> and <see cref="_rangeSet" /> every skipped chunk the search from some seed
    ///     enters through an open chunk face, once.
    /// </summary>
    /// <param name="context">The tick context, used to find neighbour chunks.</param>
    /// <param name="reach">The largest chunk offset from a seed along each axis. Ignored when unbounded.</param>
    /// <param name="unbounded">Whether the search has no box, which is the case without a substep cap.</param>
    /// <remarks>
    ///     Each seed has its own box, so a chunk one seed passed through can still be entered from another seed's
    ///     box, and each seed starts with a fresh visited mark. Without a box every seed reaches the same chunks, so
    ///     the marks are kept across seeds. A chunk is marked only when the search enters it, so a sealed face
    ///     leaves it open to another path. The walk order does not matter, because a chunk is entered exactly when
    ///     some path of open faces inside the box leads to it.
    /// </remarks>
    private void FindChunksWithin(AtmosSolverExecutionContext context, Int3 reach, bool unbounded)
    {
        if (unbounded)
            BeginVisit();

        foreach (AtmosChunk seed in _seeds)
        {
            if (!unbounded)
                BeginVisit();

            if (IsVisited(seed))
                continue;

            MarkVisited(seed);

            _frontier.Clear();
            _frontier.Add(seed);
            while (_frontier.Count > 0)
            {
                AtmosChunk chunk = _frontier[^1];
                _frontier.RemoveAt(_frontier.Count - 1);
                for (int direction = 0; direction < NeighborResolution.NeighborDirections.Length; direction++)
                {
                    Int3 position = chunk.GridPosition + NeighborResolution.NeighborDirections[direction];
                    if (!unbounded && !IsWithinBox(position - seed.GridPosition, reach))
                        continue;

                    if (!context.TryGetChunk(position, out AtmosChunk neighbor) || IsVisited(neighbor) ||
                        !EulerFaceTopology.HasOpenFace(chunk, direction, neighbor))
                        continue;

                    MarkVisited(neighbor);
                    _frontier.Add(neighbor);
                    if (IsSkipped(neighbor) && _rangeSet.Add(neighbor))
                        _reached.Add(neighbor);
                }
            }
        }
    }

    private static bool IsWithinBox(Int3 offset, Int3 reach)
    {
        return Math.Abs(offset.X) <= reach.X && Math.Abs(offset.Y) <= reach.Y && Math.Abs(offset.Z) <= reach.Z;
    }

    /// <summary>
    ///     Starts a new visited set for <see cref="IsVisited" /> without clearing the marks.
    /// </summary>
    private void BeginVisit()
    {
        if (_visitGeneration == int.MaxValue)
        {
            Array.Clear(_visitMarks);
            _visitGeneration = 0;
        }

        _visitGeneration++;
    }

    /// <summary>
    ///     Whether a registered chunk is in the current visited set.
    /// </summary>
    /// <remarks>
    ///     Marks are keyed on <see cref="AtmosChunk.DenseId" />, which is unique among registered chunks. A recycled
    ///     id can only carry a mark from an older set, so it reads as unvisited.
    /// </remarks>
    private bool IsVisited(AtmosChunk chunk)
    {
        return chunk.DenseId < _visitMarks.Length && _visitMarks[chunk.DenseId] == _visitGeneration;
    }

    private void MarkVisited(AtmosChunk chunk)
    {
        if (chunk.DenseId >= _visitMarks.Length)
            Array.Resize(ref _visitMarks, Math.Max(chunk.DenseId + 1, Math.Max(4, _visitMarks.Length * 2)));

        _visitMarks[chunk.DenseId] = _visitGeneration;
    }

    /// <summary>
    ///     Activates every chunk some workspace asked for since the last call. Tick thread only, never while a
    ///     sweep is running: it inserts into the active list and rewrites neighbour slots that workers read. The
    ///     newly activated chunks are gathered before it returns.
    /// </summary>
    private void ApplyWakeRequests(AtmosSolverExecutionContext context)
    {
        // Requests are collected before any activation because Activate shifts the active list.
        for (int index = 0; index < _activeCount; index++)
        {
            EulerChunkWorkspace workspace = _active[index];
            for (int direction = 0; direction < NeighborResolution.NeighborDirections.Length; direction++)
            {
                if (!workspace.WakeRequests[direction])
                    continue;

                workspace.WakeRequests[direction] = false;
                _pendingActivation.Add(workspace.NeighborChunks[direction]!);
            }
        }

        foreach (AtmosChunk chunk in _pendingActivation)
        {
            if (LookupWorkspace(chunk) == null)
                Activate(chunk, context);
        }

        _pendingActivation.Clear();
        GatherPending(context.TickConfig);
    }

    private AtmosFlowDiagnostics Advance(
        AtmosSolverExecutionContext context, EulerFlowConfig settings, int cap, SettlePass pass)
    {
        AtmosSolverConfigSnapshot config = context.TickConfig;
        Metre voxelEdge = MathF.Cbrt(config.VoxelVolume);
        SquareMetre faceArea = voxelEdge * voxelEdge;

        var totals = new FlowTotals();
        LastCutLinkCount = 0;
        LastBatchCount = 0;
        if (!EulerAcousticScaling.IsEnabled(settings, ForcedSoundSpeedReduction) || GroupingDisabled)
        {
            // One loop over the whole active list, which a mid-call activation grows.
            _wholeList.Members = _active;
            _wholeList.Count = _activeCount;
            Scalar wholeScale = EulerAcousticScaling.ChooseScale(
                _active, _activeCount, settings, ForcedSoundSpeedReduction);
            totals.Add(RunGroup(_wholeList, -1, wholeScale, context, settings, cap, voxelEdge, faceArea), _activeCount);
            totals.Groups = 1;
        }
        else
        {
            _groups.Build(_active, _activeCount);
            int groupCount = _groups.GroupCount;
            // Every group picks its factor from the gathered real state, before any batch scales or sweeps.
            // One parallel measuring pass covers all groups, so many small groups do not each pay a dispatch.
            bool forced = ForcedSoundSpeedReduction.HasValue;
            if (!forced)
                EulerAcousticScaling.MeasureAll(_active, _activeCount);

            for (int index = 0; index < groupCount; index++)
            {
                EulerFlowGroups.Group group = _groups.GroupAt(index);
                float scale = forced
                    ? EulerAcousticScaling.ChooseScale(group.Members, group.Count, settings, ForcedSoundSpeedReduction)
                    : EulerAcousticScaling.Decide(group.Members, group.Count, settings);
                _groups.SetGroupScale(index, scale);
            }

            _groups.Batch(_active, _activeCount);
            int batchCount = _groups.Count;
            LastBatchCount = batchCount;
            // Batches share no open face, so each one starts from the same sweep order. Its result then does not
            // depend on the batches that ran before it.
            int firstPhase = context.EulerSweepPhase;
            for (int index = 0; index < batchCount; index++)
            {
                context.EulerSweepPhase = firstPhase;
                EulerFlowGroups.Group batch = _groups[index];
                totals.Add(
                    RunGroup(batch, index, _groups.BatchScale(index), context, settings, cap, voxelEdge, faceArea),
                    batch.Count);
            }

            totals.Groups = groupCount;

            context.EulerSweepPhase = (firstPhase + totals.Substeps) % SweepOrderCount;

            // The write-back, diffusion, settling, and sleep read links between groups.
            LastCutLinkCount = _groups.RestoreCuts();
        }

        WriteBackActive(config, settings, context.DeltaTime, pass.Active);
        int negativeInternalEnergy = 0;
        int clampedVelocity = 0;
        int negativeMoles = 0;
        int nonFiniteCells = 0;
        for (int index = 0; index < _activeCount; index++)
        {
            EulerSweepCounters repairs = _active[index].SweepCounters;
            clampedVelocity += repairs.ClampedVelocity;
            negativeMoles += repairs.NegativeMoles;
            nonFiniteCells += repairs.NonFiniteCells;
            nonFiniteCells += _active[index].NonFiniteCellsGathered;
            negativeInternalEnergy += _active[index].NegativeInternalEnergyWritten;
        }

        if (pass.Active)
        {
            // Diffusion reads the written-back fields of neighbouring chunks, so it waits for every write-back.
            // Cleanup compares neighbour pressures the same way and waits for diffusion. Diffusion covers the whole
            // period since the last pass.
            EulerSpeciesDiffusion.Run(_active, _activeCount, WorkerCount, config, pass.Time);
            VacuumCleanup.Apply(context);

            ParallelPhase.Run(_activeCount, new SettleMomentumAction(_active, config, settings.VelocityFloor));

            // Sleep decisions read neighbours' final pressures, so they wait for everything above. Scoring only reads
            // fields, so it runs in parallel. The policy call can run an overridden Sleep, so it stays on this thread.
            // The score and the non-finite pressure count are stale between passes, so only a pass reads them.
            ParallelPhase.Run(_activeCount, new ScoreSleepAction(_active, config, settings));
            for (int index = 0; index < _activeCount; index++)
            {
                EulerChunkWorkspace workspace = _active[index];
                AtmosChunk chunk = workspace.Chunk;
                if (ChunkSleepPolicy.AdvanceSleepTimer(
                        chunk, config, workspace.SleepScore, pass.ElapsedTicks, context.SleepDelayTicks))
                {
                    // Motion slower than SleepSpeed lets the chunk sleep, but a sleeping chunk carries no momentum.
                    // An infinite floor stops every voxel, with its kinetic energy turned into heat. The scores of
                    // the chunks after this one were taken before, so the order of this loop does not matter to
                    // them.
                    if (workspace.HasMotion)
                        SettleMomentum(workspace, config, StopEveryVoxel);

                    chunk.Sleep();
                }

                AssertSleepingChunkIsStill(chunk);
                nonFiniteCells += workspace.NonFinitePressures;
            }
        }

        return new AtmosFlowDiagnostics(
            totals.Substeps,
            totals.SimulatedSeconds,
            totals.CapEngaged,
            totals.MaxCourant,
            totals.MaxSignalSpeed,
            clampedVelocity,
            negativeInternalEnergy,
            negativeMoles,
            nonFiniteCells,
            totals.Scale,
            totals.Groups,
            totals.SlowedChunks);
    }

    /// <summary>
    ///     What one batch's loop did.
    /// </summary>
    /// <param name="Substeps">Substeps the loop took.</param>
    /// <param name="SimulatedSeconds">Real time the loop advanced, which is scaled time times the factor.</param>
    /// <param name="CapEngaged">Whether the substep cap ended the loop.</param>
    /// <param name="MaxCourant">Largest Courant number after a substep, in scaled variables.</param>
    /// <param name="MaxSignalSpeed">Largest signal speed seen, in scaled variables.</param>
    /// <param name="Scale">The batch's factor α.</param>
    private readonly record struct GroupRun(
        int Substeps, Second64 SimulatedSeconds, bool CapEngaged, Scalar MaxCourant, MetrePerSecond MaxSignalSpeed,
        Scalar Scale);

    /// <summary>
    ///     The diagnostics of a call, folded over its batches: the largest substep count, Courant number, signal speed,
    ///     and factor, the smallest simulated time, and the counts. <see cref="Groups" /> is the number of flow groups,
    ///     which the caller sets, and <see cref="SlowedChunks" /> counts the members of slowed batches.
    /// </summary>
    private struct FlowTotals
    {
        public int Substeps;
        public Second64 SimulatedSeconds;
        public bool CapEngaged;
        public Scalar MaxCourant;
        public MetrePerSecond MaxSignalSpeed;
        public Scalar Scale;
        public int Groups;
        public int SlowedChunks;
        private int _runs;

        public void Add(in GroupRun run, int memberCount)
        {
            SimulatedSeconds = _runs++ == 0 ? run.SimulatedSeconds : Math.Min(SimulatedSeconds, run.SimulatedSeconds);
            Substeps = Math.Max(Substeps, run.Substeps);
            CapEngaged |= run.CapEngaged;
            MaxCourant = MathF.Max(MaxCourant, run.MaxCourant);
            MaxSignalSpeed = MathF.Max(MaxSignalSpeed, run.MaxSignalSpeed);
            Scale = MathF.Max(Scale, run.Scale);
            if (run.Scale > 1f)
                SlowedChunks += memberCount;
        }
    }

    /// <summary>
    ///     Runs one batch of flow groups that share a factor: scales it up, takes its substeps, and scales it back to
    ///     real momentum and energy.
    /// </summary>
    /// <param name="group">The batch's members. The solver refills it when a sweep activates more chunks.</param>
    /// <param name="index">The batch's number, or -1 when <paramref name="group" /> is the whole active list.</param>
    /// <param name="scale">The factor α of every member, chosen by the caller.</param>
    /// <param name="context">The stage's execution context.</param>
    /// <param name="settings">The stage's settings.</param>
    /// <param name="cap">Substep cap of the call, 0 for none.</param>
    /// <param name="voxelEdge">Edge length of one voxel.</param>
    /// <param name="faceArea">Area of one voxel face.</param>
    /// <remarks>
    ///     The loop reads and writes only the members' buffers, and the groups in it share one time step. A chunk
    ///     activated here joins the batch, and <see cref="Activate" /> cuts its links to other batches' workspaces so
    ///     that no sweep reads across batches. The factor applied and removed is the same for every member, including
    ///     those added mid-loop.
    /// </remarks>
    private GroupRun RunGroup(
        EulerFlowGroups.Group group, int index, Scalar scale, AtmosSolverExecutionContext context,
        EulerFlowConfig settings, int cap, Metre voxelEdge, SquareMetre faceArea)
    {
        AtmosSolverConfigSnapshot config = context.TickConfig;
        Scalar courant = settings.CourantNumber;

        if (scale > 1f)
            ScaleUpMembers(group, scale, config);

        _scale = scale;
        _runningGroup = index;

        // Simulated time is tracked in scaled time and reported in real time.
        Second64 remaining = context.DeltaTime / scale;
        Second64 simulated = 0d;
        int substeps = 0;
        bool capEngaged = false;
        Scalar maxCourant = 0f;
        MetrePerSecond maxSignalSpeed = 0f;

        while (remaining > 0d)
        {
            if (cap > 0 && substeps == cap)
            {
                capEngaged = true;
                break;
            }

            MetrePerSecond signalSpeed = MaxSignalSpeed(group.Members, group.Count);
            maxSignalSpeed = MathF.Max(maxSignalSpeed, signalSpeed);
            // The last substep takes whatever is left of the tick, however tiny. Stretching the previous substep
            // to absorb it would exceed the Courant number the positivity bound assumes.
            Second64 step = remaining;
            if (signalSpeed > 0f)
            {
                Second cflStep = courant * voxelEdge / signalSpeed;
                step = Math.Min(cflStep, remaining);
            }

            Second timeStep = (float)step;
            int order = context.EulerSweepPhase;
            context.EulerSweepPhase = NextSweepPhase(order);
            for (int position = 0; position < 3; position++)
            {
                int axis = SweepOrders[order * 3 + position];
                if (!AxisHasWork(group.Members, group.Count, axis))
                    continue;

                ShareAxisGases(group.Members, group.Count, axis, config);
                RunSweep(group.Members, group.Count, axis, timeStep, faceArea, config);
                for (int member = 0; member < group.Count; member++)
                    group.Members[member].Swap();

                // A chunk activated here is swept from the next sweep on at this substep's Δt. Its gas is at rest,
                // so its signal speed is its sound speed, which the next substep's Δt accounts for.
                int before = _activeCount;
                ApplyWakeRequests(context);
                if (_activeCount != before)
                    RefillGroup(group, index);
            }

            MetrePerSecond signalSpeedAfter = MaxSignalSpeed(group.Members, group.Count);
            maxSignalSpeed = MathF.Max(maxSignalSpeed, signalSpeedAfter);
            Scalar courantAfter = (float)(signalSpeedAfter * step / voxelEdge);
            maxCourant = MathF.Max(maxCourant, courantAfter);

            remaining -= step;
            simulated += step;
            substeps++;
        }

        _scale = 1f;
        _runningGroup = -1;
        if (scale > 1f || AnyScaled(group))
            EulerAcousticScaling.ScaleDown(group.Members, group.Count, scale);

        return new GroupRun(substeps, simulated * scale, capEngaged, maxCourant, maxSignalSpeed, scale);
    }

    /// <summary>
    ///     Brings a batch's member list up to date after an activation. The whole-list group points at the active
    ///     array again, because an activation can replace it.
    /// </summary>
    private void RefillGroup(EulerFlowGroups.Group group, int index)
    {
        if (index < 0)
        {
            group.Members = _active;
            group.Count = _activeCount;
            return;
        }

        _groups.Refill(index, _active, _activeCount);
    }

    /// <summary>
    ///     Scales the stored momentum of the batch's chunks for a call at factor <paramref name="scale" /> and
    ///     gathers the chunks that changed again, so their buffers hold the scaled state.
    /// </summary>
    /// <remarks>
    ///     Runs once, before the first substep, when the gathered state is still the real one the factor was chosen
    ///     from. The gather rebuilds a workspace from its chunk and keeps its face masks.
    /// </remarks>
    private void ScaleUpMembers(EulerFlowGroups.Group group, float scale, AtmosSolverConfigSnapshot config)
    {
        for (int index = 0; index < group.Count; index++)
        {
            EulerChunkWorkspace workspace = group.Members[index];
            if (EulerAcousticScaling.ScaleUp(workspace.Chunk, scale))
                _pendingGather.Add(workspace);
        }

        GatherPending(config);
    }

    /// <summary>
    ///     Whether any chunk of the group carries an acoustic scaling factor other than 1, which the feature-off
    ///     path resets.
    /// </summary>
    private static bool AnyScaled(EulerFlowGroups.Group group)
    {
        for (int index = 0; index < group.Count; index++)
        {
            if (group.Members[index].Chunk.EulerMomentumScale != 1f)
                return true;
        }

        return false;
    }

    /// <summary>
    ///     Gives every active chunk a row for each gas its active neighbours across the two faces of
    ///     <paramref name="axis" /> list, so the sweep can move any of them into the chunk without a worker ever
    ///     creating a row.
    /// </summary>
    /// <remarks>
    ///     This is the only place rows grow after binding, and it runs on the tick thread between sweeps. The
    ///     merge is one pass over the active list, so a chunk can receive a gas from two chunks away (its neighbour
    ///     just received the row from its own neighbour). That costs a zero row, never a wrong bit. A merge whose
    ///     neighbour list has not changed since the last merge adds nothing, and
    ///     <see cref="EulerChunkWorkspace.AddGasesOf" /> skips it. A listed gas gets a channel only if the
    ///     write-back finds some of it in the chunk.
    /// </remarks>
    private static void ShareAxisGases(
        EulerChunkWorkspace[] members, int count, int axis, AtmosSolverConfigSnapshot config)
    {
        for (int index = 0; index < count; index++)
        {
            EulerChunkWorkspace workspace = members[index];
            for (int direction = 2 * axis; direction <= 2 * axis + 1; direction++)
            {
                // A neighbour in another group sits across a closed face, so no gas crosses and its list may differ.
                EulerChunkWorkspace? neighbor = workspace.NeighborWorkspaces[direction];
                if (neighbor != null && neighbor.Group == workspace.Group)
                    workspace.AddGasesOf(neighbor, direction, config);
            }
        }
    }

    /// <summary>
    ///     Runs one axis sweep over every active chunk on the worker pool.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Two schedules. With at least as many active chunks as workers,
    ///         one work item per chunk sweeps all of that chunk's pencils. Otherwise the pencils of every chunk are
    ///         tiled into ranges of <c>ceil(totalPencils / workers)</c>, so one dense chunk still occupies the
    ///         pool. Both schedules call <see cref="EulerSweep.RunPencils" /> and therefore visit a chunk's pencils
    ///         in the same ascending order. A pencil writes only its own cells of the write buffer and its own
    ///         reduction slots, and reads only buffers no sweep writes. Splitting the same ordered pencil list
    ///         differently across workers therefore cannot change a bit.
    ///     </para>
    ///     <para>
    ///         Workers also read the live pressure of inactive neighbour chunks and set activation flags on their
    ///         own workspace. Neither is written by anything else until the barrier.
    ///     </para>
    /// </remarks>
    private void RunSweep(
        EulerChunkWorkspace[] members, int count, int axis, Second timeStep, SquareMetre faceArea,
        AtmosSolverConfigSnapshot config)
    {
        int workerCount = WorkerCount;
        int pencilsPerChunk = EulerSweep.PencilCount(members[0].Chunk, axis);
        if (count >= workerCount)
        {
            ParallelPhase.Run(count, new SweepChunkAction(members, axis, pencilsPerChunk, timeStep, faceArea, config));
            return;
        }

        int tileSize = Math.Max(1, ParallelPhase.DivideRoundUp(checked(pencilsPerChunk * count), workerCount));
        int tilesPerChunk = ParallelPhase.DivideRoundUp(pencilsPerChunk, tileSize);
        int itemCount = tilesPerChunk * count;
        if (_workItems.Length < itemCount)
            _workItems = new PencilWorkItem[Math.Max(itemCount, _workItems.Length * 2)];

        int item = 0;
        for (int index = 0; index < count; index++)
        {
            for (int first = 0; first < pencilsPerChunk; first += tileSize)
                _workItems[item++] = new PencilWorkItem(index, first, Math.Min(tileSize, pencilsPerChunk - first));
        }

        ParallelPhase.Run(itemCount, new SweepPencilsAction(members, _workItems, axis, timeStep, faceArea, config));

        // A chunk's tiles run on different workers, so the reduction waits for the barrier.
        for (int index = 0; index < count; index++)
            ReducePencils(members[index], axis);
    }

    /// <summary>
    ///     Folds a chunk's per-pencil slots into its write buffer's signal speed and its
    ///     <see cref="EulerChunkWorkspace.SweepCounters" />. Max and sum, so the order is immaterial.
    /// </summary>
    /// <remarks>
    ///     Writes only this workspace. The chunk schedule calls it from the item that swept the chunk, after the
    ///     last pencil. Neighbours read the chunk's read buffer, never the write buffer or the counters, so no
    ///     barrier is needed first. The counters are integers, so the tick-end sum is the same for any order.
    /// </remarks>
    private static void ReducePencils(EulerChunkWorkspace workspace, int axis)
    {
        ref EulerSweepCounters counters = ref workspace.SweepCounters;
        int pencilCount = EulerSweep.PencilCount(workspace.Chunk, axis);
        MetrePerSecond signalSpeed = 0f;
        for (int pencil = 0; pencil < pencilCount; pencil++)
        {
            signalSpeed = MathF.Max(signalSpeed, workspace.PencilSignalSpeed[pencil]);
            counters.ClampedVelocity += workspace.PencilCounters[pencil].ClampedVelocity;
            counters.NegativeMoles += workspace.PencilCounters[pencil].NegativeMoles;
            counters.NonFiniteCells += workspace.PencilCounters[pencil].NonFiniteCells;
        }

        workspace.Write.MaxSignalSpeed = signalSpeed;
    }

    /// <summary>
    ///     Voxels along one axis of every chunk in this kernel.
    /// </summary>
    private int Extent(int axis)
    {
        return axis switch
        {
            0 => _dimensions.X,
            1 => _dimensions.Y,
            _ => _dimensions.Z
        };
    }

    /// <summary>
    ///     Whether a sweep along <paramref name="axis" /> can move anything this substep.
    /// </summary>
    /// <remarks>
    ///     An axis one voxel long has no interior faces, but its two chunk faces still belong to the sweep, so the
    ///     axis is skipped only when no active chunk has a neighbour on either side and both faces are walls. For
    ///     gas at rest along the axis the two wall fluxes are <c>p</c> each and cancel, so the skip changes no bit.
    ///     Gas moving along such an axis (momentum set through <c>SetVoxelMomentum</c> or a checkpoint) is not
    ///     reflected: the walls would see <c>p + ρu(2u + c)</c> ahead and <c>p − ρuc</c> behind and decelerate it.
    ///     Its momentum stays until the velocity floor or damping removes it at tick end. A neighbour activated
    ///     mid-tick is seen on the next substep, the first one that could sweep it.
    /// </remarks>
    private bool AxisHasWork(EulerChunkWorkspace[] members, int count, int axis)
    {
        if (Extent(axis) > 1)
            return true;

        for (int index = 0; index < count; index++)
        {
            AtmosChunk?[] neighbours = members[index].NeighborChunks;
            if (neighbours[2 * axis] != null || neighbours[2 * axis + 1] != null)
                return true;
        }

        return false;
    }

    private MetrePerSecond MaxSignalSpeed(EulerChunkWorkspace[] members, int count)
    {
        // A skipped chunk would report the same speed every substep, because its state never changes.
        MetrePerSecond speed = _skippedSignalSpeed;
        for (int index = 0; index < count; index++)
            speed = MathF.Max(speed, members[index].Read.MaxSignalSpeed);

        return speed;
    }

    /// <summary>
    ///     Runs <see cref="FloorMomentumAndWriteBack" /> over every active chunk on the worker pool, in the tiles of
    ///     <see cref="BuildVoxelTiles" />. Tick thread only.
    /// </summary>
    /// <remarks>
    ///     <see cref="EulerChunkWorkspace.BeginWriteBack" /> creates channels from the whole chunk, so it runs once
    ///     per chunk before any tile of that chunk writes a voxel. With at least as many chunks as workers, one item
    ///     per chunk does both. Otherwise one phase begins every chunk and a second runs the tiles. Each tile writes
    ///     only its own voxels and its own count. The tick thread sums the counts into
    ///     <see cref="EulerChunkWorkspace.NegativeInternalEnergyWritten" />.
    /// </remarks>
    private void WriteBackActive(
        AtmosSolverConfigSnapshot config, EulerFlowConfig settings, Second deltaTime, bool applyFloor)
    {
        bool whole = _activeCount >= WorkerCount;
        if (!whole)
            ParallelPhase.Run(_activeCount, new BeginWriteBackAction(_active, config));

        int tileCount = BuildVoxelTiles(_active.AsSpan(0, _activeCount), whole);
        ParallelPhase.Run(
            tileCount,
            new WriteBackTileAction(_active, _voxelTiles, _tileCount, config, settings, deltaTime, applyFloor, whole));

        for (int index = 0; index < _activeCount; index++)
            _active[index].NegativeInternalEnergyWritten = 0;

        for (int tile = 0; tile < tileCount; tile++)
            _active[_voxelTiles[tile].WorkspaceIndex].NegativeInternalEnergyWritten += _tileCount[tile];

        for (int index = 0; index < _activeCount; index++)
            _active[index].Chunk.MarkChanged();
    }

    /// <summary>
    ///     Tick-end work for one tile of a chunk's buffer: trace-cell zeroing, the momentum floor on a settle pass,
    ///     damping, then the write-back of the voxels at list positions <c>[firstActive, endActive)</c>.
    /// </summary>
    /// <returns>The number of voxels written at 0 K.</returns>
    /// <remarks>
    ///     Every call zeroes the momentum of every trace cell. A settle pass (<c>applyFloor</c>) also zeroes every
    ///     cell slower than <see cref="EulerFlowConfig.VelocityFloor" /> divided by the chunk's
    ///     <see cref="AtmosChunk.EulerMomentumScale" />. Damping then scales what is left by
    ///     <c>max(0, 1 − α·Δt_call)</c> on every call. A floor of zero in <see cref="EulerMixture.ShouldStop" />
    ///     leaves only the trace test, so a settle pass matches the old per-tick behavior bit for bit. All of this runs
    ///     before the write-back computes temperatures, so the kinetic energy they remove turns into heat instead
    ///     of vanishing. The method touches only the tile's voxels, so tiles and chunks run in parallel once
    ///     <see cref="EulerChunkWorkspace.BeginWriteBack" /> has run for the chunk.
    /// </remarks>
    private static int FloorMomentumAndWriteBack(
        EulerChunkWorkspace workspace, AtmosSolverConfigSnapshot config, EulerFlowConfig settings, Second deltaTime,
        bool applyFloor, int firstActive, int endActive)
    {
        AtmosChunk chunk = workspace.Chunk;
        EulerChunkState buffer = workspace.Read;
        // ScaleDown has already set the chunk's scale, so the floor compares the solver's working velocity.
        MetrePerSecond floor = applyFloor ? settings.VelocityFloor / chunk.EulerMomentumScale : 0f;
        Scalar damping = settings.MomentumDamping > 0f
            ? MathF.Max(0f, 1f - settings.MomentumDamping * deltaTime)
            : 1f;

        for (int activeIndex = firstActive; activeIndex < endActive; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            Kilogram mass = buffer.Mass[voxelIndex];
            KilogramMetrePerSecond momentumX = buffer.MomentumX[voxelIndex];
            KilogramMetrePerSecond momentumY = buffer.MomentumY[voxelIndex];
            KilogramMetrePerSecond momentumZ = buffer.MomentumZ[voxelIndex];
            bool zero = EulerMixture.ShouldStop(
                mass, buffer.TotalMoles[voxelIndex], momentumX, momentumY, momentumZ, floor);

            if (zero)
            {
                momentumX = 0f;
                momentumY = 0f;
                momentumZ = 0f;
            }
            else if (damping != 1f)
            {
                momentumX *= damping;
                momentumY *= damping;
                momentumZ *= damping;
            }

            buffer.MomentumX[voxelIndex] = momentumX;
            buffer.MomentumY[voxelIndex] = momentumY;
            buffer.MomentumZ[voxelIndex] = momentumZ;
        }

        int negativeInternalEnergy = 0;
        workspace.WriteBackRange(config, firstActive, endActive, ref negativeInternalEnergy);
        return negativeInternalEnergy;
    }

    /// <summary>
    ///     Re-establishes the momentum invariants on the live fields after diffusion and vacuum cleanup: a vacuum or
    ///     trace voxel carries none, and a voxel slower than <paramref name="floor" /> is brought to rest with its
    ///     kinetic energy turned into heat, as the pre-write-back floor does.
    /// </summary>
    /// <param name="workspace">The chunk to settle.</param>
    /// <param name="config">The tick's configuration snapshot.</param>
    /// <param name="floor">
    ///     The speed below which a voxel stops. A settle pass passes <see cref="EulerFlowConfig.VelocityFloor" />.
    ///     A chunk that falls asleep passes infinity, which stops every voxel with a finite speed.
    /// </param>
    /// <remarks>
    ///     <para>
    ///         The largest speed among the voxels left moving lands in <see cref="EulerChunkWorkspace.MaxSpeed" />
    ///         for the sleep scoring, because the mass it needs is summed here anyway.
    ///     </para>
    ///     <para>
    ///         Diffusion changes a voxel's mass and cleanup empties voxels outright, so the floor test is repeated on
    ///         the fields the sleep pass and the next tick's gather read. On a voxel neither pass touched it changes
    ///         nothing: the mass is summed over the workspace's gas list in the same ascending order as the buffer's,
    ///         so the comparison repeats bit for bit. The exception is a channel diffusion created for a gas outside
    ///         the list, which is summed after it. The pressure a stopped voxel is left with comes from the
    ///         channel-order sum the write-back and every other refresh use, not from the list-order sum, so the
    ///         cache still matches a later refresh bit for bit.
    ///     </para>
    /// </remarks>
    private static void SettleMomentum(
        EulerChunkWorkspace workspace, AtmosSolverConfigSnapshot config, MetrePerSecond floor)
    {
        AtmosChunk chunk = workspace.Chunk;
        MetrePerSecond maxSpeed = 0f;
        workspace.ResolveSettleChannels(config);
        int[] listedChannels = workspace.SettleListedChannels;
        int[] unlistedChannels = workspace.SettleUnlistedChannels;
        KilogramPerMole[] unlistedMolarMass = workspace.SettleUnlistedMolarMass;
        int unlistedCount = workspace.SettleUnlistedCount;
        for (int activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            if (!chunk.HasMomentum(voxelIndex))
                continue;

            KilogramMetrePerSecond momentumX = chunk.MomentumX[voxelIndex];
            KilogramMetrePerSecond momentumY = chunk.MomentumY[voxelIndex];
            KilogramMetrePerSecond momentumZ = chunk.MomentumZ[voxelIndex];
            Kilogram mass = 0f;
            Mole totalMoles = 0f;
            bool zero = chunk.IsVacuum[voxelIndex];
            if (!zero)
            {
                for (int present = 0; present < workspace.GasCount; present++)
                {
                    int channel = listedChannels[present];
                    if (channel < 0)
                        continue;

                    Mole moles = chunk.ActiveGases[channel].Moles[voxelIndex];
                    mass += moles * workspace.MolarMass[present];
                    totalMoles += moles;
                }

                for (int extra = 0; extra < unlistedCount; extra++)
                {
                    Mole moles = chunk.ActiveGases[unlistedChannels[extra]].Moles[voxelIndex];
                    mass += moles * unlistedMolarMass[extra];
                    totalMoles += moles;
                }

                zero = EulerMixture.ShouldStop(mass, totalMoles, momentumX, momentumY, momentumZ, floor);
            }

            if (!zero)
            {
                // MathF.Max returns NaN when either side is NaN, so a non-finite speed survives the reduction.
                maxSpeed = MathF.Max(maxSpeed, EulerMixture.Speed(mass, momentumX, momentumY, momentumZ));
                continue;
            }

            JoulePerKelvin heatCapacity = chunk.TotalHeatCapacity[voxelIndex];
            if (mass > 0f && AtmosSolverMath.IsFinitePositive(heatCapacity))
            {
                Joule64 energy = (double)heatCapacity * config.GetValidatedTemp(chunk.Temperature[voxelIndex]) +
                                 EulerMixture.KineticEnergy(mass, momentumX, momentumY, momentumZ);

                chunk.Temperature[voxelIndex] = config.GetValidatedTemp((float)(energy / heatCapacity));
                chunk.TotalPressure[voxelIndex] = AtmosSolverMath.CalculatePressureAtVoxel(config, chunk, voxelIndex);
            }

            chunk.ZeroMomentum(voxelIndex);
        }

        workspace.MaxSpeed = maxSpeed;
    }

    /// <summary>
    ///     The sleep score of one written-back chunk: the metric the policy compares with the sleep threshold.
    /// </summary>
    /// <remarks>
    ///     The metric is the largest pressure difference across an open face as a percentage of the higher
    ///     pressure, with a gas-to-void face counting as 100%. Faces into other chunks count against the
    ///     neighbour's written-back pressure, or its live pressure if it never activated. They count because the
    ///     intra-chunk scan cannot see a gradient that sits on a chunk face. The metric is forced to at least
    ///     <see cref="AtmosSolverConfigSnapshot.SleepEpsilon" /> while any cell carries momentum and
    ///     <see cref="EulerChunkWorkspace.MaxSpeed" /> is not below <see cref="EulerFlowConfig.SleepSpeed" />. A
    ///     NaN speed counts as fast. Slower motion leaves the pressure differences to decide, and the stage stops
    ///     that motion when the chunk falls asleep, so a sleeping chunk has zero momentum everywhere. With a
    ///     <see cref="EulerFlowConfig.SleepSpeed" /> of 0 any momentum holds the chunk awake. A non-finite pressure
    ///     on either side of a face scores as a gas-to-void face. A NaN would otherwise ride through every
    ///     <see cref="MathF.Max" /> and make the decision depend on where in the chunk it sits. The result lands in
    ///     <see cref="EulerChunkWorkspace.SleepScore" />, <see cref="EulerChunkWorkspace.HasMotion" />, and
    ///     <see cref="EulerChunkWorkspace.NonFinitePressures" /> (the count of air voxels with a non-finite pressure).
    ///     The method reads chunk fields and writes only the workspace, so chunks can be scored in parallel.
    /// </remarks>
    private static void ScoreSleepState(
        EulerChunkWorkspace workspace, AtmosSolverConfigSnapshot config, EulerFlowConfig settings)
    {
        AtmosChunk chunk = workspace.Chunk;
        Int3 dimensions = chunk.Dimensions;
        bool hasMotion = false;
        int nonFinitePressures = 0;
        Scalar maximumRelativePressureDelta = 0f;
        for (int activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            hasMotion |= chunk.HasMomentum(voxelIndex);

            Pascal pressure = chunk.TotalPressure[voxelIndex];
            if (!float.IsFinite(pressure))
            {
                nonFinitePressures++;
                maximumRelativePressureDelta = 100f;
                continue;
            }

            byte open = workspace.OpenMask[voxelIndex];
            byte sink = workspace.VoidMask[voxelIndex];
            if (sink != 0 && pressure > 0f)
                maximumRelativePressureDelta = 100f;

            if (open == 0)
                continue;

            Int3 position = chunk.GetXyzInt3(voxelIndex);
            for (int direction = 0; direction < NeighborResolution.NeighborDirections.Length; direction++)
            {
                if (!EulerFaceTopology.IsOpen(open, direction))
                    continue;

                Int3 neighborPosition = position + NeighborResolution.NeighborDirections[direction];
                Pascal neighborPressure;
                if (neighborPosition.IsWithin(dimensions))
                {
                    // Positive directions only: the negative face of this voxel is the positive face of its
                    // neighbour, which scores it.
                    if ((direction & 1) == 0)
                        continue;

                    neighborPressure = chunk.TotalPressure[chunk.GetIndexUnsafe(neighborPosition)];
                }
                else
                {
                    AtmosChunk neighbor = workspace.NeighborChunks[direction]!;
                    neighborPressure = neighbor.TotalPressure[
                        EulerFaceTopology.NeighborIndexAcrossFace(chunk, voxelIndex, direction)];
                }

                if (!float.IsFinite(neighborPressure))
                {
                    maximumRelativePressureDelta = 100f;
                    continue;
                }

                if (pressure <= 0f && neighborPressure <= 0f)
                    continue;

                Scalar relative = ChunkSleepPolicy.RelativePressureDelta(pressure, neighborPressure);
                maximumRelativePressureDelta = MathF.Max(maximumRelativePressureDelta, relative);
            }
        }

        // Inverted so a NaN speed holds the chunk awake. A SleepSpeed of 0 holds it awake for any motion.
        if (hasMotion && !(workspace.MaxSpeed < settings.SleepSpeed))
            maximumRelativePressureDelta = MathF.Max(maximumRelativePressureDelta, config.SleepEpsilon);

        AssertEmptyVoxelsAreStill(chunk);
        workspace.SleepScore = maximumRelativePressureDelta;
        workspace.HasMotion = hasMotion;
        workspace.NonFinitePressures = nonFinitePressures;
    }

    [Conditional("DEBUG")]
    private static void AssertSleepingChunkIsStill(AtmosChunk chunk)
    {
        if (chunk.IsAwake)
            return;

        for (int activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
        {
            Debug.Assert(
                !chunk.HasMomentum(chunk.ActiveAirIndices[activeIndex]),
                "A chunk must not fall asleep while it still carries momentum.");
        }
    }

    [Conditional("DEBUG")]
    private static void AssertEmptyVoxelsAreStill(AtmosChunk chunk)
    {
        for (int activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            if (!chunk.IsVacuum[voxelIndex])
                continue;

            Debug.Assert(!chunk.HasMomentum(voxelIndex), "A vacuum voxel must carry no momentum.");
        }
    }

    /// <summary>
    ///     A range of one chunk's pencils for the tiled schedule.
    /// </summary>
    private readonly record struct PencilWorkItem(int WorkspaceIndex, int FirstPencil, int PencilCount);

    /// <summary>
    ///     The list positions <c>[FirstActive, EndActive)</c> of one chunk's active-air list, a tile of the gather or
    ///     write-back.
    /// </summary>
    private readonly record struct VoxelTile(int WorkspaceIndex, int FirstActive, int EndActive);

    private readonly struct StaticAction(
        AtmosSolverExecutionContext context, bool[] result, MetrePerSecond[] speed,
        AtmosChunkVersion[] interiorPassedAt) : IAction
    {
        public void Invoke(int index)
        {
            AtmosChunk chunk = context.Chunks[index];
            MetrePerSecond signalSpeed = 0f;
            result[index] = chunk.IsAwake && chunk.ActiveGasCount > 0 &&
                            EulerStaticChunk.IsStatic(
                                chunk, context, ref interiorPassedAt[chunk.DenseId], out signalSpeed);

            speed[index] = signalSpeed;
        }
    }

    private readonly struct PrepareGatherAction(List<EulerChunkWorkspace> batch, AtmosSolverConfigSnapshot config)
        : IAction
    {
        public void Invoke(int index)
        {
            EulerChunkWorkspace workspace = batch[index];
            PrepareForGather(workspace, config);
            AtmosChunk chunk = workspace.Chunk;
            workspace.ActiveAirAscending = IsStrictlyAscending(chunk.ActiveAirIndices.AsSpan(0, chunk.ActiveAirCount));
        }
    }

    /// <summary>
    ///     Gathers one tile. With <c>prepare</c> set, every workspace is one tile and the item prepares it first.
    /// </summary>
    private readonly struct GatherTileAction(
        List<EulerChunkWorkspace> batch, VoxelTile[] tiles, MetrePerSecond[] signalSpeed, int[] nonFiniteCells,
        AtmosSolverConfigSnapshot config, bool prepare) : IAction
    {
        public void Invoke(int index)
        {
            VoxelTile tile = tiles[index];
            EulerChunkWorkspace workspace = batch[tile.WorkspaceIndex];
            if (prepare)
                PrepareForGather(workspace, config);

            int count = 0;
            signalSpeed[index] = workspace.GatherRange(config, tile.FirstActive, tile.EndActive, ref count);
            nonFiniteCells[index] = count;
        }
    }

    private readonly struct ScanAction(
        List<EulerChunkWorkspace> batch, Pascal wakeDelta, MetrePerSecond wakeSpeed) : IAction
    {
        public void Invoke(int index)
        {
            ScanBoundaryFaces(batch[index], wakeDelta, wakeSpeed);
        }
    }

    private readonly struct BeginWriteBackAction(EulerChunkWorkspace[] active, AtmosSolverConfigSnapshot config)
        : IAction
    {
        public void Invoke(int index)
        {
            EulerChunkWorkspace workspace = active[index];
            workspace.BeginWriteBack(config);
            AtmosChunk chunk = workspace.Chunk;
            workspace.ActiveAirAscending = IsStrictlyAscending(chunk.ActiveAirIndices.AsSpan(0, chunk.ActiveAirCount));
        }
    }

    /// <summary>
    ///     Floors, damps, and writes back one tile. With <c>begin</c> set, every workspace is one tile and the item
    ///     begins its write-back first.
    /// </summary>
    private readonly struct WriteBackTileAction(
        EulerChunkWorkspace[] active, VoxelTile[] tiles, int[] negativeInternalEnergy,
        AtmosSolverConfigSnapshot config, EulerFlowConfig settings, Second deltaTime, bool applyFloor, bool begin)
        : IAction
    {
        public void Invoke(int index)
        {
            VoxelTile tile = tiles[index];
            EulerChunkWorkspace workspace = active[tile.WorkspaceIndex];
            if (begin)
                workspace.BeginWriteBack(config);

            negativeInternalEnergy[index] = FloorMomentumAndWriteBack(
                workspace, config, settings, deltaTime, applyFloor, tile.FirstActive, tile.EndActive);
        }
    }

    private readonly struct SettleMomentumAction(
        EulerChunkWorkspace[] active, AtmosSolverConfigSnapshot config, MetrePerSecond floor) : IAction
    {
        public void Invoke(int index)
        {
            // A chunk written with scale s holds real momentum, so its floor in real velocity is floor / s.
            MetrePerSecond scaledFloor = floor / active[index].Chunk.EulerMomentumScale;
            SettleMomentum(active[index], config, scaledFloor);
        }
    }

    private readonly struct ScoreSleepAction(
        EulerChunkWorkspace[] active, AtmosSolverConfigSnapshot config, EulerFlowConfig settings) : IAction
    {
        public void Invoke(int index)
        {
            ScoreSleepState(active[index], config, settings);
        }
    }

    private readonly struct SweepChunkAction(
        EulerChunkWorkspace[] active, int axis, int pencilCount,
        Second timeStep, SquareMetre faceArea, AtmosSolverConfigSnapshot config) : IAction
    {
        public void Invoke(int index)
        {
            EulerChunkWorkspace workspace = active[index];
            EulerSweep.RunPencils(workspace, axis, 0, pencilCount, timeStep, faceArea, config);
            ReducePencils(workspace, axis);
        }
    }

    private readonly struct SweepPencilsAction(
        EulerChunkWorkspace[] active, PencilWorkItem[] items, int axis,
        Second timeStep, SquareMetre faceArea, AtmosSolverConfigSnapshot config) : IAction
    {
        public void Invoke(int index)
        {
            PencilWorkItem item = items[index];
            EulerSweep.RunPencils(
                active[item.WorkspaceIndex], axis, item.FirstPencil, item.PencilCount, timeStep, faceArea, config);
        }
    }
}
