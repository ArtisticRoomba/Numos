using System.Reflection;
using Numos.CoreSim;
using Numos.CoreSim.Replay;

namespace Numos.API.Tests;

[TestFixture]
public sealed class AtmosWorldLongTickTests
{
    // A multiple of every built-in stage interval at 960 Hz, 127 ticks below int.MaxValue.
    private const long GridTick = 2_147_483_520L;

    [Test]
    public void Tick_ContinuesPastInt32MaxValue()
    {
        using var world = new AtmosWorld();
        world.CreateSimulation(1, 1, 1);
        world.ReplayTo(ShiftedCheckpoint(world), [], new AtmosTimelinePosition((ulong)GridTick, 0));

        TickMany(world, 300);

        Assert.Multiple(() =>
        {
            Assert.That(world.TickCount, Is.EqualTo(GridTick + 300));
            Assert.That(world.TickCount, Is.GreaterThan(int.MaxValue));
            Assert.That(world.SimulatedTime, Is.EqualTo((GridTick + 300) / (double)world.TickRate));
            Assert.That(world.Simulations.Single().TickCount, Is.EqualTo(GridTick + 300));
            Assert.That(world.CaptureCheckpoint().TickCount, Is.EqualTo(GridTick + 300));
        });
    }

    [Test]
    public void Recording_ReplaysAcrossInt32MaxValue()
    {
        using var world = new AtmosWorld();
        world.CreateSimulation(1, 1, 1);
        var start = ShiftedCheckpoint(world);
        world.ReplayTo(start, [], new AtmosTimelinePosition((ulong)GridTick, 0));

        world.StartRecording();
        TickMany(world, 100);
        world.Solvers.SetEnabled(AtmosBuiltInSolvers.EulerFlow, false);
        TickMany(world, 100);
        var recording = world.StopRecording();
        var expected = world.ComputeStateHash();

        world.ReplayTo(start, recording.Operations, recording.Head);

        Assert.Multiple(() =>
        {
            Assert.That(recording.Head.Tick, Is.GreaterThan((ulong)int.MaxValue));
            Assert.That(world.TickCount, Is.EqualTo(GridTick + 200));
            Assert.That(world.ComputeStateHash(), Is.EqualTo(expected));
        });
    }

    private static void TickMany(AtmosWorld world, int count)
    {
        for (int index = 0; index < count; index++)
            world.Tick();
    }

    // The checkpoint has no public constructor, so move a captured one to the target tick through its backing fields.
    private static AtmosWorldCheckpoint ShiftedCheckpoint(AtmosWorld world)
    {
        var checkpoint = world.CaptureCheckpoint();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var position = typeof(AtmosWorldCheckpoint).GetField("__numosPosition", flags)!;
        var solvers = typeof(AtmosWorldCheckpoint).GetField("__numosSolvers", flags)!;

        var oldPosition = (AtmosTimelinePosition)position.GetValue(checkpoint)!;
        position.SetValue(checkpoint, oldPosition with { Tick = (ulong)GridTick });
        var shifted = checkpoint.Solvers
            .Select(static solver => solver with
            {
                LastRunTick = solver.LastRunTick + GridTick,
                NextDueTick = solver.NextDueTick + GridTick
            })
            .ToArray();
        solvers.SetValue(checkpoint, shifted);
        return checkpoint;
    }
}
