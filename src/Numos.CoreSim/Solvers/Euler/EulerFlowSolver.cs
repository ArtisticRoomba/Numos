using System.Diagnostics;
using CommunityToolkit.HighPerformance.Helpers;
using Numos.CoreSim.EulerFlow;
using Numos.Maths;

namespace Numos.CoreSim.Solvers.Euler;

/// <summary>
///     The <c>euler-flow</c> stage: compressible Euler gas transport across every active chunk, dimensionally split
///     into first-order Godunov sweeps with <see cref="HllcFlux" /> at every face and CFL substeps inside the tick.
/// </summary>
/// <remarks>
///     <para>
///         Per tick the stage gathers each active chunk (awake, with at least one gas channel, not skipped as static, plus
///         whatever activation adds) into pooled conserved buffers, runs CFL substeps until the fixed time step is used up
///         or the substep cap engages, and writes the buffers back. Tick-end work then runs in this order:
///         momentum floor and damping (before the write-back, so the kinetic energy they remove becomes heat),
///         <see cref="EulerSpeciesDiffusion" />, <see cref="VacuumCleanup" />, <see cref="SettleMomentum" />, and
///         last the sleep metric and <see cref="ChunkSleepPolicy" />, which score the pressures diffusion and
///         cleanup left behind. Explicit links are handled by their own stages, not this one.
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
///         <c>(2·TickCount + substep) mod 6</c>, which starts every tick on the first order of a pair regardless
///         of the previous tick's substep count. It depends only on checkpointed state and the substep index, never
///         on a counter carried across ticks, so a restored world sweeps in the original order.
///     </para>
///     <para>
///         The active set grows during the tick. An open face into a chunk with no workspace is a wall for the
///         sweep that meets it, and that sweep requests the chunk's activation when the two sides differ in
///         pressure by more than <see cref="EulerFlowConfig.WakePressureDelta" /> or the active side moves normal
///         to the face faster than <see cref="EulerFlowConfig.VelocityFloor" />. Requests are applied on the tick
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
///         rule. Such a chunk stays awake, so its sleep timer still advances at the end of <see cref="Solve" />.
///         Its signal speed still counts toward the shared time step, so the substeps match an all-swept tick.
///         The skip changes no bit of any chunk, and a tick in which nothing is swept leaves
///         <see cref="LastDiagnostics" /> at its default.
///     </para>
///     <para>
///         The stage keeps nothing between ticks beyond the chunk's own momentum arrays. The stage rebuilds the buffers
///         from the chunk fields every tick. See docs/atmospherics_technical_documentation.md section 5 for the
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
    ///     Whether static chunks out of reach of every source skip the sweeps (see <see cref="EulerStaticChunk" />).
    ///     Tests clear it to compare against the all-swept result. The skip changes no bit, so it is not a
    ///     configuration field.
    /// </summary>
    internal static bool SkipStaticChunks = true;

    /// <summary>
    ///     How many chunks the most recent tick skipped. A test hook, shared by every solver instance.
    /// </summary>
    internal static int LastSkippedCount;

    private static readonly Int3[] ReachOffsets = CreateReachOffsets();

    /// <summary>
    ///     The axis orders a substep can sweep in, as consecutive triples: <c>XYZ, ZYX, YZX, XZY, ZXY, YXZ</c>, each
    ///     order followed by its reverse.
    /// </summary>
    private static ReadOnlySpan<byte> SweepOrders => [0, 1, 2, 2, 1, 0, 1, 2, 0, 0, 2, 1, 2, 0, 1, 1, 0, 2];

    private readonly Int3 _dimensions;
    private readonly List<AtmosChunk> _pendingActivation = [];
    private readonly List<EulerChunkWorkspace> _pendingGather = [];
    private readonly List<EulerChunkWorkspace> _pendingScan = [];
    private readonly Stack<EulerChunkWorkspace> _pool = new();
    private EulerChunkWorkspace[] _active = [];
    private int _activeCount;
    private readonly List<AtmosChunk> _frontier = [];
    private readonly List<AtmosChunk> _nextFrontier = [];
    private readonly List<AtmosChunk> _reached = [];
    private readonly HashSet<AtmosChunk> _rangeSet = [];
    private readonly List<AtmosChunk> _seeds = [];
    private readonly HashSet<AtmosChunk> _seedSet = [];
    private readonly HashSet<AtmosChunk> _visited = [];
    private MetrePerSecond _skippedSignalSpeed;
    private MetrePerSecond[] _staticSpeed = [];
    private bool[] _static = [];
    private EulerChunkWorkspace?[] _workspaceByDenseId = [];
    private PencilWorkItem[] _workItems = [];

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
                ParallelPhase.Run(context.Chunks.Length, new StaticAction(context, _static, _staticSpeed));
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
                ActivateWithinReach(context);
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
                if (staticCount > 0)
                    VacuumCleanup.Apply(context);
            }
            else
            {
                LastDiagnostics = Advance(context, config.EulerFlow);
            }

            // An awake chunk that never got a workspace has nothing to move, but its sleep timer still has to
            // advance. Chunks the stage put to sleep are no longer awake here.
            for (int chunkIndex = 0; chunkIndex < context.Chunks.Length; chunkIndex++)
            {
                AtmosChunk chunk = context.Chunks[chunkIndex];
                if (chunk.IsAwake && LookupWorkspace(chunk) == null)
                    ChunkSleepPolicy.UpdateSleepState(chunk, config, 0f);
            }
        }
        finally
        {
            for (int index = 0; index < _activeCount; index++)
            {
                EulerChunkWorkspace workspace = _active[index];
                _workspaceByDenseId[workspace.Chunk.DenseId] = null;
                _pool.Push(workspace);
                _active[index] = null!;
            }

            _activeCount = 0;
            _skippedSignalSpeed = 0f;
            _pendingScan.Clear();
            _pendingActivation.Clear();
            _pendingGather.Clear();
        }
    }

    /// <summary>
    ///     Forgets the chunk-to-workspace lookup. Called when a checkpoint replaces the kernel's chunks.
    ///     The pool stays: chunk dimensions are fixed per solver and <see cref="EulerChunkWorkspace.Bind" /> resets
    ///     every workspace, so pooled instances hold no simulation state.
    /// </summary>
    internal void ClearTransientState()
    {
        Array.Clear(_workspaceByDenseId);
    }

    /// <summary>
    ///     Gives a chunk a workspace for the rest of the tick: wakes it if asleep, links it to its active
    ///     neighbours, and queues it for <see cref="GatherPending" />. Tick thread only.
    /// </summary>
    /// <remarks>
    ///     The workspace is not prepared yet. Its buffers, face masks, and air map hold nothing until the caller
    ///     runs <see cref="GatherPending" />, which every batch of activations must do before anything reads them.
    ///     Only the pool pop, the neighbour links, and the active-list insertion happen here, because they depend
    ///     on activation order.
    ///     The workspace lists only the chunk's own gases here. Rows for gases a neighbour can push across a face
    ///     are added by <see cref="ShareAxisGases" /> before each sweep, so a chunk activated mid-tick is covered
    ///     by the next sweep's merge like everyone else.
    /// </remarks>
    private void Activate(AtmosChunk chunk, AtmosSolverExecutionContext context)
    {
        if (!chunk.IsAwake)
            chunk.Wake();

        EulerChunkWorkspace workspace = _pool.Count > 0 ? _pool.Pop() : new EulerChunkWorkspace();
        workspace.Attach(chunk);
        for (int direction = 0; direction < NeighborResolution.NeighborDirections.Length; direction++)
        {
            if (!context.TryGetChunk(chunk.GridPosition + NeighborResolution.NeighborDirections[direction], out AtmosChunk neighbor))
                continue;

            workspace.NeighborChunks[direction] = neighbor;
            EulerChunkWorkspace? neighborWorkspace = LookupWorkspace(neighbor);
            workspace.NeighborWorkspaces[direction] = neighborWorkspace;
            if (neighborWorkspace != null)
                neighborWorkspace.NeighborWorkspaces[direction ^ 1] = workspace;
        }

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
            Array.Resize(ref _workspaceByDenseId, Math.Max(chunk.DenseId + 1, Math.Max(4, _workspaceByDenseId.Length * 2)));

        _workspaceByDenseId[chunk.DenseId] = workspace;
        _pendingScan.Add(workspace);
    }

    /// <summary>
    ///     Prepares and gathers every workspace activated since the last call. Tick thread only.
    /// </summary>
    /// <remarks>
    ///     Each item reads its chunk, the neighbour chunks' room maps, and its own neighbour slots, which are
    ///     final once <see cref="Activate" /> returns. It writes only its own workspace. The batch result therefore
    ///     does not depend on which worker runs which item.
    /// </remarks>
    private void GatherPending(AtmosSolverConfigSnapshot config)
    {
        ParallelPhase.Run(_pendingGather.Count, new GatherAction(_pendingGather, config));
        _pendingGather.Clear();
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
        MetrePerSecond velocityFloor = settings.VelocityFloor;
        while (_pendingScan.Count > 0)
        {
            ParallelPhase.Run(_pendingScan.Count, new ScanAction(_pendingScan, wakeDelta, velocityFloor));

            _pendingScan.Clear();
            ApplyWakeRequests(context);
        }
    }

    private static void ScanBoundaryFaces(
        EulerChunkWorkspace workspace, Pascal wakeDelta, MetrePerSecond velocityFloor)
    {
        AtmosChunk chunk = workspace.Chunk;
        EulerChunkState read = workspace.Read;
        Int3 last = chunk.Dimensions - new Int3(1, 1, 1);
        for (int activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            byte open = workspace.OpenMask[voxelIndex];
            if (open == 0)
                continue;

            Int3 position = chunk.GetXyzInt3(voxelIndex);
            for (int direction = 0; direction < NeighborResolution.NeighborDirections.Length; direction++)
            {
                if (!EulerFaceTopology.IsOnEdge(position, last, direction) || workspace.WakeRequests[direction] ||
                    workspace.NeighborWorkspaces[direction] != null || !EulerFaceTopology.IsOpen(open, direction))
                    continue;

                AtmosChunk neighbor = workspace.NeighborChunks[direction]!;
                ushort neighborIndex = EulerFaceTopology.NeighborIndexAcrossFace(chunk, voxelIndex, direction);
                MetrePerSecond normalVelocity = read.CellVelocity(direction >> 1)[voxelIndex];
                Pascal pressureDelta = chunk.TotalPressure[voxelIndex] - neighbor.TotalPressure[neighborIndex];
                if (EulerSweep.WakesNeighbor(normalVelocity, pressureDelta, velocityFloor, wakeDelta))
                    workspace.WakeRequests[direction] = true;
            }
        }
    }

    /// <summary>
    ///     Activates every static chunk that a source could reach this tick. The chunks left without a workspace
    ///     are exactly the ones a sweep could not change. Tick thread only.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A source is a chunk active after the pre-pass. A first-order split sweep moves information one cell
    ///         per axis sweep, so one substep spreads it by one cell along each axis. A tick runs at most
    ///         <see cref="EulerFlowConfig.MaxSubstepsPerTick" /> substeps, and the tick-end species diffusion adds
    ///         one cell. Chunks at chunk distance <c>D</c> or less are activated, with
    ///         <c>D = ceil((MaxSubstepsPerTick + 2) / minExtent)</c>. A chunk beyond <c>D</c> has more than
    ///         <c>MaxSubstepsPerTick + 1</c> cells between it and any source.
    ///     </para>
    ///     <para>
    ///         Distance is measured by a breadth-first search over the 26 neighbouring chunk positions through
    ///         every registered chunk. Without a substep cap the bound is unknown, so every static chunk is
    ///         activated. An activated static chunk is not a source, because it changes nothing until a source
    ///         reaches it. The search repeats only when the pre-pass wakes a chunk that was not static.
    ///     </para>
    /// </remarks>
    private void ActivateWithinReach(AtmosSolverExecutionContext context)
    {
        int bound = context.TickConfig.EulerFlow.MaxSubstepsPerTick;
        int minExtent = Math.Min(_dimensions.X, Math.Min(_dimensions.Y, _dimensions.Z));
        int depth = bound == 0 ? 0 : ParallelPhase.DivideRoundUp(bound + 2, minExtent);
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
            if (bound == 0)
            {
                for (int index = 0; index < context.Chunks.Length; index++)
                {
                    if (IsSkipped(context.Chunks[index]))
                        _reached.Add(context.Chunks[index]);
                }
            }
            else
            {
                FindChunksWithin(context, depth);
            }

            if (_reached.Count == 0)
                break;

            foreach (AtmosChunk chunk in _reached)
            {
                _rangeSet.Add(chunk);
                Activate(chunk, context);
            }

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
    ///     Collects into <see cref="_reached" /> every skipped chunk within <paramref name="depth" /> steps of a seed.
    /// </summary>
    private void FindChunksWithin(AtmosSolverExecutionContext context, int depth)
    {
        _visited.Clear();
        _frontier.Clear();
        foreach (AtmosChunk seed in _seeds)
        {
            _visited.Add(seed);
            _frontier.Add(seed);
        }

        for (int level = 0; level < depth && _frontier.Count > 0; level++)
        {
            _nextFrontier.Clear();
            foreach (AtmosChunk chunk in _frontier)
            {
                foreach (Int3 offset in ReachOffsets)
                {
                    if (!context.TryGetChunk(chunk.GridPosition + offset, out AtmosChunk neighbor) ||
                        !_visited.Add(neighbor))
                        continue;

                    _nextFrontier.Add(neighbor);
                    if (IsSkipped(neighbor))
                        _reached.Add(neighbor);
                }
            }

            _frontier.Clear();
            _frontier.AddRange(_nextFrontier);
        }
    }

    private static Int3[] CreateReachOffsets()
    {
        var offsets = new List<Int3>();
        for (int z = -1; z <= 1; z++)
        for (int y = -1; y <= 1; y++)
        for (int x = -1; x <= 1; x++)
        {
            if (x != 0 || y != 0 || z != 0)
                offsets.Add(new Int3(x, y, z));
        }

        return offsets.ToArray();
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

    private AtmosFlowDiagnostics Advance(AtmosSolverExecutionContext context, EulerFlowConfig settings)
    {
        AtmosSolverConfigSnapshot config = context.TickConfig;
        Metre voxelEdge = MathF.Cbrt(config.VoxelVolume);
        SquareMetre faceArea = voxelEdge * voxelEdge;
        Scalar courant = settings.CourantNumber;
        int cap = settings.MaxSubstepsPerTick;

        Second64 remaining = AtmosSolverConstants.FixedTimeStep;
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

            MetrePerSecond signalSpeed = MaxSignalSpeed();
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
            int order = (int)((2L * context.TickCount + substeps) % 6);
            for (int position = 0; position < 3; position++)
            {
                int axis = SweepOrders[order * 3 + position];
                if (!AxisHasWork(axis))
                    continue;

                ShareAxisGases(axis, config);
                RunSweep(axis, timeStep, faceArea, config);
                for (int index = 0; index < _activeCount; index++)
                    _active[index].Swap();

                // A chunk activated here is swept from the next sweep on at this substep's Δt. Its gas is at rest,
                // so its signal speed is its sound speed, which the next substep's Δt accounts for.
                ApplyWakeRequests(context);
            }

            MetrePerSecond signalSpeedAfter = MaxSignalSpeed();
            maxSignalSpeed = MathF.Max(maxSignalSpeed, signalSpeedAfter);
            Scalar courantAfter = (float)(signalSpeedAfter * step / voxelEdge);
            maxCourant = MathF.Max(maxCourant, courantAfter);

            remaining -= step;
            simulated += step;
            substeps++;
        }

        // Each chunk writes only its own fields and its own counter here. The tick thread sums the counters.
        ParallelPhase.Run(_activeCount, new WriteBackAction(_active, config, settings));
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

        // Diffusion reads the written-back fields of neighbouring chunks, so it waits for every write-back.
        // Cleanup compares neighbour pressures the same way and waits for diffusion.
        EulerSpeciesDiffusion.Run(_active, _activeCount, WorkerCount, config);
        VacuumCleanup.Apply(context);

        ParallelPhase.Run(_activeCount, new SettleMomentumAction(_active, config, settings));

        // Sleep decisions read neighbours' final pressures, so they wait for everything above. Scoring only reads
        // fields, so it runs in parallel. The policy call can run an overridden Sleep, so it stays on this thread.
        ParallelPhase.Run(_activeCount, new ScoreSleepAction(_active, config));
        for (int index = 0; index < _activeCount; index++)
        {
            EulerChunkWorkspace workspace = _active[index];
            ChunkSleepPolicy.UpdateSleepState(workspace.Chunk, config, workspace.SleepScore);
            Debug.Assert(
                workspace.Chunk.IsAwake || !workspace.HasMotion,
                "A chunk must not fall asleep while it still carries momentum.");

            nonFiniteCells += workspace.NonFinitePressures;
        }

        return new AtmosFlowDiagnostics(
            substeps,
            simulated,
            capEngaged,
            maxCourant,
            maxSignalSpeed,
            clampedVelocity,
            negativeInternalEnergy,
            negativeMoles,
            nonFiniteCells);
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
    private void ShareAxisGases(int axis, AtmosSolverConfigSnapshot config)
    {
        for (int index = 0; index < _activeCount; index++)
        {
            EulerChunkWorkspace workspace = _active[index];
            for (int direction = 2 * axis; direction <= 2 * axis + 1; direction++)
            {
                EulerChunkWorkspace? neighbor = workspace.NeighborWorkspaces[direction];
                if (neighbor != null)
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
    private void RunSweep(int axis, Second timeStep, SquareMetre faceArea, AtmosSolverConfigSnapshot config)
    {
        int workerCount = WorkerCount;
        int pencilsPerChunk = EulerSweep.PencilCount(_active[0].Chunk, axis);
        if (_activeCount >= workerCount)
        {
            ParallelPhase.Run(_activeCount, new SweepChunkAction(_active, axis, pencilsPerChunk, timeStep, faceArea, config));
            return;
        }

        int tileSize = Math.Max(1, ParallelPhase.DivideRoundUp(checked(pencilsPerChunk * _activeCount), workerCount));
        int tilesPerChunk = ParallelPhase.DivideRoundUp(pencilsPerChunk, tileSize);
        int itemCount = tilesPerChunk * _activeCount;
        if (_workItems.Length < itemCount)
            _workItems = new PencilWorkItem[Math.Max(itemCount, _workItems.Length * 2)];

        int item = 0;
        for (int index = 0; index < _activeCount; index++)
        {
            for (int first = 0; first < pencilsPerChunk; first += tileSize)
                _workItems[item++] = new PencilWorkItem(index, first, Math.Min(tileSize, pencilsPerChunk - first));
        }

        ParallelPhase.Run(itemCount, new SweepPencilsAction(_active, _workItems, axis, timeStep, faceArea, config));

        // A chunk's tiles run on different workers, so the reduction waits for the barrier.
        for (int index = 0; index < _activeCount; index++)
            ReducePencils(_active[index], axis);
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
    private bool AxisHasWork(int axis)
    {
        if (Extent(axis) > 1)
            return true;

        for (int index = 0; index < _activeCount; index++)
        {
            AtmosChunk?[] neighbours = _active[index].NeighborChunks;
            if (neighbours[2 * axis] != null || neighbours[2 * axis + 1] != null)
                return true;
        }

        return false;
    }

    private MetrePerSecond MaxSignalSpeed()
    {
        // A skipped chunk would report the same speed every substep, because its state never changes.
        MetrePerSecond speed = _skippedSignalSpeed;
        for (int index = 0; index < _activeCount; index++)
            speed = MathF.Max(speed, _active[index].Read.MaxSignalSpeed);

        return speed;
    }

    /// <summary>
    ///     Tick-end work for one chunk's buffer: the momentum floor and damping, then the write-back.
    /// </summary>
    /// <remarks>
    ///     The floor zeroes the momentum of every cell slower than <see cref="EulerFlowConfig.VelocityFloor" />
    ///     and of every trace cell. Damping then scales what is left by <c>max(0, 1 − α·Δt_tick)</c>. Both run
    ///     before the write-back computes temperatures, so the kinetic energy they remove turns into heat instead
    ///     of vanishing. The method touches only this chunk's fields and workspace, so chunks run in parallel. The
    ///     count of voxels written at 0 K lands in <see cref="EulerChunkWorkspace.NegativeInternalEnergyWritten" />.
    /// </remarks>
    private static void FloorMomentumAndWriteBack(
        EulerChunkWorkspace workspace, AtmosSolverConfigSnapshot config, EulerFlowConfig settings)
    {
        AtmosChunk chunk = workspace.Chunk;
        EulerChunkState buffer = workspace.Read;
        MetrePerSecond floor = settings.VelocityFloor;
        Scalar damping = settings.MomentumDamping > 0f
            ? MathF.Max(0f, 1f - settings.MomentumDamping * AtmosSolverConstants.FixedTimeStep)
            : 1f;

        for (int activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
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
        workspace.WriteBack(config, ref negativeInternalEnergy);
        workspace.NegativeInternalEnergyWritten = negativeInternalEnergy;
        chunk.MarkChanged();
    }

    /// <summary>
    ///     Re-establishes the momentum invariants on the live fields after diffusion and vacuum cleanup: a vacuum or
    ///     trace voxel carries none, and a voxel slower than <see cref="EulerFlowConfig.VelocityFloor" /> is brought
    ///     to rest with its kinetic energy turned into heat, as the pre-write-back floor does.
    /// </summary>
    /// <remarks>
    ///     Diffusion changes a voxel's mass and cleanup empties voxels outright, so the floor test is repeated on
    ///     the fields the sleep pass and the next tick's gather read. On a voxel neither pass touched it changes
    ///     nothing: the mass is summed over the workspace's gas list in the same ascending order as the buffer's,
    ///     so the comparison repeats bit for bit. The exception is a channel diffusion created for a gas outside
    ///     the list, which is summed after it. The pressure a stopped voxel is left with comes from the
    ///     channel-order sum the write-back and every other refresh use, not from the list-order sum, so the cache
    ///     still matches a later refresh bit for bit.
    /// </remarks>
    private static void SettleMomentum(
        EulerChunkWorkspace workspace, AtmosSolverConfigSnapshot config, EulerFlowConfig settings)
    {
        AtmosChunk chunk = workspace.Chunk;
        MetrePerSecond floor = settings.VelocityFloor;
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
                continue;

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
    }

    /// <summary>
    ///     The sleep score of one written-back chunk: the metric the policy compares with the sleep threshold.
    /// </summary>
    /// <remarks>
    ///     The metric is the largest pressure difference across an open face as a percentage of the higher
    ///     pressure, with a gas-to-void face counting as 100%. Faces into other chunks count against the
    ///     neighbour's written-back pressure, or its live pressure if it never activated. They count because the
    ///     intra-chunk scan cannot see a gradient that sits on a chunk face. The metric is forced to at least
    ///     <see cref="AtmosSolverConfigSnapshot.SleepEpsilon" /> while any cell carries momentum, so a sleeping
    ///     chunk always has zero momentum everywhere. A non-finite pressure on either side of a face scores as a
    ///     gas-to-void face. A NaN would otherwise ride through every <see cref="MathF.Max" /> and make the decision
    ///     depend on where in the chunk it sits. The result lands in <see cref="EulerChunkWorkspace.SleepScore" />,
    ///     <see cref="EulerChunkWorkspace.HasMotion" />, and <see cref="EulerChunkWorkspace.NonFinitePressures" />
    ///     (the count of air voxels with a non-finite pressure). The method reads chunk fields and writes only the
    ///     workspace, so chunks can be scored in parallel.
    /// </remarks>
    private static void ScoreSleepState(EulerChunkWorkspace workspace, AtmosSolverConfigSnapshot config)
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

                Pascal reference = MathF.Max(pressure, neighborPressure);
                if (reference <= 0f)
                    continue;

                Scalar relative = MathF.Abs(pressure - neighborPressure) / reference * 100f;
                maximumRelativePressureDelta = MathF.Max(maximumRelativePressureDelta, relative);
            }
        }

        if (hasMotion)
            maximumRelativePressureDelta = MathF.Max(maximumRelativePressureDelta, config.SleepEpsilon);

        AssertEmptyVoxelsAreStill(chunk);
        workspace.SleepScore = maximumRelativePressureDelta;
        workspace.HasMotion = hasMotion;
        workspace.NonFinitePressures = nonFinitePressures;
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

    private readonly struct StaticAction(AtmosSolverExecutionContext context, bool[] result, MetrePerSecond[] speed) : IAction
    {
        public void Invoke(int index)
        {
            AtmosChunk chunk = context.Chunks[index];
            MetrePerSecond signalSpeed = 0f;
            result[index] = chunk.IsAwake && chunk.ActiveGasCount > 0 &&
                            EulerStaticChunk.IsStatic(chunk, context, out signalSpeed);

            speed[index] = signalSpeed;
        }
    }

    private readonly struct GatherAction(List<EulerChunkWorkspace> batch, AtmosSolverConfigSnapshot config) : IAction
    {
        public void Invoke(int index)
        {
            EulerChunkWorkspace workspace = batch[index];
            workspace.Prepare(config);
            EulerFaceTopology.Build(workspace.Chunk, workspace.NeighborChunks, workspace.OpenMask, workspace.VoidMask);
            EulerFaceTopology.BuildAirMap(workspace.Chunk, workspace.AirMap, workspace.RowAllAir);
            workspace.Gather(config);
        }
    }

    private readonly struct ScanAction(
        List<EulerChunkWorkspace> batch, Pascal wakeDelta, MetrePerSecond velocityFloor) : IAction
    {
        public void Invoke(int index)
        {
            ScanBoundaryFaces(batch[index], wakeDelta, velocityFloor);
        }
    }

    private readonly struct WriteBackAction(
        EulerChunkWorkspace[] active, AtmosSolverConfigSnapshot config, EulerFlowConfig settings) : IAction
    {
        public void Invoke(int index)
        {
            FloorMomentumAndWriteBack(active[index], config, settings);
        }
    }

    private readonly struct SettleMomentumAction(
        EulerChunkWorkspace[] active, AtmosSolverConfigSnapshot config, EulerFlowConfig settings) : IAction
    {
        public void Invoke(int index)
        {
            SettleMomentum(active[index], config, settings);
        }
    }

    private readonly struct ScoreSleepAction(EulerChunkWorkspace[] active, AtmosSolverConfigSnapshot config) : IAction
    {
        public void Invoke(int index)
        {
            ScoreSleepState(active[index], config);
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
