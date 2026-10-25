using Numos.API;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.CoreSim.EulerFlow;
using Numos.CoreSim.Solvers.Euler;
using Numos.Maths;

namespace Numos.CoreSim.IntegrationTests.Euler;

/// <summary>
///     The Euler stage across chunk faces: the shared-face rule (a tube split across two chunks is the same bits as
///     the tube in one chunk), activation of inactive neighbours before and during a tick, and sleep across a face.
/// </summary>
[TestFixture]
public sealed class EulerCrossChunkTests
{
    private const int Length = 100;
    private const int Half = Length / 2;
    private const float LeftDensity = 1f;
    private const float LeftPressure = 1e4f;
    private const float RightDensity = 0.125f;
    private const float RightPressure = 1e3f;
    private const float Temperature = 293f;
    private const float Atmosphere = AtmosPhysicalConstants.StandardAtmosphericPressure;

    /// <summary>
    ///     Conservation tolerance for the activation tests: the conserved variables are float and a tick is forty
    ///     to seventy substeps, so the totals drift by about <c>1e-7</c> relative, the same as inside one chunk.
    /// </summary>
    private const double ConservationPercent = 1e-4;

    [TestCase(Half)]
    [TestCase(Half - 1)]
    public void TwoChunkSod_IsBitwiseTheSingleChunkTube(int diaphragm)
    {
        var config = SimTestHelpers.CreateEulerConfig();
        using var single = SimTestHelpers.CreateEulerSimulation(config, Length, 1, 1, out var tube);
        using var split = SimTestHelpers.CreateEulerSimulation(config, Half, 1, 1, out var left);
        var right = SimTestHelpers.CreateOpenChunk(split, Int3.PosX);
        FillSod(single, tube, null, diaphragm, mirrored: false);
        FillSod(split, left, right, diaphragm, mirrored: false);

        double initialMoles = TotalMoles(split, left, right);
        double initialEnergy = TotalEnergy(config, split, left, right);
        for (int tick = 1; tick <= 5; tick++)
        {
            single.Tick();
            split.Tick();
            if (tick != 1 && tick != 5)
                continue;

            Assert.That(split.LastFlowDiagnostics, Is.EqualTo(single.LastFlowDiagnostics), $"diagnostics after tick {tick}");
            var expected = single.GetChunkSnapshot(tube);
            var actualLeft = split.GetChunkSnapshot(left);
            var actualRight = split.GetChunkSnapshot(right);
            Assert.Multiple(() =>
            {
                for (int x = 0; x < Length; x++)
                {
                    var actual = x < Half ? actualLeft : actualRight;
                    int local = x % Half;
                    AssertSameBits(
                        SimTestHelpers.Moles(expected, SimTestHelpers.EulerUnitGasId, x),
                        SimTestHelpers.Moles(actual, SimTestHelpers.EulerUnitGasId, local), $"moles at {x} after tick {tick}");
                    AssertSameBits(expected.Temperature[x], actual.Temperature[local], $"temperature at {x} after tick {tick}");
                    AssertSameBits(expected.TotalPressure[x], actual.TotalPressure[local], $"pressure at {x} after tick {tick}");
                    AssertSameBits(expected.VelocityX[x], actual.VelocityX[local], $"velocity at {x} after tick {tick}");
                }

                Assert.That(TotalMoles(split, left, right), Is.EqualTo(initialMoles).Within(1e-6).Percent, $"moles after tick {tick}");
                Assert.That(TotalEnergy(config, split, left, right), Is.EqualTo(initialEnergy).Within(1e-5).Percent, $"energy after tick {tick}");
            });
        }
    }

    [Test]
    public void TwoChunkSod_MirrorsBitwiseAcrossTheFace()
    {
        var config = SimTestHelpers.CreateEulerConfig();
        using var forward = SimTestHelpers.CreateEulerSimulation(config, Half, 1, 1, out var forwardLeft);
        var forwardRight = SimTestHelpers.CreateOpenChunk(forward, Int3.PosX);
        using var mirrored = SimTestHelpers.CreateEulerSimulation(config, Half, 1, 1, out var mirroredLeft);
        var mirroredRight = SimTestHelpers.CreateOpenChunk(mirrored, Int3.PosX);
        FillSod(forward, forwardLeft, forwardRight, Half - 1, mirrored: false);
        FillSod(mirrored, mirroredLeft, mirroredRight, Half - 1, mirrored: true);

        for (int tick = 0; tick < 5; tick++)
        {
            forward.Tick();
            mirrored.Tick();
        }

        var expectedLeft = forward.GetChunkSnapshot(forwardLeft);
        var expectedRight = forward.GetChunkSnapshot(forwardRight);
        var actualLeft = mirrored.GetChunkSnapshot(mirroredLeft);
        var actualRight = mirrored.GetChunkSnapshot(mirroredRight);
        Assert.Multiple(() =>
        {
            for (int x = 0; x < Length; x++)
            {
                int m = Length - 1 - x;
                var expected = x < Half ? expectedLeft : expectedRight;
                var actual = m < Half ? actualLeft : actualRight;
                AssertSameBits(
                    SimTestHelpers.Moles(expected, SimTestHelpers.EulerUnitGasId, x % Half),
                    SimTestHelpers.Moles(actual, SimTestHelpers.EulerUnitGasId, m % Half), $"moles at {x}");
                AssertSameBits(expected.TotalPressure[x % Half], actual.TotalPressure[m % Half], $"pressure at {x}");
                AssertSameBits(expected.Temperature[x % Half], actual.Temperature[m % Half], $"temperature at {x}");
                AssertSameBits(expected.VelocityX[x % Half], -actual.VelocityX[m % Half], $"velocity at {x}");
            }
        });
    }

    [Test]
    public void PrePass_WakesALowerPressureSleepingNeighbourInTheFirstTick()
    {
        var config = SimTestHelpers.CreateEulerConfig();
        using var simulation = SimTestHelpers.CreateEulerSimulation(config, 4, 4, 4, out var source);
        var target = SimTestHelpers.CreateOpenChunk(simulation, Int3.PosX);
        FillRoom(simulation, source, 4, 4, 4, SimTestHelpers.MolesFor(config, 2f * Atmosphere, Temperature));
        FillRoom(simulation, target, 4, 4, 4, SimTestHelpers.MolesFor(config, Atmosphere, Temperature));
        simulation.SleepChunk(target);
        Assert.That(simulation.GetChunkSnapshot(target).IsAwake, Is.False);
        double initialMoles = TotalMoles(simulation, source, target);
        double targetMoles = SimTestHelpers.TotalMoles(simulation.GetChunkSnapshot(target));

        simulation.Tick();

        var after = simulation.GetChunkSnapshot(target);
        TestContext.Out.WriteLine($"target: {targetMoles:F5} -> {SimTestHelpers.TotalMoles(after):F5} mol, {simulation.LastFlowDiagnostics}");
        Assert.Multiple(() =>
        {
            Assert.That(after.IsAwake, Is.True);
            Assert.That(SimTestHelpers.TotalMoles(after), Is.GreaterThan(targetMoles * 1.001f), "gas crossed the face");
            Assert.That(TotalMoles(simulation, source, target), Is.EqualTo(initialMoles).Within(ConservationPercent).Percent);
        });
    }

    [Test]
    public void PrePass_LeavesAnEqualPressureSleepingNeighbourAsleep()
    {
        // Nothing here is a source, so the stage would skip the active chunk. Sweep it to test the pre-pass.
        EulerFlowSolver.SkipStaticChunks = false;
        try
        {
            AssertEqualPressureNeighbourStaysAsleep();
        }
        finally
        {
            EulerFlowSolver.SkipStaticChunks = true;
        }
    }

    private static void AssertEqualPressureNeighbourStaysAsleep()
    {
        var config = SimTestHelpers.CreateEulerConfig();
        using var simulation = SimTestHelpers.CreateEulerSimulation(config, 4, 4, 4, out var active);
        var sleeping = SimTestHelpers.CreateOpenChunk(simulation, Int3.PosX);
        float moles = SimTestHelpers.MolesFor(config, Atmosphere, Temperature);
        FillRoom(simulation, active, 4, 4, 4, moles);
        FillRoom(simulation, sleeping, 4, 4, 4, moles);
        simulation.SleepChunk(sleeping);
        ulong sleepingBefore = Digest(simulation.GetChunkSnapshot(sleeping));
        ulong activeBefore = Digest(simulation.GetChunkSnapshot(active));

        for (int tick = 0; tick < 5; tick++)
        {
            simulation.Tick();
            Assert.That(simulation.LastFlowDiagnostics.Substeps, Is.GreaterThan(1), "the active chunk is swept");
        }

        Assert.Multiple(() =>
        {
            Assert.That(simulation.GetChunkSnapshot(sleeping).IsAwake, Is.False);
            Assert.That(Digest(simulation.GetChunkSnapshot(sleeping)), Is.EqualTo(sleepingBefore), "sleeping chunk untouched");
            Assert.That(Digest(simulation.GetChunkSnapshot(active)), Is.EqualTo(activeBefore), "the wall face is exact at rest");
        });
    }

    [Test]
    public void MidTick_AFrontReachingTheFaceWakesTheNeighbourThatTick()
    {
        const int width = 8;
        var config = SimTestHelpers.CreateEulerConfig();
        using var simulation = SimTestHelpers.CreateEulerSimulation(config, width, 1, 1, out var source);
        var target = SimTestHelpers.CreateOpenChunk(simulation, Int3.PosX);
        float moles = SimTestHelpers.MolesFor(config, Atmosphere, Temperature);
        FillRoom(simulation, source, width, 1, 1, moles);
        FillRoom(simulation, target, width, 1, 1, moles);
        // A pulse in the two cells furthest from the face. The face cells match the neighbour exactly.
        simulation.AddGasToVoxel(source, 0, 0, 0, SimTestHelpers.EulerAirGasName, 0.5f * moles, Temperature);
        simulation.AddGasToVoxel(source, 1, 0, 0, SimTestHelpers.EulerAirGasName, 0.5f * moles, Temperature);
        simulation.SleepChunk(target);
        double initialMoles = TotalMoles(simulation, source, target);

        simulation.Tick();

        var after = simulation.GetChunkSnapshot(target);
        TestContext.Out.WriteLine($"{simulation.LastFlowDiagnostics}; target[0]={SimTestHelpers.Moles(after, SimTestHelpers.EulerAirGasId, 0):F5} mol");
        Assert.Multiple(() =>
        {
            Assert.That(after.IsAwake, Is.True, "woken in the tick the front arrived");
            Assert.That(SimTestHelpers.Moles(after, SimTestHelpers.EulerAirGasId, 0), Is.GreaterThan(moles * 1.001f), "gas crossed the face");
            Assert.That(TotalMoles(simulation, source, target), Is.EqualTo(initialMoles).Within(ConservationPercent).Percent);
        });
    }

    [Test]
    public void MidTick_NoFrontLeavesTheNeighbourAsleep()
    {
        const int width = 8;
        var config = SimTestHelpers.CreateEulerConfig();
        using var simulation = SimTestHelpers.CreateEulerSimulation(config, width, 1, 1, out var source);
        var target = SimTestHelpers.CreateOpenChunk(simulation, Int3.PosX);
        float moles = SimTestHelpers.MolesFor(config, Atmosphere, Temperature);
        FillRoom(simulation, source, width, 1, 1, moles);
        FillRoom(simulation, target, width, 1, 1, moles);
        simulation.SleepChunk(target);

        simulation.Tick();

        Assert.That(simulation.GetChunkSnapshot(target).IsAwake, Is.False);
    }

    [TestCase(0.001f, false)]
    [TestCase(0.1f, true)]
    public void PrePass_WakesAnEqualPressureNeighbourOnlyForMotionAboveTheVelocityFloor(float speed, bool wakes)
    {
        const int width = 4;
        var config = SimTestHelpers.CreateEulerConfig();
        using var simulation = SimTestHelpers.CreateEulerSimulation(config, width, width, width, out var active);
        var sleeping = SimTestHelpers.CreateOpenChunk(simulation, Int3.PosX);
        float moles = SimTestHelpers.MolesFor(config, Atmosphere, Temperature);
        FillRoom(simulation, active, width, width, width, moles);
        FillRoom(simulation, sleeping, width, width, width, moles);
        float mass = moles * SimTestHelpers.EulerAirMolarMass;
        simulation.SetVoxelMomentum(active, width - 1, 1, 1, mass * speed, 0f, 0f);
        simulation.SleepChunk(sleeping);

        simulation.Tick();

        Assert.That(simulation.GetChunkSnapshot(sleeping).IsAwake, Is.EqualTo(wakes));
    }

    [Test]
    public void Chain_ThePrePassFollowsAPressureStaircaseToTheEnd()
    {
        var config = SimTestHelpers.CreateEulerConfig();
        using var simulation = SimTestHelpers.CreateEulerSimulation(config, 4, 1, 1, out var first);
        var second = SimTestHelpers.CreateOpenChunk(simulation, Int3.PosX);
        var third = SimTestHelpers.CreateOpenChunk(simulation, 2 * Int3.PosX);
        FillRoom(simulation, first, 4, 1, 1, SimTestHelpers.MolesFor(config, 3f * Atmosphere, Temperature));
        FillRoom(simulation, second, 4, 1, 1, SimTestHelpers.MolesFor(config, 2f * Atmosphere, Temperature));
        FillRoom(simulation, third, 4, 1, 1, SimTestHelpers.MolesFor(config, Atmosphere, Temperature));
        simulation.SleepChunk(second);
        simulation.SleepChunk(third);
        double thirdMoles = SimTestHelpers.TotalMoles(simulation.GetChunkSnapshot(third));
        double initialMoles = TotalMoles(simulation, first, second, third);

        // Every face differs by an atmosphere at the start of the tick, so the pre-pass activates the second
        // chunk from the first and then the third from the second before the first sweep runs.
        simulation.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(simulation.GetChunkSnapshot(second).IsAwake, Is.True);
            Assert.That(simulation.GetChunkSnapshot(third).IsAwake, Is.True);
            Assert.That(SimTestHelpers.TotalMoles(simulation.GetChunkSnapshot(third)), Is.GreaterThan(thirdMoles * 1.001f));
            Assert.That(TotalMoles(simulation, first, second, third), Is.EqualTo(initialMoles).Within(ConservationPercent).Percent);
        });
    }

    // Chunks one voxel long on the flow axis have no interior faces on it, which once made the stage skip the
    // axis outright, chunk faces included. Both cases below are that axis with a neighbour across it.
    [TestCase(1, 1, 1, 1, 0, 0)]
    [TestCase(4, 4, 1, 0, 0, 1)]
    public void OneVoxelThickChunks_ExchangeGasAcrossTheThinAxis(
        int width, int height, int depth, int offsetX, int offsetY, int offsetZ)
    {
        var config = SimTestHelpers.CreateEulerConfig();
        using var simulation = SimTestHelpers.CreateEulerSimulation(config, width, height, depth, out var source);
        var target = SimTestHelpers.CreateOpenChunk(simulation, new Int3(offsetX, offsetY, offsetZ));
        float targetMoles = SimTestHelpers.MolesFor(config, Atmosphere, Temperature);
        FillRoom(simulation, source, width, height, depth, SimTestHelpers.MolesFor(config, 2f * Atmosphere, Temperature));
        FillRoom(simulation, target, width, height, depth, targetMoles);
        double initialMoles = TotalMoles(simulation, source, target);

        simulation.Tick();

        var targetSnapshot = simulation.GetChunkSnapshot(target);
        Assert.Multiple(() =>
        {
            Assert.That(simulation.LastFlowDiagnostics.Substeps, Is.GreaterThan(0));
            Assert.That(SimTestHelpers.TotalMoles(targetSnapshot), Is.GreaterThan(targetMoles * width * height * depth));
            Assert.That(TotalMoles(simulation, source, target), Is.EqualTo(initialMoles).Within(ConservationPercent).Percent);
        });
    }

    [Test]
    public void Chain_AFrontIntoEmptyChunksWakesEachAsItArrives()
    {
        var config = SimTestHelpers.CreateEulerConfig();
        using var simulation = SimTestHelpers.CreateEulerSimulation(config, 4, 1, 1, out var first);
        var second = SimTestHelpers.CreateOpenChunk(simulation, Int3.PosX);
        var third = SimTestHelpers.CreateOpenChunk(simulation, 2 * Int3.PosX);
        FillRoom(simulation, first, 4, 1, 1, SimTestHelpers.MolesFor(config, Atmosphere, Temperature));
        simulation.SleepChunk(second);
        simulation.SleepChunk(third);
        double initialMoles = TotalMoles(simulation, first, second, third);

        // The pre-pass only sees the first face differ (the second and third chunks are both empty), so the
        // third chunk is activated mid-tick, when the expansion front crosses the four cells of the second one.
        simulation.Tick();

        var thirdSnapshot = simulation.GetChunkSnapshot(third);
        TestContext.Out.WriteLine($"{simulation.LastFlowDiagnostics}; third chunk holds {SimTestHelpers.TotalMoles(thirdSnapshot):E3} mol");
        Assert.Multiple(() =>
        {
            Assert.That(simulation.GetChunkSnapshot(second).IsAwake, Is.True);
            Assert.That(thirdSnapshot.IsAwake, Is.True);
            Assert.That(SimTestHelpers.TotalMoles(thirdSnapshot), Is.GreaterThan(0f));
            Assert.That(TotalMoles(simulation, first, second, third), Is.EqualTo(initialMoles).Within(ConservationPercent).Percent);
        });
    }

    [Test]
    public void TraceGas_CrossingAChunkFaceIsConserved()
    {
        const int width = 8;
        const float traceMoles = 1e-3f;
        var config = SimTestHelpers.CreateEulerConfig();
        using var split = SimTestHelpers.CreateEulerSimulation(config, width, 1, 1, out var left);
        var right = SimTestHelpers.CreateOpenChunk(split, Int3.PosX);
        using var single = SimTestHelpers.CreateEulerSimulation(config, 2 * width, 1, 1, out var tube);
        float high = SimTestHelpers.MolesFor(config, 101_000f, Temperature);
        float low = SimTestHelpers.MolesFor(config, 100_000f, Temperature);
        for (int x = 0; x < width; x++)
        {
            // A 1 kPa step moves about 6e-5 mol of the trace gas across the face per tick, well under
            // MinimumTrackedMoles, so the receiving chunk only ever sees sub-threshold arrivals of it.
            split.AddGasToVoxel(left, x, 0, 0, SimTestHelpers.EulerAirGasName, high, Temperature);
            split.AddGasToVoxel(left, x, 0, 0, SimTestHelpers.EulerUnitGasName, traceMoles, Temperature);
            split.AddGasToVoxel(right, x, 0, 0, SimTestHelpers.EulerAirGasName, low, Temperature);
            single.AddGasToVoxel(tube, x, 0, 0, SimTestHelpers.EulerAirGasName, high, Temperature);
            single.AddGasToVoxel(tube, x, 0, 0, SimTestHelpers.EulerUnitGasName, traceMoles, Temperature);
            single.AddGasToVoxel(tube, width + x, 0, 0, SimTestHelpers.EulerAirGasName, low, Temperature);
        }

        double initialTrace = TotalMoles(split, SimTestHelpers.EulerUnitGasId, left, right);
        for (int tick = 0; tick < 20; tick++)
        {
            split.Tick();
            single.Tick();
        }

        double splitTrace = TotalMoles(split, SimTestHelpers.EulerUnitGasId, left, right);
        double singleTrace = TotalMoles(single, SimTestHelpers.EulerUnitGasId, tube);
        TestContext.Out.WriteLine(
            $"trace gas: initial {initialTrace:E7}, split {splitTrace:E7}, single {singleTrace:E7}; " +
            $"right chunk holds {TotalMoles(split, SimTestHelpers.EulerUnitGasId, right):E3}");
        Assert.Multiple(() =>
        {
            Assert.That(splitTrace, Is.EqualTo(initialTrace).Within(1e-4).Percent, "split tube");
            Assert.That(singleTrace, Is.EqualTo(initialTrace).Within(1e-4).Percent, "single tube");
            Assert.That(splitTrace, Is.EqualTo(singleTrace).Within(1e-4).Percent, "split against single");
        });
    }

    [Test]
    public void Sleep_WaitsForTheFaceToEqualizeThenHoldsBothChunksStill()
    {
        var config = SimTestHelpers.CreateEulerConfig(new EulerFlowConfig { MaxSubstepsPerTick = 0, MomentumDamping = 4f });
        config.SleepThreshold = 2;
        config.SleepEpsilon = 3.5f;
        using var simulation = SimTestHelpers.CreateEulerSimulation(config, 4, 1, 1, out var left);
        var right = SimTestHelpers.CreateOpenChunk(simulation, Int3.PosX);
        FillRoom(simulation, left, 4, 1, 1, SimTestHelpers.MolesFor(config, 1.5f * Atmosphere, Temperature));
        FillRoom(simulation, right, 4, 1, 1, SimTestHelpers.MolesFor(config, Atmosphere, Temperature));

        int asleepAt = -1;
        for (int tick = 1; tick <= 2000; tick++)
        {
            simulation.Tick();
            var leftSnapshot = simulation.GetChunkSnapshot(left);
            var rightSnapshot = simulation.GetChunkSnapshot(right);
            float facePressureDelta = MathF.Abs(leftSnapshot.TotalPressure[3] - rightSnapshot.TotalPressure[0]);
            float relative = facePressureDelta / MathF.Max(leftSnapshot.TotalPressure[3], rightSnapshot.TotalPressure[0]) * 100f;
            if (relative >= config.SleepEpsilon)
            {
                Assert.That(leftSnapshot.IsAwake, Is.True, $"left asleep on tick {tick} with {relative:F2}% across the face");
                Assert.That(rightSnapshot.IsAwake, Is.True, $"right asleep on tick {tick} with {relative:F2}% across the face");
            }

            if (leftSnapshot.IsAwake || rightSnapshot.IsAwake)
                continue;

            asleepAt = tick;
            break;
        }

        TestContext.Out.WriteLine($"both asleep after tick {asleepAt}");
        Assert.That(asleepAt, Is.GreaterThan(0), "the pair never slept");
        var leftAsleep = simulation.GetChunkSnapshot(left);
        var rightAsleep = simulation.GetChunkSnapshot(right);
        ulong leftDigest = Digest(leftAsleep);
        ulong rightDigest = Digest(rightAsleep);
        for (int tick = 0; tick < 10; tick++)
            simulation.Tick();

        Assert.Multiple(() =>
        {
            for (int x = 0; x < 4; x++)
            {
                Assert.That(leftAsleep.VelocityX[x], Is.Zero, $"left momentum at {x}");
                Assert.That(rightAsleep.VelocityX[x], Is.Zero, $"right momentum at {x}");
            }

            Assert.That(Digest(simulation.GetChunkSnapshot(left)), Is.EqualTo(leftDigest));
            Assert.That(Digest(simulation.GetChunkSnapshot(right)), Is.EqualTo(rightDigest));
            Assert.That(simulation.LastFlowDiagnostics.Substeps, Is.Zero);
        });
    }

    /// <summary>
    ///     Sod's data over a 100-cell tube, into one 100-cell chunk or two 50-cell chunks.
    /// </summary>
    private static void FillSod(
        AtmosSimulation simulation, AtmosChunkHandle left, AtmosChunkHandle? right, int diaphragm, bool mirrored)
    {
        float leftTemperature = LeftPressure / (LeftDensity * AtmosPhysicalConstants.MolarGasConstant);
        float rightTemperature = RightPressure / (RightDensity * AtmosPhysicalConstants.MolarGasConstant);
        for (int i = 0; i < Length; i++)
        {
            int tubeX = mirrored ? Length - 1 - i : i;
            bool high = tubeX < diaphragm;
            AtmosChunkHandle chunk = right == null || i < Half ? left : right.Value;
            int x = right == null ? i : i % Half;
            simulation.AddGasToVoxel(
                chunk, x, 0, 0, SimTestHelpers.EulerUnitGasName,
                high ? LeftDensity : RightDensity,
                high ? leftTemperature : rightTemperature);
        }
    }

    private static void FillRoom(
        AtmosSimulation simulation, AtmosChunkHandle chunk,
        int width, int height, int depth, float moles)
    {
        for (int z = 0; z < depth; z++)
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
            simulation.AddGasToVoxel(chunk, x, y, z, SimTestHelpers.EulerAirGasName, moles, Temperature);
    }

    private static double TotalMoles(AtmosSimulation simulation, int gasId, params AtmosChunkHandle[] chunks)
    {
        double total = 0d;
        foreach (var chunk in chunks)
        {
            var snapshot = simulation.GetChunkSnapshot(chunk);
            for (int index = 0; index < snapshot.Temperature.Length; index++)
                total += SimTestHelpers.Moles(snapshot, gasId, index);
        }

        return total;
    }

    private static double TotalMoles(AtmosSimulation simulation, params AtmosChunkHandle[] chunks)
    {
        double total = 0d;
        foreach (var chunk in chunks)
        {
            foreach (var gas in simulation.GetChunkSnapshot(chunk).Gases)
            {
                foreach (float moles in gas.Moles)
                    total += moles;
            }
        }

        return total;
    }

    private static double TotalEnergy(AtmosConfig config, AtmosSimulation simulation, params AtmosChunkHandle[] chunks)
    {
        double total = 0d;
        foreach (var chunk in chunks)
            total += SimTestHelpers.TotalEulerEnergy(config, simulation.GetChunkSnapshot(chunk));

        return total;
    }

    /// <summary>
    ///     FNV-1a over everything in a chunk snapshot the Euler stage can change, plus its sleep state.
    /// </summary>
    internal static ulong Digest(AtmosChunkSnapshot snapshot)
    {
        ulong hash = 14695981039346656037UL;
        void Add(int value)
        {
            hash = (hash ^ (uint)value) * 1099511628211UL;
        }

        Add(snapshot.IsAwake ? 1 : 0);
        Add(snapshot.SleepTimer);
        for (int index = 0; index < snapshot.Temperature.Length; index++)
        {
            Add(BitConverter.SingleToInt32Bits(snapshot.Temperature[index]));
            Add(BitConverter.SingleToInt32Bits(snapshot.TotalPressure[index]));
            Add(BitConverter.SingleToInt32Bits(snapshot.VelocityX[index]));
            Add(BitConverter.SingleToInt32Bits(snapshot.VelocityY[index]));
            Add(BitConverter.SingleToInt32Bits(snapshot.VelocityZ[index]));
            foreach (var gas in snapshot.Gases)
                Add(BitConverter.SingleToInt32Bits(gas.Moles[index]));
        }

        return hash;
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
