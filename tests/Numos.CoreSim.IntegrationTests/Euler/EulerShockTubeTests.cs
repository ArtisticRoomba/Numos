using Numos.API;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.CoreSim.EulerFlow;

namespace Numos.CoreSim.IntegrationTests.Euler;

/// <summary>
///     Sod's shock tube (Toro test 1) on the Euler stage, plus the symmetry properties the sweep promises.
/// </summary>
/// <remarks>
///     Toro's data is <c>(ρ, u, p) = (1, 0, 1) | (0.125, 0, 0.1)</c> at <c>t = 0.25</c> in a unit domain, with waves
///     that cover about 70% of it. The gas here is a 1 kg/mol species in 1 m³ voxels, so moles per voxel are
///     density in kg/m³, and the pressures are scaled to <c>10⁴ | 10³ Pa</c>: that makes <c>c_L = 118 m/s</c>, so
///     five ticks (0.25 s) in a 100 m tube reproduce Toro's wave geometry cell for cell. At <c>10⁵ Pa</c> the
///     shock would leave the tube in under 0.1 s.
/// </remarks>
[TestFixture]
public sealed class EulerShockTubeTests
{
    private const int Length = 100;
    private const int Diaphragm = 50;
    private const int Ticks = 5;
    private const double Gamma = 1.4;
    private const float LeftDensity = 1f;
    private const float LeftPressure = 1e4f;
    private const float RightDensity = 0.125f;
    private const float RightPressure = 1e3f;

    [Test]
    public void SodShockTube_MatchesTheExactRiemannSolution()
    {
        var config = SimTestHelpers.CreateEulerConfig();
        using var simulation = SimTestHelpers.CreateEulerSimulation(config, Length, 1, 1, out var chunk);
        FillSod(simulation, chunk, (x, y, z) => (x, y, z), mirrored: false);
        var initial = simulation.GetChunkSnapshot(chunk);
        double initialMoles = SimTestHelpers.TotalMoles(initial);
        double initialEnergy = SimTestHelpers.TotalEulerEnergy(config, initial);

        var exact = new ExactRiemannSolver(LeftDensity, 0d, LeftPressure, RightDensity, 0d, RightPressure, Gamma);
        double time = 0d;
        for (int tick = 0; tick < Ticks; tick++)
        {
            simulation.Tick();
            AtmosFlowDiagnostics flow = simulation.LastFlowDiagnostics;
            time += flow.SimulatedSeconds;
            TestContext.Out.WriteLine(
                $"tick {tick}: substeps={flow.Substeps} simulated={flow.SimulatedSeconds:R} courant={flow.MaxCourantNumber:F3} " +
                $"lambda={flow.MaxSignalSpeed:F1} clamps={flow.ClampedVelocityCount} negE={flow.NegativeInternalEnergyCount} negN={flow.NegativeMolesCount}");

            Assert.That(flow.SubstepCapEngaged, Is.False);
            Assert.That(flow.ClampedVelocityCount, Is.Zero);
            Assert.That(flow.NegativeMolesCount, Is.Zero);
        }

        var snapshot = simulation.GetChunkSnapshot(chunk);
        TestContext.Out.WriteLine(
            $"exact: p*={exact.StarPressure:F2} u*={exact.StarVelocity:F3} rho*L={exact.StarDensityLeft:F5} rho*R={exact.StarDensityRight:F5} " +
            $"head={Diaphragm + exact.LeftHeadSpeed * time:F1} tail={Diaphragm + exact.LeftTailSpeed * time:F1} " +
            $"contact={Diaphragm + exact.StarVelocity * time:F1} shock={Diaphragm + exact.RightHeadSpeed * time:F1}");

        // First-order Godunov smears the contact over about ten cells at this resolution and the shock and
        // rarefaction tail over four or five, so the plateau checks stay clear of those. The L1 norm covers
        // the whole tube.
        const double contactMargin = 10d;
        const double waveMargin = 5d;
        double tail = Diaphragm + exact.LeftTailSpeed * time;
        double contact = Diaphragm + exact.StarVelocity * time;
        double shock = Diaphragm + exact.RightHeadSpeed * time;
        double densityError = 0d;
        for (int x = 0; x < Length; x++)
        {
            double centre = x + 0.5d;
            var (density, velocity, pressure) = exact.Sample((centre - Diaphragm) / time);
            float moles = SimTestHelpers.Moles(snapshot, SimTestHelpers.EulerUnitGasId, x);
            densityError += Math.Abs(moles - density);
            TestContext.Out.WriteLine(
                $"x={x,3}: rho={moles:F5} ({density:F5}) u={snapshot.VelocityX[x],8:F3} ({velocity,8:F3}) " +
                $"p={snapshot.TotalPressure[x],9:F2} ({pressure,9:F2})");
        }

        TestContext.Out.WriteLine($"L1 density error={densityError:F4} over {Length} cells");

        int starCells = 0;
        int leftStarCells = 0;
        int rightStarCells = 0;
        Assert.Multiple(() =>
        {
            for (int x = 0; x < Length; x++)
            {
                double centre = x + 0.5d;
                var (_, velocity, pressure) = exact.Sample((centre - Diaphragm) / time);
                float moles = SimTestHelpers.Moles(snapshot, SimTestHelpers.EulerUnitGasId, x);

                if (centre > tail + waveMargin && centre < shock - waveMargin)
                {
                    starCells++;
                    Assert.That(snapshot.TotalPressure[x], Is.EqualTo(pressure).Within(2).Percent, $"p at x={x}");
                    Assert.That(snapshot.VelocityX[x], Is.EqualTo(velocity).Within(2).Percent, $"u at x={x}");
                }

                if (centre > tail + waveMargin && centre < contact - contactMargin)
                {
                    leftStarCells++;
                    Assert.That(moles, Is.EqualTo(exact.StarDensityLeft).Within(3).Percent, $"rho*L at x={x}");
                }
                else if (centre > contact + contactMargin && centre < shock - waveMargin)
                {
                    rightStarCells++;
                    Assert.That(moles, Is.EqualTo(exact.StarDensityRight).Within(3).Percent, $"rho*R at x={x}");
                }
            }
        });

        TestContext.Out.WriteLine($"star cells={starCells} left={leftStarCells} right={rightStarCells}");

        Assert.Multiple(() =>
        {
            Assert.That(starCells, Is.GreaterThan(20));
            Assert.That(leftStarCells, Is.GreaterThan(4));
            Assert.That(rightStarCells, Is.GreaterThan(4));
            Assert.That(densityError, Is.LessThan(0.03d * Length));
            Assert.That(SimTestHelpers.TotalMoles(snapshot), Is.EqualTo(initialMoles).Within(1e-6).Percent);
            Assert.That(
                SimTestHelpers.TotalEulerEnergy(config, snapshot),
                Is.EqualTo(initialEnergy).Within(1e-4).Percent);
        });
    }

    [TestCase(1)]
    [TestCase(2)]
    public void SodShockTube_IsIdenticalAlongEveryAxis(int axis)
    {
        var config = SimTestHelpers.CreateEulerConfig();
        using var reference = SimTestHelpers.CreateEulerSimulation(config, Length, 1, 1, out var referenceChunk);
        FillSod(reference, referenceChunk, (x, y, z) => (x, y, z), mirrored: false);

        (int, int, int) dimensions = axis == 1 ? (1, Length, 1) : (1, 1, Length);
        using var rotated = SimTestHelpers.CreateEulerSimulation(
            config, dimensions.Item1, dimensions.Item2, dimensions.Item3, out var rotatedChunk);

        FillSod(rotated, rotatedChunk, (x, y, z) => axis == 1 ? (y, x, z) : (z, y, x), mirrored: false);

        for (int tick = 0; tick < Ticks; tick++)
        {
            reference.Tick();
            rotated.Tick();
        }

        var expected = reference.GetChunkSnapshot(referenceChunk);
        var actual = rotated.GetChunkSnapshot(rotatedChunk);
        Assert.That(reference.LastFlowDiagnostics, Is.EqualTo(rotated.LastFlowDiagnostics));
        Assert.Multiple(() =>
        {
            for (int i = 0; i < Length; i++)
            {
                // A pencil along Y or Z has the same flat index sequence as one along X.
                AssertSameBits(
                    SimTestHelpers.Moles(expected, SimTestHelpers.EulerUnitGasId, i),
                    SimTestHelpers.Moles(actual, SimTestHelpers.EulerUnitGasId, i), $"moles at {i}");
                AssertSameBits(expected.TotalPressure[i], actual.TotalPressure[i], $"pressure at {i}");
                AssertSameBits(expected.Temperature[i], actual.Temperature[i], $"temperature at {i}");
                AssertSameBits(
                    expected.VelocityX[i],
                    axis == 1 ? actual.VelocityY[i] : actual.VelocityZ[i], $"normal velocity at {i}");
                Assert.That(actual.VelocityX[i], Is.Zero, $"tangential velocity at {i}");
            }
        });
    }

    [Test]
    public void SodShockTube_MirrorsBitwise()
    {
        var config = SimTestHelpers.CreateEulerConfig();
        using var forward = SimTestHelpers.CreateEulerSimulation(config, Length, 1, 1, out var forwardChunk);
        FillSod(forward, forwardChunk, (x, y, z) => (x, y, z), mirrored: false);
        using var mirrored = SimTestHelpers.CreateEulerSimulation(config, Length, 1, 1, out var mirroredChunk);
        FillSod(mirrored, mirroredChunk, (x, y, z) => (x, y, z), mirrored: true);

        for (int tick = 0; tick < Ticks; tick++)
        {
            forward.Tick();
            mirrored.Tick();
        }

        var expected = forward.GetChunkSnapshot(forwardChunk);
        var actual = mirrored.GetChunkSnapshot(mirroredChunk);
        Assert.Multiple(() =>
        {
            for (int x = 0; x < Length; x++)
            {
                int m = Length - 1 - x;
                AssertSameBits(
                    SimTestHelpers.Moles(expected, SimTestHelpers.EulerUnitGasId, x),
                    SimTestHelpers.Moles(actual, SimTestHelpers.EulerUnitGasId, m), $"moles at {x}");
                AssertSameBits(expected.TotalPressure[x], actual.TotalPressure[m], $"pressure at {x}");
                AssertSameBits(expected.Temperature[x], actual.Temperature[m], $"temperature at {x}");
                AssertSameBits(expected.VelocityX[x], -actual.VelocityX[m], $"velocity at {x}");
            }
        });
    }

    [Test]
    public void SubstepCap_CutsTheTickShortAndReportsIt()
    {
        var config = SimTestHelpers.CreateEulerConfig(new EulerFlowConfig { MaxSubstepsPerTick = 2 });
        using var simulation = SimTestHelpers.CreateEulerSimulation(config, Length, 1, 1, out var chunk);
        FillSod(simulation, chunk, (x, y, z) => (x, y, z), mirrored: false);

        simulation.Tick();

        AtmosFlowDiagnostics flow = simulation.LastFlowDiagnostics;
        TestContext.Out.WriteLine($"capped: {flow}");
        Assert.Multiple(() =>
        {
            Assert.That(flow.SubstepCapEngaged, Is.True);
            Assert.That(flow.Substeps, Is.EqualTo(2));
            Assert.That(flow.SimulatedSeconds, Is.LessThan(0.05d));
        });
    }

    [Test]
    public void NoSubstepCap_SimulatesTheWholeTickWithinTheCourantNumber()
    {
        var config = SimTestHelpers.CreateEulerConfig(new EulerFlowConfig { MaxSubstepsPerTick = 0, CourantNumber = 0.5f });
        using var simulation = SimTestHelpers.CreateEulerSimulation(config, Length, 1, 1, out var chunk);
        FillSod(simulation, chunk, (x, y, z) => (x, y, z), mirrored: false);

        simulation.Tick();
        AtmosFlowDiagnostics first = simulation.LastFlowDiagnostics;
        simulation.Tick();
        AtmosFlowDiagnostics second = simulation.LastFlowDiagnostics;
        TestContext.Out.WriteLine($"first: {first}");
        TestContext.Out.WriteLine($"second: {second}");

        double fixedTimeStep = AtmosSolverConstants.FixedTimeStep;
        Assert.Multiple(() =>
        {
            Assert.That(first.SubstepCapEngaged, Is.False);
            Assert.That(first.SimulatedSeconds, Is.EqualTo(fixedTimeStep).Within(1e-12));
            Assert.That(second.SimulatedSeconds, Is.EqualTo(fixedTimeStep).Within(1e-12));
            Assert.That(first.Substeps, Is.GreaterThan(1));
            // The very first substep starts from the discontinuity, where the star state is faster than either
            // initial state, so its after-the-fact Courant number overshoots. The fan is established by tick two.
            Assert.That(first.MaxCourantNumber, Is.LessThan(1f));
            Assert.That(second.MaxCourantNumber, Is.LessThanOrEqualTo(0.5f * 1.02f));
        });
    }

    /// <summary>
    ///     Fills a tube with Sod's data. <paramref name="map" /> turns tube coordinates into chunk coordinates
    ///     for the rotated variants. <paramref name="mirrored" /> puts the high-pressure side at high x.
    /// </summary>
    private static void FillSod(
        AtmosSimulation simulation, AtmosChunkHandle chunk,
        Func<int, int, int, (int X, int Y, int Z)> map, bool mirrored)
    {
        float leftTemperature = LeftPressure / (LeftDensity * AtmosPhysicalConstants.MolarGasConstant);
        float rightTemperature = RightPressure / (RightDensity * AtmosPhysicalConstants.MolarGasConstant);
        for (int i = 0; i < Length; i++)
        {
            int tubeX = mirrored ? Length - 1 - i : i;
            bool left = tubeX < Diaphragm;
            var (x, y, z) = map(i, 0, 0);
            simulation.AddGasToVoxel(
                chunk, x, y, z, SimTestHelpers.EulerUnitGasName,
                left ? LeftDensity : RightDensity,
                left ? leftTemperature : rightTemperature);
        }
    }

    /// <summary>
    ///     Bitwise equality, except that the two zeros count as equal: a product with a zero factor takes its sign
    ///     from the other factors, so a mirrored run can legitimately land on the other zero.
    /// </summary>
    private static void AssertSameBits(float expected, float actual, string message)
    {
        if (expected == 0f && actual == 0f)
            return;

        Assert.That(
            BitConverter.SingleToInt32Bits(actual),
            Is.EqualTo(BitConverter.SingleToInt32Bits(expected)),
            $"{message}: expected {expected:R}, actual {actual:R}");
    }
}
