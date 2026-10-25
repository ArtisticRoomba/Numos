using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using Numos.CoreSim.EulerFlow;

namespace Numos.CoreSim.Solvers.Euler;

/// <summary>
///     The read buffer that holds the upwind cell of a face.
/// </summary>
internal enum EulerUpwindSide : byte
{
    /// <summary>
    ///     This chunk's read buffer.
    /// </summary>
    Self = 0,

    /// <summary>
    ///     The read buffer of the neighbour across the lower face of the sweep axis.
    /// </summary>
    LowerNeighbor = 1,

    /// <summary>
    ///     The read buffer of the neighbour across the upper face of the sweep axis.
    /// </summary>
    UpperNeighbor = 2
}

/// <summary>
///     Extensive flux through one voxel face for one substep: per-second rates, positive from the lower-coordinate
///     cell to the higher one, already multiplied by the face area.
/// </summary>
/// <remarks>
///     Species are not stored per face. The face keeps the upwind cell and <c>A·Mass/m_upwind</c>, and the cell
///     update multiplies that by each species' moles in the upwind cell of the <em>read</em> buffer. Every species
///     therefore moves in the proportion it has upwind, and <c>Σ_k M_k·G_k = A·G_mass</c> holds to rounding, which
///     is the Larrouturou-consistent transport that keeps mass and moles from drifting apart. The upwind cell of a
///     chunk-boundary face can belong to the neighbouring chunk, so the face names the side as well as the index.
///     The struct holds no references, so a scratch array of faces costs the garbage collector nothing to scan.
/// </remarks>
internal struct EulerFaceRate
{
    public KilogramPerSecond Mass;
    public PerSecond SpeciesRate;

    /// <summary>
    ///     Which read buffer holds the upwind cell.
    /// </summary>
    public EulerUpwindSide UpwindSide;

    /// <summary>
    ///     Voxel index of the upwind cell in the buffer <see cref="UpwindSide" /> names.
    /// </summary>
    public int UpwindIndex;
    public Newton NormalMomentum;
    public Newton Tangential1Momentum;
    public Newton Tangential2Momentum;
    public Watt Energy;
}

/// <summary>
///     Counts the repairs a sweep had to make, surfaced through <see cref="EulerFlow.AtmosFlowDiagnostics" />.
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
///         Work is cut into pencil bundles. A bundle is a set of parallel lines that each march along the sweep
///         axis: an X sweep takes one z-slab (the <c>H</c> rows of one <c>z</c>), a Y sweep takes one z-slab (the
///         <c>W</c> columns of one <c>z</c>), and a Z sweep takes one y-sheet (the <c>W</c> columns of one
///         <c>y</c>). Bundle data is stored march-major, so the cells and faces at one march position are
///         contiguous. A bundle runs in three passes. The first solves every face of the bundle once into
///         a scratch array. Interior faces of rows that hold only air go through <see cref="HllcFluxVector" />, which
///         gives the same bits as <see cref="HllcFlux.Solve" />. The second updates the species of every air cell, one
///         gas at a time, and sums the mixture as it goes. The third updates momentum and energy per cell and rebuilds its primitive state. Rows that hold only air use
///         <see cref="EulerCellKernel" /> for this, with the same bits as the scalar update.
///         Going gas by gas keeps one source row, one target row, and the two neighbour rows in hand for a whole
///         bundle instead of looking them up again per cell.
///     </para>
///     <para>
///         A face shared with an active neighbouring chunk is solved by both chunks from identical inputs: each
///         side projects the same two <see cref="EulerCellState" />s (its own from its read buffer, the other from
///         the neighbour's read buffer, never recomputed from the neighbour's channels) and calls
///         <see cref="HllcFlux.Solve" /> with the lower-coordinate cell on the left. The two evaluations are the
///         same bits and each side applies its own <c>F_right − F_left</c>, which is what makes a two-chunk run
///         bitwise equal to the same tube in one chunk. A face into a chunk that is not active this sweep is a
///         wall, and the sweep raises an activation request for it. A wall is exact while the neighbour is at
///         rest at the same pressure: the wall flux is then <c>p</c>, the same as the interior flux of a gas at
///         rest, and the mass flux is zero either way.
///     </para>
///     <para>
///         Determinism rests on three things. A cell's update is a two-term difference of the rates of its own two
///         faces, each rate computed from read buffers no sweep writes, so the order pencils run in, or which worker
///         runs them, cannot change a result. Mixture sums run in the workspace's ascending gas list, because the
///         second pass visits the gases in that order. Nothing uses <see cref="MathF.FusedMultiplyAdd" /> or any call
///         whose rounding is platform dependent beyond what <see cref="HllcFlux" /> already documents. Each pencil
///         writes its maximum signal speed and repair counts into its own slot, and the tick thread reduces them with
///         max and sum, which are order free.
///     </para>
/// </remarks>
internal static class EulerSweep
{
    /// <summary>
    ///     Number of pencil bundles an axis sweep of a chunk is made of: z-slabs for X and Y, y-sheets for Z.
    /// </summary>
    internal static int PencilCount(AtmosChunk chunk, int axis)
    {
        return axis switch
        {
            0 or 1 => chunk.Depth,
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
    ///     Both parallel schedules call this: a whole chunk is the range <c>[0, PencilCount)</c> and a tile is a
    ///     sub-range. No pencil writes another pencil's cells or slots, so how a chunk's range is split cannot
    ///     change any bit.
    /// </remarks>
    internal static void RunPencils(
        EulerChunkWorkspace workspace, int axis, int firstPencil, int pencilCount,
        Second timeStep, SquareMetre faceArea, AtmosSolverConfigSnapshot config)
    {
        BundleScratch scratch = t_scratch ??= new BundleScratch();
        for (int pencil = firstPencil; pencil < firstPencil + pencilCount; pencil++)
        {
            workspace.PencilCounters[pencil] = default;
            workspace.PencilSignalSpeed[pencil] = SweepBundle(
                workspace, axis, pencil, timeStep, faceArea, config,
                scratch, ref workspace.PencilCounters[pencil]);
        }
    }

    /// <summary>
    ///     Sweeps one pencil bundle and returns the fastest signal speed among the cells it wrote.
    /// </summary>
    /// <remarks>
    ///     Face <c>(march, line)</c> lies between positions <c>march − 1</c> and <c>march</c> of its line, so the
    ///     bundle has <c>marchCount + 1</c> rows of faces, and the trailing wall faces are solved by the same loop
    ///     as the interior ones. Cell <c>(march, line)</c> has the faces at <c>march</c> and <c>march + 1</c>. The
    ///     scratch arrays are indexed by position in the bundle, not by voxel index. A Y or Z bundle is
    ///     <c>march · lineCount + line</c>. An X bundle is line-major, with <c>line · marchCount + march</c> for
    ///     cells and <c>line · (marchCount + 1) + march</c> for faces. Only the first and last face of a line can
    ///     name a neighbour chunk. The other faces name one of their own two cells.
    /// </remarks>
    private static MetrePerSecond SweepBundle(
        EulerChunkWorkspace workspace, int axis, int bundle, Second timeStep, SquareMetre faceArea,
        AtmosSolverConfigSnapshot config, BundleScratch scratch, ref EulerSweepCounters counters)
    {
        AtmosChunk chunk = workspace.Chunk;
        int width = chunk.Width;
        int height = chunk.Height;
        int baseIndex;
        int marchStride;
        int marchCount;
        int lineStride;
        int lineCount;
        switch (axis)
        {
            case 0:
                baseIndex = bundle * width * height;
                marchStride = 1;
                marchCount = width;
                lineStride = width;
                lineCount = height;
                break;
            case 1:
                baseIndex = bundle * width * height;
                marchStride = width;
                marchCount = height;
                lineStride = 1;
                lineCount = width;
                break;
            default:
                baseIndex = bundle * width;
                marchStride = width * height;
                marchCount = chunk.Depth;
                lineStride = 1;
                lineCount = width;
                break;
        }

        // A run is a stretch of cells that is contiguous in voxel memory: a line for X, a march row for Y and Z.
        // The scratch is laid out run by run, so a run is contiguous there too.
        bool alongMarch = axis == 0;
        int runLength = alongMarch ? marchCount : lineCount;
        int runCount = alongMarch ? lineCount : marchCount;
        int runVoxelStride = alongMarch ? lineStride : marchStride;
        int runFaceStride = alongMarch ? marchCount + 1 : lineCount;
        int aheadOffset = alongMarch ? 1 : lineCount;

        int cellCount = marchCount * lineCount;
        int faceCount = (marchCount + 1) * lineCount;
        scratch.EnsureCapacity(faceCount, cellCount, runCount);
        PerSecond[] faceRate = scratch.FaceSpeciesRate;
        MolePerSecond[] faceFlux = scratch.FaceFlux;
        int[] upperMask = scratch.FaceUpperMask;
        int[] fixupFaces = scratch.FixupFaces;
        EulerFaceRate[] faces = scratch.Faces;
        Newton[] faceNormal = scratch.FaceNormal;
        Newton[] faceTangential1 = scratch.FaceTangential1;
        Newton[] faceTangential2 = scratch.FaceTangential2;
        Watt[] faceEnergy = scratch.FaceEnergy;
        bool[] airMap = workspace.AirMap;
        bool[] rowAllAir = workspace.RowAllAir;

        EulerFlowConfig settings = config.EulerFlow;
        EulerChunkState read = workspace.Read;

        int innerRunCount = alongMarch ? lineCount : marchCount - 1;
        int innerRunLength = alongMarch ? marchCount - 1 : lineCount;

        // Interior faces of an all-air run go through the vector kernel. Every other face is solved one at a time.
        bool vectorFaces = HllcFluxVector.Enabled && !settings.AllMachCorrection && innerRunLength > 0;
        bool[] vectorRun = scratch.VectorRun;
        if (vectorFaces)
        {
            scratch.EnsureStage(innerRunLength + 1);
            for (int run = 0; run < innerRunCount; run++)
            {
                int firstLower = baseIndex + run * runVoxelStride;
                bool allAir = rowAllAir[firstLower / width] &&
                              (alongMarch || rowAllAir[(firstLower + marchStride) / width]);
                vectorRun[run] = allAir;
                if (allAir)
                {
                    int firstFace = alongMarch ? run * runFaceStride + 1 : (run + 1) * lineCount;
                    SolveRun(
                        scratch, read.Cells, axis, alongMarch, firstLower, read.Cells, firstLower + marchStride,
                        innerRunLength, firstFace, faceArea);
                }
            }
        }

        // The faces on the chunk boundary of a Y or Z bundle are one contiguous row each, so a row whose cells and
        // neighbour cells are all open air goes through the vector kernel too.
        bool lowerEdgeDone = vectorFaces && !alongMarch &&
                             SolveEdgeRow(workspace, read, scratch, axis, baseIndex, lineCount, 0, faceArea);
        bool upperEdgeDone = vectorFaces && !alongMarch &&
                             SolveEdgeRow(
                                 workspace, read, scratch, axis, baseIndex + (marchCount - 1) * marchStride, lineCount,
                                 marchCount, faceArea);

        for (int march = 0; march <= marchCount; march++)
        {
            if ((march == 0 && lowerEdgeDone) || (march == marchCount && upperEdgeDone))
                continue;

            int lowerRow = march > 0 ? baseIndex + (march - 1) * marchStride : -1;
            int upperRow = march < marchCount ? baseIndex + march * marchStride : -1;
            bool innerRow = vectorFaces && march >= 1 && march < marchCount;
            for (int line = 0; line < lineCount; line++)
            {
                if (innerRow && vectorRun[alongMarch ? line : march - 1])
                    continue;

                int face = alongMarch ? line * runFaceStride + march : march * lineCount + line;
                SolveFace(
                    workspace, read, axis,
                    lowerRow < 0 ? -1 : lowerRow + line * lineStride,
                    upperRow < 0 ? -1 : upperRow + line * lineStride,
                    faceArea, settings, out faces[face]);

                ref readonly EulerFaceRate solved = ref faces[face];
                faceRate[face] = solved.SpeciesRate;
                faceNormal[face] = solved.NormalMomentum;
                faceTangential1[face] = solved.Tangential1Momentum;
                faceTangential2[face] = solved.Tangential2Momentum;
                faceEnergy[face] = solved.Energy;
            }
        }

        // The faces that can name a neighbour chunk are the first and last face of every line. They go to the
        // scalar fix-up list, as does any interior face whose upwind cell is not one of its own two cells.
        // Every other face gets a mask that picks its lower or upper cell.
        int fixupCount = 0;
        for (int line = 0; line < lineCount; line++)
        {
            fixupFaces[fixupCount++] = alongMarch ? line * runFaceStride : line;
            fixupFaces[fixupCount++] = alongMarch ? line * runFaceStride + marchCount : marchCount * lineCount + line;
        }

        for (int run = 0; run < innerRunCount; run++)
        {
            if (vectorFaces && vectorRun[run])
                continue;

            int firstFace = alongMarch ? run * runFaceStride + 1 : (run + 1) * lineCount;
            int firstLower = baseIndex + run * runVoxelStride;
            for (int i = 0; i < innerRunLength; i++)
            {
                ref readonly EulerFaceRate rate = ref faces[firstFace + i];
                int lower = firstLower + i;
                bool own = rate.UpwindSide == EulerUpwindSide.Self;
                if (own && rate.UpwindIndex == lower)
                {
                    upperMask[firstFace + i] = 0;
                }
                else if (own && rate.UpwindIndex == lower + marchStride)
                {
                    upperMask[firstFace + i] = -1;
                }
                else
                {
                    upperMask[firstFace + i] = 0;
                    fixupFaces[fixupCount++] = firstFace + i;
                }
            }
        }

        Array.Clear(scratch.NonFiniteSpecies, 0, cellCount);
        Array.Clear(scratch.Mass, 0, cellCount);
        Array.Clear(scratch.TotalMoles, 0, cellCount);
        Array.Clear(scratch.HeatCapacity, 0, cellCount);

        // Every gas an upwind neighbour cell can hold is in this list (the tick thread merged the axis
        // neighbours' lists before the sweep), so a null upwind row is a gas that side does not have.
        EulerChunkWorkspace? lowerNeighbor = workspace.NeighborWorkspaces[2 * axis];
        EulerChunkWorkspace? upperNeighbor = workspace.NeighborWorkspaces[2 * axis + 1];
        for (int present = 0; present < workspace.GasCount; present++)
        {
            int gasId = workspace.GasIds[present];
            Mole[] source = read.Species[gasId]!;
            Mole[]? lowerRows = NeighborRow(lowerNeighbor, gasId);
            Mole[]? upperRows = NeighborRow(upperNeighbor, gasId);
            var pass = new EulerSpeciesPass(
                source, workspace.Write.Species[gasId]!, scratch.FaceFlux, scratch.NonFiniteSpecies,
                scratch.Mass, scratch.TotalMoles, scratch.HeatCapacity,
                timeStep, workspace.MolarMass[present], workspace.HeatCapacity[present]);

            for (int run = 0; run < innerRunCount; run++)
            {
                int firstFace = alongMarch ? run * runFaceStride + 1 : (run + 1) * lineCount;
                EulerSpeciesKernel.FaceFluxRun(
                    faceRate, upperMask, source, faceFlux,
                    firstFace, baseIndex + run * runVoxelStride, marchStride, innerRunLength);
            }

            for (int k = 0; k < fixupCount; k++)
            {
                int face = fixupFaces[k];
                faceFlux[face] = faceRate[face] * UpwindMoles(in faces[face], source, lowerRows, upperRows);
            }

            for (int run = 0; run < runCount; run++)
            {
                int firstVoxel = baseIndex + run * runVoxelStride;
                int firstCell = run * runLength;
                int firstBehind = run * runFaceStride;
                int firstAhead = firstBehind + aheadOffset;
                if (rowAllAir[firstVoxel / width])
                {
                    EulerSpeciesKernel.UpdateRun(
                        in pass, firstVoxel, firstCell, firstBehind, firstAhead, runLength, ref counters.NegativeMoles);

                    continue;
                }

                for (int i = 0; i < runLength; i++)
                {
                    if (airMap[firstVoxel + i])
                    {
                        EulerSpeciesKernel.UpdateCell(
                            in pass, firstVoxel + i, firstCell + i, firstBehind + i, firstAhead + i,
                            ref counters.NegativeMoles);
                    }
                }
            }
        }

        var cellPass = new EulerCellPass(workspace, axis, timeStep, config, scratch);
        MetrePerSecond maxSignalSpeed = 0f;
        bool vectorCells = EulerCellKernel.Enabled && runLength >= EulerCellKernel.Width;
        for (int run = 0; run < runCount; run++)
        {
            int firstVoxel = baseIndex + run * runVoxelStride;
            int firstCell = run * runLength;
            int firstBehind = run * runFaceStride;
            int done = 0;
            if (vectorCells && rowAllAir[firstVoxel / width])
            {
                done = EulerCellKernel.UpdateRun(
                    in cellPass.Run, firstVoxel, firstCell, firstBehind, firstBehind + aheadOffset, runLength,
                    ref counters.ClampedVelocity, ref counters.NonFiniteCells, ref maxSignalSpeed);
            }

            for (int i = done; i < runLength; i++)
            {
                int cell = firstCell + i;
                if (!airMap[firstVoxel + i])
                    continue;

                MetrePerSecond signalSpeed = cellPass.UpdateCell(
                    firstVoxel + i, firstBehind + i, firstBehind + aheadOffset + i,
                    scratch.Mass[cell], scratch.TotalMoles[cell], scratch.HeatCapacity[cell],
                    scratch.NonFiniteSpecies[cell], ref counters);

                maxSignalSpeed = MathF.Max(maxSignalSpeed, signalSpeed);
            }
        }

        return maxSignalSpeed;
    }

    /// <summary>
    ///     The row of one gas in a neighbour's read buffer, or <see langword="null" /> if the neighbour is not
    ///     active or has no row for the gas.
    /// </summary>
    /// <remarks>
    ///     The id comes from this chunk's list and the buffer is a neighbour's, whose row table is only as wide as
    ///     the registry plus the ids it lists itself. An id past its end (a channel restored from a checkpoint with
    ///     an id the registry never saw, listed here via another neighbour) is a gas that buffer holds none of, so
    ///     it reads as a null row rather than as an index fault. A null row reads as zero, the exact value a zero
    ///     row would give.
    /// </remarks>
    private static Mole[]? NeighborRow(EulerChunkWorkspace? neighbor, int gasId)
    {
        if (neighbor == null)
            return null;

        Mole[]?[] table = neighbor.Read.Species;
        return gasId < table.Length ? table[gasId] : null;
    }

    /// <summary>
    ///     Moles of one gas in a face's upwind cell, from the row of the buffer the face names.
    /// </summary>
    private static Mole UpwindMoles(
        in EulerFaceRate rate, Mole[] selfRow, Mole[]? lowerRow, Mole[]? upperRow)
    {
        Mole[]? row = rate.UpwindSide switch
        {
            EulerUpwindSide.Self => selfRow,
            EulerUpwindSide.LowerNeighbor => lowerRow,
            _ => upperRow
        };

        return row == null ? 0f : row[rate.UpwindIndex];
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
    ///     <see cref="EulerFaceTopology.Build" /> opens every air-to-air face inside a chunk, so two air cells
    ///     share one flux. Supporting blocked faces would need a wall pressure per side instead, which is what the
    ///     assertion guards. Every other face is one-sided and goes to <see cref="SolveOneSided" />.
    /// </remarks>
    private static void SolveFace(
        EulerChunkWorkspace workspace, EulerChunkState read, int axis,
        int lowerIndex, int upperIndex, SquareMetre faceArea, EulerFlowConfig settings,
        out EulerFaceRate rate)
    {
        bool lowerAir = lowerIndex >= 0 && workspace.AirMap[lowerIndex];
        bool upperAir = upperIndex >= 0 && workspace.AirMap[upperIndex];
        if (lowerAir && upperAir)
        {
            Debug.Assert(
                EulerFaceTopology.IsOpen(workspace.OpenMask[lowerIndex], 2 * axis + 1) &&
                EulerFaceTopology.IsOpen(workspace.OpenMask[upperIndex], 2 * axis),
                "Two air voxels always share an open face until blocked faces exist.");

            EulerFaceState left = EulerFaceState.FromCell(in read.Cells[lowerIndex], axis);
            EulerFaceState right = EulerFaceState.FromCell(in read.Cells[upperIndex], axis);
            HllcFlux.Solve(in left, in right, settings.AllMachCorrection, out EulerFaceFlux flux);
            Store(in flux, EulerUpwindSide.Self, flux.UpwindIsLeft ? lowerIndex : upperIndex, faceArea, out rate);
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
    ///         Riemann problem described on the class, with the lower global coordinate on the left regardless of
    ///         which chunk this is. Otherwise the face is a wall for this sweep, and the neighbour is requested for
    ///         activation when the pressures differ by more than <see cref="EulerFlowConfig.WakePressureDelta" /> or
    ///         this cell moves normal to the face faster than <see cref="EulerFlowConfig.VelocityFloor" />. The
    ///         stage zeroes slower momentum at tick end, so that motion cannot carry gas into the neighbour. The
    ///         neighbour's pressure is its live <see cref="AtmosChunk.TotalPressure" />, which nothing writes while
    ///         the chunk has no workspace, and its momentum is zero by invariant.
    ///     </para>
    ///     <para>
    ///         Gas that crosses into a void cell is gone. The void cell is never updated. A trace cell presents as
    ///         the vacuum state, so the face is an expansion into vacuum and the trace cell can only receive.
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
                    Store(in flux, EulerUpwindSide.Self, cellIndex, faceArea, out rate);
                else
                    Store(in flux, (direction & 1) == 1 ? EulerUpwindSide.UpperNeighbor : EulerUpwindSide.LowerNeighbor,
                        neighborIndex, faceArea, out rate);

                return;
            }

            AtmosChunk neighborChunk = workspace.NeighborChunks[direction]!;
            Pascal neighborPressure = neighborChunk.TotalPressure[neighborIndex];
            if (MathF.Abs(cell.NormalVelocity) > settings.VelocityFloor ||
                MathF.Abs(cell.Pressure - neighborPressure) > settings.WakePressureDelta)
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

        Store(in flux, EulerUpwindSide.Self, cellIndex, faceArea, out rate);
    }

    /// <summary>
    ///     Solves the row of faces between a chunk-boundary row of a Y or Z bundle and the facing row of the active
    ///     neighbour chunk with <see cref="HllcFluxVector" />, if every face of the row is an open air-to-air face.
    /// </summary>
    /// <param name="workspace">The chunk's workspace.</param>
    /// <param name="read">This chunk's read buffer.</param>
    /// <param name="scratch">The worker's scratch.</param>
    /// <param name="axis">The sweep axis, 1 for Y or 2 for Z.</param>
    /// <param name="selfFirst">Voxel index of the first cell of this chunk's boundary row.</param>
    /// <param name="count">Number of faces in the row, which is the chunk width.</param>
    /// <param name="march">0 for the lower boundary of the bundle, or the march count for the upper one.</param>
    /// <param name="faceArea">Area of one voxel face.</param>
    /// <returns>
    ///     <see langword="true" /> if the row is solved. <see langword="false" /> leaves every face of the row to
    ///     <see cref="SolveFace" />, which also raises the wake requests of faces into an inactive neighbour.
    /// </returns>
    /// <remarks>
    ///     The row takes the path of <see cref="SolveOneSided" /> for an active neighbour: the lower-coordinate cell is
    ///     on the left, the neighbour's cell comes from its read buffer, and an upwind cell on the neighbour's side
    ///     is named by side and index. The neighbour has a workspace, so no wake request can come from this row.
    /// </remarks>
    private static bool SolveEdgeRow(
        EulerChunkWorkspace workspace, EulerChunkState read, BundleScratch scratch, int axis, int selfFirst,
        int count, int march, SquareMetre faceArea)
    {
        AtmosChunk chunk = workspace.Chunk;
        bool lowerBoundary = march == 0;
        int direction = lowerBoundary ? 2 * axis : 2 * axis + 1;
        EulerChunkWorkspace? neighbor = workspace.NeighborWorkspaces[direction];
        if (neighbor == null)
            return false;

        int offset = axis == 1 ? (chunk.Height - 1) * chunk.Width : (chunk.Depth - 1) * chunk.Width * chunk.Height;
        int neighborFirst = lowerBoundary ? selfFirst + offset : selfFirst - offset;
        int width = chunk.Width;
        if (!workspace.RowAllAir[selfFirst / width] || !neighbor.RowAllAir[neighborFirst / width])
            return false;

        byte[] openMask = workspace.OpenMask;
        for (int i = 0; i < count; i++)
        {
            if (!EulerFaceTopology.IsOpen(openMask[selfFirst + i], direction))
                return false;
        }

        EulerChunkState neighborRead = neighbor.Read;
        int firstFace = march * count;
        if (lowerBoundary)
        {
            SolveRun(
                scratch, neighborRead.Cells, axis, false, neighborFirst, read.Cells, selfFirst, count, firstFace,
                faceArea);
        }
        else
        {
            SolveRun(
                scratch, read.Cells, axis, false, selfFirst, neighborRead.Cells, neighborFirst, count, firstFace,
                faceArea);
        }

        // The species pass finds the upwind cell of an edge face through its side and index.
        int[] upperMask = scratch.FaceUpperMask;
        EulerFaceRate[] faces = scratch.Faces;
        EulerUpwindSide neighborSide = lowerBoundary ? EulerUpwindSide.LowerNeighbor : EulerUpwindSide.UpperNeighbor;
        for (int i = 0; i < count; i++)
        {
            // The mask is set where the upwind cell is the higher-coordinate one. That is this chunk's cell on the
            // lower boundary and the neighbour's cell on the upper one.
            bool upwindIsSelf = (upperMask[firstFace + i] != 0) == lowerBoundary;
            ref EulerFaceRate face = ref faces[firstFace + i];
            face.UpwindSide = upwindIsSelf ? EulerUpwindSide.Self : neighborSide;
            face.UpwindIndex = upwindIsSelf ? selfFirst + i : neighborFirst + i;
        }

        return true;
    }

    /// <summary>
    ///     Solves a run of interior faces whose cells are all air with <see cref="HllcFluxVector" /> and stores the
    ///     rates exactly as <see cref="Store" /> does, into the bundle's per-field face arrays.
    /// </summary>
    /// <param name="scratch">The worker's scratch, holding the staging arrays and the face arrays.</param>
    /// <param name="cells">The read buffer's cells.</param>
    /// <param name="axis">The sweep axis.</param>
    /// <param name="alongMarch">Whether the lower and upper cells of the run are neighbours in one voxel row.</param>
    /// <param name="firstLower">Index of the lower cell of the first face in <paramref name="cells" />.</param>
    /// <param name="upperCells">
    ///     The array that holds the upper cells. Ignored when <paramref name="alongMarch" /> is set, because the
    ///     upper cell is then the next one in <paramref name="cells" />.
    /// </param>
    /// <param name="upperFirst">Index of the upper cell of the first face in <paramref name="upperCells" />.</param>
    /// <param name="count">Number of faces. Face <c>i</c> has its lower cell at <c>firstLower + i</c>.</param>
    /// <param name="firstFace">Index of the first face in the scratch face arrays.</param>
    /// <param name="faceArea">Area of one voxel face.</param>
    /// <remarks>
    ///     <para>
    ///         The cells are copied into field arrays first so that each vector load is contiguous. Lanes past
    ///         <paramref name="count" /> read zero padding, which is a vacuum on both sides.
    ///     </para>
    ///     <para>
    ///         The run writes no <see cref="EulerFaceRate" />. Its upwind cell is always the lower or the upper cell,
    ///         so the species pass needs only the upper-cell mask. A vector that covers lanes past
    ///         <paramref name="count" /> goes through a spill buffer, so no lane writes a face of another run.
    ///     </para>
    /// </remarks>
    private static void SolveRun(
        BundleScratch scratch, EulerCellState[] cells, int axis, bool alongMarch, int firstLower,
        EulerCellState[] upperCells, int upperFirst, int count, int firstFace, SquareMetre faceArea)
    {
        float[] stage = scratch.Stage;
        int stride = scratch.StageStride;
        int half = scratch.StageHalf;
        int rightOffset;
        if (alongMarch)
        {
            StageCells(cells, firstLower, count + 1, axis, stage, stride, 0, half, ref scratch.StageDirty[0]);
            rightOffset = 1;
        }
        else
        {
            StageCells(cells, firstLower, count, axis, stage, stride, 0, half, ref scratch.StageDirty[0]);
            StageCells(upperCells, upperFirst, count, axis, stage, stride, half, half, ref scratch.StageDirty[1]);
            rightOffset = half;
        }

        float[] speciesRate = scratch.FaceSpeciesRate;
        float[] normalRate = scratch.FaceNormal;
        float[] tangential1Rate = scratch.FaceTangential1;
        float[] tangential2Rate = scratch.FaceTangential2;
        float[] energyRate = scratch.FaceEnergy;
        int[] upperMask = scratch.FaceUpperMask;

        int width = HllcFluxVector.Width;
        Vector<float> area = new((float)faceArea);
        Span<float> spill = stackalloc float[width];
        Span<int> maskSpill = stackalloc int[width];
        for (int first = 0; first < count; first += width)
        {
            HllcLanes left = LoadLanes(stage, stride, first);
            HllcLanes right = LoadLanes(stage, stride, rightOffset + first);
            HllcLaneFlux flux = HllcFluxVector.Solve(in left, in right);
            Vector<float> species = flux.SpeciesWeight * area;
            Vector<float> normal = flux.NormalMomentum * area;
            Vector<float> tangential1 = flux.Tangential1Momentum * area;
            Vector<float> tangential2 = flux.Tangential2Momentum * area;
            Vector<float> energy = flux.Energy * area;
            Vector<int> upwindIsUpper = ~flux.UpwindIsLeft;

            int face = firstFace + first;
            if (first + width <= count)
            {
                species.CopyTo(speciesRate, face);
                normal.CopyTo(normalRate, face);
                tangential1.CopyTo(tangential1Rate, face);
                tangential2.CopyTo(tangential2Rate, face);
                energy.CopyTo(energyRate, face);
                upwindIsUpper.CopyTo(upperMask, face);
                continue;
            }

            int lanes = count - first;
            species.CopyTo(spill);
            spill[..lanes].CopyTo(speciesRate.AsSpan(face));
            normal.CopyTo(spill);
            spill[..lanes].CopyTo(normalRate.AsSpan(face));
            tangential1.CopyTo(spill);
            spill[..lanes].CopyTo(tangential1Rate.AsSpan(face));
            tangential2.CopyTo(spill);
            spill[..lanes].CopyTo(tangential2Rate.AsSpan(face));
            energy.CopyTo(spill);
            spill[..lanes].CopyTo(energyRate.AsSpan(face));
            upwindIsUpper.CopyTo(maskSpill);
            maskSpill[..lanes].CopyTo(upperMask.AsSpan(face));
        }
    }

    private static HllcLanes LoadLanes(float[] stage, int stride, int offset)
    {
        return new HllcLanes(
            new Vector<float>(stage, offset),
            new Vector<float>(stage, stride + offset),
            new Vector<float>(stage, 2 * stride + offset),
            new Vector<float>(stage, 3 * stride + offset),
            new Vector<float>(stage, 4 * stride + offset),
            new Vector<float>(stage, 5 * stride + offset),
            new Vector<float>(stage, 6 * stride + offset),
            new Vector<float>(stage, 7 * stride + offset));
    }

    /// <summary>
    ///     Copies the face-frame primitives of <paramref name="count" /> cells into the staging arrays, in the field
    ///     order of <see cref="HllcLanes" />. The part of the half that starts at <paramref name="dest" /> past
    ///     <paramref name="count" /> stays zero: <paramref name="dirty" /> remembers how far an earlier call wrote.
    /// </summary>
    /// <remarks>
    ///     The staging arrays are plain floats for the vector kernel, which carries no units, so the dimensional
    ///     analyzer cannot check these copies.
    /// </remarks>
#pragma warning disable QNT002
    private static void StageCells(
        EulerCellState[] cells, int first, int count, int axis, float[] stage, int stride, int dest, int half,
        ref int dirty)
    {
        // The checks here make the unchecked accesses below safe.
        if (count > half || first < 0 || first + count > cells.Length || dest + 7 * stride + half > stage.Length)
            throw new ArgumentOutOfRangeException(nameof(count));

        ref float density = ref stage[dest];
        ref float normal = ref stage[stride + dest];
        ref float tangential1 = ref stage[2 * stride + dest];
        ref float tangential2 = ref stage[3 * stride + dest];
        ref float pressure = ref stage[4 * stride + dest];
        ref float energy = ref stage[5 * stride + dest];
        ref float sound = ref stage[6 * stride + dest];
        ref float inverseMass = ref stage[7 * stride + dest];
        ref EulerCellState source = ref cells[first];
        for (int i = 0; i < count; i++)
        {
            ref readonly EulerCellState cell = ref Unsafe.Add(ref source, i);
            Unsafe.Add(ref density, i) = cell.Density;
            Unsafe.Add(ref pressure, i) = cell.Pressure;
            Unsafe.Add(ref energy, i) = cell.EnergyDensity;
            Unsafe.Add(ref sound, i) = cell.SoundSpeed;
            Unsafe.Add(ref inverseMass, i) = cell.InverseMass;
            switch (axis)
            {
                case 0:
                    Unsafe.Add(ref normal, i) = cell.VelocityX;
                    Unsafe.Add(ref tangential1, i) = cell.VelocityY;
                    Unsafe.Add(ref tangential2, i) = cell.VelocityZ;
                    break;
                case 1:
                    Unsafe.Add(ref normal, i) = cell.VelocityY;
                    Unsafe.Add(ref tangential1, i) = cell.VelocityZ;
                    Unsafe.Add(ref tangential2, i) = cell.VelocityX;
                    break;
                default:
                    Unsafe.Add(ref normal, i) = cell.VelocityZ;
                    Unsafe.Add(ref tangential1, i) = cell.VelocityX;
                    Unsafe.Add(ref tangential2, i) = cell.VelocityY;
                    break;
            }
        }

        if (dirty > count)
        {
            for (int field = 0; field < 8; field++)
                stage.AsSpan(field * stride + dest + count, dirty - count).Clear();
        }

        dirty = count;
    }
#pragma warning restore QNT002

    private static void Store(
        in EulerFaceFlux flux, EulerUpwindSide upwindSide, int upwindIndex, SquareMetre faceArea,
        out EulerFaceRate rate)
    {
        rate.Mass = flux.Mass * faceArea;
        rate.SpeciesRate = flux.SpeciesWeight * faceArea;
        rate.UpwindSide = upwindSide;
        rate.UpwindIndex = upwindIndex;
        rate.NormalMomentum = flux.NormalMomentum * faceArea;
        rate.Tangential1Momentum = flux.Tangential1Momentum * faceArea;
        rate.Tangential2Momentum = flux.Tangential2Momentum * faceArea;
        rate.Energy = flux.Energy * faceArea;
    }

    /// <summary>
    ///     Per-worker scratch for <see cref="SweepBundle" />.
    /// </summary>
    [ThreadStatic]
    private static BundleScratch? t_scratch;

    /// <summary>
    ///     The per-cell update of one bundle: the buffers and constants it needs, resolved once for the bundle.
    /// </summary>
    /// <remarks>
    ///     The normal and tangential momentum arrays are picked from the sweep axis here, so the cell loop does not
    ///     branch on the axis to read them. The tangential pair follows
    ///     <see cref="EulerFaceState.FromCell" />'s cyclic order: <c>(axis + 1) mod 3</c> and <c>(axis + 2) mod 3</c>.
    /// </remarks>
    private readonly struct EulerCellPass
    {
        private readonly int _axis;
        private readonly Second _timeStep;
        private readonly MetrePerSecond _maxVelocity;
        private readonly CubicMetre _volume;
        private readonly PascalPerMoleKelvin _pressurePerMoleKelvin;
        private readonly KilogramMetrePerSecond[] _readNormal;
        private readonly KilogramMetrePerSecond[] _readTangential1;
        private readonly KilogramMetrePerSecond[] _readTangential2;
        private readonly Joule64[] _readEnergy;
        private readonly EulerChunkState _write;
        private readonly Newton[] _faceNormal;
        private readonly Newton[] _faceTangential1;
        private readonly Newton[] _faceTangential2;
        private readonly Watt[] _faceEnergy;

        /// <summary>
        ///     The same arrays and constants as plain floats, for <see cref="EulerCellKernel" />.
        /// </summary>
        public readonly EulerCellRun Run;

        public EulerCellPass(
            EulerChunkWorkspace workspace, int axis, Second timeStep, AtmosSolverConfigSnapshot config,
            BundleScratch scratch)
        {
            _faceNormal = scratch.FaceNormal;
            _faceTangential1 = scratch.FaceTangential1;
            _faceTangential2 = scratch.FaceTangential2;
            _faceEnergy = scratch.FaceEnergy;
            EulerChunkState read = workspace.Read;
            _axis = axis;
            _timeStep = timeStep;
            _maxVelocity = config.EulerFlow.MaxVelocity;
            _volume = config.VoxelVolume;
            _pressurePerMoleKelvin = config.PressurePerMoleKelvin;
            _readEnergy = read.Energy;
            _write = workspace.Write;
            _readNormal = Momentum(read, axis);
            _readTangential1 = Momentum(read, (axis + 1) % 3);
            _readTangential2 = Momentum(read, (axis + 2) % 3);
            EulerChunkState write = workspace.Write;
            Run = new EulerCellRun(
                axis, timeStep, _maxVelocity, _volume, _pressurePerMoleKelvin,
                _readNormal, _readTangential1, _readTangential2, _readEnergy,
                Momentum(write, axis), Momentum(write, (axis + 1) % 3), Momentum(write, (axis + 2) % 3), write.Energy,
                write.Mass, write.TotalMoles, write.HeatCapacity, write.Cells,
                _faceNormal, _faceTangential1, _faceTangential2, _faceEnergy,
                scratch.Mass, scratch.TotalMoles, scratch.HeatCapacity, scratch.NonFiniteSpecies);
        }

        private static KilogramMetrePerSecond[] Momentum(EulerChunkState buffer, int axis)
        {
            return axis switch
            {
                0 => buffer.MomentumX,
                1 => buffer.MomentumY,
                _ => buffer.MomentumZ
            };
        }

        /// <summary>
        ///     Advances one air cell by the difference of its two face rates and refreshes its derived state.
        /// </summary>
        /// <param name="voxelIndex">The air cell.</param>
        /// <param name="behind">The scratch index of the face on the cell's lower side.</param>
        /// <param name="ahead">The scratch index of the face on the cell's upper side.</param>
        /// <param name="mass">Mass of the cell's new mixture, summed by the species pass.</param>
        /// <param name="totalMoles">Moles of the cell's new mixture, summed by the species pass.</param>
        /// <param name="heatCapacity">Heat capacity of the cell's new mixture, summed by the species pass.</param>
        /// <param name="nonFiniteSpecies">Whether the species pass found a non-finite species update in this cell.</param>
        /// <param name="counters">The pencil's repair counts.</param>
        /// <returns>The cell's new signal speed, or zero for a trace cell.</returns>
        /// <remarks>
        ///     <para>
        ///         Every field of the write buffer is written for every air cell, so no stale value from an earlier
        ///         sweep survives in it.
        ///     </para>
        ///     <para>
        ///         The species pass has already written the species rows. Momentum is updated in float. The energy
        ///         difference is formed in double so the subtraction from the large thermal part does not lose it.
        ///         The speed is clamped to <see cref="EulerFlow.EulerFlowConfig.MaxVelocity" /> before the primitive
        ///         state is rebuilt, and the clamp is counted. A cell whose primitive state does not fit in float
        ///         keeps its conserved variables but is written as a vacuum with no momentum, and is counted too. A
        ///         species update that is not finite is written as zero and counts the cell as non-finite too, so a
        ///         NaN never reaches a gas channel, where every later comparison would let it through.
        ///     </para>
        /// </remarks>
        public MetrePerSecond UpdateCell(
            int voxelIndex, int behind, int ahead,
            Kilogram mass, Mole totalMoles, JoulePerKelvin64 heatCapacity, bool nonFiniteSpecies,
            ref EulerSweepCounters counters)
        {
            EulerChunkState write = _write;
            Second timeStep = _timeStep;

            if (nonFiniteSpecies)
                counters.NonFiniteCells++;

            KilogramMetrePerSecond normal = _readNormal[voxelIndex] -
                                            timeStep * (_faceNormal[ahead] - _faceNormal[behind]);

            KilogramMetrePerSecond tangential1 = _readTangential1[voxelIndex] -
                                                 timeStep * (_faceTangential1[ahead] - _faceTangential1[behind]);

            KilogramMetrePerSecond tangential2 = _readTangential2[voxelIndex] -
                                                 timeStep * (_faceTangential2[ahead] - _faceTangential2[behind]);

            Joule64 energy = _readEnergy[voxelIndex] - (double)timeStep * ((double)_faceEnergy[ahead] - _faceEnergy[behind]);
            write.Energy[voxelIndex] = energy;

            write.Mass[voxelIndex] = mass;
            write.TotalMoles[voxelIndex] = totalMoles;
            write.HeatCapacity[voxelIndex] = heatCapacity;

            KilogramMetrePerSecond momentumX;
            KilogramMetrePerSecond momentumY;
            KilogramMetrePerSecond momentumZ;
            MetrePerSecond signalSpeed = 0f;
            ref EulerCellState cell = ref write.Cells[voxelIndex];
            if (EulerChunkWorkspace.IsTrace(mass, totalMoles))
            {
                momentumX = 0f;
                momentumY = 0f;
                momentumZ = 0f;
                cell = default;
            }
            else
            {
                switch (_axis)
                {
                    case 0:
                        momentumX = normal;
                        momentumY = tangential1;
                        momentumZ = tangential2;
                        break;
                    case 1:
                        momentumY = normal;
                        momentumZ = tangential1;
                        momentumX = tangential2;
                        break;
                    default:
                        momentumZ = normal;
                        momentumX = tangential1;
                        momentumY = tangential2;
                        break;
                }

                PerKilogram inverseMass = 1f / mass;
                MetrePerSecond velocityX = momentumX * inverseMass;
                MetrePerSecond velocityY = momentumY * inverseMass;
                MetrePerSecond velocityZ = momentumZ * inverseMass;
                Scalar speedRatio = (velocityX * velocityX + velocityY * velocityY + velocityZ * velocityZ) /
                                    (_maxVelocity * _maxVelocity);

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
                    energy, _volume, _pressurePerMoleKelvin,
                    out cell);

                if (kind == EulerCellKind.Gas)
                {
                    signalSpeed = cell.SignalSpeed;
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
    }

    /// <summary>
    ///     Scratch arrays for one bundle, owned by one worker thread and grown on demand.
    /// </summary>
    /// <remarks>
    ///     The face array holds <c>(marchCount + 1) · lineCount</c> faces. The other arrays hold one entry per cell
    ///     of the bundle, indexed by position in the bundle. A bundle zeroes the entries it uses before it adds to
    ///     them, so stale values from an earlier bundle never reach a sum.
    /// </remarks>
    private sealed class BundleScratch
    {
        public MolePerSecond[] FaceFlux = [];
        public PerSecond[] FaceSpeciesRate = [];
        public int[] FaceUpperMask = [];
        public int[] FixupFaces = [];
        public EulerFaceRate[] Faces = [];

        /// <summary>
        ///     Momentum and energy rates of every face, indexed like <see cref="Faces" />. The cell update reads these.
        ///     The scalar solver copies its result in and the vector kernel writes them directly.
        /// </summary>
        public Newton[] FaceNormal = [];
        public Newton[] FaceTangential1 = [];
        public Newton[] FaceTangential2 = [];
        public Watt[] FaceEnergy = [];
        public JoulePerKelvin64[] HeatCapacity = [];
        public Kilogram[] Mass = [];
        public bool[] NonFiniteSpecies = [];
        public Mole[] TotalMoles = [];

        /// <summary>
        ///     Whether the face solve covered the interior faces of each inner run with the vector kernel.
        /// </summary>
        public bool[] VectorRun = [];

        /// <summary>
        ///     Field arrays of cell primitives for the vector face solve, <see cref="StageStride" /> apart.
        /// </summary>
        public float[] Stage = [];

        public int StageStride;

        /// <summary>
        ///     Lanes of one half of a field array: a lower run and an upper run.
        /// </summary>
        public int StageHalf;

        /// <summary>
        ///     Cells the last staging wrote into each half, so the next one can clear only the stale tail.
        /// </summary>
        public readonly int[] StageDirty = new int[2];

        public void EnsureStage(int runLength)
        {
            int width = HllcFluxVector.Width;
            int half = (runLength + width - 1) / width * width + width;
            if (StageHalf == half)
                return;

            StageHalf = half;
            StageStride = 2 * half;
            Stage = new float[8 * StageStride];
            StageDirty[0] = 0;
            StageDirty[1] = 0;
        }

        public void EnsureCapacity(int faceCount, int cellCount, int runCount)
        {
            if (Faces.Length < faceCount)
            {
                Faces = new EulerFaceRate[faceCount];
                FaceNormal = new Newton[faceCount];
                FaceTangential1 = new Newton[faceCount];
                FaceTangential2 = new Newton[faceCount];
                FaceEnergy = new Watt[faceCount];
                FaceFlux = new MolePerSecond[faceCount];
                FaceSpeciesRate = new PerSecond[faceCount];
                FaceUpperMask = new int[faceCount];
                FixupFaces = new int[faceCount];
            }

            if (VectorRun.Length < runCount)
                VectorRun = new bool[runCount];

            if (Mass.Length < cellCount)
            {
                HeatCapacity = new JoulePerKelvin64[cellCount];
                Mass = new Kilogram[cellCount];
                NonFiniteSpecies = new bool[cellCount];
                TotalMoles = new Mole[cellCount];
            }
        }
    }
}
