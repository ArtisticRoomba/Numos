using Numos.API;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.GasReactions;
using Numos.CoreSim.Solvers;
using Numos.Maths;

namespace Numos.CoreSim.IntegrationTests;

/// <summary>
///     Every built-in stage must leave <see cref="AtmosChunk.TotalPressure" />, <see cref="AtmosChunk.TotalHeatCapacity" />
///     and <see cref="AtmosChunk.IsVacuum" /> equal to what the current moles and temperatures produce. A probe stage
///     registered after each built-in stage recomputes the three fields and compares them.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class StageDerivedStateTests
{
    private const int Hydrogen = 0;
    private const int Oxygen = 1;
    private const int Nitrogen = 2;
    private const int Water = 3;
    private const int ChunkSize = 4;
    private const int TickRate = 960;
    private const int Ticks = 960;
    private const string ProbePrefix = "probe/";

    private static readonly string[] Fields = ["pressure", "heat-capacity", "is-vacuum"];

    [TestCase(AtmosTransportModel.Euler)]
    [TestCase(AtmosTransportModel.Legacy)]
    public void EveryStage_LeavesDerivedStateCurrent(AtmosTransportModel model)
    {
        var config = CreateConfig();
        using var simulation = new AtmosSimulation(config, ChunkSize, ChunkSize, ChunkSize, model, TickRate);
        var stages = AtmosWorldSolverPipeline.GetDefaultLayout(model);
        var recorder = new Recorder();
        var runs = new Dictionary<string, int>();
        var world = simulation.World;
        var steps = world.Solvers.Steps.ToDictionary(step => step.Name);

        foreach (string stage in stages)
        {
            string name = stage;
            runs[name] = 0;
            world.Solvers.Register(
                ProbePrefix + name,
                context =>
                {
                    runs[name]++;
                    foreach (var probed in context.Simulations)
                        Check(config, probed, name, recorder);
                },
                after: name,
                interval: steps[name].Interval);
        }

        Populate(simulation);

        for (int tick = 0; tick < Ticks; tick++)
            simulation.Tick();

        foreach (string stage in stages)
            Assert.That(runs[stage], Is.GreaterThan(2), $"{stage} probe ran too few times to mean anything");

        foreach (string stage in stages)
        {
            foreach (string field in Fields)
            {
                var (maxUlp, mismatches, samples) = recorder.Get(stage, field);
                TestContext.Out.WriteLine(
                    $"{model} {stage,-26} {field,-14} samples={samples} mismatches={mismatches} maxUlp={maxUlp}");
            }
        }

        Assert.Multiple(() =>
        {
            foreach (string stage in stages)
            {
                foreach (string field in Fields)
                {
                    var (maxUlp, mismatches, _) = recorder.Get(stage, field);
                    Assert.That(maxUlp, Is.LessThanOrEqualTo(ToleranceUlp(model, stage, field)),
                        $"{model} {stage} left {field} stale or inexact ({mismatches} voxels differ)");
                }
            }
        });
    }

    /// <summary>
    ///     <c>advection</c> needs only current caches, not the <c>derived-refresh</c> stage itself. From caches that
    ///     match a fresh refresh, a legacy world without the stage reaches the same state after one gas run.
    /// </summary>
    [Test]
    public void Legacy_WithoutDerivedRefresh_MatchesFromConsistentCaches()
    {
        var config = CreateConfig();
        using var refreshed = CreateLegacy(config, true);
        using var unrefreshed = CreateLegacy(config, false);
        int gasInterval = refreshed.World.Solvers.Steps
            .Single(static step => step.Name == AtmosBuiltInSolvers.DerivedRefresh).Interval;

        for (int tick = 0; tick < gasInterval; tick++)
        {
            refreshed.Tick();
            unrefreshed.Tick();
        }

        Assert.Multiple(() =>
        {
            Assert.That(
                unrefreshed.CaptureCheckpoint().ComputeStateHash(),
                Is.EqualTo(refreshed.CaptureCheckpoint().ComputeStateHash()),
                "grid state hash");

            foreach (var handle in refreshed.GetChunkHandles())
            {
                var expected = refreshed.Kernel.GetChunkForDangerousAccess(handle.Position);
                var actual = unrefreshed.Kernel.GetChunkForDangerousAccess(handle.Position);
                for (int index = 0; index < expected.VoxelCount; index++)
                {
                    Assert.That(
                        BitConverter.SingleToInt32Bits(actual.TotalPressure[index]),
                        Is.EqualTo(BitConverter.SingleToInt32Bits(expected.TotalPressure[index])),
                        $"pressure of voxel {index} in {handle.Position}");
                    Assert.That(
                        BitConverter.SingleToInt32Bits(actual.TotalHeatCapacity[index]),
                        Is.EqualTo(BitConverter.SingleToInt32Bits(expected.TotalHeatCapacity[index])),
                        $"heat capacity of voxel {index} in {handle.Position}");
                    Assert.That(
                        actual.IsVacuum[index], Is.EqualTo(expected.IsVacuum[index]),
                        $"vacuum flag of voxel {index} in {handle.Position}");
                }
            }
        });

        static AtmosSimulation CreateLegacy(AtmosConfig config, bool refreshEnabled)
        {
            var simulation = new AtmosSimulation(config, ChunkSize, ChunkSize, ChunkSize, AtmosTransportModel.Legacy, TickRate);
            simulation.World.Solvers.SetEnabled(AtmosBuiltInSolvers.DerivedRefresh, refreshEnabled);
            Populate(simulation);

            // Pin the caches to what the stage computes, so the premise does not depend on how population keeps them.
            var snapshot = new AtmosSolverConfigSnapshot();
            snapshot.Capture(simulation.Kernel.GetAtmosConfig());
            foreach (var handle in simulation.GetChunkHandles())
                DerivedStateRefresh.RefreshChunk(simulation.Kernel.GetChunkForDangerousAccess(handle.Position), snapshot);

            return simulation;
        }
    }

    /// <summary>
    ///     <c>derived-refresh</c> rebuilds caches that something outside the solvers left wrong.
    /// </summary>
    [Test]
    public void DerivedRefresh_CorrectsStaleCaches()
    {
        var config = CreateConfig();
        using var simulation = new AtmosSimulation(config, ChunkSize, ChunkSize, ChunkSize, AtmosTransportModel.Legacy, TickRate);
        var world = simulation.World;
        var stale = new Recorder();
        var recorder = new Recorder();
        int runs = 0;
        world.Solvers.Register(
            ProbePrefix + AtmosBuiltInSolvers.DerivedRefresh,
            context =>
            {
                runs++;
                foreach (var probed in context.Simulations)
                    Check(config, probed, AtmosBuiltInSolvers.DerivedRefresh, recorder);
            },
            after: AtmosBuiltInSolvers.DerivedRefresh,
            interval: world.Solvers.Steps.Single(static step => step.Name == AtmosBuiltInSolvers.DerivedRefresh).Interval);

        Populate(simulation);
        foreach (var handle in simulation.GetChunkHandles())
        {
            var chunk = simulation.Kernel.GetChunkForDangerousAccess(handle.Position);
            for (int active = 0; active < chunk.ActiveAirCount; active++)
            {
                ushort index = chunk.ActiveAirIndices[active];
                chunk.TotalPressure[index] = 12_345f;
                chunk.TotalHeatCapacity[index] = 1f;
                chunk.IsVacuum[index] = !chunk.IsVacuum[index];
            }
        }

        Check(config, simulation, "stale", stale);
        while (runs == 0)
            simulation.Tick();

        Assert.Multiple(() =>
        {
            foreach (string field in Fields)
            {
                Assert.That(stale.Get("stale", field).Mismatches, Is.GreaterThan(0), $"{field} was staled");
                var (maxUlp, mismatches, _) = recorder.Get(AtmosBuiltInSolvers.DerivedRefresh, field);
                Assert.That(maxUlp, Is.Zero, $"derived-refresh left {field} stale ({mismatches} voxels differ)");
            }
        });
    }

    [TearDown]
    public void RestoreSwitches()
    {
        AdvectionSolver.UseRefreshedTotals = true;
    }

    /// <summary>
    ///     In the default legacy layout <c>advection</c> takes the mole totals <c>derived-refresh</c> kept, and the
    ///     result matches summing them again on every run.
    /// </summary>
    [Test]
    public void Legacy_RefreshedTotals_AreUsedAndChangeNoBit()
    {
        var config = CreateConfig();
        using var handOff = CreateLegacyAtPinnedRate(config);
        using var summing = CreateLegacyAtPinnedRate(config);
        int chunkCount = handOff.GetChunkHandles().Length;

        for (int tick = 1; tick <= 20; tick++)
        {
            TickPair(handOff, summing);
            Assert.That(AdvectionSolver.LastRefreshedChunkCount, Is.GreaterThan(0).And.LessThanOrEqualTo(chunkCount),
                $"hand-off used on tick {tick}");
            Assert.That(handOff.World.ComputeStateHash(), Is.EqualTo(summing.World.ComputeStateHash()), $"tick {tick}");
        }
    }

    /// <summary>
    ///     A restore drops the kept totals. Ticking on from the restored state matches the run that never restored.
    /// </summary>
    [Test]
    public void Legacy_RestoreMidRun_MatchesUnbrokenRun()
    {
        var config = CreateConfig();
        using var simulation = CreateLegacyAtPinnedRate(config);
        var world = simulation.World;
        for (int tick = 0; tick < 5; tick++)
            simulation.Tick();

        var midway = world.CaptureCheckpoint();
        for (int tick = 0; tick < 10; tick++)
            simulation.Tick();

        var unbroken = world.ComputeStateHash();
        world.RestoreCheckpoint(midway);
        simulation.Tick();
        Assert.That(AdvectionSolver.LastRefreshedChunkCount, Is.GreaterThan(0), "hand-off used after the restore");
        for (int tick = 1; tick < 10; tick++)
            simulation.Tick();

        Assert.That(world.ComputeStateHash(), Is.EqualTo(unbroken));
    }

    /// <summary>
    ///     When moles can have changed since <c>derived-refresh</c>, or it did not run on this tick, <c>advection</c>
    ///     sums the moles itself. The result then matches a world that never takes the kept totals.
    /// </summary>
    /// <param name="variation">What breaks the hand-off after a few normal ticks.</param>
    [TestCase("stage-edits-moles")]
    [TestCase("stage-between")]
    [TestCase("refresh-disabled")]
    [TestCase("refresh-interval")]
    public void Legacy_InvalidHandOff_FallsBackToSumming(string variation)
    {
        var config = CreateConfig();
        using var handOff = CreateLegacyAtPinnedRate(config);
        using var summing = CreateLegacyAtPinnedRate(config);
        for (int tick = 0; tick < 3; tick++)
            TickPair(handOff, summing);

        Apply(handOff);
        Apply(summing);
        bool everySkipped = true;
        for (int tick = 1; tick <= 12; tick++)
        {
            TickPair(handOff, summing);
            Assert.That(handOff.World.ComputeStateHash(), Is.EqualTo(summing.World.ComputeStateHash()), $"tick {tick}");
            if (variation != "refresh-interval")
                Assert.That(AdvectionSolver.LastRefreshedChunkCount, Is.Zero, $"hand-off refused on tick {tick}");
            else
                everySkipped &= AdvectionSolver.LastRefreshedChunkCount == 0;
        }

        if (variation == "refresh-interval")
            Assert.That(everySkipped, Is.False, "ticks on which both stages run still use the hand-off");

        void Apply(AtmosSimulation simulation)
        {
            var solvers = simulation.World.Solvers;
            switch (variation)
            {
                case "stage-edits-moles":
                    var snapshot = new AtmosSolverConfigSnapshot();
                    snapshot.Capture(simulation.Kernel.GetAtmosConfig());
                    solvers.Register("test/add-moles", context =>
                    {
                        // A raw write that refreshes the caches but never calls MarkChanged. Only the pipeline's
                        // revision bump before this stage tells advection the kept totals are stale.
                        foreach (var probed in context.Simulations)
                        foreach (var handle in probed.GetChunkHandles())
                        {
                            var chunk = probed.Kernel.GetChunkForDangerousAccess(handle.Position);
                            if (!chunk.IsAwake || chunk.ActiveGasCount == 0)
                                continue;

                            for (int active = 0; active < chunk.ActiveAirCount; active += 3)
                                chunk.ActiveGases[0].Moles[chunk.ActiveAirIndices[active]] += 4f;

                            DerivedStateRefresh.RefreshChunk(chunk, snapshot);
                        }
                    }, after: AtmosBuiltInSolvers.DerivedRefresh);
                    break;
                case "stage-between":
                    solvers.Register("test/no-op", static _ => { }, after: AtmosBuiltInSolvers.DerivedRefresh);
                    break;
                case "refresh-disabled":
                    solvers.SetEnabled(AtmosBuiltInSolvers.DerivedRefresh, false);
                    break;
                case "refresh-interval":
                    solvers.SetInterval(AtmosBuiltInSolvers.DerivedRefresh, 3);
                    break;
            }
        }
    }

    private static AtmosSimulation CreateLegacyAtPinnedRate(AtmosConfig config)
    {
        // At the pinned rate every gas stage runs on every tick.
        var simulation = new AtmosSimulation(
            config, ChunkSize, ChunkSize, ChunkSize, AtmosTransportModel.Legacy, SimTestHelpers.PinnedTickRate);
        Populate(simulation);
        return simulation;
    }

    /// <summary>
    ///     Ticks <paramref name="handOff" /> with the hand-off allowed, then <paramref name="summing" /> without it.
    ///     <see cref="AdvectionSolver.LastRefreshedChunkCount" /> then describes the hand-off world's tick.
    /// </summary>
    private static void TickPair(AtmosSimulation handOff, AtmosSimulation summing)
    {
        AdvectionSolver.UseRefreshedTotals = false;
        summing.Tick();
        AdvectionSolver.UseRefreshedTotals = true;
        handOff.Tick();
    }

    /// <summary>
    ///     The allowed ULP difference. Zero means bit-exact. Each nonzero entry is the largest difference measured for
    ///     that stage and field. The gaps are a few ULP, so they come from summation order and not from a stale cache.
    /// </summary>
    private static long ToleranceUlp(AtmosTransportModel model, string stage, string field)
    {
        return (model, stage, field) switch
        {
            // The reaction stage changes moles per voxel and patches the pressure cache.
            (AtmosTransportModel.Euler, AtmosBuiltInSolvers.GasReactions, "pressure") => 3,
            (AtmosTransportModel.Legacy, AtmosBuiltInSolvers.GasReactions, "pressure") => 3,
            (AtmosTransportModel.Legacy, AtmosBuiltInSolvers.GasReactions, "heat-capacity") => 3,
            // Legacy stages after advection patch the heat capacity cache while they move moles. The result differs
            // from a fresh sum only in the last bits.
            (AtmosTransportModel.Legacy, AtmosBuiltInSolvers.BoundaryFlow, "heat-capacity") => 4,
            (AtmosTransportModel.Legacy, AtmosBuiltInSolvers.Thermodynamics, "heat-capacity") => 4,
            (AtmosTransportModel.Legacy, AtmosBuiltInSolvers.ExplicitThermalTransport, "heat-capacity") => 4,
            (AtmosTransportModel.Legacy, AtmosBuiltInSolvers.ThermalBoundary, "heat-capacity") => 3,
            _ => 0
        };
    }

    private static void Check(AtmosConfig config, AtmosSimulation simulation, string stage, Recorder recorder)
    {
        foreach (var handle in simulation.GetChunkHandles())
        {
            var chunk = simulation.Kernel.GetChunkForDangerousAccess(handle.Position);
            for (int active = 0; active < chunk.ActiveAirCount; active++)
            {
                ushort index = chunk.ActiveAirIndices[active];
                float totalMoles = AtmosSolverMath.GetTotalMoles(chunk, index);

                // Pure recomputation. CalculatePressureAtVoxel writes IsVacuum and can empty the voxel.
                bool expectedVacuum = totalMoles <= 0f;
                float expectedPressure = expectedVacuum
                    ? 0f
                    : AtmosSolverMath.CalculatePressure(config, totalMoles, chunk.Temperature[index]);
                float expectedHeat = AtmosSolverMath.CalculateHeatCapacityAtVoxel(config, chunk, index);

                recorder.Add(stage, "pressure", UlpDistance(expectedPressure, chunk.TotalPressure[index]));
                recorder.Add(stage, "heat-capacity", UlpDistance(expectedHeat, chunk.TotalHeatCapacity[index]));
                recorder.Add(stage, "is-vacuum", expectedVacuum == chunk.IsVacuum[index] ? 0 : long.MaxValue);
            }
        }
    }

    private static long UlpDistance(float expected, float actual)
    {
        if (BitConverter.SingleToInt32Bits(expected) == BitConverter.SingleToInt32Bits(actual))
            return 0;

        if (!float.IsFinite(expected) || !float.IsFinite(actual))
            return long.MaxValue;

        return Math.Abs(Ordered(expected) - Ordered(actual));

        static long Ordered(float value)
        {
            int bits = BitConverter.SingleToInt32Bits(value);
            return bits < 0 ? int.MinValue - (long)bits : bits;
        }
    }

    private static void Populate(AtmosSimulation simulation)
    {
        // Gas gradient and temperature gradient across x.
        var left = SimTestHelpers.CreateOpenChunk(simulation, default);
        // Hot hydrogen and oxygen pocket beside nitrogen, so the reaction stage has work to do.
        var middle = SimTestHelpers.CreateOpenChunk(simulation, Int3.PosX);
        // A void chunk: every face toward it vents.
        SimTestHelpers.CreateOpenChunk(simulation, 2 * Int3.PosX, new VoxelClassification(VoxelClassification.RoomVoid));
        // A chunk with a solid pillar and water vapour.
        var upper = SimTestHelpers.CreateOpenChunk(simulation, Int3.PosY);

        for (int z = 0; z < ChunkSize; z++)
        for (int y = 0; y < ChunkSize; y++)
        for (int x = 0; x < ChunkSize; x++)
        {
            float t = 280f + 40f * x + 10f * y;
            simulation.AddGasToVoxel(left, x, y, z, Nitrogen, 30f * (x + 1), t);
            simulation.AddGasToVoxel(left, x, y, z, Oxygen, 8f * (ChunkSize - x), t);

            bool hot = x < 2 && y < 2;
            float middleTemperature = hot ? 1500f : 300f;
            simulation.AddGasToVoxel(middle, x, y, z, Nitrogen, 40f, middleTemperature);
            if (x == 3 && y == 3)
                continue;

            simulation.AddGasToVoxel(middle, x, y, z, Hydrogen, hot ? 20f : 2f, middleTemperature);
            simulation.AddGasToVoxel(middle, x, y, z, Oxygen, hot ? 10f : 1f, middleTemperature);

            if (x == 1 && y == 1)
                simulation.SetVoxelClassification(upper, x, y, z, new VoxelClassification(VoxelClassification.RoomSolid));
            else
                simulation.AddGasToVoxel(upper, x, y, z, Water, 5f * (z + 1), 350f + 20f * z);
        }
    }

    private static AtmosConfig CreateConfig()
    {
        var hydrogen = new GasProperties
        {
            Name = "Hydrogen",
            MolarHeatCapacityAtConstantVolume = AtmosPhysicalConstants.IdealDiatomicMolarHeatCapacityAtConstantVolume,
            BoilingPoint = 20.4f,
            MolarEnthalpyOfVaporization = 904f,
            DiffusionCoefficient = 0.02f,
            MolarMass = 0.002016f,
            ThermalConductivity = 0.168f
        };

        var oxygen = new GasProperties
        {
            Name = "Oxygen",
            MolarHeatCapacityAtConstantVolume = AtmosPhysicalConstants.IdealDiatomicMolarHeatCapacityAtConstantVolume,
            BoilingPoint = 90.2f,
            MolarEnthalpyOfVaporization = 6_820f,
            DiffusionCoefficient = 0.02f,
            MolarMass = 0.031998f,
            ThermalConductivity = 0.024f
        };

        var nitrogen = new GasProperties
        {
            Name = "Nitrogen",
            MolarHeatCapacityAtConstantVolume = AtmosPhysicalConstants.IdealDiatomicMolarHeatCapacityAtConstantVolume,
            DiffusionCoefficient = 0.02f,
            MolarMass = 0.028014f,
            ThermalConductivity = 0.024f
        };

        var water = new GasProperties
        {
            Name = "Water Vapour",
            MolarHeatCapacityAtConstantVolume = 28f,
            BoilingPoint = 373.15f,
            CondensationEnabled = true,
            MolarEnthalpyOfVaporization = 40_657f,
            LiquidId = 0,
            DiffusionCoefficient = 0.02f,
            MolarMass = 0.018015f,
            ThermalConductivity = 0.0245f
        };

        var hydrogenFire = new StandardGasReaction(
            new Dictionary<GasProperties, float> { { hydrogen, 2 }, { oxygen, 1 } },
            new Dictionary<GasProperties, float> { { water, 2 } },
            UnitConversions.FromKilojoulePerMole(483.6f),
            1.8e13f,
            UnitConversions.FromKilojoulePerMole(256f),
            new Dictionary<GasProperties, float> { { hydrogen, 1 }, { oxygen, 0.5f } },
            true);

        return new AtmosConfig
        {
            CondensationRateFactor = 0.5f,
            GasRegistry = [hydrogen, oxygen, nitrogen, water],
            SolverConfigurations = [new GasReactionConfig(standardReactions: [hydrogenFire])]
        };
    }

    private sealed class Recorder
    {
        private readonly Dictionary<(string Stage, string Field), (long MaxUlp, int Mismatches, int Samples)> _data = new();

        internal void Add(string stage, string field, long ulp)
        {
            _data.TryGetValue((stage, field), out var entry);
            _data[(stage, field)] = (Math.Max(entry.MaxUlp, ulp), entry.Mismatches + (ulp != 0 ? 1 : 0), entry.Samples + 1);
        }

        internal (long MaxUlp, int Mismatches, int Samples) Get(string stage, string field)
        {
            return _data.GetValueOrDefault((stage, field));
        }
    }
}
