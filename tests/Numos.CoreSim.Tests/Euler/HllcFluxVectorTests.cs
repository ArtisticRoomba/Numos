using System.Numerics;
using Numos.CoreSim.Solvers.Euler;

namespace Numos.CoreSim.Tests.Euler;

[NonParallelizable]
public class HllcFluxVectorTests
{
    private const int Pairs = 400_000;

    private static readonly float[] SpecialValues =
    [
        0f, -0f, 1f, -1f, 0.5f, 2f, 343f, -343f, 1e-30f, -1e-30f, 1e30f, -1e30f, float.Epsilon, -float.Epsilon,
        1.1754942e-38f, float.MaxValue, float.MinValue, float.PositiveInfinity, float.NegativeInfinity, float.NaN
    ];

    private static float RandomBits(Random random)
    {
        return BitConverter.Int32BitsToSingle(random.Next(int.MinValue, int.MaxValue));
    }

    private static float LogUniform(Random random, double low, double high)
    {
        return (float)Math.Exp(Math.Log(low) + random.NextDouble() * (Math.Log(high) - Math.Log(low)));
    }

    private static float Signed(Random random, float magnitude)
    {
        return random.Next(2) == 0 ? magnitude : -magnitude;
    }

    private static EulerFaceState Physical(Random random)
    {
        float density = LogUniform(random, 1e-6, 1e3);
        float pressure = LogUniform(random, 1e-3, 1e8);
        float gamma = 1.1f + (float)random.NextDouble() * 0.6f;
        float sound = MathF.Sqrt(gamma * pressure / density);
        float scale = random.Next(4) switch
        {
            0 => 0f,
            1 => 0.01f,
            2 => 1f,
            _ => 4f
        };

        EulerFaceState state;
        state.Density = density;
        state.NormalVelocity = Signed(random, scale * sound * (float)random.NextDouble());
        state.TangentialVelocity1 = random.Next(3) == 0 ? 0f : Signed(random, scale * sound * (float)random.NextDouble());
        state.TangentialVelocity2 = random.Next(3) == 0 ? -0f : Signed(random, scale * sound * (float)random.NextDouble());
        state.Pressure = pressure;
        float speedSquared = state.NormalVelocity * state.NormalVelocity +
                             state.TangentialVelocity1 * state.TangentialVelocity1 +
                             state.TangentialVelocity2 * state.TangentialVelocity2;
        state.EnergyDensity = pressure / (gamma - 1f) + 0.5f * density * speedSquared;
        state.SoundSpeed = sound;
        state.InverseMass = 1f / (density * LogUniform(random, 0.01, 100));
        return state;
    }

    private static EulerFaceState Special(Random random)
    {
        EulerFaceState state;
        state.Density = Pick(random);
        state.NormalVelocity = Pick(random);
        state.TangentialVelocity1 = Pick(random);
        state.TangentialVelocity2 = Pick(random);
        state.Pressure = Pick(random);
        state.EnergyDensity = Pick(random);
        state.SoundSpeed = Pick(random);
        state.InverseMass = Pick(random);
        return state;
    }

    private static float Pick(Random random)
    {
        return random.Next(3) == 0 ? RandomBits(random) : SpecialValues[random.Next(SpecialValues.Length)];
    }

    private static EulerFaceState RandomState(Random random, int mode)
    {
        switch (mode)
        {
            case 0:
                return default;
            case 1:
                return Special(random);
            case 2:
            {
                EulerFaceState state = Physical(random);
                state.NormalVelocity = Signed(random, state.SoundSpeed * (2f + 3f * (float)random.NextDouble()));
                return state;
            }
            case 3:
            {
                EulerFaceState state = Physical(random);
                state.Density *= LogUniform(random, 1e-20, 1e20);
                return state;
            }
            case 4:
            {
                // Fully random bits in every field.
                EulerFaceState state;
                state.Density = RandomBits(random);
                state.NormalVelocity = RandomBits(random);
                state.TangentialVelocity1 = RandomBits(random);
                state.TangentialVelocity2 = RandomBits(random);
                state.Pressure = RandomBits(random);
                state.EnergyDensity = RandomBits(random);
                state.SoundSpeed = RandomBits(random);
                state.InverseMass = RandomBits(random);
                return state;
            }
            default:
                return Physical(random);
        }
    }

    private static void MakePair(Random random, out EulerFaceState left, out EulerFaceState right)
    {
        left = RandomState(random, random.Next(12));
        switch (random.Next(8))
        {
            case 0:
                // Equal states: a gas at rest, or a uniform flow.
                right = left;
                break;
            case 1:
                // The mirror image of the other side.
                right = left;
                right.NormalVelocity = -left.NormalVelocity;
                break;
            case 2:
                // A vacuum next to anything.
                right = default;
                if (random.Next(2) == 0)
                    (left, right) = (right, left);
                break;
            case 3:
                // Same pressure, different density, both at rest.
                right = Physical(random);
                left = right;
                left.Density *= LogUniform(random, 0.1, 10);
                left.NormalVelocity = 0f;
                right.NormalVelocity = random.Next(2) == 0 ? 0f : -0f;
                break;
            default:
                right = RandomState(random, random.Next(12));
                break;
        }
    }

    private static HllcLanes Pack(EulerFaceState[] states, int first)
    {
        int width = Vector<float>.Count;
        var density = new float[width];
        var normal = new float[width];
        var tangential1 = new float[width];
        var tangential2 = new float[width];
        var pressure = new float[width];
        var energy = new float[width];
        var sound = new float[width];
        var inverseMass = new float[width];
        for (int lane = 0; lane < width; lane++)
        {
            EulerFaceState state = states[first + lane];
            density[lane] = state.Density;
            normal[lane] = state.NormalVelocity;
            tangential1[lane] = state.TangentialVelocity1;
            tangential2[lane] = state.TangentialVelocity2;
            pressure[lane] = state.Pressure;
            energy[lane] = state.EnergyDensity;
            sound[lane] = state.SoundSpeed;
            inverseMass[lane] = state.InverseMass;
        }

        return new HllcLanes(
            new Vector<float>(density), new Vector<float>(normal), new Vector<float>(tangential1),
            new Vector<float>(tangential2), new Vector<float>(pressure), new Vector<float>(energy),
            new Vector<float>(sound), new Vector<float>(inverseMass));
    }

    /// <summary>
    ///     Bitwise equality, except that any two NaNs match. An operation on a NaN returns one of its NaN operands, and
    ///     which payload comes back depends on the operand order the compiler picks.
    /// </summary>
    private static bool Same(float expected, float actual)
    {
        if (float.IsNaN(expected))
            return float.IsNaN(actual);

        return BitConverter.SingleToInt32Bits(expected) == BitConverter.SingleToInt32Bits(actual);
    }

    [Test]
    public void LaneKernel_MatchesScalarSolveBitForBit()
    {
        Assume.That(Vector.IsHardwareAccelerated, "No vector hardware: the sweep keeps the scalar solver.");
        int width = Vector<float>.Count;
        var random = new Random(20261024);
        int total = Pairs / width * width;
        var lefts = new EulerFaceState[total];
        var rights = new EulerFaceState[total];
        for (int i = 0; i < total; i++)
            MakePair(random, out lefts[i], out rights[i]);

        var mass = new float[width];
        var normal = new float[width];
        var tangential1 = new float[width];
        var tangential2 = new float[width];
        var energy = new float[width];
        var species = new float[width];
        var upwind = new int[width];
        int failures = 0;
        for (int first = 0; first < total; first += width)
        {
            HllcLaneFlux lanes = HllcFluxVector.Solve(Pack(lefts, first), Pack(rights, first));
            lanes.Mass.CopyTo(mass);
            lanes.NormalMomentum.CopyTo(normal);
            lanes.Tangential1Momentum.CopyTo(tangential1);
            lanes.Tangential2Momentum.CopyTo(tangential2);
            lanes.Energy.CopyTo(energy);
            lanes.SpeciesWeight.CopyTo(species);
            lanes.UpwindIsLeft.CopyTo(upwind);
            for (int lane = 0; lane < width; lane++)
            {
                HllcFlux.Solve(lefts[first + lane], rights[first + lane], allMach: false, out EulerFaceFlux expected);
                bool match = Same(expected.Mass, mass[lane]) &&
                             Same(expected.NormalMomentum, normal[lane]) &&
                             Same(expected.Tangential1Momentum, tangential1[lane]) &&
                             Same(expected.Tangential2Momentum, tangential2[lane]) &&
                             Same(expected.Energy, energy[lane]) &&
                             Same(expected.SpeciesWeight, species[lane]) &&
                             expected.UpwindIsLeft == (upwind[lane] != 0);
                if (!match && failures++ < 5)
                {
                    TestContext.Out.WriteLine(
                        $"pair {first + lane}: expected mass {expected.Mass} normal {expected.NormalMomentum} " +
                        $"t1 {expected.Tangential1Momentum} t2 {expected.Tangential2Momentum} " +
                        $"energy {expected.Energy} weight {expected.SpeciesWeight} left {expected.UpwindIsLeft}, " +
                        $"got mass {mass[lane]} normal {normal[lane]} t1 {tangential1[lane]} t2 {tangential2[lane]} " +
                        $"energy {energy[lane]} weight {species[lane]} left {upwind[lane] != 0}");
                }
            }
        }

        Assert.That(failures, Is.Zero, $"{failures} of {total} faces differ from the scalar solver");
    }

    [Test]
    public void LaneKernel_HandlesSignedZerosAndRestExactly()
    {
        Assume.That(Vector.IsHardwareAccelerated, "No vector hardware: the sweep keeps the scalar solver.");
        int width = Vector<float>.Count;
        var random = new Random(7);
        var lefts = new EulerFaceState[width];
        var rights = new EulerFaceState[width];
        for (int lane = 0; lane < width; lane++)
        {
            EulerFaceState state = Physical(random);
            state.NormalVelocity = lane % 2 == 0 ? 0f : -0f;
            lefts[lane] = state;
            rights[lane] = state;
        }

        HllcLaneFlux flux = HllcFluxVector.Solve(Pack(lefts, 0), Pack(rights, 0));
        var mass = new float[width];
        flux.Mass.CopyTo(mass);
        for (int lane = 0; lane < width; lane++)
        {
            HllcFlux.Solve(lefts[lane], rights[lane], allMach: false, out EulerFaceFlux expected);
            Assert.That(BitConverter.SingleToInt32Bits(mass[lane]), Is.EqualTo(BitConverter.SingleToInt32Bits(expected.Mass)));
        }
    }

    [Test]
    public void MinAndMax_MatchMathFForNaNAndSignedZeros()
    {
        Assume.That(Vector.IsHardwareAccelerated, "No vector hardware.");
        int width = Vector<float>.Count;
        var random = new Random(3);
        var a = new float[width];
        var b = new float[width];
        var low = new float[width];
        var high = new float[width];
        for (int round = 0; round < 20000; round++)
        {
            for (int lane = 0; lane < width; lane++)
            {
                a[lane] = Pick(random);
                b[lane] = Pick(random);
            }

            Vector.Min(new Vector<float>(a), new Vector<float>(b)).CopyTo(low);
            Vector.Max(new Vector<float>(a), new Vector<float>(b)).CopyTo(high);
            for (int lane = 0; lane < width; lane++)
            {
                Assert.That(Same(MathF.Min(a[lane], b[lane]), low[lane]), Is.True, $"min({a[lane]}, {b[lane]})");
                Assert.That(Same(MathF.Max(a[lane], b[lane]), high[lane]), Is.True, $"max({a[lane]}, {b[lane]})");
            }
        }
    }

    [Test]
    public void DoubleMax_MatchesMathMaxForNaNAndSignedZeros()
    {
        Assume.That(Vector.IsHardwareAccelerated, "No vector hardware.");
        int width = Vector<double>.Count;
        var random = new Random(5);
        var a = new double[width];
        var b = new double[width];
        var high = new double[width];
        for (int round = 0; round < 20000; round++)
        {
            for (int lane = 0; lane < width; lane++)
            {
                a[lane] = random.Next(4) == 0 ? 0d : (double)Pick(random);
                b[lane] = random.Next(4) == 0 ? -0d : (double)Pick(random);
            }

            Vector.Max(new Vector<double>(a), new Vector<double>(b)).CopyTo(high);
            for (int lane = 0; lane < width; lane++)
            {
                double expected = Math.Max(a[lane], b[lane]);
                bool same = double.IsNaN(expected)
                    ? double.IsNaN(high[lane])
                    : BitConverter.DoubleToInt64Bits(expected) == BitConverter.DoubleToInt64Bits(high[lane]);
                Assert.That(same, Is.True, $"max({a[lane]}, {b[lane]})");
            }
        }
    }
}
