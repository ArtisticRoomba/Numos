using System.Numerics;

namespace Numos.CoreSim.Solvers.Euler;

/// <summary>
///     The arrays and constants of the cell update of one bundle, for <see cref="EulerCellKernel" />.
/// </summary>
/// <remarks>
///     <see cref="EulerSweep" /> fills the struct once per bundle. The vector update and the scalar update both
///     read it.
/// </remarks>
internal readonly struct EulerCellRun
{
    public EulerCellRun(
        int axis, Second timeStep, MetrePerSecond maxVelocity, CubicMetre volume,
        PascalPerMoleKelvin pressurePerMoleKelvin,
        KilogramMetrePerSecond[] readNormal, KilogramMetrePerSecond[] readTangential1,
        KilogramMetrePerSecond[] readTangential2, Joule64[] readEnergy,
        KilogramMetrePerSecond[] writeNormal, KilogramMetrePerSecond[] writeTangential1,
        KilogramMetrePerSecond[] writeTangential2, Joule64[] writeEnergy,
        Kilogram[] writeMass, Mole[] writeTotalMoles, JoulePerKelvin64[] writeHeatCapacity, EulerChunkState cells,
        Newton[] faceNormal, Newton[] faceTangential1, Newton[] faceTangential2, Watt[] faceEnergy,
        Kilogram[] mass, Mole[] totalMoles, JoulePerKelvin64[] heatCapacity, bool[] nonFiniteSpecies)
    {
        Axis = axis;
        TimeStep = timeStep;
        MaxVelocity = maxVelocity;
        Volume = volume;
        PressurePerMoleKelvin = pressurePerMoleKelvin;
        ReadNormal = readNormal;
        ReadTangential1 = readTangential1;
        ReadTangential2 = readTangential2;
        ReadEnergy = readEnergy;
        WriteNormal = writeNormal;
        WriteTangential1 = writeTangential1;
        WriteTangential2 = writeTangential2;
        WriteEnergy = writeEnergy;
        WriteMass = writeMass;
        WriteTotalMoles = writeTotalMoles;
        WriteHeatCapacity = writeHeatCapacity;
        Cells = cells;
        FaceNormal = faceNormal;
        FaceTangential1 = faceTangential1;
        FaceTangential2 = faceTangential2;
        FaceEnergy = faceEnergy;
        Mass = mass;
        TotalMoles = totalMoles;
        HeatCapacity = heatCapacity;
        NonFiniteSpecies = nonFiniteSpecies;
    }

    public readonly int Axis;
    public readonly Second TimeStep;
    public readonly MetrePerSecond MaxVelocity;
    public readonly CubicMetre Volume;
    public readonly PascalPerMoleKelvin PressurePerMoleKelvin;

    /// <summary>
    ///     Read-buffer momentum along the sweep axis and along the two axes after it in cyclic order.
    /// </summary>
    public readonly KilogramMetrePerSecond[] ReadNormal;
    public readonly KilogramMetrePerSecond[] ReadTangential1;
    public readonly KilogramMetrePerSecond[] ReadTangential2;
    public readonly Joule64[] ReadEnergy;

    /// <summary>
    ///     Write-buffer momentum arrays, picked the same way as the read ones.
    /// </summary>
    public readonly KilogramMetrePerSecond[] WriteNormal;
    public readonly KilogramMetrePerSecond[] WriteTangential1;
    public readonly KilogramMetrePerSecond[] WriteTangential2;
    public readonly Joule64[] WriteEnergy;
    public readonly Kilogram[] WriteMass;
    public readonly Mole[] WriteTotalMoles;
    public readonly JoulePerKelvin64[] WriteHeatCapacity;
    public readonly EulerChunkState Cells;
    public readonly Newton[] FaceNormal;
    public readonly Newton[] FaceTangential1;
    public readonly Newton[] FaceTangential2;
    public readonly Watt[] FaceEnergy;
    public readonly Kilogram[] Mass;
    public readonly Mole[] TotalMoles;
    public readonly JoulePerKelvin64[] HeatCapacity;
    public readonly bool[] NonFiniteSpecies;
}

/// <summary>
///     The cell update of <see cref="EulerSweep" />: a vector version for runs of air cells and the scalar update
///     for the other cells.
/// </summary>
/// <remarks>
///     <para>
///         Each lane does what <see cref="UpdateCell" /> and <see cref="EulerMixture.ToPrimitive(Kilogram, Mole,
///         JoulePerKelvin64, KilogramMetrePerSecond, KilogramMetrePerSecond, KilogramMetrePerSecond, Joule64,
///         CubicMetre, PascalPerMoleKelvin, out EulerCellState)" /> do for one cell, with the same IEEE operations
///         on the same operands in the same order. The energy path runs in double on half vectors, widened and
///         narrowed with the same rounding as the scalar casts. Square roots are correctly rounded, so a vector
///         square root gives the scalar bits. The kernel uses no fused multiply-add and no reciprocal.
///     </para>
///     <para>
///         Every branch of the scalar code becomes a mask: trace cells, empty cells, cells that do not fit in float,
///         and cells over the speed limit. The kernel writes the same arrays as the scalar update. Each primitive
///         field of the cells goes to its own array with one vector store.
///     </para>
/// </remarks>
internal static class EulerCellKernel
{
    internal static int Width => Vector<float>.Count;

    /// <summary>
    ///     Whether the kernel runs on vector hardware. Tests clear it to compare against the scalar update.
    /// </summary>
    internal static bool Enabled = Vector.IsHardwareAccelerated && Vector<float>.Count >= 4 &&
                                   Vector<double>.Count * 2 == Vector<float>.Count;

    /// <summary>
    ///     Updates the air cells <c>[0, count)</c> of a run, a whole number of vectors at a time.
    /// </summary>
    /// <param name="run">The bundle's arrays and constants.</param>
    /// <param name="firstVoxel">Voxel index of the first cell. The cells are contiguous in voxel memory.</param>
    /// <param name="firstCell">Scratch index of the first cell.</param>
    /// <param name="behind">Scratch index of the face behind the first cell. Face distance between cells is one.</param>
    /// <param name="ahead">Scratch index of the face ahead of the first cell.</param>
    /// <param name="count">Number of cells, at least <see cref="Width" />. Every cell must be air.</param>
    /// <param name="clampedVelocity">The pencil's count of clamped velocities.</param>
    /// <param name="nonFiniteCells">The pencil's count of non-finite cells.</param>
    /// <param name="maxSignalSpeed">Raised to the fastest signal speed of the cells written.</param>
    /// <returns>Number of cells done, a multiple of <see cref="Width" /> that is at most <paramref name="count" />.</returns>
    internal static int UpdateRun(
        in EulerCellRun run, int firstVoxel, int firstCell, int behind, int ahead, int count,
        ref int clampedVelocity, ref int nonFiniteCells, ref MetrePerSecond maxSignalSpeed)
    {
        int width = Vector<float>.Count;
        int halfWidth = Vector<double>.Count;
        EulerChunkState cells = run.Cells;
        Span<MetrePerSecond> signal = stackalloc MetrePerSecond[width];
        Span<int> flags = stackalloc int[width];
        int done = 0;
        for (; done + width <= count; done += width)
        {
            int voxel = firstVoxel + done;
            int cell = firstCell + done;
            int back = behind + done;
            int front = ahead + done;

            Vector<Second> timeStep = new Vector<float>(run.TimeStep);
            Vector<KilogramMetrePerSecond> normal = new Vector<float>(run.ReadNormal, voxel) -
                         timeStep * (new Vector<float>(run.FaceNormal, front) - new Vector<float>(run.FaceNormal, back));
            Vector<KilogramMetrePerSecond> tangential1 = new Vector<float>(run.ReadTangential1, voxel) -
                              timeStep * (new Vector<float>(run.FaceTangential1, front) -
                                          new Vector<float>(run.FaceTangential1, back));
            Vector<KilogramMetrePerSecond> tangential2 = new Vector<float>(run.ReadTangential2, voxel) -
                              timeStep * (new Vector<float>(run.FaceTangential2, front) -
                                          new Vector<float>(run.FaceTangential2, back));

            // Energy difference in double: (double)ahead - behind, scaled by (double)timeStep.
            Vector.Widen(
                new Vector<float>(run.FaceEnergy, front), out Vector<Watt64> frontLow, out Vector<Watt64> frontHigh);
            Vector.Widen(
                new Vector<float>(run.FaceEnergy, back), out Vector<Watt64> backLow, out Vector<Watt64> backHigh);
            Vector<Second64> timeStepDouble = new Vector<double>(run.TimeStep);
            Vector<Joule64> energyLow = new Vector<double>(run.ReadEnergy, voxel) -
                                       timeStepDouble * (frontLow - backLow);
            Vector<Joule64> energyHigh = new Vector<double>(run.ReadEnergy, voxel + halfWidth) -
                                        timeStepDouble * (frontHigh - backHigh);
            energyLow.CopyTo(run.WriteEnergy, voxel);
            energyHigh.CopyTo(run.WriteEnergy, voxel + halfWidth);

            Vector<Kilogram> mass = new Vector<float>(run.Mass, cell);
            Vector<Mole> moles = new Vector<float>(run.TotalMoles, cell);
            Vector<JoulePerKelvin64> heatLow = new Vector<double>(run.HeatCapacity, cell);
            Vector<JoulePerKelvin64> heatHigh = new Vector<double>(run.HeatCapacity, cell + halfWidth);
            mass.CopyTo(run.WriteMass, voxel);
            moles.CopyTo(run.WriteTotalMoles, voxel);
            heatLow.CopyTo(run.WriteHeatCapacity, voxel);
            heatHigh.CopyTo(run.WriteHeatCapacity, voxel + halfWidth);

            Vector<float> zero = Vector<float>.Zero;
            Vector<int> trace = Vector.LessThanOrEqual(mass, zero) |
                                Vector.LessThan(moles, new Vector<float>(AtmosSolverConstants.MinimumTrackedMoles));

            Vector<KilogramMetrePerSecond> momentumX;
            Vector<KilogramMetrePerSecond> momentumY;
            Vector<KilogramMetrePerSecond> momentumZ;
            switch (run.Axis)
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

            Vector<PerKilogram> inverseMass = Vector<float>.One / mass;
            Vector<MetrePerSecond> velocityX = momentumX * inverseMass;
            Vector<MetrePerSecond> velocityY = momentumY * inverseMass;
            Vector<MetrePerSecond> velocityZ = momentumZ * inverseMass;
            Vector<Scalar> speedRatio =
                (velocityX * velocityX + velocityY * velocityY + velocityZ * velocityZ) /
                new Vector<float>(run.MaxVelocity * run.MaxVelocity);

            Vector<int> clamp = Vector.AndNot(Vector.GreaterThan(speedRatio, Vector<float>.One), trace);
            Vector<Scalar> scale = Vector<float>.One / Vector.SquareRoot(speedRatio);
            momentumX = Vector.ConditionalSelect(clamp, momentumX * scale, momentumX);
            momentumY = Vector.ConditionalSelect(clamp, momentumY * scale, momentumY);
            momentumZ = Vector.ConditionalSelect(clamp, momentumZ * scale, momentumZ);

            // ToPrimitive, from the clamped momentum.
            Vector<int> empty = Vector.LessThanOrEqual(mass, zero) | Vector.LessThanOrEqual(moles, zero) |
                                Vector.Narrow(
                                    Vector.LessThanOrEqual(heatLow, Vector<double>.Zero),
                                    Vector.LessThanOrEqual(heatHigh, Vector<double>.Zero));
            velocityX = momentumX * inverseMass;
            velocityY = momentumY * inverseMass;
            velocityZ = momentumZ * inverseMass;

            Vector.Widen(
                momentumX, out Vector<KilogramMetrePerSecond64> pxLow, out Vector<KilogramMetrePerSecond64> pxHigh);
            Vector.Widen(
                momentumY, out Vector<KilogramMetrePerSecond64> pyLow, out Vector<KilogramMetrePerSecond64> pyHigh);
            Vector.Widen(
                momentumZ, out Vector<KilogramMetrePerSecond64> pzLow, out Vector<KilogramMetrePerSecond64> pzHigh);
            Vector.Widen(velocityX, out Vector<MetrePerSecond64> vxLow, out Vector<MetrePerSecond64> vxHigh);
            Vector.Widen(velocityY, out Vector<MetrePerSecond64> vyLow, out Vector<MetrePerSecond64> vyHigh);
            Vector.Widen(velocityZ, out Vector<MetrePerSecond64> vzLow, out Vector<MetrePerSecond64> vzHigh);
            Vector<Scalar64> half = new Vector<double>(0.5d);
            Vector<Joule64> kineticLow = half * (pxLow * vxLow + pyLow * vyLow + pzLow * vzLow);
            Vector<Joule64> kineticHigh = half * (pxHigh * vxHigh + pyHigh * vyHigh + pzHigh * vzHigh);
            Vector<Joule64> internalLow = Vector.Max(Vector<double>.Zero, energyLow - kineticLow);
            Vector<Joule64> internalHigh = Vector.Max(Vector<double>.Zero, energyHigh - kineticHigh);
            Vector<Kelvin> temperature = Vector.Narrow(internalLow / heatLow, internalHigh / heatHigh);

            Vector<Pascal> pressure = moles * temperature * new Vector<float>(run.PressurePerMoleKelvin);
            Vector<JoulePerKelvin> molarGasTerm = moles * new Vector<float>(AtmosPhysicalConstants.MolarGasConstant);
            Vector.Widen(molarGasTerm, out Vector<JoulePerKelvin64> gasLow, out Vector<JoulePerKelvin64> gasHigh);
            Vector<Scalar> gamma = Vector.Narrow((heatLow + gasLow) / heatLow, (heatHigh + gasHigh) / heatHigh);
            Vector<MetrePerSecond> soundSpeed = Vector.SquareRoot(gamma * molarGasTerm * temperature * inverseMass);
            Vector<CubicMetre64> volumeDouble = new Vector<double>(run.Volume);
            Vector<JoulePerCubicMetre> energyDensity =
                Vector.Narrow(energyLow / volumeDouble, energyHigh / volumeDouble);

            Vector<float> infinity = new Vector<float>(float.PositiveInfinity);
            Vector<int> finite = Vector.LessThan(Vector.Abs(temperature), infinity) &
                                 Vector.LessThan(Vector.Abs(pressure), infinity) &
                                 Vector.LessThan(Vector.Abs(soundSpeed), infinity) &
                                 Vector.LessThan(Vector.Abs(energyDensity), infinity) &
                                 Vector.LessThan(Vector.Abs(inverseMass), infinity);

            Vector<int> evaluated = Vector.AndNot(~empty, trace);
            Vector<int> gas = evaluated & finite;
            Vector<int> notFinite = Vector.AndNot(evaluated, finite);
            Vector<float> gasBits = Vector.AsVectorSingle(gas);

            Vector<MetrePerSecond> signalSpeed = Vector.Max(
                                            Vector.Max(Vector.Abs(velocityX), Vector.Abs(velocityY)),
                                            Vector.Abs(velocityZ)) + soundSpeed;

            Vector.BitwiseAnd(momentumX, gasBits).CopyTo(PickMomentum(run, 0), voxel);
            Vector.BitwiseAnd(momentumY, gasBits).CopyTo(PickMomentum(run, 1), voxel);
            Vector.BitwiseAnd(momentumZ, gasBits).CopyTo(PickMomentum(run, 2), voxel);

            Vector.BitwiseAnd(mass / new Vector<float>(run.Volume), gasBits).CopyTo(cells.CellDensity, voxel);
            Vector.BitwiseAnd(velocityX, gasBits).CopyTo(cells.CellVelocityX, voxel);
            Vector.BitwiseAnd(velocityY, gasBits).CopyTo(cells.CellVelocityY, voxel);
            Vector.BitwiseAnd(velocityZ, gasBits).CopyTo(cells.CellVelocityZ, voxel);
            Vector.BitwiseAnd(pressure, gasBits).CopyTo(cells.CellPressure, voxel);
            Vector.BitwiseAnd(energyDensity, gasBits).CopyTo(cells.CellEnergyDensity, voxel);
            Vector.BitwiseAnd(soundSpeed, gasBits).CopyTo(cells.CellSoundSpeed, voxel);
            Vector.BitwiseAnd(inverseMass, gasBits).CopyTo(cells.CellInverseMass, voxel);
            Vector.BitwiseAnd(signalSpeed, gasBits).CopyTo(signal);
            ((clamp & Vector<int>.One) | (notFinite & new Vector<int>(2))).CopyTo(flags);

            for (int lane = 0; lane < width; lane++)
            {
                maxSignalSpeed = MathF.Max(maxSignalSpeed, signal[lane]);

                bool speciesFault = run.NonFiniteSpecies[cell + lane];
                if (speciesFault)
                    nonFiniteCells++;

                if ((flags[lane] & 1) != 0)
                    clampedVelocity++;

                if ((flags[lane] & 2) != 0 && !speciesFault)
                    nonFiniteCells++;
            }
        }

        return done;
    }

    /// <summary>
    ///     Advances one air cell by the difference of its two face rates and refreshes its derived state.
    /// </summary>
    /// <param name="run">The bundle's arrays and constants.</param>
    /// <param name="voxelIndex">The air cell.</param>
    /// <param name="cell">Scratch index of the cell, where the species pass left its new mixture sums.</param>
    /// <param name="behind">The scratch index of the face on the cell's lower side.</param>
    /// <param name="ahead">The scratch index of the face on the cell's upper side.</param>
    /// <param name="counters">The pencil's repair counts.</param>
    /// <returns>The cell's new signal speed, or zero for a trace cell.</returns>
    /// <remarks>
    ///     <para>
    ///         The sweep calls this for the cells <see cref="UpdateRun" /> leaves. Those are the tail of a run and the
    ///         air cells of a row that holds non-air voxels. Every field of the write buffer is written for every air
    ///         cell, so no stale value from an earlier sweep survives in it.
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
    internal static MetrePerSecond UpdateCell(
        in EulerCellRun run, int voxelIndex, int cell, int behind, int ahead, ref EulerSweepCounters counters)
    {
        EulerChunkState write = run.Cells;
        Second timeStep = run.TimeStep;
        Kilogram mass = run.Mass[cell];
        Mole totalMoles = run.TotalMoles[cell];
        JoulePerKelvin64 heatCapacity = run.HeatCapacity[cell];
        bool nonFiniteSpecies = run.NonFiniteSpecies[cell];

        if (nonFiniteSpecies)
            counters.NonFiniteCells++;

        KilogramMetrePerSecond normal = run.ReadNormal[voxelIndex] -
                                        timeStep * (run.FaceNormal[ahead] - run.FaceNormal[behind]);

        KilogramMetrePerSecond tangential1 = run.ReadTangential1[voxelIndex] -
                                             timeStep * (run.FaceTangential1[ahead] - run.FaceTangential1[behind]);

        KilogramMetrePerSecond tangential2 = run.ReadTangential2[voxelIndex] -
                                             timeStep * (run.FaceTangential2[ahead] - run.FaceTangential2[behind]);

        Joule64 energy = run.ReadEnergy[voxelIndex] -
                         (double)timeStep * ((double)run.FaceEnergy[ahead] - run.FaceEnergy[behind]);

        run.WriteEnergy[voxelIndex] = energy;

        run.WriteMass[voxelIndex] = mass;
        run.WriteTotalMoles[voxelIndex] = totalMoles;
        run.WriteHeatCapacity[voxelIndex] = heatCapacity;

        KilogramMetrePerSecond momentumX;
        KilogramMetrePerSecond momentumY;
        KilogramMetrePerSecond momentumZ;
        MetrePerSecond signalSpeed = 0f;
        if (EulerChunkWorkspace.IsTrace(mass, totalMoles))
        {
            momentumX = 0f;
            momentumY = 0f;
            momentumZ = 0f;
            write.SetCell(voxelIndex, default);
        }
        else
        {
            switch (run.Axis)
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
                                (run.MaxVelocity * run.MaxVelocity);

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
                energy, run.Volume, run.PressurePerMoleKelvin,
                out EulerCellState cellState);

            write.SetCell(voxelIndex, in cellState);
            if (kind == EulerCellKind.Gas)
            {
                signalSpeed = cellState.SignalSpeed;
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

    private static KilogramMetrePerSecond[] PickMomentum(in EulerCellRun run, int axisOfArray)
    {
        // The normal array belongs to the sweep axis. The next two follow in cyclic order.
        int offset = (axisOfArray - run.Axis + 3) % 3;
        return offset switch
        {
            0 => run.WriteNormal,
            1 => run.WriteTangential1,
            _ => run.WriteTangential2
        };
    }
}
