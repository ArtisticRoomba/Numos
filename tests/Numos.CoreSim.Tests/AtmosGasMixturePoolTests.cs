using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.GasReactions;
using Numos.CoreSim.Solvers;
using Numos.Maths;

namespace Numos.CoreSim.Tests;

[TestFixture]
public sealed class AtmosGasMixturePoolTests
{
    private const int Hydrogen = 0;
    private const int Oxygen = 1;
    private const int Nitrogen = 2;
    private const int Water = 3;
    private const int GasCount = 4;

    private static AtmosConfigSnapshot CreateConfig(bool reactions = true, float condensationRateFactor = 0.5f)
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

        var config = new AtmosConfig
        {
            CondensationRateFactor = condensationRateFactor,
            GasRegistry = [hydrogen, oxygen, nitrogen, water]
        };

        if (reactions)
            config.SolverConfigurations = [new GasReactionConfig(standardReactions: [hydrogenFire])];

        return config.CreateSnapshot();
    }

    // Runs the pool stages on the schedule a standalone kernel gives its own stages at its default 20 Hz.
    private static void Tick(AtmosGasMixturePool pool, long tick, int tickRate = 20)
    {
        int gasInterval = BuiltInStageSchedule.IntervalFor(BuiltInStageSchedule.GasRunsPerSecond, tickRate);
        int thermalInterval = BuiltInStageSchedule.IntervalFor(BuiltInStageSchedule.ThermalRunsPerSecond, tickRate);
        pool.BeginTick(tick);
        try
        {
            if (tick % thermalInterval == 0)
                pool.SolveThermodynamics();
            if (tick % gasInterval == 0)
                pool.SolveGasReactions(BuiltInStageSchedule.GapTime(gasInterval, tickRate));
        }
        finally
        {
            pool.CompleteTick();
        }
    }

    private static float Ignition(AtmosChunk chunk, ushort cell)
    {
        return chunk.TryGetField(ReactionSolver.IgnitionPercentField, out var array) ? array[cell] : 0f;
    }

    private static float IgnitedTemperature(AtmosChunk chunk, ushort cell)
    {
        return chunk.TryGetField(ReactionSolver.IgnitedVolumeTempField, out var array) ? array[cell] : 0f;
    }

    [Test]
    public void Allocate_ReusesLowestFreeSlotAfterRelease()
    {
        using var pool = new AtmosGasMixturePool();
        var first = pool.Allocate(1f, 300f);
        var second = pool.Allocate(1f, 300f);
        var third = pool.Allocate(1f, 300f);
        Assert.That(new[] { first.Slot, second.Slot, third.Slot }, Is.EqualTo(new[] { 0, 1, 2 }));

        pool.Release(second.Slot, second.Generation);
        pool.Release(first.Slot, first.Generation);

        Assert.That(pool.Allocate(1f, 300f).Slot, Is.EqualTo(0));
        Assert.That(pool.Allocate(1f, 300f).Slot, Is.EqualTo(1));
        Assert.That(pool.Allocate(1f, 300f).Slot, Is.EqualTo(3));
        Assert.That(pool.AllocatedCount, Is.EqualTo(4));
    }

    [Test]
    public void Release_BumpsGenerationAndStalesTheOldHandle()
    {
        using var pool = new AtmosGasMixturePool();
        var handle = pool.Allocate(2f, 300f);
        pool.Release(handle.Slot, handle.Generation);

        Assert.That(pool.IsLive(handle.Slot, handle.Generation), Is.False);
        Assert.That(() => pool.GetVolume(handle.Slot, handle.Generation), Throws.InvalidOperationException);
        Assert.That(() => pool.Release(handle.Slot, handle.Generation), Throws.InvalidOperationException);

        var reused = pool.Allocate(3f, 300f);
        Assert.That(reused.Slot, Is.EqualTo(handle.Slot));
        Assert.That(reused.Generation, Is.Not.EqualTo(handle.Generation));
        Assert.That(() => pool.GetVolume(handle.Slot, handle.Generation), Throws.InvalidOperationException);
        Assert.That(pool.GetVolume(reused.Slot, reused.Generation), Is.EqualTo(3f));
    }

    [Test]
    public void Allocate_AppendsASecondPageAtTheNextSlotAfterAFullPage()
    {
        using var pool = new AtmosGasMixturePool();
        for (int i = 0; i < AtmosGasMixturePool.PageSize; i++)
            Assert.That(pool.Allocate(1f, 300f).Slot, Is.EqualTo(i));

        Assert.That(pool.PageCount, Is.EqualTo(1));
        Assert.That(pool.SlotCapacity, Is.EqualTo(AtmosGasMixturePool.PageSize));

        var next = pool.Allocate(1f, 300f);
        Assert.That(next.Slot, Is.EqualTo(AtmosGasMixturePool.PageSize));
        Assert.That(pool.PageCount, Is.EqualTo(2));
        Assert.That(pool.SlotCapacity, Is.EqualTo(2 * AtmosGasMixturePool.PageSize));

        var (page, cell) = pool.Resolve(next.Slot, next.Generation);
        Assert.That(page, Is.SameAs(pool.GetPage(1)));
        Assert.That(cell, Is.EqualTo(0));
        Assert.That(page.IsAwake, Is.True);
    }

    [Test]
    public void Allocate_GivesIdenticalHandlesForTheSameSequenceOnTwoPools()
    {
        static List<(int, uint)> Run()
        {
            using var pool = new AtmosGasMixturePool();
            var handles = new List<(int Slot, uint Generation)>();
            var live = new List<(int Slot, uint Generation)>();
            for (int step = 0; step < 700; step++)
            {
                if (step % 3 == 2 && live.Count > 0)
                {
                    int index = (step * 7) % live.Count;
                    pool.Release(live[index].Slot, live[index].Generation);
                    live.RemoveAt(index);
                }

                var handle = pool.Allocate(1f + step % 5, 250f);
                handles.Add(handle);
                live.Add(handle);
            }

            return handles;
        }

        Assert.That(Run(), Is.EqualTo(Run()));
    }

    [Test]
    public void Allocate_RejectsInvalidVolumeAndTemperature()
    {
        using var pool = new AtmosGasMixturePool();
        Assert.That(() => pool.Allocate(0f, 300f), Throws.InstanceOf<ArgumentOutOfRangeException>());
        Assert.That(() => pool.Allocate(float.NaN, 300f), Throws.InstanceOf<ArgumentOutOfRangeException>());
        Assert.That(() => pool.Allocate(1f, -1f), Throws.InstanceOf<ArgumentOutOfRangeException>());
        Assert.That(() => pool.Allocate(1f, float.PositiveInfinity), Throws.InstanceOf<ArgumentOutOfRangeException>());
        Assert.That(pool.AllocatedCount, Is.Zero);
    }

    [Test]
    public void StructuralChanges_AreRejectedDuringATick()
    {
        using var pool = new AtmosGasMixturePool();
        var handle = pool.Allocate(1f, 300f);

        pool.BeginTick(1);
        Assert.That(() => pool.Allocate(1f, 300f), Throws.InvalidOperationException);
        Assert.That(() => pool.Release(handle.Slot, handle.Generation), Throws.InvalidOperationException);
        Assert.That(() => pool.BeginTick(2), Throws.InvalidOperationException);
        Assert.That(pool.Dispose, Throws.InvalidOperationException);
        Assert.That(() => pool.SetConfig(CreateConfig()), Throws.Nothing);
        pool.CompleteTick();

        Assert.That(() => pool.Release(handle.Slot, handle.Generation), Throws.Nothing);
    }

    [Test]
    public void MixtureOperations_UseTheCellVolume()
    {
        using var pool = new AtmosGasMixturePool();
        pool.SetConfig(CreateConfig());
        var small = pool.Allocate(1f, 300f);
        var large = pool.Allocate(4f, 300f);

        foreach (var cell in new[] { small, large })
            pool.AddGas(cell.Slot, cell.Generation, Nitrogen, 10f, 300f);

        float smallPressure = pool.GetPressure(small.Slot, small.Generation);
        float largePressure = pool.GetPressure(large.Slot, large.Generation);
        Assert.That(smallPressure, Is.EqualTo(10f * 300f * AtmosPhysicalConstants.MolarGasConstant / 1f).Within(1e-3f).Percent);
        Assert.That(largePressure, Is.EqualTo(smallPressure / 4f).Within(1e-5f).Percent);

        pool.SetVolume(large.Slot, large.Generation, 1f);
        Assert.That(pool.GetPressure(large.Slot, large.Generation), Is.EqualTo(smallPressure));
        var (page, cell2) = pool.Resolve(large.Slot, large.Generation);
        Assert.That(page.TotalPressure[cell2], Is.EqualTo(smallPressure));

        Assert.That(() => pool.SetVolume(large.Slot, large.Generation, 0f), Throws.InstanceOf<ArgumentOutOfRangeException>());
        Assert.That(pool.GetVolume(large.Slot, large.Generation), Is.EqualTo(1f));

        var state = pool.Capture(large.Slot, large.Generation);
        Assert.That(state.Volume, Is.EqualTo(1f));
        Assert.That(state.Temperature, Is.EqualTo(300f));
        Assert.That(state.Gases, Is.EqualTo(new[] { KeyValuePair.Create(Nitrogen, 10f) }));
    }

    [Test]
    public void SetVolume_KeepsTheOldVolumeWhenPressureOverflows()
    {
        using var pool = new AtmosGasMixturePool();
        pool.SetConfig(CreateConfig());
        var cell = pool.Allocate(1f, 1e30f);
        pool.AddGas(cell.Slot, cell.Generation, Nitrogen, 1f, 1e30f);

        Assert.That(() => pool.SetVolume(cell.Slot, cell.Generation, 1e-30f), Throws.InvalidOperationException);
        Assert.That(pool.GetVolume(cell.Slot, cell.Generation), Is.EqualTo(1f));
    }

    [Test]
    public void MixtureOperations_RoundTripThroughCaptureAndReplace()
    {
        using var pool = new AtmosGasMixturePool();
        pool.SetConfig(CreateConfig());
        var source = pool.Allocate(2f, 300f);
        pool.AddGas(source.Slot, source.Generation, Oxygen, 3f, 400f);
        pool.AddGas(source.Slot, source.Generation, Hydrogen, 1f, 200f);
        pool.AdjustMoles(source.Slot, source.Generation, Nitrogen, 5f);
        pool.AdjustMoles(source.Slot, source.Generation, Nitrogen, -50f);
        pool.SetMoles(source.Slot, source.Generation, Water, 0.5f);

        Assert.That(pool.GetActiveGasCount(source.Slot, source.Generation), Is.EqualTo(3));
        Assert.That(pool.GetMoles(source.Slot, source.Generation, Nitrogen), Is.Zero);
        Assert.That(pool.GetTotalMoles(source.Slot, source.Generation), Is.EqualTo(4.5f));

        var state = pool.Capture(source.Slot, source.Generation);
        Assert.That(state.Gases.Select(gas => gas.Key), Is.EqualTo(new[] { Hydrogen, Oxygen, Water }));

        var target = pool.Allocate(2f, 100f);
        pool.Replace(target.Slot, target.Generation, state.Temperature, state.Gases);
        var copy = pool.Capture(target.Slot, target.Generation);
        Assert.That(copy.Temperature, Is.EqualTo(state.Temperature));
        Assert.That(copy.Gases, Is.EqualTo(state.Gases));
        Assert.That(pool.GetPressure(target.Slot, target.Generation),
            Is.EqualTo(pool.GetPressure(source.Slot, source.Generation)));

        pool.Clear(target.Slot, target.Generation);
        Assert.That(pool.GetTotalMoles(target.Slot, target.Generation), Is.Zero);
        Assert.That(pool.GetPressure(target.Slot, target.Generation), Is.Zero);
    }

    [Test]
    public void Tick_WithAnEmptyPoolOrNoConfigurationDoesNothing()
    {
        using var empty = new AtmosGasMixturePool();
        for (int tick = 1; tick <= 4; tick++)
            Assert.That(() => Tick(empty, tick), Throws.Nothing);

        using var unconfigured = new AtmosGasMixturePool();
        var cell = unconfigured.Allocate(1f, 300f);
        for (int tick = 1; tick <= 4; tick++)
            Assert.That(() => Tick(unconfigured, tick), Throws.Nothing);

        Assert.That(unconfigured.GetTemperature(cell.Slot, cell.Generation), Is.EqualTo(300f));
    }

    [Test]
    public void Tick_NeverMovesGasOrHeatBetweenAdjacentCellsOrSpreadsIgnition()
    {
        using var pool = new AtmosGasMixturePool();
        pool.SetConfig(CreateConfig());

        var burning = pool.Allocate(1f, 300f);
        var coldFuel = pool.Allocate(1f, 300f);
        var inert = pool.Allocate(1f, 300f);
        var condensing = pool.Allocate(1f, 300f);

        pool.AddGas(burning.Slot, burning.Generation, Hydrogen, 20f, 1500f);
        pool.AddGas(burning.Slot, burning.Generation, Oxygen, 10f, 1500f);
        pool.AddGas(coldFuel.Slot, coldFuel.Generation, Hydrogen, 20f, 250f);
        pool.AddGas(coldFuel.Slot, coldFuel.Generation, Oxygen, 10f, 250f);
        pool.AddGas(inert.Slot, inert.Generation, Nitrogen, 7f, 90f);
        pool.AddGas(condensing.Slot, condensing.Generation, Water, 30f, 330f);

        var coldBefore = pool.Capture(coldFuel.Slot, coldFuel.Generation);
        var inertBefore = pool.Capture(inert.Slot, inert.Generation);
        float burningHydrogen = pool.GetMoles(burning.Slot, burning.Generation, Hydrogen);
        float condensingWater = pool.GetMoles(condensing.Slot, condensing.Generation, Water);
        var (page, _) = pool.Resolve(burning.Slot, burning.Generation);

        bool everIgnited = false;
        for (int tick = 1; tick <= 60; tick++)
        {
            Tick(pool, tick);

            everIgnited |= Ignition(page, (ushort)burning.Slot) > 0f;
            var cold = pool.Capture(coldFuel.Slot, coldFuel.Generation);
            var cold2 = pool.Capture(inert.Slot, inert.Generation);
            Assert.That(cold.Temperature, Is.EqualTo(coldBefore.Temperature), $"cold fuel temperature, tick {tick}");
            Assert.That(cold.Gases, Is.EqualTo(coldBefore.Gases), $"cold fuel gases, tick {tick}");
            Assert.That(Ignition(page, (ushort)coldFuel.Slot), Is.Zero, $"cold fuel ignition, tick {tick}");
            Assert.That(cold2.Temperature, Is.EqualTo(inertBefore.Temperature), $"inert temperature, tick {tick}");
            Assert.That(cold2.Gases, Is.EqualTo(inertBefore.Gases), $"inert gases, tick {tick}");
            Assert.That(pool.GetMoles(burning.Slot, burning.Generation, Nitrogen), Is.Zero);
            Assert.That(pool.GetMoles(condensing.Slot, condensing.Generation, Hydrogen), Is.Zero);
        }

        Assert.That(everIgnited, Is.True, "the hot cell should ignite");
        Assert.That(pool.GetMoles(burning.Slot, burning.Generation, Hydrogen), Is.LessThan(burningHydrogen));
        Assert.That(pool.GetMoles(condensing.Slot, condensing.Generation, Water), Is.LessThan(condensingWater));
    }

    [Test]
    public void Tick_MatchesAnIsolatedGridVoxelWhenAFlammableMixtureIgnites()
    {
        AssertMatchesIsolatedVoxel(
            [(Hydrogen, 20f, 1500f), (Oxygen, 10f, 1500f), (Nitrogen, 2f, 1500f)],
            ticks: 50,
            expectIgnition: true);
    }

    [Test]
    public void Tick_MatchesAnIsolatedGridVoxelWhenWaterVapourCondenses()
    {
        AssertMatchesIsolatedVoxel(
            [(Water, 9000f / 256f, 293f), (Nitrogen, 4f, 293f)],
            ticks: 50,
            expectIgnition: false);
    }

    [Test]
    public void Tick_MatchesAnIsolatedGridVoxelForAnIgnitionThatFadesOut()
    {
        AssertMatchesIsolatedVoxel(
            [(Hydrogen, 1f, 900f), (Oxygen, 30f, 900f), (Water, 3f, 900f)],
            ticks: 50,
            expectIgnition: null);
    }

    private static void AssertMatchesIsolatedVoxel(
        (int GasId, float Moles, float Temperature)[] mixture, int ticks, bool? expectIgnition)
    {
        var config = CreateConfig();
        using var kernel = new AtmosKernel(3, 3, 3);
        kernel.SetAtmosConfig(config);
        kernel.CreateAndRegisterChunk(default, 3, 3, 3);
        Assert.That(kernel.TryGetChunk(default, out var chunk), Is.True);
        ushort voxel = chunk.GetIndex(1, 1, 1);
        chunk.SetChunkClassification(VoxelClassification.RoomSolid);
        chunk.SetVoxelClassification(voxel, VoxelClassification.RoomUnassigned);
        chunk.Wake();

        using var pool = new AtmosGasMixturePool();
        pool.SetConfig(config);
        var cell = pool.Allocate(config.GetVoxelVolume(), mixture[0].Temperature);
        var (page, pageCell) = pool.Resolve(cell.Slot, cell.Generation);

        var (generation, _) = kernel.GetVoxelMixtureIdentity(default, 1, 1, 1);
        foreach (var (gasId, moles, temperature) in mixture)
        {
            kernel.AddVoxelMixtureGas(default, generation, voxel, gasId, moles, temperature);
            pool.AddGas(cell.Slot, cell.Generation, gasId, moles, temperature);
        }

        bool sawIgnition = false;
        bool sawChange = false;
        float initialTemperature = chunk.Temperature[voxel];
        for (int tick = 1; tick <= ticks; tick++)
        {
            kernel.Tick();
            Tick(pool, kernel.TickCount);

            string at = $"tick {tick}";
            for (int gasId = 0; gasId < GasCount; gasId++)
            {
                Assert.That(
                    GasMixtureCells.GetGasMoles(page, pageCell, gasId),
                    Is.EqualTo(GasMixtureCells.GetGasMoles(chunk, voxel, gasId)),
                    $"gas {gasId}, {at}");
            }

            Assert.That(page.Temperature[pageCell], Is.EqualTo(chunk.Temperature[voxel]), $"temperature, {at}");
            Assert.That(Ignition(page, pageCell), Is.EqualTo(Ignition(chunk, voxel)), $"ignition percent, {at}");
            Assert.That(
                IgnitedTemperature(page, pageCell),
                Is.EqualTo(IgnitedTemperature(chunk, voxel)),
                $"ignited temperature, {at}");

            sawIgnition |= Ignition(page, pageCell) > 0f;
            sawChange |= page.Temperature[pageCell] != initialTemperature;
        }

        Assert.That(sawChange, Is.True, "the scenario should change the mixture");
        if (expectIgnition is { } expected)
            Assert.That(sawIgnition, Is.EqualTo(expected));
    }

    [Test]
    public void Tick_ScalesCondensationWithTheCellVolume()
    {
        using var pool = new AtmosGasMixturePool();
        // A slow rate keeps the run clear of the absolute leftover cutoff, which would end the 1x cell's
        // condensation earlier than the 4x cell's.
        pool.SetConfig(CreateConfig(reactions: false, condensationRateFactor: 0.1f));

        var one = pool.Allocate(1f, 293f);
        var four = pool.Allocate(4f, 293f);
        const float moles = 9000f / 256f;
        pool.AddGas(one.Slot, one.Generation, Water, moles, 293f);
        pool.AddGas(four.Slot, four.Generation, Water, 4f * moles, 293f);
        pool.AddGas(one.Slot, one.Generation, Nitrogen, 2f, 293f);
        pool.AddGas(four.Slot, four.Generation, Nitrogen, 8f, 293f);

        float startTemperature = pool.GetTemperature(one.Slot, one.Generation);
        for (int tick = 1; tick <= 40; tick++)
        {
            Tick(pool, tick);

            Assert.That(
                pool.GetTemperature(four.Slot, four.Generation),
                Is.EqualTo(pool.GetTemperature(one.Slot, one.Generation)).Within(1e-4f).Percent,
                $"temperature, tick {tick}");

            Assert.That(
                pool.GetPressure(four.Slot, four.Generation),
                Is.EqualTo(pool.GetPressure(one.Slot, one.Generation)).Within(1e-3f).Percent,
                $"pressure, tick {tick}");

            Assert.That(
                pool.GetMoles(four.Slot, four.Generation, Water),
                Is.EqualTo(4f * pool.GetMoles(one.Slot, one.Generation, Water)).Within(1e-3f).Percent,
                $"vapour, tick {tick}");
        }

        Assert.That(pool.GetTemperature(one.Slot, one.Generation), Is.GreaterThan(startTemperature));
        Assert.That(pool.GetMoles(one.Slot, one.Generation, Water), Is.LessThan(moles));
    }

    [Test]
    public void Tick_KeepsTheCachesInSyncWithTheMoles()
    {
        using var pool = new AtmosGasMixturePool();
        pool.SetConfig(CreateConfig());
        var cell = pool.Allocate(1f, 1500f);
        pool.AddGas(cell.Slot, cell.Generation, Hydrogen, 20f, 1500f);
        pool.AddGas(cell.Slot, cell.Generation, Oxygen, 10f, 1500f);
        var (page, pageCell) = pool.Resolve(cell.Slot, cell.Generation);

        for (int tick = 1; tick <= 20; tick++)
        {
            Tick(pool, tick);

            Assert.That(page.TotalPressure[pageCell], Is.EqualTo(pool.GetPressure(cell.Slot, cell.Generation)), $"tick {tick}");
            Assert.That(
                page.TotalHeatCapacity[pageCell],
                Is.EqualTo(AtmosSolverMath.CalculateHeatCapacityAtVoxel(CreateConfig(), page, pageCell)),
                $"tick {tick}");
        }
    }
}
