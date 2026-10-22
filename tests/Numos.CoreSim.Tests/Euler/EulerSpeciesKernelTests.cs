using Numos.CoreSim.Solvers.Euler;

namespace Numos.CoreSim.Tests.Euler;

public class EulerSpeciesKernelTests
{
    private const int Pad = 3;

    [Test]
    public void UpdateRun_MatchesTheScalarCellUpdateBitForBit([Range(1, 37)] int length)
    {
        var random = new Random(1234 + length);
        int total = length + 2 * Pad;
        float[] source = new float[total];
        float[] flux = new float[total + 1 + Pad];
        for (int i = 0; i < source.Length; i++)
            source[i] = i % 11 == 4 ? -0f : (float)random.NextDouble();

        for (int i = 0; i < flux.Length; i++)
        {
            flux[i] = i % 13 switch
            {
                3 => float.NaN,
                5 => float.PositiveInfinity,
                7 => float.NegativeInfinity,
                _ => (float)(random.NextDouble() - 0.5) * 2f
            };
        }

        Run(source, flux, length, out var vector, out int vectorNegatives);
        Run(source, flux, length, out var scalar, out int scalarNegatives, forceScalar: true);

        Assert.Multiple(() =>
        {
            Assert.That(vectorNegatives, Is.EqualTo(scalarNegatives));
            Assert.That(Bits(vector.Target), Is.EqualTo(Bits(scalar.Target)));
            Assert.That(Bits(vector.MassSum), Is.EqualTo(Bits(scalar.MassSum)));
            Assert.That(Bits(vector.MoleSum), Is.EqualTo(Bits(scalar.MoleSum)));
            Assert.That(vector.HeatSum.Select(BitConverter.DoubleToInt64Bits),
                Is.EqualTo(scalar.HeatSum.Select(BitConverter.DoubleToInt64Bits)));
            Assert.That(vector.NonFinite, Is.EqualTo(scalar.NonFinite));
        });
    }

    [Test]
    public void UpdateRun_RepairsNonFiniteAndNegativeCellsAndKeepsNegativeZero()
    {
        const int length = 12;
        float[] source = new float[length];
        float[] flux = new float[length + 1];
        Array.Fill(source, 1f);
        source[0] = -0f;
        flux[3] = 0.5f;
        flux[4] = 0f;
        flux[2] = float.NaN;
        flux[8] = float.PositiveInfinity;

        Run(source, flux, length, firstVoxel: 0, firstBehind: 0, firstAhead: 1, out var result, out int negatives);

        Assert.Multiple(() =>
        {
            // Face 2 is NaN, so cells 1 and 2 are non-finite. Face 8 is +inf, so cells 7 and 8 are.
            Assert.That(result.NonFinite.Where(f => f).Count(), Is.EqualTo(4));
            Assert.That(
                new[] { 1, 2, 7, 8 }.All(cell => result.NonFinite[cell] && result.Target[cell] == 0f), Is.True);

            // Cell 3 has behind face 0.5 and ahead face 0, so it gains 0.5.
            Assert.That(result.Target[3], Is.EqualTo(1.5f));
            // A cell that stays at -0f is not negative and is not repaired.
            Assert.That(BitConverter.SingleToInt32Bits(result.Target[0]), Is.EqualTo(BitConverter.SingleToInt32Bits(-0f)));
            Assert.That(negatives, Is.Zero);
        });
    }

    [Test]
    public void UpdateRun_CountsAStrongOutflowAsOneNegativeRepairPerCell()
    {
        const int length = 16;
        float[] source = new float[length];
        float[] flux = new float[length + 1];
        Array.Fill(source, 0.1f);
        for (int i = 0; i <= length; i++)
            flux[i] = i % 2 == 0 ? 0f : 5f;

        Run(source, flux, length, firstVoxel: 0, firstBehind: 0, firstAhead: 1, out var result, out int negatives);

        // Odd faces carry 5, so even cells lose 5 (ahead face odd) and odd cells gain it.
        Assert.Multiple(() =>
        {
            Assert.That(negatives, Is.EqualTo(8));
            for (int cell = 0; cell < length; cell++)
            {
                if (cell % 2 == 0)
                    Assert.That(result.Target[cell], Is.Zero);
                else
                    Assert.That(result.Target[cell], Is.EqualTo(5.1f).Within(1e-5f));
            }
        });
    }

    [Test]
    public void FaceFluxRun_MatchesTheScalarUpwindProduct([Range(1, 37)] int length)
    {
        var random = new Random(99 + length);
        int total = length + 1;
        float[] source = new float[total];
        for (int i = 0; i < total; i++)
            source[i] = (float)random.NextDouble();

        float[] rate = new float[length];
        int[] mask = new int[length];
        for (int i = 0; i < length; i++)
        {
            rate[i] = (float)(random.NextDouble() - 0.5);
            mask[i] = random.Next(2) == 0 ? 0 : -1;
        }

        float[] flux = new float[length];
        EulerSpeciesKernel.FaceFluxRun(rate, mask, source, flux, 0, 0, 1, length);

        for (int i = 0; i < length; i++)
        {
            float expected = rate[i] * source[mask[i] == 0 ? i : i + 1];
            Assert.That(BitConverter.SingleToInt32Bits(flux[i]), Is.EqualTo(BitConverter.SingleToInt32Bits(expected)), $"face {i}");
        }
    }

    private static void Run(
        float[] source, float[] flux, int length, out EulerSpeciesPassResult result, out int negatives,
        bool forceScalar = false)
    {
        Run(source, flux, length, Pad, Pad, Pad + 1, out result, out negatives, forceScalar);
    }

    private static void Run(
        float[] source, float[] flux, int length, int firstVoxel, int firstBehind, int firstAhead,
        out EulerSpeciesPassResult result, out int negatives, bool forceScalar = false)
    {
        int cells = length + 2 * Pad;
        result = new EulerSpeciesPassResult(source.Length, cells);
        for (int i = 0; i < cells; i++)
        {
            result.MassSum[i] = 0.25f * i;
            result.MoleSum[i] = 0.5f * i;
            result.HeatSum[i] = 0.125 * i;
        }

        var pass = new EulerSpeciesPass(
            source, result.Target, flux, result.NonFinite, result.MassSum, result.MoleSum, result.HeatSum,
            1f, 0.02897f, 20.8f);

        negatives = 0;
        if (forceScalar)
        {
            for (int i = 0; i < length; i++)
                EulerSpeciesKernel.UpdateCell(in pass, firstVoxel + i, firstVoxel + i, firstBehind + i, firstAhead + i, ref negatives);
        }
        else
        {
            EulerSpeciesKernel.UpdateRun(in pass, firstVoxel, firstVoxel, firstBehind, firstAhead, length, ref negatives);
        }
    }

    private static IEnumerable<int> Bits(float[] values)
    {
        return values.Select(BitConverter.SingleToInt32Bits);
    }

    private sealed class EulerSpeciesPassResult
    {
        public EulerSpeciesPassResult(int voxels, int cells)
        {
            Target = new float[voxels];
            MassSum = new float[cells];
            MoleSum = new float[cells];
            HeatSum = new double[cells];
            NonFinite = new bool[cells];
        }

        public float[] Target { get; }
        public float[] MassSum { get; }
        public float[] MoleSum { get; }
        public double[] HeatSum { get; }
        public bool[] NonFinite { get; }
    }
}
