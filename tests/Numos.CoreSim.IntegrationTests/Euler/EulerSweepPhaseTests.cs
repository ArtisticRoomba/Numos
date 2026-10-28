using Numos.API;
using Numos.CoreSim.EulerFlow;
using Numos.CoreSim.Solvers.Euler;

namespace Numos.CoreSim.IntegrationTests.Euler;

/// <summary>
///     The Euler sweep order follows a checkpointed cycle that advances once per substep, so a run of one-substep
///     ticks still visits every order and a restored world continues the cycle.
/// </summary>
[TestFixture]
public sealed class EulerSweepPhaseTests
{
    [Test]
    public void NextSweepPhase_WalksAllSixOrdersAndWraps()
    {
        int phase = 0;
        var seen = new List<int>();
        for (int step = 0; step < 12; step++)
        {
            seen.Add(phase);
            phase = EulerFlowSolver.NextSweepPhase(phase);
        }

        Assert.That(seen, Is.EqualTo(new[] { 0, 1, 2, 3, 4, 5, 0, 1, 2, 3, 4, 5 }));
    }

    [Test]
    public void OneSubstepPerCall_AdvancesThePhaseThroughEveryOrder()
    {
        var config = SimTestHelpers.CreateEulerConfig(new EulerFlowConfig { MaxSubstepsPerSecond = 0 });
        using var simulation = CreateRoom(config);
        var phases = new List<int>();
        for (int tick = 0; tick < 12; tick++)
        {
            phases.Add(simulation.World.CaptureCheckpoint().Simulations[0].Checkpoint.EulerSweepPhase);
            simulation.Tick();
        }

        Assert.That(phases.Distinct().Count(), Is.EqualTo(6));
        Assert.That(phases.Take(6), Is.EqualTo(phases.Skip(6)));
    }

    [Test]
    public void RestoreMidCycle_ContinuesBitwise()
    {
        var config = SimTestHelpers.CreateEulerConfig(new EulerFlowConfig { MaxSubstepsPerSecond = 0 });
        using var reference = CreateRoom(config);
        using var restored = CreateRoom(config);
        for (int tick = 0; tick < 8; tick++)
        {
            reference.World.Tick();
            restored.World.Tick();
        }

        var checkpoint = restored.World.CaptureCheckpoint();
        Assert.That(checkpoint.Simulations[0].Checkpoint.EulerSweepPhase, Is.Not.Zero);
        for (int tick = 0; tick < 5; tick++)
            restored.World.Tick();

        restored.World.RestoreCheckpoint(checkpoint);
        Assert.That(restored.World.ComputeStateHash(), Is.EqualTo(reference.World.ComputeStateHash()));
        for (int tick = 0; tick < 30; tick++)
        {
            reference.World.Tick();
            restored.World.Tick();
            Assert.That(restored.World.ComputeStateHash(), Is.EqualTo(reference.World.ComputeStateHash()), $"tick {tick + 1}");
        }
    }

    private static AtmosSimulation CreateRoom(AtmosConfig config)
    {
        var simulation = SimTestHelpers.CreateEulerSimulation(config, 8, 8, 8, out var chunk, tickRate: 960);
        // Every 1 ms call takes one substep, which is the case these tests need.
        simulation.World.Solvers.SetInterval(AtmosBuiltInSolvers.EulerFlow, 1);
        for (int x = 0; x < 8; x++)
        for (int y = 0; y < 8; y++)
        for (int z = 0; z < 8; z++)
        {
            float moles = SimTestHelpers.MolesFor(config, 101325f + 40f * x + 25f * y + 10f * z, 293f);
            simulation.AddGasToVoxel(chunk, x, y, z, SimTestHelpers.EulerAirGasName, moles, 293f);
        }

        return simulation;
    }
}
