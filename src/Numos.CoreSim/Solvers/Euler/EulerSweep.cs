using System.Buffers;
using System.Diagnostics;
using Numos.CoreSim.EulerFlow;

namespace Numos.CoreSim.Solvers.Euler;

/// <summary>
///     Extensive flux through one voxel face for one substep: per-second rates, positive from the lower-coordinate
///     cell to the higher one, already multiplied by the face area.
/// </summary>
/// <remarks>
///     Species are not stored per face. The face keeps the upwind cell and <c>A·Mass/m_upwind</c>, and the cell
///     update multiplies that by each species' moles in the upwind cell of the <em>read</em> buffer. Every species
///     therefore moves in the proportion it has upwind, and <c>Σ_k M_k·G_k = A·G_mass</c> holds to rounding, which
///     is the Larrouturou-consistent transport that keeps mass and moles from drifting apart. The upwind cell of a
///     chunk-boundary face can belong to the neighbouring chunk, so the face names the buffer as well as the index.
/// </remarks>
internal struct EulerFaceRate
{
    public KilogramPerSecond Mass;
    public PerSecond SpeciesRate;

    /// <summary>
    ///     Read buffer holding the upwind cell: this chunk's, or the neighbour's across a chunk face.
    /// </summary>
    public EulerChunkState UpwindState;

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
    public int NonFiniteCells;
}

/// <summary>
///     One dimensionally split first-order Godunov sweep along a grid axis for one chunk, reading the chunk's
///     read buffer (and its active neighbours' read buffers at chunk faces) and writing every active cell of its
///     write buffer.
/// </summary>
/// <remarks>
///     <para>
///         Work is cut into pencil bundles. An X sweep marches one row <c>(y, z)</c> along x; a Y sweep marches a
///         whole z-slab along y, handling the <c>W</c> faces of one x-row at a time; a Z sweep marches a y-sheet
///         along z the same way. The bundle keeps a rolling pair of face-rate rows (the faces behind the row of
///         cells being updated and the faces ahead of it), so each face is solved exactly once per chunk and each
///         cell sees both of its faces before it is written.
///     </para>
///     <para>
///         A face shared with an active neighbouring chunk is solved by both chunks, redundantly, from identical
///         inputs: each side projects the same two <see cref="EulerCellState" />s (its own from its read buffer,
///         the other from the neighbour's read buffer, never recomputed from the neighbour's channels) and calls
///         <see cref="HllcFlux.Solve" /> with the lower-coordinate cell on the left, so the two evaluations are the
///         same bits and each side applies its own <c>F_right − F_left</c>. That is what makes a two-chunk run
///         bitwise equal to the same tube in one chunk without any reserve-and-merge pass. A face into a chunk
///         that is not active this sweep is a wall, and the sweep raises an activation request for it; a wall is
///         exact while the neighbour is at rest at the same pressure, because the wall flux is then <c>p</c>, which
///         is also the interior flux of a gas at rest, and the mass flux is zero either way.
///     </para>
///     <para>
///         Determinism rests on three things. A cell's update is a two-term difference of the rates of its own two
///         faces, each rate computed from read buffers no sweep writes, so the order pencils run in, or which
///         worker runs them, cannot change a result. Mixture sums run in the workspace's ascending gas list. Nothing
///         uses <see cref="MathF.FusedMultiplyAdd" /> or any call whose rounding is platform dependent beyond what
///         <see cref="HllcFlux" /> already documents. Each pencil writes its maximum signal speed and repair counts
///         into its own slot, and the tick thread reduces them with max and sum, which are order free.
///     </para>
/// </remarks>
internal static class EulerSweep
{
    /// <summary>
    ///     Number of pencils an axis sweep of a chunk is made of: rows for X, slabs for Y, sheets for Z.
    /// </summary>
    internal static int PencilCount(AtmosChunk chunk, int axis)
    {
        return axis switch
        {
            0 => chunk.Height * chunk.Depth,
            1 => chunk.Depth,
            _ => chunk.Height
        };
    }

    /// <summary>
    ///     Sweeps a contiguous range of one chunk's pencils, in ascending pencil order, writing each pencil's
    ///     signal speed and repair counts into the workspace's per-pencil slots.
    /// </summary>
    /// <param name="workspace">The chunk's bound and gathered workspace.</param>
    /// <param name="axis">0 for X, 1 for Y, 2 for Z.</param>
    /// <param name="firstPencil">The first pencil of the range.</param>
    /// <param name="pencilCount">How many pencils to sweep.</param>
    /// <param name="timeStep">The substep length.</param>
    /// <param name="faceArea">Area of one voxel face, <c>Δx²</c>.</param>
    /// <param name="config">The tick's configuration snapshot.</param>
    /// <remarks>
    ///     This is the unit both parallel schedules call: a whole chunk is the range <c>[0, PencilCount)</c> and a
    ///     tile is a sub-range of it. No pencil writes another pencil's cells or slots, so splitting a chunk's
    ///     range differently cannot change any bit of the result.
    /// </remarks>
    internal static void RunPencils(
        EulerChunkWorkspace workspace, int axis, int firstPencil, int pencilCount,
        Second timeStep, SquareMetre faceArea, AtmosSolverConfigSnapshot config)
    {
        int rowLength = axis == 0 ? 1 : workspace.Chunk.Width;
        EulerFaceRate[] behind = ArrayPool<EulerFaceRate>.Shared.Rent(rowLength);
        EulerFaceRate[] ahead = ArrayPool<EulerFaceRate>.Shared.Rent(rowLength);
        try
        {
            for (int pencil = firstPencil; pencil < firstPencil + pencilCount; pencil++)
            {
                workspace.PencilCounters[pencil] = default;
                workspace.PencilSignalSpeed[pencil] = SweepBundle(
                    workspace, axis, pencil, timeStep, faceArea, config,
                    behind, ahead, ref workspace.PencilCounters[pencil]);
            }
        }
        finally
        {
            ArrayPool<EulerFaceRate>.Shared.Return(behind);
            ArrayPool<EulerFaceRate>.Shared.Return(ahead);
        }
    }

    /// <summary>
    ///     Sweeps one pencil bundle and returns the fastest signal speed among the cells it wrote. The rolling
    ///     face-rate rows are the caller's, rented once per pencil range.
    /// </summary>
    /// <remarks>
    ///     The march index runs one past the last cell so the trailing wall faces are solved by the same loop as
    ///     the interior ones: at march position <c>j</c> the loop solves the faces between rows <c>j − 1</c> and
    ///     <c>j</c>, then updates row <c>j − 1</c>, whose leading faces were solved on the previous step.
    /// </remarks>
    private static MetrePerSecond SweepBundle(
        EulerChunkWorkspace workspace, int axis, int bundle, Second timeStep, SquareMetre faceArea,
        AtmosSolverConfigSnapshot config, EulerFaceRate[] behind, EulerFaceRate[] ahead,
        ref EulerSweepCounters counters)
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

        MetrePerSecond maxSignalSpeed = 0f;
        EulerFlowConfig settings = config.EulerFlow;
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
                    faceArea, settings, out ahead[offset]);
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

        return maxSignalSpeed;
    }

    /// <summary>
    ///     Classifies and solves the face between a lower-coordinate cell and the next cell along the axis.
    /// </summary>
    /// <param name="workspace">The chunk's workspace, for the face masks and neighbour slots.</param>
    /// <param name="read">The buffer the sweep reads cell states from.</param>
    /// <param name="axis">The sweep axis, which is the face normal.</param>
    /// <param name="lowerIndex">The lower cell, or -1 outside the chunk.</param>
    /// <param name="upperIndex">The upper cell, or -1 outside the chunk.</param>
    /// <param name="faceArea">Area of one voxel face.</param>
    /// <param name="settings">The stage's settings.</param>
    /// <param name="rate">The extensive flux through the face.</param>
    /// <remarks>
    ///     Two air cells of the same chunk always share an open face today, because
    ///     <see cref="EulerFaceTopology.Build" /> sets the open bit for every air neighbour; the assertion is where a
    ///     blocked air-to-air face would need a second rate (a wall pressure per side) instead of one shared flux.
    ///     Everything else is a one-sided face of the air cell, which <see cref="SolveOneSided" /> classifies.
    /// </remarks>
    private static void SolveFace(
        EulerChunkWorkspace workspace, EulerChunkState read, int axis,
        int lowerIndex, int upperIndex, SquareMetre faceArea, EulerFlowConfig settings,
        out EulerFaceRate rate)
    {
        AtmosChunk chunk = workspace.Chunk;
        bool lowerAir = lowerIndex >= 0 && EulerFaceTopology.IsAir(chunk, lowerIndex);
        bool upperAir = upperIndex >= 0 && EulerFaceTopology.IsAir(chunk, upperIndex);
        if (lowerAir && upperAir)
        {
            Debug.Assert(
                EulerFaceTopology.IsOpen(workspace.OpenMask[lowerIndex], 2 * axis + 1) &&
                EulerFaceTopology.IsOpen(workspace.OpenMask[upperIndex], 2 * axis),
                "Two air voxels always share an open face until blocked faces exist.");

            EulerFaceState left = EulerFaceState.FromCell(in read.Cells[lowerIndex], axis);
            EulerFaceState right = EulerFaceState.FromCell(in read.Cells[upperIndex], axis);
            HllcFlux.Solve(in left, in right, settings.AllMachCorrection, out EulerFaceFlux flux);
            Store(in flux, read, flux.UpwindIsLeft ? lowerIndex : upperIndex, faceArea, out rate);
        }
        else if (lowerAir)
        {
            SolveOneSided(workspace, read, axis, lowerIndex, 2 * axis + 1, faceArea, settings, out rate);
        }
        else if (upperAir)
        {
            SolveOneSided(workspace, read, axis, upperIndex, 2 * axis, faceArea, settings, out rate);
        }
        else
        {
            rate = default;
        }
    }

    /// <summary>
    ///     Solves the face of an air cell whose other side is not an air cell of this chunk: an air cell of the
    ///     neighbouring chunk, a void, or a wall.
    /// </summary>
    /// <param name="workspace">The chunk's workspace.</param>
    /// <param name="read">This chunk's read buffer.</param>
    /// <param name="axis">The sweep axis.</param>
    /// <param name="cellIndex">The air cell.</param>
    /// <param name="direction">The direction slot of the face, seen from the cell.</param>
    /// <param name="faceArea">Area of one voxel face.</param>
    /// <param name="settings">The stage's settings.</param>
    /// <param name="rate">The extensive flux through the face.</param>
    /// <remarks>
    ///     <para>
    ///         An open bit on a face that reaches here means the face leaves the chunk into an air voxel of a
    ///         registered neighbour. If that neighbour has a workspace this sweep, the face is the shared-face
    ///         Riemann problem described on the class, solved with the lower global coordinate on the left
    ///         regardless of which chunk this is. If it has none, the face is a wall for this sweep and the sweep
    ///         asks for the neighbour to be activated when the two sides differ in pressure by more than
    ///         <see cref="EulerFlowConfig.WakePressureDelta" /> or this cell moves normal to the face at all,
    ///         either way; a wall is only exact for gas at rest. The neighbour's pressure is its live
    ///         <see cref="AtmosChunk.TotalPressure" />, which nothing writes while the chunk is inactive, and its
    ///         momentum is zero by invariant.
    ///     </para>
    ///     <para>
    ///         Gas that crosses into a void cell is gone: the void cell is never updated. A trace cell presents as
    ///         the vacuum state, so the Riemann problem against it is the expansion into vacuum and the trace cell
    ///         can only receive.
    ///     </para>
    /// </remarks>
    private static void SolveOneSided(
        EulerChunkWorkspace workspace, EulerChunkState read, int axis,
        int cellIndex, int direction, SquareMetre faceArea, EulerFlowConfig settings,
        out EulerFaceRate rate)
    {
        bool cellIsLeft = (direction & 1) == 1;
        EulerFaceState cell = EulerFaceState.FromCell(in read.Cells[cellIndex], axis);
        EulerFaceFlux flux;
        if (EulerFaceTopology.IsOpen(workspace.OpenMask[cellIndex], direction))
        {
            ushort neighborIndex = EulerFaceTopology.NeighborIndexAcrossFace(workspace.Chunk, (ushort)cellIndex, direction);
            EulerChunkWorkspace? neighbor = workspace.NeighborWorkspaces[direction];
            if (neighbor != null)
            {
                EulerChunkState neighborRead = neighbor.Read;
                EulerFaceState other = EulerFaceState.FromCell(in neighborRead.Cells[neighborIndex], axis);
                if (cellIsLeft)
                    HllcFlux.Solve(in cell, in other, settings.AllMachCorrection, out flux);
                else
                    HllcFlux.Solve(in other, in cell, settings.AllMachCorrection, out flux);

                if (flux.UpwindIsLeft == cellIsLeft)
                    Store(in flux, read, cellIndex, faceArea, out rate);
                else
                    Store(in flux, neighborRead, neighborIndex, faceArea, out rate);

                return;
            }

            AtmosChunk neighborChunk = workspace.NeighborChunks[direction]!;
            Pascal neighborPressure = neighborChunk.TotalPressure[neighborIndex];
            if (cell.NormalVelocity != 0f || MathF.Abs(cell.Pressure - neighborPressure) > settings.WakePressureDelta)
                workspace.WakeRequests[direction] = true;

            HllcFlux.SolveWall(in cell, cellIsLeft, out flux);
        }
        else if (EulerFaceTopology.IsVoid(workspace.VoidMask[cellIndex], direction))
        {
            if (cellIsLeft)
                HllcFlux.Solve(in cell, default, settings.AllMachCorrection, out flux);
            else
                HllcFlux.Solve(default, in cell, settings.AllMachCorrection, out flux);
        }
        else
        {
            HllcFlux.SolveWall(in cell, cellIsLeft, out flux);
        }

        Store(in flux, read, cellIndex, faceArea, out rate);
    }

    private static void Store(
        in EulerFaceFlux flux, EulerChunkState upwindState, int upwindIndex, SquareMetre faceArea,
        out EulerFaceRate rate)
    {
        rate.Mass = flux.Mass * faceArea;
        rate.SpeciesRate = flux.SpeciesWeight * faceArea;
        rate.UpwindState = upwindState;
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
    ///         rebuilt; both repairs are counted, as is a cell whose primitive state does not fit in float, which
    ///         keeps its conserved variables but is written as a vacuum with no momentum. A species update that is
    ///         not finite is written as zero and counts the cell as non-finite too, so a NaN never reaches a gas
    ///         channel, where every later comparison would let it through.
    ///     </para>
    /// </remarks>
    private static MetrePerSecond UpdateCell(
        EulerChunkWorkspace workspace, int axis, int voxelIndex,
        in EulerFaceRate behind, in EulerFaceRate ahead,
        Second timeStep, AtmosSolverConfigSnapshot config, ref EulerSweepCounters counters)
    {
        EulerChunkState read = workspace.Read;
        EulerChunkState write = workspace.Write;

        // Every gas an upwind neighbour cell can hold is in this list (the tick thread merged the axis
        // neighbours' lists before the sweep), so a null upwind row is a gas that side does not have.
        bool nonFiniteSpecies = false;
        for (int present = 0; present < workspace.GasCount; present++)
        {
            int gasId = workspace.GasIds[present];
            Mole[] source = read.Species[gasId]!;
            Mole[] target = write.Species[gasId]!;
            Mole moles = source[voxelIndex] -
                         timeStep *
                         (ahead.SpeciesRate * UpwindMoles(in ahead, gasId) -
                          behind.SpeciesRate * UpwindMoles(in behind, gasId));

            if (!float.IsFinite(moles))
            {
                // NaN fails the floor's comparison, so it has to be caught here or it lands in the channels.
                nonFiniteSpecies = true;
                moles = 0f;
            }
            else if (moles < 0f)
            {
                counters.NegativeMoles++;
                moles = 0f;
            }

            target[voxelIndex] = moles;
        }

        if (nonFiniteSpecies)
            counters.NonFiniteCells++;

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
            out Kilogram mass, out Mole totalMoles, out JoulePerKelvin64 heatCapacity);

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

            EulerCellKind kind = EulerMixture.ToPrimitive(
                mass, totalMoles, heatCapacity,
                momentumX, momentumY, momentumZ,
                energy, config.VoxelVolume, config.PressurePerMoleKelvin,
                out write.Cells[voxelIndex]);

            if (kind == EulerCellKind.Gas)
            {
                signalSpeed = write.Cells[voxelIndex].SignalSpeed;
            }
            else
            {
                if (kind == EulerCellKind.NonFinite && !nonFiniteSpecies)
                    counters.NonFiniteCells++;

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

    /// <summary>
    ///     Moles of one gas in a face's upwind cell; zero for a gas the upwind buffer has no row for, which is the
    ///     exact value a zero row would give.
    /// </summary>
    /// <remarks>
    ///     The id comes from this chunk's list and the buffer can be a neighbour's, whose row table is only as
    ///     wide as the registry plus the ids it lists itself. An id past its end (a channel restored from a
    ///     checkpoint with an id the registry never saw, listed here via another neighbour) is a gas that
    ///     buffer holds none of, so it reads as a null row rather than as an index fault.
    /// </remarks>
    private static Mole UpwindMoles(in EulerFaceRate rate, int gasId)
    {
        Mole[]?[] table = rate.UpwindState.Species;
        Mole[]? row = gasId < table.Length ? table[gasId] : null;
        return row == null ? 0f : row[rate.UpwindIndex];
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
