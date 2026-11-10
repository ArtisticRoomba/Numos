using Numos.API;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.Maths;

namespace Numos.CoreSim.IntegrationTests;

[TestFixture]
public sealed class VacuumCleanupStageTests
{
    /// <summary>
    ///     Transport stages leave sub-threshold gas in place. Only the <c>vacuum-cleanup</c> stage removes it.
    /// </summary>
    [TestCase(AtmosTransportModel.Legacy, true, 0f)]
    [TestCase(AtmosTransportModel.Legacy, false, 0.003f)]
    [TestCase(AtmosTransportModel.Euler, true, 0f)]
    [TestCase(AtmosTransportModel.Euler, false, 0.003f)]
    public void VacuumCleanup_DisablingTheStageKeepsSubThresholdGas(
        AtmosTransportModel model, bool cleanupEnabled, float expectedMoles)
    {
        var config = SimTestHelpers.CreateDeterministicConfig();
        config.VacuumThreshold = 1f;
        using var simulation = new AtmosSimulation(config, 1, 1, 1, model, SimTestHelpers.PinnedTickRate);
        Assert.That(simulation.World.Solvers.SetEnabled(AtmosBuiltInSolvers.VacuumCleanup, cleanupEnabled), Is.True);
        var chunk = SimTestHelpers.CreateOpenChunk(simulation, new Int3(0, 0, 0));
        simulation.SetVoxelTemperature(chunk, 0, 0, 0, 300f);
        simulation.AddGasToVoxel(chunk, 0, 0, 0, SimTestHelpers.FirstGasName, 0.003f, 300f);

        simulation.Tick();

        var snapshot = simulation.GetChunkSnapshot(chunk);
        Assert.That(
            SimTestHelpers.Moles(snapshot, SimTestHelpers.FirstGasId, 0),
            Is.EqualTo(expectedMoles).Within(SimTestHelpers.Tolerance));
    }

    /// <summary>
    ///     A transport stage can put a chunk to sleep before <c>vacuum-cleanup</c> runs. The cleanup still covers
    ///     that chunk once, without waking it, and advances its revision when it removes gas.
    /// </summary>
    /// <remarks>
    ///     At 960 Hz <c>euler-flow</c> settles every 48 ticks and <c>vacuum-cleanup</c> runs on the same ticks after
    ///     it. The legacy <c>advection</c> stage also runs right before the cleanup. A short sleep delay makes the
    ///     first sleep decision put the uniform chunk to sleep.
    /// </remarks>
    [TestCase(AtmosTransportModel.Legacy)]
    [TestCase(AtmosTransportModel.Euler)]
    public void ChunkThatFallsAsleepBeforeCleanup_IsStillCleaned(AtmosTransportModel model)
    {
        using var simulation = CreateSubThresholdChunk(model, out var chunk);

        // Records the chunk as the cleanup is about to see it.
        bool? asleepBeforeCleanup = null;
        float molesBeforeCleanup = 0f;
        AtmosChunkVersion versionBeforeCleanup = default;
        var world = simulation.World;
        world.Solvers.Register(
            "test/before-cleanup",
            _ =>
            {
                var snapshot = simulation.GetChunkSnapshot(chunk);
                asleepBeforeCleanup = !snapshot.IsAwake;
                molesBeforeCleanup = SimTestHelpers.TotalMoles(snapshot);
                versionBeforeCleanup = snapshot.Version;
            },
            before: AtmosBuiltInSolvers.VacuumCleanup,
            interval: world.Solvers.Steps.Single(static step => step.Name == AtmosBuiltInSolvers.VacuumCleanup).Interval);

        AtmosChunkSnapshot after;
        int tick = 0;
        do
        {
            simulation.Tick();
            tick++;
            after = simulation.GetChunkSnapshot(chunk);
        } while (asleepBeforeCleanup != true && tick < 960);

        Assert.Multiple(() =>
        {
            Assert.That(asleepBeforeCleanup, Is.True, "the chunk falls asleep before a cleanup run");
            Assert.That(molesBeforeCleanup, Is.GreaterThan(0f), "gas left when the chunk fell asleep");
            Assert.That(SimTestHelpers.TotalMoles(after), Is.Zero, "the cleanup emptied the sleeping chunk");
            Assert.That(after.IsAwake, Is.False, "the cleanup does not wake the chunk");
            Assert.That(after.Version, Is.Not.EqualTo(versionBeforeCleanup), "the cleanup advanced the revision");
        });
    }

    /// <summary>
    ///     The sleep timer that tells <c>vacuum-cleanup</c> a chunk fell asleep is checkpointed. A world restored
    ///     between the sleep decision and the cleanup run reaches the same state as one that never restored.
    /// </summary>
    [TestCase(AtmosTransportModel.Legacy)]
    [TestCase(AtmosTransportModel.Euler)]
    public void RestoreBetweenSleepAndCleanup_MatchesUnbrokenRun(AtmosTransportModel model)
    {
        using var simulation = CreateSubThresholdChunk(model, out var chunk);
        var world = simulation.World;

        // The transport stage decides sleep on tick 48. The cleanup first runs on tick 96.
        world.Solvers.SetInterval(AtmosBuiltInSolvers.VacuumCleanup, 96);
        TickTo(simulation, 60);
        var asleep = simulation.GetChunkSnapshot(chunk);
        var midway = world.CaptureCheckpoint();
        TickTo(simulation, 100);
        var unbroken = world.ComputeStateHash();
        var cleaned = simulation.GetChunkSnapshot(chunk);

        world.RestoreCheckpoint(midway);
        TickTo(simulation, 100);

        Assert.Multiple(() =>
        {
            Assert.That(asleep.IsAwake, Is.False, "asleep at the checkpoint");
            Assert.That(SimTestHelpers.TotalMoles(asleep), Is.GreaterThan(0f), "gas at the checkpoint");
            Assert.That(SimTestHelpers.TotalMoles(cleaned), Is.Zero, "emptied by the cleanup run");
            Assert.That(world.ComputeStateHash(), Is.EqualTo(unbroken), "state after the restore");
        });
    }

    /// <summary>
    ///     A uniform 4 x 4 chunk below the vacuum threshold at 960 Hz with a short sleep delay. The first sleep
    ///     decision, on tick 48, puts it to sleep.
    /// </summary>
    private static AtmosSimulation CreateSubThresholdChunk(AtmosTransportModel model, out AtmosChunkHandle chunk)
    {
        const int size = 4;
        const float temperature = 300f;
        var config = model == AtmosTransportModel.Euler
            ? SimTestHelpers.CreateEulerConfig()
            : SimTestHelpers.CreateDeterministicConfig();
        float moles = model == AtmosTransportModel.Euler ? SimTestHelpers.MolesFor(config, 50f, temperature) : 0.1f;
        config.SleepDelay = 0.01f;
        config.SleepEpsilon = 101f;
        config.VacuumThreshold = 1e6f;
        var simulation = new AtmosSimulation(config, size, size, 1, model, 960);
        chunk = SimTestHelpers.CreateOpenChunk(simulation, default);
        string gas = model == AtmosTransportModel.Euler ? SimTestHelpers.EulerAirGasName : SimTestHelpers.FirstGasName;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            simulation.SetVoxelTemperature(chunk, x, y, 0, temperature);
            simulation.AddGasToVoxel(chunk, x, y, 0, gas, moles, temperature);
        }

        return simulation;
    }

    private static void TickTo(AtmosSimulation simulation, long tick)
    {
        while (simulation.TickCount < tick)
            simulation.Tick();
    }
}
