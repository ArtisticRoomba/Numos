using Numos.API;
using Numos.CoreSim;
using Numos.CoreSim.State;
using Numos.Maths;
using Numos.Tests;

namespace Numos.Testing.Tests;

/// <summary>
///     Per-simulation state that grows every tick.
/// </summary>
[SimulationComponent("tests/replay-check/ledger")]
internal sealed partial class LedgerComponent
{
    [NumosSerialize] public float Scale = 1f;
    [NumosSerialize(HostEditable = false)] public readonly List<float> Entries = [];
}

/// <summary>
///     Appends a reading to each simulation's ledger, scaled by a setting the host changes between ticks.
/// </summary>
internal sealed class LedgerSolver : IAtmosWorldSolver
{
    public void Solve(AtmosWorldSolverContext context)
    {
        foreach (var simulation in context.Simulations)
        {
            var ledger = context.Get<LedgerComponent>(simulation);
            ledger.Entries.Add(ledger.Scale * (ledger.Entries.Count + 1));
        }
    }
}

[TestFixture]
public sealed class AtmosReplayCheckComponentTests
{
    [Test]
    public void SolverThatGrowsAListInAComponent_PassesEveryCheck()
    {
        var result = AtmosReplayCheck.Run(
            static () =>
            {
                var world = new AtmosWorld(new TestAtmosConfig());
                var simulation = world.CreateSimulation(2, 2, 1);
                SimSetup.CreateOpenChunk(simulation, Int3.Zero);
                world.CreateSimulation(1, 1, 1);
                world.Solvers.Register("tests/replay-check/ledger", new LedgerSolver());
                return world;
            },
            static (world, tick) =>
            {
                if (tick % 11 == 0)
                    world.Simulations[(int)(tick % 2)].GetComponent<LedgerComponent>().Scale = 1f + tick % 3;
            },
            90);

        Assert.Multiple(() =>
        {
            Assert.That(result.Passed, Is.True, () => string.Join("\n", result.Failures));
            Assert.That(result.Skipped, Is.Empty);
        });
    }
}
