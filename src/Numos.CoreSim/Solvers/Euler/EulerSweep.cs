using System.Diagnostics;
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
///         a scratch array. The second updates the species of every air cell, one gas at a time, and sums the
///         mixture as it goes. The third updates momentum and energy per cell and rebuilds its primitive state.
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
        bool[] isAir = scratch.IsAir;
        bool[] runAllAir = scratch.RunAllAir;

        EulerFlowConfig settings = config.EulerFlow;
        EulerChunkState read = workspace.Read;
        for (int march = 0; march <= marchCount; march++)
        {
            int lowerRow = march > 0 ? baseIndex + (march - 1) * marchStride : -1;
            int upperRow = march < marchCount ? baseIndex + march * marchStride : -1;
            for (int line = 0; line < lineCount; line++)
            {
                int face = alongMarch ? line * runFaceStride + march : march * lineCount + line;
                SolveFace(
                    workspace, read, axis,
                    lowerRow < 0 ? -1 : lowerRow + line * lineStride,
                    upperRow < 0 ? -1 : upperRow + line * lineStride,
                    faceArea, settings, out faces[face]);

                faceRate[face] = faces[face].SpeciesRate;
            }
        }

        for (int run = 0; run < runCount; run++)
        {
            int firstVoxel = baseIndex + run * runVoxelStride;
            bool allAir = true;
            for (int i = 0; i < runLength; i++)
            {
                bool air = EulerFaceTopology.IsAir(chunk, firstVoxel + i);
                isAir[run * runLength + i] = air;
                allAir &= air;
            }

            runAllAir[run] = allAir;
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

        int innerRunCount = alongMarch ? lineCount : marchCount - 1;
        int innerRunLength = alongMarch ? marchCount - 1 : lineCount;
        for (int run = 0; run < innerRunCount; run++)
        {
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
                if (runAllAir[run])
                {
                    EulerSpeciesKernel.UpdateRun(
                        in pass, firstVoxel, firstCell, firstBehind, firstAhead, runLength, ref counters.NegativeMoles);

                    continue;
                }

                for (int i = 0; i < runLength; i++)
                {
                    if (isAir[firstCell + i])
                    {
                        EulerSpeciesKernel.UpdateCell(
                            in pass, firstVoxel + i, firstCell + i, firstBehind + i, firstAhead + i,
                            ref counters.NegativeMoles);
                    }
                }
            }
        }

        var cellPass = new EulerCellPass(workspace, axis, timeStep, config);
        MetrePerSecond maxSignalSpeed = 0f;
        for (int run = 0; run < runCount; run++)
        {
            int firstVoxel = baseIndex + run * runVoxelStride;
            int firstCell = run * runLength;
            int firstBehind = run * runFaceStride;
            for (int i = 0; i < runLength; i++)
            {
                int cell = firstCell + i;
                if (!isAir[cell])
                    continue;

                MetrePerSecond signalSpeed = cellPass.UpdateCell(
                    firstVoxel + i, in faces[firstBehind + i], in faces[firstBehind + aheadOffset + i],
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
    ///         stage zeroes slower momentum at tick end, so that motion cannot carry gas into the neighbour. The neighbour's pressure is its live
    ///         <see cref="AtmosChunk.TotalPressure" />, which nothing writes while the chunk is inactive, and its
    ///         momentum is zero by invariant.
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

        public EulerCellPass(EulerChunkWorkspace workspace, int axis, Second timeStep, AtmosSolverConfigSnapshot config)
        {
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
        /// <param name="behind">The face on the cell's lower side.</param>
        /// <param name="ahead">The face on the cell's upper side.</param>
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
            int voxelIndex, in EulerFaceRate behind, in EulerFaceRate ahead,
            Kilogram mass, Mole totalMoles, JoulePerKelvin64 heatCapacity, bool nonFiniteSpecies,
            ref EulerSweepCounters counters)
        {
            EulerChunkState write = _write;
            Second timeStep = _timeStep;

            if (nonFiniteSpecies)
                counters.NonFiniteCells++;

            KilogramMetrePerSecond normal = _readNormal[voxelIndex] -
                                            timeStep * (ahead.NormalMomentum - behind.NormalMomentum);

            KilogramMetrePerSecond tangential1 = _readTangential1[voxelIndex] -
                                                 timeStep * (ahead.Tangential1Momentum - behind.Tangential1Momentum);

            KilogramMetrePerSecond tangential2 = _readTangential2[voxelIndex] -
                                                 timeStep * (ahead.Tangential2Momentum - behind.Tangential2Momentum);

            Joule64 energy = _readEnergy[voxelIndex] - (double)timeStep * ((double)ahead.Energy - behind.Energy);
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
        public bool[] RunAllAir = [];
        public EulerFaceRate[] Faces = [];
        public JoulePerKelvin64[] HeatCapacity = [];
        public bool[] IsAir = [];
        public Kilogram[] Mass = [];
        public bool[] NonFiniteSpecies = [];
        public Mole[] TotalMoles = [];

        public void EnsureCapacity(int faceCount, int cellCount, int runCount)
        {
            if (Faces.Length < faceCount)
            {
                Faces = new EulerFaceRate[faceCount];
                FaceFlux = new MolePerSecond[faceCount];
                FaceSpeciesRate = new PerSecond[faceCount];
                FaceUpperMask = new int[faceCount];
                FixupFaces = new int[faceCount];
            }

            if (RunAllAir.Length < runCount)
                RunAllAir = new bool[runCount];

            if (Mass.Length < cellCount)
            {
                HeatCapacity = new JoulePerKelvin64[cellCount];
                IsAir = new bool[cellCount];
                Mass = new Kilogram[cellCount];
                NonFiniteSpecies = new bool[cellCount];
                TotalMoles = new Mole[cellCount];
            }
        }
    }
}
