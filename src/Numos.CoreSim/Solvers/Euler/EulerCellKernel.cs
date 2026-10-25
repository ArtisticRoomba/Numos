using System.Numerics;

namespace Numos.CoreSim.Solvers.Euler;

/// <summary>
///     The arrays and constants of the cell update of one bundle, as plain floats for <see cref="EulerCellKernel" />.
/// </summary>
/// <remarks>
///     The kernel works on <see cref="Vector{T}" /> lanes, which carry no units.
///     <see cref="EulerSweep" /> fills the struct from the same arrays its scalar cell update uses.
/// </remarks>
internal readonly struct EulerCellRun
{
    public EulerCellRun(
        int axis, float timeStep, float maxVelocity, float volume, float pressurePerMoleKelvin,
        float[] readNormal, float[] readTangential1, float[] readTangential2, double[] readEnergy,
        float[] writeNormal, float[] writeTangential1, float[] writeTangential2, double[] writeEnergy,
        float[] writeMass, float[] writeTotalMoles, double[] writeHeatCapacity, EulerChunkState cells,
        float[] faceNormal, float[] faceTangential1, float[] faceTangential2, float[] faceEnergy,
        float[] mass, float[] totalMoles, double[] heatCapacity, bool[] nonFiniteSpecies)
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
    public readonly float TimeStep;
    public readonly float MaxVelocity;
    public readonly float Volume;
    public readonly float PressurePerMoleKelvin;

    /// <summary>
    ///     Read-buffer momentum along the sweep axis and along the two axes after it in cyclic order.
    /// </summary>
    public readonly float[] ReadNormal;
    public readonly float[] ReadTangential1;
    public readonly float[] ReadTangential2;
    public readonly double[] ReadEnergy;

    /// <summary>
    ///     Write-buffer momentum arrays, picked the same way as the read ones.
    /// </summary>
    public readonly float[] WriteNormal;
    public readonly float[] WriteTangential1;
    public readonly float[] WriteTangential2;
    public readonly double[] WriteEnergy;
    public readonly float[] WriteMass;
    public readonly float[] WriteTotalMoles;
    public readonly double[] WriteHeatCapacity;
    public readonly EulerChunkState Cells;
    public readonly float[] FaceNormal;
    public readonly float[] FaceTangential1;
    public readonly float[] FaceTangential2;
    public readonly float[] FaceEnergy;
    public readonly float[] Mass;
    public readonly float[] TotalMoles;
    public readonly double[] HeatCapacity;
    public readonly bool[] NonFiniteSpecies;
}

/// <summary>
///     Vector version of the cell update of <see cref="EulerSweep" /> for runs of air cells.
/// </summary>
/// <remarks>
///     <para>
///         Each lane does what the scalar cell update and <see cref="EulerMixture.ToPrimitive(Kilogram, Mole,
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
        ref int clampedVelocity, ref int nonFiniteCells, ref float maxSignalSpeed)
    {
        int width = Vector<float>.Count;
        int halfWidth = Vector<double>.Count;
        EulerChunkState cells = run.Cells;
        Span<float> signal = stackalloc float[width];
        Span<int> flags = stackalloc int[width];
        int done = 0;
        for (; done + width <= count; done += width)
        {
            int voxel = firstVoxel + done;
            int cell = firstCell + done;
            int back = behind + done;
            int front = ahead + done;

            var timeStep = new Vector<float>(run.TimeStep);
            var normal = new Vector<float>(run.ReadNormal, voxel) -
                         timeStep * (new Vector<float>(run.FaceNormal, front) - new Vector<float>(run.FaceNormal, back));
            var tangential1 = new Vector<float>(run.ReadTangential1, voxel) -
                              timeStep * (new Vector<float>(run.FaceTangential1, front) -
                                          new Vector<float>(run.FaceTangential1, back));
            var tangential2 = new Vector<float>(run.ReadTangential2, voxel) -
                              timeStep * (new Vector<float>(run.FaceTangential2, front) -
                                          new Vector<float>(run.FaceTangential2, back));

            // Energy difference in double: (double)ahead - behind, scaled by (double)timeStep.
            Vector.Widen(new Vector<float>(run.FaceEnergy, front), out Vector<double> frontLow, out Vector<double> frontHigh);
            Vector.Widen(new Vector<float>(run.FaceEnergy, back), out Vector<double> backLow, out Vector<double> backHigh);
            var timeStepDouble = new Vector<double>(run.TimeStep);
            Vector<double> energyLow = new Vector<double>(run.ReadEnergy, voxel) -
                                       timeStepDouble * (frontLow - backLow);
            Vector<double> energyHigh = new Vector<double>(run.ReadEnergy, voxel + halfWidth) -
                                        timeStepDouble * (frontHigh - backHigh);
            energyLow.CopyTo(run.WriteEnergy, voxel);
            energyHigh.CopyTo(run.WriteEnergy, voxel + halfWidth);

            var mass = new Vector<float>(run.Mass, cell);
            var moles = new Vector<float>(run.TotalMoles, cell);
            Vector<double> heatLow = new Vector<double>(run.HeatCapacity, cell);
            Vector<double> heatHigh = new Vector<double>(run.HeatCapacity, cell + halfWidth);
            mass.CopyTo(run.WriteMass, voxel);
            moles.CopyTo(run.WriteTotalMoles, voxel);
            heatLow.CopyTo(run.WriteHeatCapacity, voxel);
            heatHigh.CopyTo(run.WriteHeatCapacity, voxel + halfWidth);

            Vector<float> zero = Vector<float>.Zero;
            Vector<int> trace = Vector.LessThanOrEqual(mass, zero) |
                                Vector.LessThan(moles, new Vector<float>(AtmosSolverConstants.MinimumTrackedMoles));

            Vector<float> momentumX;
            Vector<float> momentumY;
            Vector<float> momentumZ;
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

            Vector<float> inverseMass = Vector<float>.One / mass;
            Vector<float> velocityX = momentumX * inverseMass;
            Vector<float> velocityY = momentumY * inverseMass;
            Vector<float> velocityZ = momentumZ * inverseMass;
            Vector<float> speedRatio =
                (velocityX * velocityX + velocityY * velocityY + velocityZ * velocityZ) /
                new Vector<float>(run.MaxVelocity * run.MaxVelocity);

            Vector<int> clamp = Vector.AndNot(Vector.GreaterThan(speedRatio, Vector<float>.One), trace);
            Vector<float> scale = Vector<float>.One / Vector.SquareRoot(speedRatio);
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

            Vector.Widen(momentumX, out Vector<double> pxLow, out Vector<double> pxHigh);
            Vector.Widen(momentumY, out Vector<double> pyLow, out Vector<double> pyHigh);
            Vector.Widen(momentumZ, out Vector<double> pzLow, out Vector<double> pzHigh);
            Vector.Widen(velocityX, out Vector<double> vxLow, out Vector<double> vxHigh);
            Vector.Widen(velocityY, out Vector<double> vyLow, out Vector<double> vyHigh);
            Vector.Widen(velocityZ, out Vector<double> vzLow, out Vector<double> vzHigh);
            var half = new Vector<double>(0.5d);
            Vector<double> kineticLow = half * (pxLow * vxLow + pyLow * vyLow + pzLow * vzLow);
            Vector<double> kineticHigh = half * (pxHigh * vxHigh + pyHigh * vyHigh + pzHigh * vzHigh);
            Vector<double> internalLow = Vector.Max(Vector<double>.Zero, energyLow - kineticLow);
            Vector<double> internalHigh = Vector.Max(Vector<double>.Zero, energyHigh - kineticHigh);
            Vector<float> temperature = Vector.Narrow(internalLow / heatLow, internalHigh / heatHigh);

            Vector<float> pressure = moles * temperature * new Vector<float>(run.PressurePerMoleKelvin);
            Vector<float> molarGasTerm = moles * new Vector<float>(AtmosPhysicalConstants.MolarGasConstant);
            Vector.Widen(molarGasTerm, out Vector<double> gasLow, out Vector<double> gasHigh);
            Vector<float> gamma = Vector.Narrow((heatLow + gasLow) / heatLow, (heatHigh + gasHigh) / heatHigh);
            Vector<float> soundSpeed = Vector.SquareRoot(gamma * molarGasTerm * temperature * inverseMass);
            var volumeDouble = new Vector<double>(run.Volume);
            Vector<float> energyDensity = Vector.Narrow(energyLow / volumeDouble, energyHigh / volumeDouble);

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

            Vector<float> signalSpeed = Vector.Max(
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

    private static float[] PickMomentum(in EulerCellRun run, int axisOfArray)
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
