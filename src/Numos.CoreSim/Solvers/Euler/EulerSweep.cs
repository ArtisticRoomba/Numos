using System.Buffers;
using System.Diagnostics;

namespace Numos.CoreSim.Solvers.Euler;

/// <summary>
///     Extensive flux through one voxel face for one substep: per-second rates, positive from the lower-coordinate
///     cell to the higher one, already multiplied by the face area.
/// </summary>
/// <remarks>
///     Species are not stored per face. The face keeps the upwind cell and <c>A·Mass/m_upwind</c>, and the cell
///     update multiplies that by each species' moles in the upwind cell of the <em>read</em> buffer. Every species
///     therefore moves in the proportion it has upwind, and <c>Σ_k M_k·G_k = A·G_mass</c> holds to rounding, which
///     is the Larrouturou-consistent transport that keeps mass and moles from drifting apart.
/// </remarks>
internal struct EulerFaceRate
{
    public KilogramPerSecond Mass;
    public PerSecond SpeciesRate;
    public int UpwindIndex;
    public Newton NormalMomentum;
    public Newton Tangential1Momentum;
    public Newton Tangential2Momentum;
    public Watt Energy;
}

/// <summary>
///     Counts the repairs a sweep had to make; a bug indicator surfaced through
///     <see cref="EulerFlow.AtmosFlowDiagnostics" />.
/// </summary>
internal struct EulerSweepCounters
{
    public int ClampedVelocity;
    public int NegativeMoles;
}

/// <summary>
///     One dimensionally split first-order Godunov sweep along a grid axis for one chunk, reading the chunk's
///     read buffer and writing every active cell of its write buffer.
/// </summary>
/// <remarks>
///     <para>
///         Work is cut into pencil bundles. An X sweep marches one row <c>(y, z)</c> along x; a Y sweep marches a
///         whole z-slab along y, handling the <c>W</c> faces of one x-row at a time; a Z sweep marches a y-sheet
///         along z the same way. The bundle keeps a rolling pair of face-rate rows (the faces behind the row of
///         cells being updated and the faces ahead of it), so each face is solved exactly once and each cell sees
///         both of its faces before it is written.
///     </para>
///     <para>
///         Determinism rests on three things. A cell's update is a two-term difference of the rates of its own two
///         faces, each rate computed once from the immutable read buffer, so the order bundles run in cannot change
///         a result. Mixture sums run in ascending gas id. Nothing uses <see cref="MathF.FusedMultiplyAdd" /> or any
///         call whose rounding is platform dependent beyond what <see cref="HllcFlux" /> already documents. The
///         per-chunk maximum signal speed is a max reduction and is order free.
///     </para>
///     <para>
///         <see cref="SweepBundle" /> is the unit a parallel action can call once bundles are dispatched across
///         workers; the only state it shares with other bundles of the same sweep is the read buffer, which no
///         bundle writes.
///     </para>
/// </remarks>
internal static class EulerSweep
{
    /// <summary>
    ///     Runs every bundle of one axis sweep for a chunk, sequentially.
    /// </summary>
    /// <param name="workspace">The chunk's bound and gathered workspace.</param>
    /// <param name="axis">0 for X, 1 for Y, 2 for Z.</param>
    /// <param name="timeStep">The substep length.</param>
    /// <param name="faceArea">Area of one voxel face, <c>Δx²</c>.</param>
    /// <param name="config">The tick's configuration snapshot.</param>
    /// <param name="counters">Receives this sweep's repair counts.</param>
    internal static void RunChunk(
        EulerChunkWorkspace workspace, int axis, Second timeStep, SquareMetre faceArea,
        AtmosSolverConfigSnapshot config, ref EulerSweepCounters counters)
    {
        AtmosChunk chunk = workspace.Chunk;
        int bundleCount = axis switch
        {
            0 => chunk.Height * chunk.Depth,
            1 => chunk.Depth,
            _ => chunk.Height
        };

        EulerChunkState write = workspace.Write;
        write.MaxSignalSpeed = 0f;
        for (int bundle = 0; bundle < bundleCount; bundle++)
        {
            MetrePerSecond bundleSignalSpeed =
                SweepBundle(workspace, axis, bundle, timeStep, faceArea, config, ref counters);

            write.MaxSignalSpeed = MathF.Max(write.MaxSignalSpeed, bundleSignalSpeed);
        }
    }

    /// <summary>
    ///     Sweeps one pencil bundle and returns the fastest signal speed among the cells it wrote.
    /// </summary>
    /// <remarks>
    ///     The march index runs one past the last cell so the trailing wall faces are solved by the same loop as
    ///     the interior ones: at march position <c>j</c> the loop solves the faces between rows <c>j − 1</c> and
    ///     <c>j</c>, then updates row <c>j − 1</c>, whose leading faces were solved on the previous step.
    /// </remarks>
    internal static MetrePerSecond SweepBundle(
        EulerChunkWorkspace workspace, int axis, int bundle, Second timeStep, SquareMetre faceArea,
        AtmosSolverConfigSnapshot config, ref EulerSweepCounters counters)
    {
        AtmosChunk chunk = workspace.Chunk;
        int width = chunk.Width;
        int height = chunk.Height;
        int baseIndex;
        int marchStride;
        int marchCount;
        int rowLength;
        switch (axis)
        {
            case 0:
                baseIndex = bundle % height * width + bundle / height * width * height;
                marchStride = 1;
                marchCount = width;
                rowLength = 1;
                break;
            case 1:
                baseIndex = bundle * width * height;
                marchStride = width;
                marchCount = height;
                rowLength = width;
                break;
            default:
                baseIndex = bundle * width;
                marchStride = width * height;
                marchCount = chunk.Depth;
                rowLength = width;
                break;
        }

        EulerFaceRate[] behind = ArrayPool<EulerFaceRate>.Shared.Rent(rowLength);
        EulerFaceRate[] ahead = ArrayPool<EulerFaceRate>.Shared.Rent(rowLength);
        MetrePerSecond maxSignalSpeed = 0f;
        try
        {
            bool allMach = config.EulerFlow.AllMachCorrection;
            EulerChunkState read = workspace.Read;
            for (int march = 0; march <= marchCount; march++)
            {
                int lowerRow = march > 0 ? baseIndex + (march - 1) * marchStride : -1;
                int upperRow = march < marchCount ? baseIndex + march * marchStride : -1;
                for (int offset = 0; offset < rowLength; offset++)
                {
                    SolveFace(
                        workspace, read, axis,
                        lowerRow < 0 ? -1 : lowerRow + offset,
                        upperRow < 0 ? -1 : upperRow + offset,
                        faceArea, allMach, out ahead[offset]);
                }

                if (march > 0)
                {
                    for (int offset = 0; offset < rowLength; offset++)
                    {
                        int voxelIndex = lowerRow + offset;
                        if (!EulerFaceTopology.IsAir(chunk, voxelIndex))
                            continue;

                        MetrePerSecond signalSpeed = UpdateCell(
                            workspace, axis, voxelIndex, in behind[offset], in ahead[offset],
                            timeStep, config, ref counters);

                        maxSignalSpeed = MathF.Max(maxSignalSpeed, signalSpeed);
                    }
                }

                (behind, ahead) = (ahead, behind);
            }
        }
        finally
        {
            ArrayPool<EulerFaceRate>.Shared.Return(behind);
            ArrayPool<EulerFaceRate>.Shared.Return(ahead);
        }

        return maxSignalSpeed;
    }

    /// <summary>
    ///     Classifies and solves the face between a lower-coordinate cell and the next cell along the axis.
    /// </summary>
    /// <param name="workspace">The chunk's workspace, for the face masks.</param>
    /// <param name="read">The buffer the sweep reads cell states from.</param>
    /// <param name="axis">The sweep axis, which is the face normal.</param>
    /// <param name="lowerIndex">The lower cell, or -1 outside the chunk.</param>
    /// <param name="upperIndex">The upper cell, or -1 outside the chunk.</param>
    /// <param name="faceArea">Area of one voxel face.</param>
    /// <param name="allMach">Whether <see cref="HllcFlux.Solve" /> applies its low-Mach pressure fix.</param>
    /// <param name="rate">The extensive flux through the face.</param>
    /// <remarks>
    ///     Two air cells always share an open face today, because <see cref="EulerFaceTopology.Build" /> sets the
    ///     open bit for every air neighbour; the assertion is where a blocked air-to-air face would need a second
    ///     rate (a wall pressure per side) instead of one shared flux. Gas that crosses into a void cell is gone:
    ///     the void cell is never updated. A trace cell presents as the vacuum state, so the Riemann problem
    ///     against it is the expansion into vacuum and the trace cell can only receive.
    /// </remarks>
    private static void SolveFace(
        EulerChunkWorkspace workspace, EulerChunkState read, int axis,
        int lowerIndex, int upperIndex, SquareMetre faceArea, bool allMach,
        out EulerFaceRate rate)
    {
        AtmosChunk chunk = workspace.Chunk;
        bool lowerAir = lowerIndex >= 0 && EulerFaceTopology.IsAir(chunk, lowerIndex);
        bool upperAir = upperIndex >= 0 && EulerFaceTopology.IsAir(chunk, upperIndex);
        if (!lowerAir && !upperAir)
        {
            rate = default;
            return;
        }

        int positiveDirection = 2 * axis + 1;
        int negativeDirection = 2 * axis;
        EulerFaceFlux flux;
        int upwindIndex;
        if (lowerAir && upperAir)
        {
            Debug.Assert(
                EulerFaceTopology.IsOpen(workspace.OpenMask[lowerIndex], positiveDirection) &&
                EulerFaceTopology.IsOpen(workspace.OpenMask[upperIndex], negativeDirection),
                "Two air voxels always share an open face until blocked faces exist.");

            EulerFaceState left = EulerFaceState.FromCell(in read.Cells[lowerIndex], axis);
            EulerFaceState right = EulerFaceState.FromCell(in read.Cells[upperIndex], axis);
            HllcFlux.Solve(in left, in right, allMach, out flux);
            upwindIndex = flux.UpwindIsLeft ? lowerIndex : upperIndex;
        }
        else if (lowerAir)
        {
            EulerFaceState left = EulerFaceState.FromCell(in read.Cells[lowerIndex], axis);
            if (EulerFaceTopology.IsVoid(workspace.VoidMask[lowerIndex], positiveDirection))
                HllcFlux.Solve(in left, default, allMach, out flux);
            else
                HllcFlux.SolveWall(in left, true, out flux);

            upwindIndex = lowerIndex;
        }
        else
        {
            EulerFaceState right = EulerFaceState.FromCell(in read.Cells[upperIndex], axis);
            if (EulerFaceTopology.IsVoid(workspace.VoidMask[upperIndex], negativeDirection))
                HllcFlux.Solve(default, in right, allMach, out flux);
            else
                HllcFlux.SolveWall(in right, false, out flux);

            upwindIndex = upperIndex;
        }

        rate.Mass = flux.Mass * faceArea;
        rate.SpeciesRate = flux.SpeciesWeight * faceArea;
        rate.UpwindIndex = upwindIndex;
        rate.NormalMomentum = flux.NormalMomentum * faceArea;
        rate.Tangential1Momentum = flux.Tangential1Momentum * faceArea;
        rate.Tangential2Momentum = flux.Tangential2Momentum * faceArea;
        rate.Energy = flux.Energy * faceArea;
    }

    /// <summary>
    ///     Advances one air cell by the difference of its two face rates and refreshes its derived state.
    /// </summary>
    /// <returns>The cell's new signal speed, or zero for a trace cell.</returns>
    /// <remarks>
    ///     <para>
    ///         Every field of the write buffer is written for every air cell, so no stale value from an earlier
    ///         sweep survives in it. The tangential momentum components land on the axes
    ///         <see cref="EulerFaceState.FromCell" /> took them from: <c>(axis + 1) mod 3</c> and
    ///         <c>(axis + 2) mod 3</c>.
    ///     </para>
    ///     <para>
    ///         Species and momentum are updated in float; the energy difference is formed in double so the
    ///         subtraction from the large thermal part does not lose it. Species are floored at zero and the
    ///         speed is clamped to <see cref="EulerFlow.EulerFlowConfig.MaxVelocity" /> before the primitive state is
    ///         rebuilt; both repairs are counted.
    ///     </para>
    /// </remarks>
    private static MetrePerSecond UpdateCell(
        EulerChunkWorkspace workspace, int axis, int voxelIndex,
        in EulerFaceRate behind, in EulerFaceRate ahead,
        Second timeStep, AtmosSolverConfigSnapshot config, ref EulerSweepCounters counters)
    {
        EulerChunkState read = workspace.Read;
        EulerChunkState write = workspace.Write;

        for (int present = 0; present < workspace.PresentGasCount; present++)
        {
            int gasId = workspace.PresentGasIds[present];
            Mole[] source = read.Species[gasId]!;
            Mole[] target = write.Species[gasId]!;
            Mole moles = source[voxelIndex] -
                         timeStep *
                         (ahead.SpeciesRate * source[ahead.UpwindIndex] -
                          behind.SpeciesRate * source[behind.UpwindIndex]);

            if (moles < 0f)
            {
                counters.NegativeMoles++;
                moles = 0f;
            }

            target[voxelIndex] = moles;
        }

        // Normal momentum goes to the sweep axis; the tangential pair follows FromCell's cyclic order.
        KilogramMetrePerSecond normal = ReadMomentum(read, axis, voxelIndex) -
                                        timeStep * (ahead.NormalMomentum - behind.NormalMomentum);

        int tangential1Axis = (axis + 1) % 3;
        int tangential2Axis = (axis + 2) % 3;
        KilogramMetrePerSecond tangential1 = ReadMomentum(read, tangential1Axis, voxelIndex) -
                                             timeStep * (ahead.Tangential1Momentum - behind.Tangential1Momentum);

        KilogramMetrePerSecond tangential2 = ReadMomentum(read, tangential2Axis, voxelIndex) -
                                             timeStep * (ahead.Tangential2Momentum - behind.Tangential2Momentum);

        Joule64 energy = read.Energy[voxelIndex] - (double)timeStep * ((double)ahead.Energy - behind.Energy);
        write.Energy[voxelIndex] = energy;

        workspace.SumMixture(
            write, voxelIndex,
            out Kilogram mass, out Mole totalMoles, out JoulePerKelvin heatCapacity);

        write.Mass[voxelIndex] = mass;
        write.TotalMoles[voxelIndex] = totalMoles;
        write.HeatCapacity[voxelIndex] = heatCapacity;

        KilogramMetrePerSecond momentumX;
        KilogramMetrePerSecond momentumY;
        KilogramMetrePerSecond momentumZ;
        MetrePerSecond signalSpeed = 0f;
        if (EulerChunkWorkspace.IsTrace(mass, totalMoles))
        {
            momentumX = 0f;
            momentumY = 0f;
            momentumZ = 0f;
            write.Cells[voxelIndex] = default;
        }
        else
        {
            StoreMomentum(axis, normal, tangential1Axis, tangential1, tangential2Axis, tangential2,
                out momentumX, out momentumY, out momentumZ);

            PerKilogram inverseMass = 1f / mass;
            MetrePerSecond velocityX = momentumX * inverseMass;
            MetrePerSecond velocityY = momentumY * inverseMass;
            MetrePerSecond velocityZ = momentumZ * inverseMass;
            MetrePerSecond maxVelocity = config.EulerFlow.MaxVelocity;
            Scalar speedRatio = (velocityX * velocityX + velocityY * velocityY + velocityZ * velocityZ) /
                                (maxVelocity * maxVelocity);

            if (speedRatio > 1f)
            {
                counters.ClampedVelocity++;
                Scalar scale = 1f / MathF.Sqrt(speedRatio);
                momentumX *= scale;
                momentumY *= scale;
                momentumZ *= scale;
            }

            if (EulerMixture.ToPrimitive(
                    mass, totalMoles, heatCapacity,
                    momentumX, momentumY, momentumZ,
                    energy, config.VoxelVolume, config.PressurePerMoleKelvin,
                    out write.Cells[voxelIndex]))
            {
                signalSpeed = write.Cells[voxelIndex].SignalSpeed;
            }
            else
            {
                momentumX = 0f;
                momentumY = 0f;
                momentumZ = 0f;
            }
        }

        write.MomentumX[voxelIndex] = momentumX;
        write.MomentumY[voxelIndex] = momentumY;
        write.MomentumZ[voxelIndex] = momentumZ;
        return signalSpeed;
    }

    private static KilogramMetrePerSecond ReadMomentum(EulerChunkState buffer, int axis, int voxelIndex)
    {
        return axis switch
        {
            0 => buffer.MomentumX[voxelIndex],
            1 => buffer.MomentumY[voxelIndex],
            _ => buffer.MomentumZ[voxelIndex]
        };
    }

    private static void StoreMomentum(
        int normalAxis, KilogramMetrePerSecond normal,
        int tangential1Axis, KilogramMetrePerSecond tangential1,
        int tangential2Axis, KilogramMetrePerSecond tangential2,
        out KilogramMetrePerSecond momentumX, out KilogramMetrePerSecond momentumY, out KilogramMetrePerSecond momentumZ)
    {
        momentumX = 0f;
        momentumY = 0f;
        momentumZ = 0f;
        Assign(normalAxis, normal, ref momentumX, ref momentumY, ref momentumZ);
        Assign(tangential1Axis, tangential1, ref momentumX, ref momentumY, ref momentumZ);
        Assign(tangential2Axis, tangential2, ref momentumX, ref momentumY, ref momentumZ);
    }

    private static void Assign(
        int axis, KilogramMetrePerSecond value,
        ref KilogramMetrePerSecond momentumX, ref KilogramMetrePerSecond momentumY, ref KilogramMetrePerSecond momentumZ)
    {
        switch (axis)
        {
            case 0:
                momentumX = value;
                break;
            case 1:
                momentumY = value;
                break;
            default:
                momentumZ = value;
                break;
        }
    }
}
