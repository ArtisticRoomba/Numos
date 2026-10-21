using Numos.API;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.CoreSim.EulerFlow;

namespace Numos.CoreSim.IntegrationTests.Euler;

/// <summary>
///     Uniform air driven into a wall. The reflected shock's plateau pressure is an exact Rankine–Hugoniot result,
///     so it checks the wall flux and the conservative update together.
/// </summary>
/// <remarks>
///     A gas at Mach <c>M₀</c> stopped by a wall sends back a shock whose Mach number <c>M_s</c> relative to the
///     oncoming gas solves <c>u₀ = 2c₀/(γ+1) · (M_s − 1/M_s)</c>, and the pressure behind it is
///     <c>p₂/p₁ = 1 + 2γ/(γ+1) · (M_s² − 1)</c>. For <c>M₀ = 0.1</c> that is 1.1486; for 1 m/s it is within rounding
///     of the acoustic value <c>p₁ + ρ₁c₁u₀ = p₁ + 413 Pa</c>.
/// </remarks>
[TestFixture]
public sealed class EulerWallReflectionTests
{
    private const int Length = 60;
    private const float Temperature = 293f;
    private const float Pressure = AtmosPhysicalConstants.StandardAtmosphericPressure;

    [Test]
    public void ReflectedShock_HasTheRankineHugoniotPlateau()
    {
        var config = SimTestHelpers.CreateEulerConfig();
        using var simulation = SimTestHelpers.CreateEulerSimulation(config, Length, 1, 1, out var chunk);
        float moles = SimTestHelpers.MolesFor(config, Pressure, Temperature);
        float speed = 0.1f * SoundSpeed(moles);
        Fill(simulation, chunk, moles, speed);
        var initial = simulation.GetChunkSnapshot(chunk);
        double initialEnergy = SimTestHelpers.TotalEulerEnergy(config, initial);
        double initialMomentum = TotalMomentum(initial);

        simulation.Tick();
        AtmosFlowDiagnostics flow = simulation.LastFlowDiagnostics;
        TestContext.Out.WriteLine($"tick 1: {flow}");
        var afterOneTick = simulation.GetChunkSnapshot(chunk);
        double expectedRatio = ReflectedShockPressureRatio(0.1d);
        Assert.Multiple(() =>
        {
            Assert.That(flow.SubstepCapEngaged, Is.False);
            for (int x = Length - 6; x <= Length - 3; x++)
            {
                double ratio = afterOneTick.TotalPressure[x] / initial.TotalPressure[x];
                TestContext.Out.WriteLine($"x={x}: p/p0={ratio:F5} u={afterOneTick.VelocityX[x]:F3}");
                Assert.That(ratio, Is.EqualTo(expectedRatio).Within(1).Percent, $"plateau at x={x}");
            }

            // The shock front must have moved well past the plateau cells for them to be a plateau at all.
            Assert.That(afterOneTick.TotalPressure[Length - 10], Is.GreaterThan(initial.TotalPressure[Length - 10] * 1.05f));
            Assert.That(
                SimTestHelpers.TotalEulerEnergy(config, afterOneTick),
                Is.EqualTo(initialEnergy).Within(1e-4).Percent);
        });

        double smallestMomentum = Math.Abs(initialMomentum);
        for (int tick = 2; tick <= 8; tick++)
        {
            simulation.Tick();
            var snapshot = simulation.GetChunkSnapshot(chunk);
            double momentum = TotalMomentum(snapshot);
            smallestMomentum = Math.Min(smallestMomentum, Math.Abs(momentum));
            TestContext.Out.WriteLine(
                $"tick {tick}: momentum={momentum:F2} ({momentum / initialMomentum:P1} of initial) " +
                $"energy drift={(SimTestHelpers.TotalEulerEnergy(config, snapshot) / initialEnergy - 1d):E2}");

            Assert.That(
                SimTestHelpers.TotalEulerEnergy(config, snapshot),
                Is.EqualTo(initialEnergy).Within(1e-4).Percent, $"energy after tick {tick}");
        }

        Assert.That(smallestMomentum, Is.LessThan(0.2d * Math.Abs(initialMomentum)));
    }

    [Test]
    public void WeakPulse_HasTheAcousticPlateau()
    {
        var config = SimTestHelpers.CreateEulerConfig();
        using var simulation = SimTestHelpers.CreateEulerSimulation(config, Length, 1, 1, out var chunk);
        float moles = SimTestHelpers.MolesFor(config, Pressure, Temperature);
        Fill(simulation, chunk, moles, 1f);
        var initial = simulation.GetChunkSnapshot(chunk);

        simulation.Tick();

        var snapshot = simulation.GetChunkSnapshot(chunk);
        float density = moles * SimTestHelpers.EulerAirMolarMass;
        double expectedRise = density * SoundSpeed(moles) * 1d;
        Assert.Multiple(() =>
        {
            for (int x = Length - 6; x <= Length - 3; x++)
            {
                double rise = snapshot.TotalPressure[x] - initial.TotalPressure[x];
                TestContext.Out.WriteLine($"x={x}: rise={rise:F2} Pa (acoustic {expectedRise:F2})");
                Assert.That(rise, Is.EqualTo(expectedRise).Within(2).Percent, $"plateau at x={x}");
            }
        });
    }

    private static void Fill(AtmosSimulation simulation, AtmosChunkHandle chunk, float moles, float speed)
    {
        float momentum = moles * SimTestHelpers.EulerAirMolarMass * speed;
        for (int x = 0; x < Length; x++)
        {
            simulation.AddGasToVoxel(chunk, x, 0, 0, SimTestHelpers.EulerAirGasName, moles, Temperature);
            simulation.SetVoxelMomentum(chunk, x, 0, 0, momentum, 0f, 0f);
        }
    }

    private static float SoundSpeed(float moles)
    {
        const float gamma = 1.4f;
        float mass = moles * SimTestHelpers.EulerAirMolarMass;
        return MathF.Sqrt(gamma * Pressure / mass);
    }

    private static double TotalMomentum(AtmosChunkSnapshot snapshot)
    {
        double total = 0d;
        for (int x = 0; x < Length; x++)
        {
            double mass = SimTestHelpers.Moles(snapshot, SimTestHelpers.EulerAirGasId, x) * SimTestHelpers.EulerAirMolarMass;
            total += mass * snapshot.VelocityX[x];
        }

        return total;
    }

    private static double ReflectedShockPressureRatio(double mach)
    {
        const double gamma = 1.4d;
        // u₀/c₀ = 2/(γ+1)·(M_s − 1/M_s) is a quadratic in M_s.
        double k = mach * (gamma + 1d) / 2d;
        double shockMach = 0.5d * (k + Math.Sqrt(k * k + 4d));
        return 1d + 2d * gamma / (gamma + 1d) * (shockMach * shockMach - 1d);
    }
}
