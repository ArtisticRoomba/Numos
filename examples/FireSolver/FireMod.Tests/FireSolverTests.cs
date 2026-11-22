using Numos.API;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.Serialization;
using Numos.Testing;

namespace FireMod.Tests;

public sealed class FireSolverTests
{
    /// <summary>
    /// Float rounding in the burn's energy balance, in kelvins.
    /// </summary>
    private const float FlameTolerance = 0.01f;

    [Test]
    public void ReplayCheck_PassesEveryCheck()
    {
        AtmosReplayCheckResult result = AtmosReplayCheck.Run(FireStation.Create, FireStation.Script, FireStation.Ticks);

        result.ThrowIfFailed();
        Assert.That(result.Skipped, Is.Empty);
    }

    [Test]
    public void Burns_ReachTheHostAsEvents()
    {
        using AtmosWorld world = FireStation.Create();
        var burns = new List<FireBurned>();
        using IDisposable subscription = world.Events.Subscribe((in FireBurned burn) => burns.Add(burn));

        for (long tick = 1; tick <= FireStation.Ticks; tick++)
        {
            FireStation.Script(world, tick);
            world.Tick();
        }

        AtmosSimulation station = world.Simulations[0];
        AtmosCellRef ignited = station.GetCellRef(FireStation.ColdChunk, FireStation.CenterVoxel);
        Assert.Multiple(() =>
        {
            Assert.That(burns.Select(static burn => burn.Cell), Does.Contain(ignited));
            Assert.That(burns.Select(static burn => burn.Cell.Chunk), Does.Contain(FireStation.HotChunk));
            Assert.That(station.GetComponent<FireLog>().BurntCells, Is.EqualTo(burns.Select(static burn => burn.Cell)));
            Assert.That(BurnCount(station, FireStation.HotChunk) + BurnCount(station, FireStation.ColdChunk),
                Is.EqualTo(burns.Count));
        });
    }

    [Test]
    public void SavedReplay_ImportsIntoAWorldThatRegisteredNothing()
    {
        AtmosWorldReplayArchive archive;
        AtmosWorldStateHash head;
        using (AtmosWorld world = FireStation.Create())
        {
            AtmosWorldCheckpoint start = world.CaptureCheckpoint();
            world.StartRecording();
            for (long tick = 1; tick <= FireStation.Ticks; tick++)
            {
                FireStation.Script(world, tick);
                world.Tick();
            }

            AtmosWorldRecording recording = world.StopRecording();
            head = world.ComputeStateHash();
            archive = new AtmosWorldReplayArchive(start, recording, start.ComputeStateHash(), head);
        }

        using var file = new MemoryStream();
        var metadata = new NumosReplayMetadata("Fire", DateTimeOffset.UnixEpoch, "FireMod.Tests", "1.0.0", "test");
        NumosWorldReplaySerializer.Serialize(file, new NumosWorldReplayDocument(metadata, archive));
        file.Position = 0;
        AtmosWorldReplayArchive loaded = NumosWorldReplaySerializer.Deserialize(file).Replay;

        // A host that loads the file before running anything from FireMod calls this first.
        NumosModule.EnsureRegistered();
        using var fresh = new AtmosWorld();
        AtmosWorldReplayTimeline timeline = AtmosWorldReplayTimeline.Import(fresh, loaded);
        timeline.SeekPosition(loaded.Recording.Head);

        Assert.Multiple(() =>
        {
            Assert.That(loaded.RequiredSolvers, Is.EqualTo(new[] { "fire-mod/fire" }));
            Assert.That(fresh.ComputeStateHash(), Is.EqualTo(head));
            Assert.That(fresh.Solvers.Get<FireSolver>().IgnitionTemperature,
                Is.EqualTo(FireStation.LoweredIgnitionTemperature));
            Assert.That(fresh.Simulations[0].GetComponent<FireLog>().BurntCells, Is.Not.Empty);
        });
    }

    [Test]
    public void Burn_InThinOxygen_StopsAtFlameTemperature()
    {
        using AtmosWorld world = FireStation.CreateThinOxygen();
        var solver = world.Solvers.Get<FireSolver>();
        AtmosSimulation simulation = world.Simulations[0];
        AtmosCellRef ignited = simulation.GetCellRef(FireStation.HotChunk, FireStation.CenterVoxel);
        float start = simulation.GetVoxelSnapshot(FireStation.HotChunk, FireStation.CenterVoxel).Temperature;
        var burns = new List<FireBurned>();
        using IDisposable subscription = world.Events.Subscribe((in FireBurned burn) => burns.Add(burn));

        solver.Ignite(ignited);
        for (int tick = 0; tick < FireStation.TickRate && burns.Count == 0; tick++)
            world.Tick();

        Assert.That(burns, Is.Not.Empty);
        FireBurned first = burns[0];
        float after = simulation.GetVoxelSnapshot(FireStation.HotChunk, FireStation.CenterVoxel).Temperature;
        Assert.Multiple(() =>
        {
            Assert.That(first.Cell, Is.EqualTo(ignited));
            Assert.That(first.Temperature, Is.GreaterThan(start));
            Assert.That(first.Temperature, Is.LessThanOrEqualTo(solver.FlameTemperature + FlameTolerance));
            Assert.That(after, Is.EqualTo(first.Temperature));
        });
    }

    [Test]
    public void Fire_InThinOxygen_StaysNearFlameTemperature()
    {
        using AtmosWorld world = FireStation.CreateThinOxygen();
        var solver = world.Solvers.Get<FireSolver>();
        AtmosSimulation simulation = world.Simulations[0];
        var burns = new List<FireBurned>();
        using IDisposable subscription = world.Events.Subscribe((in FireBurned burn) => burns.Add(burn));

        solver.Ignite(simulation.GetCellRef(FireStation.HotChunk, FireStation.CenterVoxel));
        float hottest = 0f;
        for (int tick = 0; tick < FireStation.Ticks; tick++)
        {
            world.Tick();
            AtmosChunkSnapshot snapshot = simulation.GetChunkSnapshot(FireStation.HotChunk);
            for (int voxel = 0; voxel < snapshot.Temperature.Length; voxel++)
            {
                if (snapshot.TotalPressure[voxel] > 0f)
                    hottest = MathF.Max(hottest, snapshot.Temperature[voxel]);
            }
        }

        // The flow compresses burned gas a few kelvins past the flame temperature, so the check on every voxel has a
        // margin. Without the ceiling, one burn in this gas adds about 107,000 K.
        Assert.Multiple(() =>
        {
            Assert.That(burns, Has.Count.GreaterThan(64));
            Assert.That(burns.Max(static burn => burn.Temperature),
                Is.LessThanOrEqualTo(solver.FlameTemperature + FlameTolerance));
            Assert.That(hottest, Is.LessThanOrEqualTo(solver.FlameTemperature * 1.05f));
        });
    }

    private static int BurnCount(AtmosSimulation station, AtmosChunkHandle chunk)
    {
        return station.TryGetChunkField(chunk, Fire.Fields.BurnCount, out var counts) ? counts[0] : 0;
    }
}
