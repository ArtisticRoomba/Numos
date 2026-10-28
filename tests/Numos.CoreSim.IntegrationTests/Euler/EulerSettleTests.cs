using Numos.API;
using Numos.CoreSim.EulerFlow;
using Numos.CoreSim.Solvers.Euler;

namespace Numos.CoreSim.IntegrationTests.Euler;

/// <summary>
///     The Euler stage settles on a period of simulated time, not on every call, so a 1 ms tick still accumulates
///     slow flow and still replays bit for bit.
/// </summary>
/// <remarks>
///     The rooms here run <c>euler-flow</c> on every 960 Hz tick, because 1 ms calls are the case under test.
/// </remarks>
[TestFixture]
public sealed class EulerSettleTests
{
    private const int Voxels = 16;
    private const int HighRate = 960;
    private const float Pressure = 101325f;
    private const float PascalPerVoxel = 0.5f;

    [TestCase(0.05f, true)]
    [TestCase(0f, false)]
    public void SmallGradientAtHighRate_BuildsMomentumOnlyWhenTheFloorWaitsForTheSettlePeriod(
        float settlePeriod, bool expectMotion)
    {
        var config = SimTestHelpers.CreateEulerConfig(new EulerFlowConfig { SettlePeriod = settlePeriod });
        using var simulation = CreateGradientRoom(config, out var chunk);

        // The gradient drives a standing wave, so the speed at the end of the run depends on the wave's phase.
        // The peak over the run does not.
        float peak = 0f;
        for (int tick = 0; tick < HighRate / 10; tick++)
        {
            simulation.Tick();
            peak = Math.Max(peak, simulation.GetChunkSnapshot(chunk).VelocityX.Max(static velocity => Math.Abs(velocity)));
        }

        TestContext.Out.WriteLine($"peak speed in 0.1 s: {peak:G4} m/s");
        if (expectMotion)
            Assert.That(peak, Is.GreaterThan(0.004f));
        else
            Assert.That(peak, Is.Zero);
    }

    [TestCase(1280, 20, 1, 64)]
    [TestCase(1280, 960, 1, 2)]
    [TestCase(1280, 960, 48, 64)]
    [TestCase(1280, 40, 1, 32)]
    [TestCase(1, 960, 1, 1)]
    [TestCase(0, 960, 1, 0)]
    public void SubstepCap_ScalesWithTheCallsSpan(int perSecond, int tickRate, int interval, int expected)
    {
        Assert.That(EulerFlowSolver.SubstepCap(perSecond, interval, tickRate), Is.EqualTo(expected));
    }

    [TestCase(0.05f, 20, 1)]
    [TestCase(0.05f, 960, 48)]
    [TestCase(0.05f, 40, 2)]
    [TestCase(0f, 960, 1)]
    [TestCase(0.0001f, 960, 1)]
    public void SettleThreshold_RoundsThePeriodToTicks(float period, int tickRate, int expected)
    {
        Assert.That(EulerFlowSolver.SettleThresholdTicks(period, tickRate), Is.EqualTo(expected));
    }

    [Test]
    public void SubstepCapAtHighRate_CutsEachCallAtTwoSubsteps()
    {
        var config = SimTestHelpers.CreateEulerConfig(new EulerFlowConfig());
        using var simulation = SimTestHelpers.CreateEulerSimulation(
            config, Voxels, 1, 1, out var chunk, tickRate: HighRate);
        simulation.World.Solvers.SetInterval(AtmosBuiltInSolvers.EulerFlow, 1);
        float moles = SimTestHelpers.MolesFor(config, Pressure, 293f);
        // A 100x pressure step is a Mach 5 shock, which needs far more than two substeps in 1 ms.
        for (int x = 0; x < Voxels; x++)
        {
            simulation.AddGasToVoxel(
                chunk, x, 0, 0, SimTestHelpers.EulerAirGasName, x < Voxels / 2 ? 100f * moles : moles, 293f);
        }

        simulation.Tick();

        var flow = simulation.LastFlowDiagnostics;
        Assert.Multiple(() =>
        {
            Assert.That(flow.Substeps, Is.LessThanOrEqualTo(2));
            Assert.That(flow.Substeps, Is.GreaterThan(0));
        });
    }

    [Test]
    public void RestoreMidPeriod_ContinuesTheSettleScheduleBitwise()
    {
        using var reference = CreateMovingRoom(out _);
        using var restored = CreateMovingRoom(out _);
        for (int tick = 0; tick < 30; tick++)
        {
            reference.World.Tick();
            restored.World.Tick();
        }

        var checkpoint = restored.World.CaptureCheckpoint();
        for (int tick = 0; tick < 40; tick++)
            restored.World.Tick();

        restored.World.RestoreCheckpoint(checkpoint);
        Assert.That(restored.World.ComputeStateHash(), Is.EqualTo(reference.World.ComputeStateHash()));
        for (int tick = 0; tick < 120; tick++)
        {
            reference.World.Tick();
            restored.World.Tick();
            Assert.That(restored.World.ComputeStateHash(), Is.EqualTo(reference.World.ComputeStateHash()), $"tick {tick + 1}");
        }
    }

    [Test]
    public void Checkpoint_HoldsTheSettleCount()
    {
        using var early = CreateMovingRoom(out _);
        using var late = CreateMovingRoom(out _);
        for (int tick = 0; tick < 10; tick++)
            early.World.Tick();

        for (int tick = 0; tick < 11; tick++)
            late.World.Tick();

        Assert.That(early.World.CaptureCheckpoint().Simulations[0].Checkpoint.EulerSettleTicks, Is.EqualTo(10));
        Assert.That(late.World.CaptureCheckpoint().Simulations[0].Checkpoint.EulerSettleTicks, Is.EqualTo(11));
    }

    private static AtmosSimulation CreateGradientRoom(AtmosConfig config, out AtmosChunkHandle chunk)
    {
        var simulation = SimTestHelpers.CreateEulerSimulation(config, Voxels, 1, 1, out chunk, tickRate: HighRate);
        simulation.World.Solvers.SetInterval(AtmosBuiltInSolvers.EulerFlow, 1);
        for (int x = 0; x < Voxels; x++)
        {
            float moles = SimTestHelpers.MolesFor(config, Pressure + PascalPerVoxel * x, 293f);
            simulation.AddGasToVoxel(chunk, x, 0, 0, SimTestHelpers.EulerAirGasName, moles, 293f);
        }

        return simulation;
    }

    private static AtmosSimulation CreateMovingRoom(out AtmosChunkHandle chunk)
    {
        var config = SimTestHelpers.CreateEulerConfig(new EulerFlowConfig { MaxSubstepsPerSecond = 0 });
        var simulation = CreateGradientRoom(config, out chunk);
        simulation.AddGasToVoxel(chunk, 3, 0, 0, SimTestHelpers.EulerAirGasName, 0.5f, 600f);
        return simulation;
    }
}
