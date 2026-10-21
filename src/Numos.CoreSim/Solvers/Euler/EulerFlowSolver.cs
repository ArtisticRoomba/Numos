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
///         Per tick the stage gathers each active chunk (awake, with at least one gas channel, plus whatever
///         activation adds) into pooled conserved buffers, runs substeps until the fixed time step is used up or
///         the substep cap engages, writes the buffers back, and then does the tick-end work the legacy transport
///         stages also own, in this order: momentum floor and damping (before the write-back, so the kinetic
///         energy they remove becomes heat), species diffusion inside and across chunks
///         (<see cref="EulerSpeciesDiffusion" />), <see cref="VacuumCleanup" />, the momentum post-condition
///         (<see cref="SettleMomentum" />), and last the sleep metric and <see cref="ChunkSleepPolicy" />, which
///         therefore score the pressures diffusion and cleanup left behind. Explicit links are not part of this
///         stage yet.
///     </para>
///     <para>
///         Faces between chunks are ordinary faces. Both chunks solve a shared face from the same two read-buffer
///         cells with the lower coordinate on the left (see <see cref="EulerSweep" />), so there is no
///         reserve-and-merge pass: the two evaluations are the same bits, and each chunk applies its own side of
///         the difference. Every substep uses one global <c>Δt</c>, sized from the fastest signal in any active
///         chunk, so every chunk advances the same gas time and the state of a shared face is the same on both
///         sides. Sweep order alternates between <c>X, Y, Z</c> and <c>Z, Y, X</c> from one substep to the next,
///         the usual symmetric splitting. The starting parity is <c>TickCount &amp; 1</c> rather than a counter
///         carried across ticks, so it is a pure function of checkpointed state and a restored world sweeps in the
///         same order the original run did.
///     </para>
///     <para>
///         The active set grows during the tick. An open face into a chunk that has no workspace is a wall for
///         the sweep that meets it, and that sweep requests the chunk's activation when the two sides differ in
///         pressure by more than <see cref="EulerFlowConfig.WakePressureDelta" /> or the active side moves normal
///         to the face. Requests are applied on the tick thread after the barrier that ends the sweep: the chunk
///         is woken if it was asleep, gathered from its live fields (exact, nothing has written them this tick),
///         and inserted into the active list in canonical chunk order. A pre-pass right after the initial gather
///         runs the same test over every boundary face from the live pressures and iterates until no new chunk
///         joins, so a chunk that is already different from its neighbour at the start of the tick joins before
///         the first sweep and only a front that arrives mid-tick pays the one-sweep wall latency. Activation is
///         derived state: it is a function of the chunk fields and the configuration, never recorded.
///     </para>
///     <para>
///         Nothing else about the stage persists between ticks: the chunk's own momentum arrays are the only
///         state it keeps, and the buffers are rebuilt from the chunk fields every tick.
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

    private readonly Int3 _dimensions;
    private readonly List<AtmosChunk> _pendingActivation = [];
    private readonly List<EulerChunkWorkspace> _pendingScan = [];
    private readonly Stack<EulerChunkWorkspace> _pool = new();
    private EulerChunkWorkspace[] _active = [];
    private int _activeCount;
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
            for (int chunkIndex = 0; chunkIndex < context.Chunks.Length; chunkIndex++)
            {
                AtmosChunk chunk = context.Chunks[chunkIndex];
                if (chunk.IsAwake && chunk.ActiveGasCount > 0)
                    Activate(chunk, context);
            }

            ActivateNeighboursAtRest(context);

            LastDiagnostics = _activeCount == 0
                ? default
                : Advance(context, config.EulerFlow);

            // An awake chunk that never got a workspace has nothing to move, but its sleep timer still has to
            // advance, as it does under advection. Chunks the stage put to sleep are no longer awake here.
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
            _pendingScan.Clear();
            _pendingActivation.Clear();
        }
    }

    /// <summary>
    ///     Drops the pooled workspaces. Called when a checkpoint replaces the kernel's chunks; the pool holds no
    ///     simulation state, this only releases memory sized for the old grid.
    /// </summary>
    internal void ClearTransientState()
    {
        _pool.Clear();
        Array.Clear(_workspaceByDenseId);
    }

    /// <summary>
    ///     Gives a chunk a workspace for the rest of the tick: wakes it if asleep, builds its topology, gathers its
    ///     live fields, and links it to its active neighbours. Tick thread only.
    /// </summary>
    /// <remarks>
    ///     The workspace lists only the chunk's own gases here. Rows for gases a neighbour can push across a face
    ///     are added by <see cref="ShareAxisGases" /> before each sweep, so a chunk activated mid-tick is covered
    ///     by the next sweep's merge like everyone else.
    /// </remarks>
    private EulerChunkWorkspace Activate(AtmosChunk chunk, AtmosSolverExecutionContext context)
    {
        AtmosSolverConfigSnapshot config = context.TickConfig;
        if (!chunk.IsAwake)
            chunk.Wake();

        EulerChunkWorkspace workspace = _pool.Count > 0 ? _pool.Pop() : new EulerChunkWorkspace();
        workspace.Bind(chunk, config);
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

        EulerFaceTopology.Build(chunk, workspace.NeighborChunks, workspace.OpenMask, workspace.VoidMask);
        workspace.Gather(config);

        // Canonical order matters for nothing numerically; it keeps diagnostics and debugging reproducible.
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
        return workspace;
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
    ///     wake delta; the gathered pressure round-trips through the energy and can differ in the last bit.
    /// </remarks>
    private void ActivateNeighboursAtRest(AtmosSolverExecutionContext context)
    {
        Pascal wakeDelta = context.TickConfig.EulerFlow.WakePressureDelta;
        while (_pendingScan.Count > 0)
        {
            foreach (EulerChunkWorkspace workspace in _pendingScan)
                ScanBoundaryFaces(workspace, wakeDelta);

            _pendingScan.Clear();
            ApplyWakeRequests(context);
        }
    }

    private static void ScanBoundaryFaces(EulerChunkWorkspace workspace, Pascal wakeDelta)
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
                ref readonly EulerCellState cell = ref read.Cells[voxelIndex];
                MetrePerSecond normalVelocity = direction switch
                {
                    0 or 1 => cell.VelocityX,
                    2 or 3 => cell.VelocityY,
                    _ => cell.VelocityZ
                };

                Pascal pressureDelta = chunk.TotalPressure[voxelIndex] - neighbor.TotalPressure[neighborIndex];
                if (normalVelocity != 0f || MathF.Abs(pressureDelta) > wakeDelta)
                    workspace.WakeRequests[direction] = true;
            }
        }
    }

    /// <summary>
    ///     Activates every chunk some workspace asked for since the last call. Tick thread only, never while a
    ///     sweep is running: it inserts into the active list and rewrites neighbour slots that workers read.
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
        int parity = context.TickCount & 1;
        var counters = new EulerSweepCounters();

        while (remaining > 0d)
        {
            if (cap > 0 && substeps == cap)
            {
                capEngaged = true;
                break;
            }

            MetrePerSecond signalSpeed = MaxSignalSpeed();
            maxSignalSpeed = MathF.Max(maxSignalSpeed, signalSpeed);
            // The last substep is however much of the tick is left, even when that is tiny. Stretching the
            // previous substep to absorb it would run that substep above the Courant number the positivity
            // bound was chosen for, which is exactly the margin a face next to vacuum lives on.
            Second64 step = remaining;
            if (signalSpeed > 0f)
            {
                Second cflStep = courant * voxelEdge / signalSpeed;
                step = Math.Min(cflStep, remaining);
            }

            Second timeStep = (float)step;
            for (int order = 0; order < 3; order++)
            {
                int axis = parity == 0 ? order : 2 - order;
                if (!AxisHasWork(axis))
                    continue;

                ShareAxisGases(axis, config);
                RunSweep(axis, timeStep, faceArea, config);
                for (int index = 0; index < _activeCount; index++)
                {
                    EulerChunkWorkspace workspace = _active[index];
                    ReducePencils(workspace, axis, ref counters);
                    workspace.Swap();
                }

                // A chunk activated here is swept from the next sweep on, at this substep's Δt. Its gas is at
                // rest, so its signal speed is its sound speed, which the next substep's Δt accounts for.
                ApplyWakeRequests(context);
            }

            MetrePerSecond signalSpeedAfter = MaxSignalSpeed();
            maxSignalSpeed = MathF.Max(maxSignalSpeed, signalSpeedAfter);
            Scalar courantAfter = (float)(signalSpeedAfter * step / voxelEdge);
            maxCourant = MathF.Max(maxCourant, courantAfter);

            remaining -= step;
            simulated += step;
            substeps++;
            parity ^= 1;
        }

        int negativeInternalEnergy = 0;
        for (int index = 0; index < _activeCount; index++)
            FloorMomentumAndWriteBack(_active[index], config, settings, ref negativeInternalEnergy);

        // Diffusion runs on the written-back fields of every chunk, including the ones across a face, so it
        // waits for every write-back; cleanup compares neighbours' pressures the same way and waits for diffusion.
        EulerSpeciesDiffusion.Run(_active, _activeCount, WorkerCount, config);
        VacuumCleanup.Apply(context);

        for (int index = 0; index < _activeCount; index++)
            SettleMomentum(_active[index], config, settings);

        // Sleep decisions read neighbours' final pressures, so they wait for everything above.
        for (int index = 0; index < _activeCount; index++)
            UpdateSleepState(_active[index], config);

        return new AtmosFlowDiagnostics(
            substeps,
            simulated,
            capEngaged,
            maxCourant,
            maxSignalSpeed,
            counters.ClampedVelocity,
            negativeInternalEnergy,
            counters.NegativeMoles);
    }

    /// <summary>
    ///     Gives every active chunk a row for each gas its active neighbours across the two faces of
    ///     <paramref name="axis" /> list, so the sweep can move any of them into the chunk without a worker ever
    ///     creating a row.
    /// </summary>
    /// <remarks>
    ///     This is the only place rows grow after binding, and it runs on the tick thread between sweeps. The
    ///     merge is one pass over the active list, which can hand a chunk a gas from two chunks away (its
    ///     neighbour just received the row from its own neighbour); that costs a zero row, never a wrong bit.
    ///     Rows for a chunk's sake only: a gas listed because a neighbour holds it does not get a channel unless the
    ///     write-back finds a tracked amount of it.
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
                    workspace.AddGasesOf(neighbor, config);
            }
        }
    }

    /// <summary>
    ///     Runs one axis sweep over every active chunk on the worker pool.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Two schedules, chosen the way advection chooses. With at least as many active chunks as workers,
    ///         one work item per chunk sweeps all of that chunk's pencils. Otherwise the pencils of every chunk are
    ///         tiled into ranges of <c>ceil(totalPencils / workers)</c>, so one dense chunk still occupies the
    ///         pool. Both schedules call <see cref="EulerSweep.RunPencils" /> and therefore visit a chunk's pencils
    ///         in the same ascending order; a pencil writes only its own cells of the write buffer and its own
    ///         reduction slots, and reads only buffers no sweep writes. Splitting the same ordered pencil list
    ///         differently across workers thus cannot change a bit, which is what the schedule-parity tests pin.
    ///     </para>
    ///     <para>
    ///         Workers also read the live pressure of inactive neighbour chunks and set activation flags on their
    ///         own workspace; neither is written by anything else until the barrier.
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
    }

    /// <summary>
    ///     Folds a chunk's per-pencil slots into its write buffer's signal speed and the tick's repair counters.
    ///     Max and sum, so the order is immaterial.
    /// </summary>
    private static void ReducePencils(EulerChunkWorkspace workspace, int axis, ref EulerSweepCounters counters)
    {
        int pencilCount = EulerSweep.PencilCount(workspace.Chunk, axis);
        MetrePerSecond signalSpeed = 0f;
        for (int pencil = 0; pencil < pencilCount; pencil++)
        {
            signalSpeed = MathF.Max(signalSpeed, workspace.PencilSignalSpeed[pencil]);
            counters.ClampedVelocity += workspace.PencilCounters[pencil].ClampedVelocity;
            counters.NegativeMoles += workspace.PencilCounters[pencil].NegativeMoles;
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
    ///     An axis one voxel long has no interior faces, but its two chunk faces are still faces of the sweep, so it
    ///     is only skipped when no active chunk has a registered neighbour on either side. Both faces are then
    ///     walls at the cell's own pressure, whose fluxes cancel exactly, so skipping changes no bit; it mostly
    ///     saves depth-one maps a third of their sweeps. A neighbour activated mid-tick is seen on the next
    ///     substep, which is also the first one that could sweep it.
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
        MetrePerSecond speed = 0f;
        for (int index = 0; index < _activeCount; index++)
            speed = MathF.Max(speed, _active[index].Read.MaxSignalSpeed);

        return speed;
    }

    /// <summary>
    ///     Tick-end work for one chunk's buffer: the momentum floor and damping, then the write-back.
    /// </summary>
    /// <remarks>
    ///     The floor zeroes the momentum of every cell slower than <see cref="EulerFlowConfig.VelocityFloor" />
    ///     and of every trace cell; damping then scales what is left by <c>max(0, 1 − α·Δt_tick)</c>. Both run
    ///     before the write-back computes temperatures, so the kinetic energy they remove turns into heat instead
    ///     of vanishing.
    /// </remarks>
    private static void FloorMomentumAndWriteBack(
        EulerChunkWorkspace workspace, AtmosSolverConfigSnapshot config, EulerFlowConfig settings,
        ref int negativeInternalEnergyCount)
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
            bool zero = EulerChunkWorkspace.IsTrace(mass, buffer.TotalMoles[voxelIndex]);
            if (!zero && floor > 0f)
            {
                PerKilogram inverseMass = 1f / mass;
                MetrePerSecond velocityX = momentumX * inverseMass;
                MetrePerSecond velocityY = momentumY * inverseMass;
                MetrePerSecond velocityZ = momentumZ * inverseMass;
                zero = velocityX * velocityX + velocityY * velocityY + velocityZ * velocityZ < floor * floor;
            }

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

        workspace.WriteBack(config, ref negativeInternalEnergyCount);
        chunk.MarkChanged();
    }

    /// <summary>
    ///     Re-establishes the momentum invariants on the live fields after diffusion and vacuum cleanup: a vacuum or
    ///     trace voxel carries none, and a voxel slower than <see cref="EulerFlowConfig.VelocityFloor" /> is brought
    ///     to rest with its kinetic energy turned into heat, as the pre-write-back floor does.
    /// </summary>
    /// <remarks>
    ///     The floor ran on the buffers before the write-back, but diffusion changes a voxel's mass and cleanup
    ///     empties voxels outright, so the test is repeated on the fields the sleep pass and the next tick's gather
    ///     read. On a voxel neither pass touched it changes nothing: cleanup zeroes the momentum of what it empties,
    ///     and the mass is summed over the workspace's gas list in the same ascending order as the buffer's, so the
    ///     floor comparison repeats bit for bit. The only gaps are a row the write-back dropped as untracked, which
    ///     makes the live mass smaller and the velocity larger, never the other way, and a channel boundary
    ///     diffusion created for a gas outside the list, which is summed after the list.
    /// </remarks>
    private void SettleMomentum(EulerChunkWorkspace workspace, AtmosSolverConfigSnapshot config, EulerFlowConfig settings)
    {
        AtmosChunk chunk = workspace.Chunk;
        MetrePerSecond floor = settings.VelocityFloor;
        for (int activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            KilogramMetrePerSecond momentumX = chunk.MomentumX[voxelIndex];
            KilogramMetrePerSecond momentumY = chunk.MomentumY[voxelIndex];
            KilogramMetrePerSecond momentumZ = chunk.MomentumZ[voxelIndex];
            if (momentumX == 0f && momentumY == 0f && momentumZ == 0f)
                continue;

            Kilogram mass = 0f;
            Mole totalMoles = 0f;
            bool zero = chunk.IsVacuum[voxelIndex];
            if (!zero)
            {
                for (int present = 0; present < workspace.GasCount; present++)
                {
                    int channel = chunk.FindGasChannel(workspace.GasIds[present]);
                    if (channel < 0)
                        continue;

                    Mole moles = chunk.ActiveGases[channel].Moles[voxelIndex];
                    mass += moles * workspace.MolarMass[present];
                    totalMoles += moles;
                }

                for (int gas = 0; gas < chunk.ActiveGasCount; gas++)
                {
                    int gasId = chunk.ActiveGases[gas].GasId;
                    if (workspace.Read.Species[gasId] != null)
                        continue;

                    Mole moles = chunk.ActiveGases[gas].Moles[voxelIndex];
                    mass += moles * config.GetMolarMass(gasId);
                    totalMoles += moles;
                }

                zero = EulerChunkWorkspace.IsTrace(mass, totalMoles);
            }

            if (!zero && floor > 0f)
            {
                PerKilogram inverseMass = 1f / mass;
                MetrePerSecond velocityX = momentumX * inverseMass;
                MetrePerSecond velocityY = momentumY * inverseMass;
                MetrePerSecond velocityZ = momentumZ * inverseMass;
                zero = velocityX * velocityX + velocityY * velocityY + velocityZ * velocityZ < floor * floor;
            }

            if (!zero)
                continue;

            JoulePerKelvin heatCapacity = chunk.TotalHeatCapacity[voxelIndex];
            if (mass > 0f && heatCapacity > 0f)
            {
                Joule64 energy = (double)heatCapacity * config.GetValidatedTemp(chunk.Temperature[voxelIndex]) +
                                 EulerMixture.KineticEnergy(mass, momentumX, momentumY, momentumZ);

                chunk.Temperature[voxelIndex] = config.GetValidatedTemp((float)(energy / heatCapacity));
                chunk.TotalPressure[voxelIndex] =
                    AtmosSolverMath.CalculatePressureAtVoxel(config, chunk, voxelIndex, totalMoles);
            }

            chunk.MomentumX[voxelIndex] = 0f;
            chunk.MomentumY[voxelIndex] = 0f;
            chunk.MomentumZ[voxelIndex] = 0f;
        }
    }

    /// <summary>
    ///     The sleep decision for one written-back chunk.
    /// </summary>
    /// <remarks>
    ///     The sleep metric is the legacy one: the largest neighbour pressure difference as a percentage of the
    ///     higher pressure, over open faces, with a gas-to-void face counting as 100%. Faces into other chunks
    ///     count too, against the neighbour's written-back pressure (or its untouched live pressure if it never
    ///     activated), because the intra-chunk scan cannot see a gradient that sits on a chunk face. The metric is
    ///     forced to at least <see cref="AtmosSolverConfigSnapshot.SleepEpsilon" /> while any cell still carries
    ///     momentum, so a chunk never sleeps mid-motion and a sleeping chunk always has zero momentum everywhere.
    /// </remarks>
    private static void UpdateSleepState(EulerChunkWorkspace workspace, AtmosSolverConfigSnapshot config)
    {
        AtmosChunk chunk = workspace.Chunk;
        Int3 dimensions = chunk.Dimensions;
        bool hasMotion = false;
        Scalar maximumRelativePressureDelta = 0f;
        for (int activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            hasMotion |= chunk.MomentumX[voxelIndex] != 0f || chunk.MomentumY[voxelIndex] != 0f ||
                         chunk.MomentumZ[voxelIndex] != 0f;

            Pascal pressure = chunk.TotalPressure[voxelIndex];
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

                Pascal reference = MathF.Max(pressure, neighborPressure);
                if (reference <= 0f)
                    continue;

                Scalar relative = MathF.Abs(pressure - neighborPressure) / reference * 100f;
                maximumRelativePressureDelta = MathF.Max(maximumRelativePressureDelta, relative);
            }
        }

        if (hasMotion)
            maximumRelativePressureDelta = MathF.Max(maximumRelativePressureDelta, config.SleepEpsilon);

        ChunkSleepPolicy.UpdateSleepState(chunk, config, maximumRelativePressureDelta);
        Debug.Assert(chunk.IsAwake || !hasMotion, "A chunk must not fall asleep while it still carries momentum.");
        AssertEmptyVoxelsAreStill(chunk);
    }

    [Conditional("DEBUG")]
    private static void AssertEmptyVoxelsAreStill(AtmosChunk chunk)
    {
        for (int activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            if (!chunk.IsVacuum[voxelIndex])
                continue;

            Debug.Assert(
                chunk.MomentumX[voxelIndex] == 0f && chunk.MomentumY[voxelIndex] == 0f && chunk.MomentumZ[voxelIndex] == 0f,
                "A vacuum voxel must carry no momentum.");
        }
    }

    /// <summary>
    ///     A range of one chunk's pencils for the tiled schedule.
    /// </summary>
    private readonly record struct PencilWorkItem(int WorkspaceIndex, int FirstPencil, int PencilCount);

    private readonly struct SweepChunkAction(
        EulerChunkWorkspace[] active, int axis, int pencilCount,
        Second timeStep, SquareMetre faceArea, AtmosSolverConfigSnapshot config) : IAction
    {
        public void Invoke(int index)
        {
            EulerSweep.RunPencils(active[index], axis, 0, pencilCount, timeStep, faceArea, config);
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
