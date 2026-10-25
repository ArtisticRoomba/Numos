using System.Numerics;

namespace Numos.CoreSim.Solvers.Euler;

/// <summary>
///     The arrays and per-gas constants of one gas in the species pass of a bundle.
/// </summary>
internal readonly struct EulerSpeciesPass
{
    public EulerSpeciesPass(
        Mole[] source, Mole[] target, MolePerSecond[] faceFlux, bool[] nonFinite,
        Kilogram[] massSum, Mole[] moleSum, JoulePerKelvin64[] heatSum,
        Second timeStep, KilogramPerMole molarMass, JoulePerMoleKelvin molarHeat)
    {
        Source = source;
        Target = target;
        FaceFlux = faceFlux;
        NonFinite = nonFinite;
        MassSum = massSum;
        MoleSum = moleSum;
        HeatSum = heatSum;
        TimeStep = timeStep;
        MolarMass = molarMass;
        MolarHeat = molarHeat;
    }

    public readonly Mole[] Source;
    public readonly Mole[] Target;
    public readonly MolePerSecond[] FaceFlux;
    public readonly bool[] NonFinite;
    public readonly Kilogram[] MassSum;
    public readonly Mole[] MoleSum;
    public readonly JoulePerKelvin64[] HeatSum;
    public readonly Second TimeStep;
    public readonly KilogramPerMole MolarMass;
    public readonly JoulePerMoleKelvin MolarHeat;
}

/// <summary>
///     Vector kernels for the species pass of <see cref="EulerSweep" />.
/// </summary>
/// <remarks>
///     <para>
///         Each lane of a vector loop does exactly what <see cref="UpdateCell" /> does for one cell: the same
///         IEEE operations on the same operands in the same order. The kernels use no fused multiply-add, and a
///         cell's sums still add its gases in ascending list order because the gas loop is outermost. The
///         heat sum widens the float moles to double before it multiplies, as the scalar code does. A vector
///         therefore gives the same bits as a scalar loop over its lanes, with or without hardware support.
///     </para>
///     <para>
///         A run is a stretch of cells that is contiguous in voxel memory and in the bundle scratch. A vector loop
///         handles whole vectors and a scalar loop handles the rest, through the same <see cref="UpdateCell" />.
///     </para>
/// </remarks>
internal static class EulerSpeciesKernel
{
    /// <summary>
    ///     Computes the species flux of a run of interior faces whose upwind cell is the lower or the upper cell.
    /// </summary>
    /// <param name="rate">The per-face species rates.</param>
    /// <param name="upperMask">Per face, all bits set if the upwind cell is the upper one, else zero.</param>
    /// <param name="source">The gas row of the read buffer.</param>
    /// <param name="flux">The per-face species flux to write.</param>
    /// <param name="firstFace">The first face of the run.</param>
    /// <param name="lowerVoxel">The lower cell of the first face. The upper cell is <paramref name="upperOffset" /> after it.</param>
    /// <param name="upperOffset">Voxel distance between the lower and the upper cell of a face.</param>
    /// <param name="length">Number of faces.</param>
    /// <remarks>
    ///     The mask picks the same cell the scalar code reads through the face's upwind index, so each lane is
    ///     <c>rate · source[upwind]</c>. The last vector overlaps the one before it when the length is not a
    ///     multiple of the vector width. The overlapped lanes store the bits they stored already.
    /// </remarks>
    public static void FaceFluxRun(
        PerSecond[] rate, int[] upperMask, Mole[] source, MolePerSecond[] flux,
        int firstFace, int lowerVoxel, int upperOffset, int length)
    {
        int width = Vector<float>.Count;
        if (!Vector.IsHardwareAccelerated || length < width)
        {
            for (int i = 0; i < length; i++)
                flux[firstFace + i] = rate[firstFace + i] * source[lowerVoxel + i + (upperMask[firstFace + i] & upperOffset)];

            return;
        }

        int start = 0;
        for (; start + width <= length; start += width)
            FaceFluxVector(rate, upperMask, source, flux, firstFace + start, lowerVoxel + start, upperOffset);

        if (start < length)
        {
            int last = length - width;
            FaceFluxVector(rate, upperMask, source, flux, firstFace + last, lowerVoxel + last, upperOffset);
        }
    }

    /// <summary>
    ///     Whether <see cref="EdgeFluxRow" /> handles the chunk-boundary rows. Tests clear it to compare against the
    ///     scalar fix-up loop.
    /// </summary>
    internal static bool EdgeRowsEnabled = Vector.IsHardwareAccelerated;

    /// <summary>
    ///     Computes the species flux of the faces between a chunk-boundary row and the facing row of the active
    ///     neighbour chunk.
    /// </summary>
    /// <param name="rate">The per-face species rates.</param>
    /// <param name="upperMask">Per face, all bits set if the upwind cell is the higher-coordinate one.</param>
    /// <param name="lowerBoundary">
    ///     Whether the row is the lower boundary of the bundle. This chunk's cell is the higher-coordinate one there.
    /// </param>
    /// <param name="source">The gas row of this chunk's read buffer.</param>
    /// <param name="neighborSource">
    ///     The gas row of the neighbour's read buffer, or <see langword="null" /> if the neighbour has none. A null
    ///     row reads as <c>+0</c>, which is the value the scalar fix-up reads for it.
    /// </param>
    /// <param name="flux">The per-face species flux to write.</param>
    /// <param name="firstFace">The first face of the row.</param>
    /// <param name="selfFirst">The voxel of the first cell of this chunk's row.</param>
    /// <param name="neighborFirst">The voxel of the first facing cell in the neighbour's buffer.</param>
    /// <param name="length">Number of faces. It must be at least one vector.</param>
    /// <remarks>
    ///     Each lane is <c>rate · cell</c> for the cell the scalar fix-up would pick, so the bits match. The last
    ///     vector overlaps the one before it when the length is not a multiple of the vector width.
    /// </remarks>
    public static void EdgeFluxRow(
        PerSecond[] rate, int[] upperMask, bool lowerBoundary, Mole[] source, Mole[]? neighborSource,
        MolePerSecond[] flux, int firstFace, int selfFirst, int neighborFirst, int length)
    {
        int width = Vector<float>.Count;
        int start = 0;
        while (true)
        {
            Vector<Mole> self = Load(source, selfFirst + start);
            Vector<Mole> neighbor =
                neighborSource == null ? Vector<float>.Zero : Load(neighborSource, neighborFirst + start);
            var mask = Load(upperMask, firstFace + start);
            Vector<Mole> upwind = lowerBoundary
                ? Vector.ConditionalSelect(mask, self, neighbor)
                : Vector.ConditionalSelect(mask, neighbor, self);
            (Load(rate, firstFace + start) * upwind).StoreAt(flux, firstFace + start);
            if (start + width >= length)
                return;

            start = Math.Min(start + width, length - width);
        }
    }

    private static void FaceFluxVector(
        PerSecond[] rate, int[] upperMask, Mole[] source, MolePerSecond[] flux, int face, int lowerVoxel,
        int upperOffset)
    {
        Vector<Mole> lower = Load(source, lowerVoxel);
        Vector<Mole> upper = Load(source, lowerVoxel + upperOffset);
        Vector<Mole> upwind = Vector.ConditionalSelect(Load(upperMask, face), upper, lower);
        (Load(rate, face) * upwind).StoreAt(flux, face);
    }

    /// <summary>
    ///     Updates a run of air cells for every gas of the bundle and adds each gas to the cell sums in turn.
    /// </summary>
    /// <param name="passes">The arrays and constants of each gas, in ascending gas order.</param>
    /// <param name="gasCount">Number of gases in <paramref name="passes" /> to update.</param>
    /// <param name="firstVoxel">The voxel of the first cell. The run is contiguous in voxel memory.</param>
    /// <param name="firstCell">The scratch index of the first cell.</param>
    /// <param name="firstBehind">The face behind the first cell.</param>
    /// <param name="firstAhead">The face ahead of the first cell. Face distance between neighbours is one.</param>
    /// <param name="length">Number of cells. Every cell must be air.</param>
    /// <param name="negatives">The pencil's count of negative moles.</param>
    /// <remarks>
    ///     <para>
    ///         Each lane adds its gases in ascending order, as <see cref="UpdateCell" /> does when it runs once per
    ///         gas. The sums stay in registers until the last gas, so a vector reads and writes each sum once per run
    ///         and not once per gas. The repair count and the non-finite flag do not depend on the order. They are
    ///         collected the same way and applied after the last gas.
    ///     </para>
    ///     <para>
    ///         Cells past the last whole vector go through <see cref="UpdateCell" />, one cell at a time over all
    ///         gases. A cell's result does not depend on which cells share its vector.
    ///     </para>
    /// </remarks>
    public static void UpdateRunGases(
        EulerSpeciesPass[] passes, int gasCount, int firstVoxel, int firstCell, int firstBehind, int firstAhead,
        int length, ref int negatives)
    {
        if (gasCount == 0)
            return;

        int i = 0;
        if (Vector.IsHardwareAccelerated && length >= Vector<float>.Count)
        {
            int width = Vector<float>.Count;
            int half = Vector<double>.Count;
            var zero = Vector<float>.Zero;
            var infinity = new Vector<float>(float.PositiveInfinity);
            for (; i + width <= length; i += width)
            {
                int cell = firstCell + i;
                Vector<Kilogram> massSum = Load(passes[0].MassSum, cell);
                Vector<Mole> moleSum = Load(passes[0].MoleSum, cell);
                Vector<JoulePerKelvin64> heatLower = Load(passes[0].HeatSum, cell);
                Vector<JoulePerKelvin64> heatUpper = Load(passes[0].HeatSum, cell + half);
                var negativeCount = Vector<int>.Zero;
                var anyNonFinite = Vector<int>.Zero;
                for (int gas = 0; gas < gasCount; gas++)
                {
                    ref readonly EulerSpeciesPass pass = ref passes[gas];
                    Vector<MolePerSecond> ahead = Load(pass.FaceFlux, firstAhead + i);
                    Vector<MolePerSecond> behind = Load(pass.FaceFlux, firstBehind + i);
                    Vector<Mole> moles =
                        Load(pass.Source, firstVoxel + i) - new Vector<float>(pass.TimeStep) * (ahead - behind);

                    Vector<int> finite = Vector.LessThan(Vector.Abs(moles), infinity);
                    Vector<int> negative = Vector.AndNot(Vector.LessThan(moles, zero), ~finite);
                    anyNonFinite |= ~finite;
                    negativeCount += negative;
                    moles = Vector.ConditionalSelect(Vector.AndNot(finite, negative), moles, zero);
                    moles.StoreAt(pass.Target, firstVoxel + i);

                    massSum += moles * new Vector<float>(pass.MolarMass);
                    moleSum += moles;
                    Vector<JoulePerMoleKelvin64> molarHeat = new Vector<double>(pass.MolarHeat);
                    heatLower += Vector.WidenLower(moles) * molarHeat;
                    heatUpper += Vector.WidenUpper(moles) * molarHeat;
                }

                ref readonly EulerSpeciesPass sums = ref passes[0];
                massSum.StoreAt(sums.MassSum, cell);
                moleSum.StoreAt(sums.MoleSum, cell);
                heatLower.StoreAt(sums.HeatSum, cell);
                heatUpper.StoreAt(sums.HeatSum, cell + half);

                // Each negative lane is -1, so the lane sum is minus the count.
                negatives -= Vector.Sum(negativeCount);
                if (!Vector.EqualsAll(anyNonFinite, Vector<int>.Zero))
                {
                    for (int lane = 0; lane < width; lane++)
                    {
                        if (anyNonFinite[lane] != 0)
                            sums.NonFinite[cell + lane] = true;
                    }
                }
            }
        }

        for (; i < length; i++)
        {
            for (int gas = 0; gas < gasCount; gas++)
            {
                UpdateCell(
                    in passes[gas], firstVoxel + i, firstCell + i, firstBehind + i, firstAhead + i, ref negatives);
            }
        }
    }

    /// <summary>
    ///     Updates one air cell of one gas and adds it to the cell sums. It defines the result of every lane.
    /// </summary>
    /// <remarks>
    ///     A non-finite result becomes zero and flags the cell. NaN fails the floor's comparison, so the finite
    ///     check has to come first. A finite negative result becomes zero and counts as a repair. A result of
    ///     <c>-0f</c> is not negative and stays <c>-0f</c>.
    /// </remarks>
    public static void UpdateCell(
        in EulerSpeciesPass pass, int voxel, int cell, int behind, int ahead, ref int negatives)
    {
        Mole moles = pass.Source[voxel] - pass.TimeStep * (pass.FaceFlux[ahead] - pass.FaceFlux[behind]);
        if (!float.IsFinite(moles))
        {
            pass.NonFinite[cell] = true;
            moles = 0f;
        }
        else if (moles < 0f)
        {
            negatives++;
            moles = 0f;
        }

        pass.Target[voxel] = moles;
        pass.MassSum[cell] += moles * pass.MolarMass;
        pass.MoleSum[cell] += moles;
        pass.HeatSum[cell] += (double)moles * pass.MolarHeat;
    }

    private static Vector<T> Load<T>(T[] array, int index)
    {
        return new Vector<T>(array, index);
    }

    private static void StoreAt<T>(this Vector<T> value, T[] array, int index)
    {
        value.CopyTo(array, index);
    }
}
