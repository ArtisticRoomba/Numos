using Numos.API;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.CoreSim.EulerFlow;
using Numos.CoreSim.Solvers.Euler;
using Numos.Maths;

namespace Numos.CoreSim.IntegrationTests.Euler;

/// <summary>
///     Acoustic scaling of the Euler stage: the scaled solve is the same equations in slow motion, the rule picks the
///     factor from the gathered state, and the velocity carried across factor changes never adds energy.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class EulerAcousticScalingTests
{
    private const int TickRate = 960;

    // One stage call at the default euler-flow interval.
    private const int CallTicks = 16;
    private const int Width = 16;
    private const int Height = 4;
    private const int Depth = 4;
    private const float Temperature = 293f;
    private const float Atmosphere = AtmosPhysicalConstants.StandardAtmosphericPressure;

    // The gas registry of the most recent room, for the energy sum. The fixture does not run in parallel.
    private static AtmosConfig? _config;

    [TearDown]
    public void ResetStaticOverrides()
    {
        EulerFlowSolver.ForcedSoundSpeedReduction = null;
        EulerFlowSolver.WorkerCountOverride = null;
        EulerFlowSolver.GroupingDisabled = false;
    }

    [Test]
    public void SlowMotion_AlphaTwoAtTwiceTheInterval_MatchesAlphaOne()
    {
        var settings = Settings(floor: 0f, sleepSpeed: 0f);
        const int calls = 8;

        EulerFlowSolver.ForcedSoundSpeedReduction = 2f;
        using var scaled = CreateRoom(settings, jump: 2000f);
        scaled.World.Solvers.SetInterval(AtmosBuiltInSolvers.EulerFlow, 16);
        Tick(scaled, calls * 16);
        var scaledFlow = scaled.LastFlowDiagnostics;
        var scaledSnapshots = Snapshots(scaled);

        EulerFlowSolver.ForcedSoundSpeedReduction = 1f;
        using var reference = CreateRoom(settings, jump: 2000f);
        reference.World.Solvers.SetInterval(AtmosBuiltInSolvers.EulerFlow, 8);
        Tick(reference, calls * 8);
        var referenceFlow = reference.LastFlowDiagnostics;
        var referenceSnapshots = Snapshots(reference);

        Assert.That(scaledFlow.SoundSpeedReduction, Is.EqualTo(2f));
        Assert.That(referenceFlow.SoundSpeedReduction, Is.EqualTo(1f));
        Assert.That(scaledFlow.Substeps, Is.EqualTo(referenceFlow.Substeps));
        Assert.That(scaledFlow.SimulatedSeconds, Is.EqualTo(2d * referenceFlow.SimulatedSeconds).Within(1e-9));

        double maxVelocity = 0d;
        foreach (var snapshot in referenceSnapshots)
            maxVelocity = Math.Max(maxVelocity, snapshot.VelocityX.Max(Math.Abs));

        Assert.That(maxVelocity, Is.GreaterThan(1d), "the disturbance should have started a flow");
        for (int chunk = 0; chunk < scaledSnapshots.Length; chunk++)
        {
            var a = scaledSnapshots[chunk];
            var b = referenceSnapshots[chunk];
            Assert.That(a.EulerMomentumScale, Is.EqualTo(2f));
            for (int voxel = 0; voxel < a.TotalPressure.Length; voxel++)
            {
                Assert.That(
                    Math.Abs(a.TotalPressure[voxel] - b.TotalPressure[voxel]),
                    Is.LessThanOrEqualTo(1e-5 * b.TotalPressure[voxel]),
                    $"chunk {chunk} voxel {voxel} pressure");
                Assert.That(
                    2d * a.VelocityX[voxel],
                    Is.EqualTo(b.VelocityX[voxel]).Within(1e-4 * maxVelocity),
                    $"chunk {chunk} voxel {voxel} velocity");
            }
        }
    }

    [Test]
    public void Rule_OpenFaceToAVacuumCell_RunsAtFullSpeed()
    {
        using var simulation = CreateRoom(Rule(), jump: 20f, hole: true);
        Tick(simulation, CallTicks);
        Assert.That(simulation.LastFlowDiagnostics.SoundSpeedReduction, Is.EqualTo(1f));
    }

    [Test]
    public void Rule_OpenFaceToAVoidCell_RunsAtFullSpeed()
    {
        using var simulation = CreateRoom(Rule(), jump: 20f);
        simulation.SetVoxelClassification(
            simulation.GetChunkHandles()[0], 3, 1, 1, VoxelClassification.RoomVoid);
        Tick(simulation, CallTicks);
        Assert.That(simulation.LastFlowDiagnostics.SoundSpeedReduction, Is.EqualTo(1f));
    }

    [Test]
    public void Rule_CalmRoom_RunsAtTheMaximum()
    {
        using var simulation = CreateRoom(Rule(), jump: 20f);
        Tick(simulation, CallTicks);
        Assert.That(simulation.LastFlowDiagnostics.SoundSpeedReduction, Is.EqualTo(8f));
        Assert.That(simulation.GetChunkSnapshot(simulation.GetChunkHandles()[0]).EulerMomentumScale, Is.EqualTo(8f));
    }

    [Test]
    public void Rule_OneFastCell_RunsAtFullSpeed()
    {
        using var simulation = CreateRoom(Rule(), jump: 20f);
        var chunk = simulation.GetChunkHandles()[0];
        float mass = MolesPerVoxel() * SimTestHelpers.EulerAirMolarMass;
        simulation.SetVoxelMomentum(chunk, 5, 1, 1, 3f * mass, 0f, 0f);
        Tick(simulation, CallTicks);
        Assert.That(simulation.LastFlowDiagnostics.SoundSpeedReduction, Is.EqualTo(1f));
    }

    [Test]
    public void Rule_LargeFaceJump_RunsAtFullSpeed()
    {
        using var simulation = CreateRoom(Rule(), jump: 600f);
        Tick(simulation, CallTicks);
        Assert.That(simulation.LastFlowDiagnostics.SoundSpeedReduction, Is.EqualTo(1f));
    }

    [Test]
    public void Rule_Hysteresis_AMiddlingJumpKeepsWhicheverModeTheGroupWasIn()
    {
        // 300 Pa sits between half the threshold (250) and the threshold (500).
        using var fast = CreateRoom(Rule(), jump: 300f);
        Tick(fast, CallTicks);
        Assert.That(fast.LastFlowDiagnostics.SoundSpeedReduction, Is.EqualTo(1f), "full speed stays full speed");

        using var slowed = CreateRoom(Rule(), jump: 20f);
        Tick(slowed, CallTicks);
        Assert.That(slowed.LastFlowDiagnostics.SoundSpeedReduction, Is.EqualTo(8f));
        var chunk = slowed.GetChunkHandles()[0];
        slowed.AddGasToVoxel(
            chunk, 0, 0, 0, SimTestHelpers.EulerAirGasName, MolesFor(300f),
            Temperature);
        Tick(slowed, CallTicks);
        Assert.That(slowed.LastFlowDiagnostics.SoundSpeedReduction, Is.EqualTo(8f), "a slowed group stays slowed");
        Assert.That(slowed.GetChunkSnapshot(chunk).EulerMomentumScale, Is.EqualTo(8f));
    }

    [Test]
    public void Rule_TurningTheFeatureOffClearsTheStoredScale()
    {
        using var simulation = CreateRoom(Rule(), jump: 20f);
        var chunk = simulation.GetChunkHandles()[0];
        Tick(simulation, CallTicks);
        Assert.That(simulation.GetChunkSnapshot(chunk).EulerMomentumScale, Is.EqualTo(8f));

        var config = new AtmosConfig(simulation.World.Config);
        config.SolverConfigurations.RemoveAll(static solver => solver is EulerFlowConfig);
        config.SolverConfigurations.Add(Settings(maxReduction: 1f));
        simulation.World.SetAtmosConfig(config);
        Tick(simulation, CallTicks);
        Assert.That(simulation.GetChunkSnapshot(chunk).EulerMomentumScale, Is.EqualTo(1f));
    }

    [Test]
    public void Floor_SlowedFlowBelowTheFloorButScaledAboveItKeepsMovingAcrossSettlePasses()
    {
        // At alpha 8 the real speed is about 1/8 of the scaled one. The floor must compare the scaled speed.
        const float floor = 0.03f;
        EulerFlowSolver.ForcedSoundSpeedReduction = 8f;
        using var simulation = CreateRoom(Settings(floor: floor, sleepSpeed: 0f), jump: 60f);

        // A settle pass runs every 48 ticks, so the first six calls cross two and the rest are measured.
        double maxSpeed = 0d;
        for (int call = 0; call < 20; call++)
        {
            Tick(simulation, CallTicks);
            if (call < 6)
                continue;

            foreach (var snapshot in Snapshots(simulation))
                maxSpeed = Math.Max(maxSpeed, snapshot.VelocityX.Max(Math.Abs));
        }

        TestContext.Out.WriteLine($"max real speed {maxSpeed:E3}");
        Assert.That(maxSpeed, Is.GreaterThan(floor / 8f), "the floor stopped a flow its scaled speed keeps alive");
        Assert.That(maxSpeed, Is.LessThan(floor), "the real speed stays below the unscaled floor");
    }

    [Test]
    public void Carry_TogglingBetweenEightAndOneNeverAddsEnergy()
    {
        var settings = Settings(floor: 0f, sleepSpeed: 0f);
        using var simulation = CreateRoom(settings, jump: 2000f);
        double initialEnergy = TotalEnergy(simulation);
        double initialSpread = PressureSpread(simulation);
        double worstEnergy = 0d;
        double worstSpread = 0d;
        for (int call = 0; call < 120; call++)
        {
            EulerFlowSolver.ForcedSoundSpeedReduction = call % 2 == 0 ? 8f : 1f;
            Tick(simulation, CallTicks);
            worstEnergy = Math.Max(worstEnergy, TotalEnergy(simulation) / initialEnergy - 1d);
            worstSpread = Math.Max(worstSpread, PressureSpread(simulation));
        }

        TestContext.Out.WriteLine($"energy drift {worstEnergy:E2}, spread {worstSpread:F1} of {initialSpread:F1}");
        Assert.That(worstEnergy, Is.LessThanOrEqualTo(1e-6));
        Assert.That(worstSpread, Is.LessThanOrEqualTo(initialSpread * 1.001));
    }

    [Test]
    public void Conservation_TwoGasesWithADraft_KeepEveryGasExact()
    {
        var settings = Settings(floor: 0f, sleepSpeed: 0f);
        EulerFlowSolver.ForcedSoundSpeedReduction = 8f;
        using var simulation = CreateRoom(settings, jump: 0f, secondGas: true);
        var chunks = simulation.GetChunkHandles();
        float mass = MolesPerVoxel() * SimTestHelpers.EulerAirMolarMass;
        foreach (var chunk in chunks)
        {
            for (int z = 0; z < Depth; z++)
            for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
                simulation.SetVoxelMomentum(chunk, x, y, z, 1.5f * mass, 0.2f * mass, 0f);
        }

        double air = GasMoles(simulation, SimTestHelpers.EulerAirGasId);
        double unit = GasMoles(simulation, SimTestHelpers.EulerUnitGasId);
        Tick(simulation, 120 * CallTicks);
        Assert.That(simulation.LastFlowDiagnostics.SoundSpeedReduction, Is.EqualTo(8f));
        Assert.That(GasMoles(simulation, SimTestHelpers.EulerAirGasId), Is.EqualTo(air).Within(1e-4).Percent);
        Assert.That(GasMoles(simulation, SimTestHelpers.EulerUnitGasId), Is.EqualTo(unit).Within(1e-4).Percent);
    }

    [Test]
    public void Determinism_WorkerCountDoesNotChangeTheHash()
    {
        EulerFlowSolver.ForcedSoundSpeedReduction = 4f;
        var whole = HashAfter(workerCount: 1);
        var tiled = HashAfter(workerCount: 1 << 16);
        var pool = HashAfter(workerCount: 4);
        Assert.Multiple(() =>
        {
            Assert.That(tiled, Is.EqualTo(whole));
            Assert.That(pool, Is.EqualTo(whole));
        });
    }

    [Test]
    public void Determinism_RuleDrivenRunReplaysToTheSameHashes()
    {
        var settings = Settings(maxReduction: 8f);
        using var simulation = CreateRoom(settings, jump: 0f, build: false);
        var world = simulation.World;
        var initial = world.CaptureCheckpoint();
        world.StartRecording();
        Fill(simulation, jump: 400f, secondGas: false, hole: false);
        var references = new List<AtmosWorldStateHash>();
        var scales = new List<float>();
        for (int call = 0; call < 40; call++)
        {
            Tick(simulation, CallTicks);
            scales.Add(simulation.LastFlowDiagnostics.SoundSpeedReduction);
            if (call % 8 == 7)
                references.Add(world.ComputeStateHash());
        }

        var recording = world.StopRecording();
        Assert.That(scales.Distinct().Count(), Is.GreaterThan(1), "the run should cross both modes");
        foreach (var hash in references)
        {
            world.ReplayTo(initial, recording.Operations, hash.Position);
            Assert.That(world.ComputeStateHash(), Is.EqualTo(hash));
        }
    }

    [Test]
    public void Determinism_RestoredCheckpointContinuesLikeTheUnbrokenRun()
    {
        var settings = Settings(maxReduction: 8f);
        using var simulation = CreateRoom(settings, jump: 400f);
        var world = simulation.World;
        for (int call = 0; call < 20; call++)
            Tick(simulation, CallTicks);

        var checkpoint = world.CaptureCheckpoint();
        for (int call = 0; call < 20; call++)
            Tick(simulation, CallTicks);

        var unbroken = world.ComputeStateHash();
        world.RestoreCheckpoint(checkpoint);
        for (int call = 0; call < 20; call++)
            Tick(simulation, CallTicks);

        Assert.That(world.ComputeStateHash(), Is.EqualTo(unbroken));
    }

    [Test]
    public void Groups_OneRoom_IsOneGroupWhoseResultMatchesTheSingleLoopPath()
    {
        var hashes = new List<AtmosWorldStateHash>[2];
        var scales = new HashSet<float>();
        for (int pass = 0; pass < 2; pass++)
        {
            EulerFlowSolver.GroupingDisabled = pass == 1;
            hashes[pass] = [];
            using var simulation = CreateRoom(Rule(), jump: 400f);
            for (int call = 0; call < 40; call++)
            {
                Tick(simulation, CallTicks);
                var flow = simulation.LastFlowDiagnostics;
                Assert.That(flow.FlowGroups, Is.EqualTo(1));
                Assert.That(flow.SlowedChunks, Is.EqualTo(flow.SoundSpeedReduction > 1f ? 2 : 0));
                Assert.That(EulerFlowSolver.LastCutLinkCount, Is.Zero);
                if (pass == 0)
                    scales.Add(flow.SoundSpeedReduction);

                hashes[pass].Add(simulation.World.ComputeStateHash());
            }
        }

        Assert.That(scales, Has.Count.GreaterThan(1), "the run should cross both modes");
        Assert.That(hashes[0], Is.EqualTo(hashes[1]));
    }

    [Test]
    public void Groups_FeatureOff_ReportsOneGroupAndNoSlowedChunks()
    {
        using var simulation = CreateRoom(Settings(), jump: 20f);
        Tick(simulation, CallTicks);
        var flow = simulation.LastFlowDiagnostics;
        Assert.That(flow.FlowGroups, Is.EqualTo(1));
        Assert.That(flow.SlowedChunks, Is.Zero);
        Assert.That(flow.SoundSpeedReduction, Is.EqualTo(1f));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void Groups_ViolentAndCalmRooms_PickTheirOwnFactor(bool violentFirst)
    {
        const int calls = 6;
        float firstJump = violentFirst ? Violent : Calm;
        float secondJump = violentFirst ? Calm : Violent;
        int violentOffset = violentFirst ? 0 : RoomSpacing;
        int calmOffset = violentFirst ? RoomSpacing : 0;

        using var both = CreateRooms(Rule(), firstJump, secondJump);
        using var reference = CreateRooms(Settings(), firstJump, secondJump);
        for (int call = 0; call < calls; call++)
        {
            Tick(both, CallTicks);
            var flow = both.LastFlowDiagnostics;
            Assert.That(flow.FlowGroups, Is.EqualTo(2), $"call {call}");
            Assert.That(flow.SlowedChunks, Is.EqualTo(2), $"call {call}");
            Assert.That(flow.SoundSpeedReduction, Is.EqualTo(8f), $"call {call}");
            Assert.That(EulerFlowSolver.LastBatchCount, Is.EqualTo(2), $"call {call}");
            Assert.That(EulerFlowSolver.LastCutLinkCount, Is.Zero);

            // The reference runs both rooms at full speed in one loop, and the violent room sets its time step.
            Tick(reference, CallTicks);
            Assert.That(reference.LastFlowDiagnostics.SoundSpeedReduction, Is.EqualTo(1f), $"call {call}");
            Assert.That(
                flow.Substeps, Is.EqualTo(reference.LastFlowDiagnostics.Substeps),
                "the violent room sets the substep count");
        }

        for (int chunk = 0; chunk < 2; chunk++)
        {
            var violent = both.GetChunkSnapshot(new AtmosChunkHandle(new Int3(violentOffset + chunk, 0, 0)));
            var calm = both.GetChunkSnapshot(new AtmosChunkHandle(new Int3(calmOffset + chunk, 0, 0)));
            var expected = reference.GetChunkSnapshot(new AtmosChunkHandle(new Int3(violentOffset + chunk, 0, 0)));
            Assert.That(violent.EulerMomentumScale, Is.EqualTo(1f));
            Assert.That(calm.EulerMomentumScale, Is.EqualTo(8f));
            Assert.That(violent.VelocityX.Max(Math.Abs), Is.GreaterThan(1f), "the violent room should be moving");
            Assert.That(violent.TotalPressure, Is.EqualTo(expected.TotalPressure));
            Assert.That(violent.Temperature, Is.EqualTo(expected.Temperature));
            Assert.That(violent.VelocityX, Is.EqualTo(expected.VelocityX));
            Assert.That(violent.VelocityY, Is.EqualTo(expected.VelocityY));
            Assert.That(violent.VelocityZ, Is.EqualTo(expected.VelocityZ));
            Assert.That(
                violent.Gases.Select(static gas => gas.Moles).ToArray(),
                Is.EqualTo(expected.Gases.Select(static gas => gas.Moles).ToArray()));
        }
    }

    [Test]
    public void Groups_ThreeCalmRoomsAndAViolentRoom_RunInTwoBatches()
    {
        var config = _config = SimTestHelpers.CreateEulerConfig(Rule());
        using var simulation = SimTestHelpers.CreateEulerSimulation(
            config, Width, Height, Depth, out _, tickRate: TickRate);
        simulation.UnregisterChunk(new AtmosChunkHandle(Int3.Zero));
        float[] jumps = [Calm, Violent, Calm, Calm];
        for (int room = 0; room < jumps.Length; room++)
        {
            int offset = room * RoomSpacing;
            SimTestHelpers.CreateOpenChunk(simulation, new Int3(offset, 0, 0));
            SimTestHelpers.CreateOpenChunk(simulation, new Int3(offset + 1, 0, 0));
            FillRoom(simulation, offset, jumps[room]);
        }

        for (int call = 0; call < 4; call++)
        {
            Tick(simulation, CallTicks);
            var flow = simulation.LastFlowDiagnostics;
            Assert.That(flow.FlowGroups, Is.EqualTo(4), $"call {call}");
            Assert.That(EulerFlowSolver.LastBatchCount, Is.EqualTo(2), $"call {call}");
            Assert.That(flow.SlowedChunks, Is.EqualTo(6), $"call {call}");
        }
    }

    /// <summary>
    ///     Two calm rooms joined by a sleeping bridge. Both slow, so they share a batch, and the woken bridge joins
    ///     it without cutting a link.
    /// </summary>
    [Test]
    public void Groups_BridgeBetweenTwoSlowedRooms_CutsNothingAndKeepsEveryMole()
    {
        var config = _config = SimTestHelpers.CreateEulerConfig(Settings(maxReduction: 8f, floor: 0f, sleepSpeed: 0f));
        using var simulation = SimTestHelpers.CreateEulerSimulation(
            config, Width, Height, Depth, out var first, tickRate: TickRate);
        var bridge = SimTestHelpers.CreateOpenChunk(simulation, Int3.PosX);
        var second = SimTestHelpers.CreateOpenChunk(simulation, new Int3(2, 0, 0));
        float ambient = MolesFor(Atmosphere);
        for (int z = 0; z < Depth; z++)
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            // Each room's pulse sits one column from the bridge face, so its wave reaches the bridge mid-call.
            simulation.AddGasToVoxel(
                first, x, y, z, SimTestHelpers.EulerAirGasName,
                MolesFor(Atmosphere + (x is >= 13 and < 15 ? Pulse : 0f)), Temperature);
            simulation.AddGasToVoxel(bridge, x, y, z, SimTestHelpers.EulerAirGasName, ambient, Temperature);
            simulation.AddGasToVoxel(
                second, x, y, z, SimTestHelpers.EulerAirGasName,
                MolesFor(Atmosphere + (x is >= 1 and < 3 ? Pulse : 0f)), Temperature);
        }

        simulation.SleepChunk(bridge);
        double moles = GasMoles(simulation, SimTestHelpers.EulerAirGasId);

        Tick(simulation, CallTicks);
        Assert.That(simulation.LastFlowDiagnostics.FlowGroups, Is.EqualTo(2));
        Assert.That(simulation.LastFlowDiagnostics.SoundSpeedReduction, Is.EqualTo(8f));
        Assert.That(EulerFlowSolver.LastBatchCount, Is.EqualTo(1));
        Assert.That(EulerFlowSolver.LastCutLinkCount, Is.Zero);
        Assert.That(simulation.GetChunkSnapshot(bridge).IsAwake, Is.True);
        for (int call = 0; call < 20; call++)
            Tick(simulation, CallTicks);

        Assert.That(GasMoles(simulation, SimTestHelpers.EulerAirGasId), Is.EqualTo(moles).Within(1e-5).Percent);
    }

    /// <summary>
    ///     A violent chunk, a sleeping bridge chunk, and a calm chunk in a row. The violent pulse sits a few
    ///     columns from the bridge face, so its wave reaches the bridge mid-call and wakes it. The calm chunk's 20 Pa
    ///     step is on its far side.
    /// </summary>
    [TestCase(true)]
    [TestCase(false)]
    public void Groups_BridgeWokenMidCall_CutsTheLinkToTheOtherGroupAndKeepsEveryMole(bool violentFirst)
    {
        var config = _config = SimTestHelpers.CreateEulerConfig(Settings(maxReduction: 8f, floor: 0f, sleepSpeed: 0f));
        using var simulation = SimTestHelpers.CreateEulerSimulation(
            config, Width, Height, Depth, out var first, tickRate: TickRate);
        var bridge = SimTestHelpers.CreateOpenChunk(simulation, Int3.PosX);
        var second = SimTestHelpers.CreateOpenChunk(simulation, new Int3(2, 0, 0));
        float ambient = MolesFor(Atmosphere);
        for (int z = 0; z < Depth; z++)
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            // The first chunk touches the bridge at its high-x face, the second at its low-x face.
            float firstJump = violentFirst
                ? x is >= 10 and < 12 ? Violent : 0f
                : x < Width / 2 ? Calm : 0f;
            float secondJump = violentFirst
                ? x >= Width / 2 ? Calm : 0f
                : x is >= 4 and < 6 ? Violent : 0f;
            simulation.AddGasToVoxel(
                first, x, y, z, SimTestHelpers.EulerAirGasName, MolesFor(Atmosphere + firstJump), Temperature);
            simulation.AddGasToVoxel(bridge, x, y, z, SimTestHelpers.EulerAirGasName, ambient, Temperature);
            simulation.AddGasToVoxel(
                second, x, y, z, SimTestHelpers.EulerAirGasName, MolesFor(Atmosphere + secondJump), Temperature);
        }

        simulation.SleepChunk(bridge);
        double moles = GasMoles(simulation, SimTestHelpers.EulerAirGasId);

        Tick(simulation, CallTicks);
        var flow = simulation.LastFlowDiagnostics;
        Assert.That(flow.FlowGroups, Is.EqualTo(2));
        Assert.That(flow.SoundSpeedReduction, Is.EqualTo(8f));
        Assert.That(EulerFlowSolver.LastCutLinkCount, Is.EqualTo(1), "the bridge should wake and its far link be cut");
        Assert.That(simulation.GetChunkSnapshot(bridge).IsAwake, Is.True);
        Assert.That(GasMoles(simulation, SimTestHelpers.EulerAirGasId), Is.EqualTo(moles).Within(1e-5).Percent);

        Tick(simulation, CallTicks);
        Assert.That(simulation.LastFlowDiagnostics.FlowGroups, Is.EqualTo(1), "the woken bridge joins the rooms");
        Assert.That(EulerFlowSolver.LastCutLinkCount, Is.Zero);
        for (int call = 0; call < 40; call++)
            Tick(simulation, CallTicks);

        Assert.That(GasMoles(simulation, SimTestHelpers.EulerAirGasId), Is.EqualTo(moles).Within(1e-5).Percent);
    }

    [Test]
    public void Groups_Determinism_WorkerCountDoesNotChangeTheHash()
    {
        var whole = TwoRoomHash(workerCount: 1);
        var tiled = TwoRoomHash(workerCount: 1 << 16);
        var pool = TwoRoomHash(workerCount: 4);
        Assert.Multiple(() =>
        {
            Assert.That(tiled, Is.EqualTo(whole));
            Assert.That(pool, Is.EqualTo(whole));
        });
    }

    [Test]
    public void Groups_Determinism_RecordedRunReplaysAndACheckpointContinuesLikeTheUnbrokenRun()
    {
        using var simulation = CreateRooms(Rule(), 0f, 0f, fill: false);
        var world = simulation.World;
        var initial = world.CaptureCheckpoint();
        world.StartRecording();
        FillRoom(simulation, 0, Violent);
        FillRoom(simulation, RoomSpacing, Calm);
        var references = new List<AtmosWorldStateHash>();
        AtmosWorldCheckpoint? midway = null;
        for (int call = 0; call < 40; call++)
        {
            Tick(simulation, CallTicks);
            if (call == 19)
                midway = world.CaptureCheckpoint();

            if (call % 8 == 7)
                references.Add(world.ComputeStateHash());
        }

        var recording = world.StopRecording();
        Assert.That(simulation.LastFlowDiagnostics.FlowGroups, Is.EqualTo(2));
        foreach (var hash in references)
        {
            world.ReplayTo(initial, recording.Operations, hash.Position);
            Assert.That(world.ComputeStateHash(), Is.EqualTo(hash));
        }

        var unbroken = references[^1];
        world.RestoreCheckpoint(midway!);
        for (int call = 20; call < 40; call++)
            Tick(simulation, CallTicks);

        Assert.That(world.ComputeStateHash(), Is.EqualTo(unbroken));
    }

    private static AtmosWorldStateHash TwoRoomHash(int workerCount)
    {
        EulerFlowSolver.WorkerCountOverride = workerCount;
        try
        {
            using var simulation = CreateRooms(Rule(), Violent, Calm);
            Tick(simulation, 30 * CallTicks);
            return simulation.World.CaptureCheckpoint().ComputeStateHash();
        }
        finally
        {
            EulerFlowSolver.WorkerCountOverride = null;
        }
    }

    // Two rooms of two chunks each, with an empty column of chunks between them so that they share no face.
    private const int RoomSpacing = 3;
    private const float Violent = 20000f;
    private const float Calm = 20f;

    // Below half of FullSpeedPressureJump, so the room still slows, but strong enough that its wave wakes a sleeping neighbour.
    private const float Pulse = 200f;

    /// <summary>
    ///     Rooms of two chunks at x = 0..1 and x = 3..4. A null jump leaves that room out. A room's left half is
    ///     raised by its jump. Without <paramref name="fill" /> the rooms stay empty.
    /// </summary>
    private static AtmosSimulation CreateRooms(
        EulerFlowConfig settings, float? firstJump, float? secondJump, bool fill = true)
    {
        var config = _config = SimTestHelpers.CreateEulerConfig(settings);
        var simulation = SimTestHelpers.CreateEulerSimulation(config, Width, Height, Depth, out _, tickRate: TickRate);
        simulation.UnregisterChunk(new AtmosChunkHandle(Int3.Zero));
        float?[] jumps = [firstJump, secondJump];
        for (int room = 0; room < 2; room++)
        {
            if (jumps[room] is not { } jump)
                continue;

            int offset = room * RoomSpacing;
            SimTestHelpers.CreateOpenChunk(simulation, new Int3(offset, 0, 0));
            SimTestHelpers.CreateOpenChunk(simulation, new Int3(offset + 1, 0, 0));
            if (fill)
                FillRoom(simulation, offset, jump);
        }

        return simulation;
    }

    private static void FillRoom(AtmosSimulation simulation, int offset, float jump)
    {
        float raised = MolesFor(Atmosphere + jump);
        float ambient = MolesFor(Atmosphere);
        for (int chunk = 0; chunk < 2; chunk++)
        {
            var handle = new AtmosChunkHandle(new Int3(offset + chunk, 0, 0));
            for (int z = 0; z < Depth; z++)
            for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
                simulation.AddGasToVoxel(
                    handle, x, y, z, SimTestHelpers.EulerAirGasName, chunk == 0 && x < Width / 2 ? raised : ambient,
                    Temperature);
        }
    }

    private static AtmosWorldStateHash HashAfter(int workerCount)
    {
        EulerFlowSolver.WorkerCountOverride = workerCount;
        try
        {
            using var simulation = CreateRoom(Settings(floor: 0f, sleepSpeed: 0f), jump: 2000f);
            Tick(simulation, 60 * CallTicks);
            return simulation.World.CaptureCheckpoint().ComputeStateHash();
        }
        finally
        {
            EulerFlowSolver.WorkerCountOverride = null;
        }
    }

    private static EulerFlowConfig Rule() => Settings(maxReduction: 8f);

    private static EulerFlowConfig Settings(float maxReduction = 1f, float floor = 0.005f, float sleepSpeed = 0.5f)
    {
        return new EulerFlowConfig
        {
            MaxSubstepsPerSecond = 0,
            MaxSoundSpeedReduction = maxReduction,
            VelocityFloor = floor,
            SleepSpeed = sleepSpeed
        };
    }

    private static void Tick(AtmosSimulation simulation, int ticks)
    {
        for (int tick = 0; tick < ticks; tick++)
            simulation.Tick();
    }

    // The test rooms use 1 m³ voxels.
    private static float MolesFor(float pressure) => pressure / (AtmosPhysicalConstants.MolarGasConstant * Temperature);

    private static float MolesPerVoxel() => MolesFor(Atmosphere);

    /// <summary>
    ///     Two 16x4x4 chunks side by side in one open room, the left half raised by <paramref name="jump" /> pascals.
    ///     With <paramref name="secondGas" /> the right half holds the second gas instead. A hole leaves one empty
    ///     voxel in the left chunk.
    /// </summary>
    private static AtmosSimulation CreateRoom(
        EulerFlowConfig settings, float jump, bool secondGas = false, bool hole = false, bool build = true)
    {
        var config = _config = SimTestHelpers.CreateEulerConfig(settings);
        var simulation = SimTestHelpers.CreateEulerSimulation(config, Width, Height, Depth, out _, tickRate: TickRate);
        SimTestHelpers.CreateOpenChunk(simulation, Int3.PosX);
        if (build)
            Fill(simulation, jump, secondGas, hole);

        return simulation;
    }

    private static void Fill(AtmosSimulation simulation, float jump, bool secondGas, bool hole)
    {
        var chunks = simulation.GetChunkHandles();
        float raised = MolesFor(Atmosphere + jump);
        float ambient = MolesFor(Atmosphere);
        for (int chunkIndex = 0; chunkIndex < chunks.Length; chunkIndex++)
        for (int z = 0; z < Depth; z++)
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            if (hole && chunkIndex == 0 && x == 4 && y == 1 && z == 1)
                continue;

            bool left = chunkIndex == 0 && x < Width / 2;
            string gas = secondGas && chunkIndex == 1 && x >= Width / 2
                ? SimTestHelpers.EulerUnitGasName
                : SimTestHelpers.EulerAirGasName;
            simulation.AddGasToVoxel(chunks[chunkIndex], x, y, z, gas, left ? raised : ambient, Temperature);
        }
    }

    private static AtmosChunkSnapshot[] Snapshots(AtmosSimulation simulation)
    {
        return simulation.GetChunkHandles().Select(simulation.GetChunkSnapshot).ToArray();
    }

    private static double TotalEnergy(AtmosSimulation simulation)
    {
        return Snapshots(simulation).Sum(snapshot => SimTestHelpers.TotalEulerEnergy(_config!, snapshot));
    }

    private static double PressureSpread(AtmosSimulation simulation)
    {
        var pressures = Snapshots(simulation).SelectMany(snapshot => snapshot.TotalPressure).ToArray();
        return pressures.Max() - pressures.Min();
    }

    private static double GasMoles(AtmosSimulation simulation, int gasId)
    {
        double total = 0d;
        foreach (var snapshot in Snapshots(simulation))
        {
            foreach (var gas in snapshot.Gases)
            {
                if (gas.GasId == gasId)
                    total += gas.Moles.Sum(static moles => (double)moles);
            }
        }

        return total;
    }
}
