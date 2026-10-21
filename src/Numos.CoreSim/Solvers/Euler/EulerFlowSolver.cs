using System.Diagnostics;
using Numos.CoreSim.EulerFlow;
using Numos.Maths;

namespace Numos.CoreSim.Solvers.Euler;

/// <summary>
///     The <c>euler-flow</c> stage: compressible Euler gas transport inside every awake chunk, dimensionally split
///     into first-order Godunov sweeps with <see cref="HllcFlux" /> at every face and CFL substeps inside the tick.
/// </summary>
/// <remarks>
///     <para>
///         Per tick the stage gathers each active chunk (awake, with at least one gas channel) into pooled
///         conserved buffers, runs substeps until the fixed time step is used up or the substep cap engages, writes
///         the buffers back, and then does the tick-end bookkeeping the legacy advection stage also owns: momentum
///         floor and damping, the sleep metric, and <see cref="ChunkSleepPolicy" />. Faces into neighbouring
///         chunks are walls for now; cross-chunk flow, species diffusion, vacuum cleanup, and explicit links are
///         not part of this stage yet.
///     </para>
///     <para>
///         Every substep uses one global <c>Δt</c>, sized from the fastest signal in any active chunk, so every
///         chunk advances the same gas time and the state of a shared face (once there are any) is the same on
///         both sides. Sweep order alternates between <c>X, Y, Z</c> and <c>Z, Y, X</c> from one substep to the
///         next, the usual symmetric splitting. The starting parity is <c>TickCount &amp; 1</c> rather than a
///         counter carried across ticks, so it is a pure function of checkpointed state and a restored world
///         sweeps in the same order the original run did. Nothing else about the stage persists between ticks:
///         the chunk's own momentum arrays are the only state it keeps, and the buffers are rebuilt from the chunk
///         fields every tick.
///     </para>
///     <para>
///         Chunks are swept sequentially in this version. <see cref="EulerSweep.RunChunk" /> is written so a
///         parallel dispatch over chunks or bundles can call it unchanged.
///     </para>
/// </remarks>
internal sealed class EulerFlowSolver : IAtmosSolverStage
{
    private readonly Int3 _dimensions;
    private readonly Stack<EulerChunkWorkspace> _pool = new();
    private EulerChunkWorkspace[] _active = [];

    internal EulerFlowSolver(int chunkWidth, int chunkHeight, int chunkDepth)
    {
        _dimensions = new Int3(chunkWidth, chunkHeight, chunkDepth);
    }

    /// <summary>
    ///     What the most recent <see cref="Solve" /> did. All zeros before the first tick.
    /// </summary>
    internal AtmosFlowDiagnostics LastDiagnostics { get; private set; }

    public void Solve(AtmosSolverExecutionContext context)
    {
        AtmosSolverConfigSnapshot config = context.TickConfig;
        EulerFlowConfig settings = config.EulerFlow;
        int activeCount = 0;
        try
        {
            for (int chunkIndex = 0; chunkIndex < context.Chunks.Length; chunkIndex++)
            {
                AtmosChunk chunk = context.Chunks[chunkIndex];
                if (!chunk.IsAwake)
                    continue;

                if (chunk.ActiveGasCount == 0)
                {
                    // Same as advection: nothing to move, but the sleep timer still has to advance.
                    ChunkSleepPolicy.UpdateSleepState(chunk, config, 0f);
                    continue;
                }

                if (_active.Length == activeCount)
                    Array.Resize(ref _active, Math.Max(4, activeCount * 2));

                EulerChunkWorkspace workspace = _pool.Count > 0 ? _pool.Pop() : new EulerChunkWorkspace();
                workspace.Bind(chunk, config);
                EulerFaceTopology.Build(chunk, workspace.OpenMask, workspace.VoidMask);
                workspace.Gather(config);
                _active[activeCount++] = workspace;
            }

            LastDiagnostics = activeCount == 0
                ? default
                : Advance(context, settings, activeCount);
        }
        finally
        {
            for (int index = 0; index < activeCount; index++)
            {
                _pool.Push(_active[index]);
                _active[index] = null!;
            }
        }
    }

    /// <summary>
    ///     Drops the pooled workspaces. Called when a checkpoint replaces the kernel's chunks; the pool holds no
    ///     simulation state, this only releases memory sized for the old grid.
    /// </summary>
    internal void ClearTransientState()
    {
        _pool.Clear();
    }

    private AtmosFlowDiagnostics Advance(
        AtmosSolverExecutionContext context, EulerFlowConfig settings, int activeCount)
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

            MetrePerSecond signalSpeed = MaxSignalSpeed(activeCount);
            maxSignalSpeed = MathF.Max(maxSignalSpeed, signalSpeed);
            Second64 step = remaining;
            if (signalSpeed > 0f)
            {
                Second cflStep = courant * voxelEdge / signalSpeed;
                step = Math.Min(cflStep, remaining);
            }

            // A residual shorter than a tenth of a step is not worth a sweep of its own.
            if (remaining - step < 0.1d * step)
                step = remaining;

            Second timeStep = (float)step;
            for (int order = 0; order < 3; order++)
            {
                int axis = parity == 0 ? order : 2 - order;
                if (Extent(axis) == 1)
                    continue;

                for (int index = 0; index < activeCount; index++)
                    EulerSweep.RunChunk(_active[index], axis, timeStep, faceArea, config, ref counters);

                for (int index = 0; index < activeCount; index++)
                    _active[index].Swap();
            }

            MetrePerSecond signalSpeedAfter = MaxSignalSpeed(activeCount);
            maxSignalSpeed = MathF.Max(maxSignalSpeed, signalSpeedAfter);
            Scalar courantAfter = (float)(signalSpeedAfter * step / voxelEdge);
            maxCourant = MathF.Max(maxCourant, courantAfter);

            remaining -= step;
            simulated += step;
            substeps++;
            parity ^= 1;
        }

        int negativeInternalEnergy = 0;
        for (int index = 0; index < activeCount; index++)
            Finish(_active[index], config, settings, ref negativeInternalEnergy);

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
    ///     Voxels along one axis of every chunk in this kernel. An axis one voxel long has nothing to sweep.
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

    private MetrePerSecond MaxSignalSpeed(int activeCount)
    {
        MetrePerSecond speed = 0f;
        for (int index = 0; index < activeCount; index++)
            speed = MathF.Max(speed, _active[index].Read.MaxSignalSpeed);

        return speed;
    }

    /// <summary>
    ///     Tick-end work for one chunk: the momentum floor and damping on the buffer, the write-back, and the
    ///     sleep decision.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The floor zeroes the momentum of every cell slower than
    ///         <see cref="EulerFlowConfig.VelocityFloor" /> and of every trace cell; damping then scales what is
    ///         left by <c>max(0, 1 − α·Δt_tick)</c>. Both run before the write-back computes temperatures, so the
    ///         kinetic energy they remove turns into heat instead of vanishing.
    ///     </para>
    ///     <para>
    ///         The sleep metric is the legacy one: the largest neighbour pressure difference as a percentage of the
    ///         higher pressure, over open faces, with a gas-to-void face counting as 100%. It is forced to at least
    ///         <see cref="AtmosSolverConfigSnapshot.SleepEpsilon" /> while any cell still carries momentum, so a
    ///         chunk never sleeps mid-motion and a sleeping chunk always has zero momentum everywhere.
    ///     </para>
    /// </remarks>
    private static void Finish(
        EulerChunkWorkspace workspace, AtmosSolverConfigSnapshot config, EulerFlowConfig settings,
        ref int negativeInternalEnergyCount)
    {
        AtmosChunk chunk = workspace.Chunk;
        EulerChunkState buffer = workspace.Read;
        MetrePerSecond floor = settings.VelocityFloor;
        Scalar damping = settings.MomentumDamping > 0f
            ? MathF.Max(0f, 1f - settings.MomentumDamping * AtmosSolverConstants.FixedTimeStep)
            : 1f;

        bool hasMotion = false;
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
            hasMotion |= momentumX != 0f || momentumY != 0f || momentumZ != 0f;
        }

        workspace.WriteBack(config, ref negativeInternalEnergyCount);

        Scalar maximumRelativePressureDelta = 0f;
        for (int activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            Pascal pressure = chunk.TotalPressure[voxelIndex];
            byte open = workspace.OpenMask[voxelIndex];
            byte sink = workspace.VoidMask[voxelIndex];
            if (sink != 0 && pressure > 0f)
                maximumRelativePressureDelta = 100f;

            // Positive directions only: the negative face of this voxel is the positive face of its neighbour.
            for (int direction = 1; direction < NeighborResolution.NeighborDirections.Length; direction += 2)
            {
                if (!EulerFaceTopology.IsOpen(open, direction))
                    continue;

                ushort neighborIndex = chunk.GetIndexUnsafe(
                    chunk.GetXyzInt3(voxelIndex) + NeighborResolution.NeighborDirections[direction]);

                Pascal neighborPressure = chunk.TotalPressure[neighborIndex];
                Pascal reference = MathF.Max(pressure, neighborPressure);
                if (reference <= 0f)
                    continue;

                Scalar relative = MathF.Abs(pressure - neighborPressure) / reference * 100f;
                maximumRelativePressureDelta = MathF.Max(maximumRelativePressureDelta, relative);
            }
        }

        if (hasMotion)
            maximumRelativePressureDelta = MathF.Max(maximumRelativePressureDelta, config.SleepEpsilon);

        chunk.MarkChanged();
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
}
