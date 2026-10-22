using System.Numerics;

namespace Numos.CoreSim.Solvers.Euler;

/// <summary>
///     The arrays and per-gas constants of one gas in the species pass of a bundle.
/// </summary>
/// <remarks>
///     The arrays are raw <see cref="float" /> and <see cref="double" /> arrays because the kernel works on
///     <see cref="Vector{T}" /> lanes, which carry no units.
/// </remarks>
internal readonly struct EulerSpeciesPass
{
    public EulerSpeciesPass(
        float[] source, float[] target, float[] faceFlux, bool[] nonFinite,
        float[] massSum, float[] moleSum, double[] heatSum,
        float timeStep, float molarMass, float molarHeat)
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

    public readonly float[] Source;
    public readonly float[] Target;
    public readonly float[] FaceFlux;
    public readonly bool[] NonFinite;
    public readonly float[] MassSum;
    public readonly float[] MoleSum;
    public readonly double[] HeatSum;
    public readonly float TimeStep;
    public readonly float MolarMass;
    public readonly float MolarHeat;
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
        float[] rate, int[] upperMask, float[] source, float[] flux,
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

    private static void FaceFluxVector(
        float[] rate, int[] upperMask, float[] source, float[] flux, int face, int lowerVoxel, int upperOffset)
    {
        var lower = Load(source, lowerVoxel);
        var upper = Load(source, lowerVoxel + upperOffset);
        var upwind = Vector.ConditionalSelect(Load(upperMask, face), upper, lower);
        (Load(rate, face) * upwind).StoreAt(flux, face);
    }

    /// <summary>
    ///     Updates a run of air cells of one gas and adds them to the cell sums.
    /// </summary>
    /// <param name="pass">The arrays and constants of the gas.</param>
    /// <param name="firstVoxel">The voxel of the first cell. The run is contiguous in voxel memory.</param>
    /// <param name="firstCell">The scratch index of the first cell.</param>
    /// <param name="firstBehind">The face behind the first cell.</param>
    /// <param name="firstAhead">The face ahead of the first cell. Face distance between neighbours is one.</param>
    /// <param name="length">Number of cells. Every cell must be air.</param>
    /// <param name="negatives">The pencil's count of negative moles.</param>
    public static void UpdateRun(
        in EulerSpeciesPass pass, int firstVoxel, int firstCell, int firstBehind, int firstAhead, int length,
        ref int negatives)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated && length >= Vector<float>.Count)
        {
            int width = Vector<float>.Count;
            var timeStep = new Vector<float>(pass.TimeStep);
            var molarMass = new Vector<float>(pass.MolarMass);
            var molarHeat = new Vector<double>(pass.MolarHeat);
            var zero = Vector<float>.Zero;
            var infinity = new Vector<float>(float.PositiveInfinity);
            for (; i + width <= length; i += width)
            {
                var ahead = Load(pass.FaceFlux, firstAhead + i);
                var behind = Load(pass.FaceFlux, firstBehind + i);
                var moles = Load(pass.Source, firstVoxel + i) - timeStep * (ahead - behind);

                Vector<int> finite = Vector.LessThan(Vector.Abs(moles), infinity);
                Vector<int> negative = Vector.AndNot(Vector.LessThan(moles, zero), ~finite);
                if (!Vector.EqualsAll(finite, Vector<int>.AllBitsSet))
                {
                    for (int lane = 0; lane < width; lane++)
                    {
                        if (finite[lane] == 0)
                            pass.NonFinite[firstCell + i + lane] = true;
                    }
                }

                // Each negative lane is -1, so the lane sum is minus the count.
                negatives -= Vector.Sum(negative);
                moles = Vector.ConditionalSelect(Vector.AndNot(finite, negative), moles, zero);
                moles.StoreAt(pass.Target, firstVoxel + i);

                (Load(pass.MassSum, firstCell + i) + moles * molarMass).StoreAt(pass.MassSum, firstCell + i);
                (Load(pass.MoleSum, firstCell + i) + moles).StoreAt(pass.MoleSum, firstCell + i);

                int half = Vector<double>.Count;
                var heatLower = Load(pass.HeatSum, firstCell + i);
                var heatUpper = Load(pass.HeatSum, firstCell + i + half);
                (heatLower + Vector.WidenLower(moles) * molarHeat).StoreAt(pass.HeatSum, firstCell + i);
                (heatUpper + Vector.WidenUpper(moles) * molarHeat).StoreAt(pass.HeatSum, firstCell + i + half);
            }
        }

        for (; i < length; i++)
            UpdateCell(in pass, firstVoxel + i, firstCell + i, firstBehind + i, firstAhead + i, ref negatives);
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
        float moles = pass.Source[voxel] - pass.TimeStep * (pass.FaceFlux[ahead] - pass.FaceFlux[behind]);
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
