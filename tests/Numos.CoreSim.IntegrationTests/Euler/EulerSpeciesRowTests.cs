using Numos.API;
using Numos.CoreSim.EulerFlow;
using Numos.CoreSim.Replay;
using Numos.Maths;

namespace Numos.CoreSim.IntegrationTests.Euler;

/// <summary>
///     Species rows are per chunk: a chunk only ever grows channels for gases that actually reach it, however many
///     gases the registry or the rest of the world carries.
/// </summary>
[TestFixture]
public sealed class EulerSpeciesRowTests
{
    private const int RegisteredGases = 8;
    private const int LeftGas = 2;
    private const int RightGas = 5;
    private const float Temperature = 293f;
    private const float Atmosphere = AtmosPhysicalConstants.StandardAtmosphericPressure;

    [Test]
    public void NeighboursHoldingDifferentGases_OnlyGrowChannelsForWhatCrosses()
    {
        const int size = 4;
        var config = CreateConfig();
        var simulation = new AtmosSimulation(config, size, size, size);
        using var _ = simulation;
        simulation.World.Solvers.ResetToDefaults(AtmosTransportModel.Euler);
        var left = SimTestHelpers.CreateOpenChunk(simulation, default);
        var right = SimTestHelpers.CreateOpenChunk(simulation, Int3.PosX);
        // A third chunk the left one never touches: its gas must not leak into anyone's channel list.
        var bystander = SimTestHelpers.CreateOpenChunk(simulation, 3 * Int3.PosX);
        float moles = SimTestHelpers.MolesFor(config, Atmosphere, Temperature);
        Fill(simulation, left, size, $"Gas{LeftGas}", moles);
        Fill(simulation, right, size, $"Gas{RightGas}", moles);
        Fill(simulation, bystander, size, "Gas7", moles);
        double initialLeftGas = TotalMoles(simulation, LeftGas, left, right);
        double initialRightGas = TotalMoles(simulation, RightGas, left, right);

        for (int tick = 0; tick < 10; tick++)
            simulation.Tick();

        var leftSnapshot = simulation.GetChunkSnapshot(left);
        var rightSnapshot = simulation.GetChunkSnapshot(right);
        var bystanderSnapshot = simulation.GetChunkSnapshot(bystander);
        TestContext.Out.WriteLine(
            $"left channels: {string.Join(",", leftSnapshot.Gases.Select(gas => gas.GasId))}; " +
            $"right channels: {string.Join(",", rightSnapshot.Gases.Select(gas => gas.GasId))}; " +
            $"bystander channels: {string.Join(",", bystanderSnapshot.Gases.Select(gas => gas.GasId))}");

        Assert.Multiple(() =>
        {
            // Channels are listed in creation order, so only the set is pinned.
            Assert.That(leftSnapshot.Gases.Select(gas => gas.GasId), Is.EquivalentTo(new[] { LeftGas, RightGas }));
            Assert.That(rightSnapshot.Gases.Select(gas => gas.GasId), Is.EquivalentTo(new[] { LeftGas, RightGas }));
            Assert.That(bystanderSnapshot.Gases.Select(gas => gas.GasId), Is.EquivalentTo(new[] { 7 }));
            foreach (var snapshot in new[] { leftSnapshot, rightSnapshot, bystanderSnapshot })
            {
                foreach (var gas in snapshot.Gases)
                {
                    Assert.That(gas.Moles.Max(), Is.GreaterThan(0f), $"channel {gas.GasId} holds nothing");
                }
            }

            Assert.That(TotalMoles(simulation, LeftGas, left, right), Is.EqualTo(initialLeftGas).Within(1e-4).Percent);
            Assert.That(TotalMoles(simulation, RightGas, left, right), Is.EqualTo(initialRightGas).Within(1e-4).Percent);
        });
    }

    [Test]
    public void GasIdBeyondTheRegistry_IsCarriedAcrossChunkFaces()
    {
        const int width = 4;
        const int strayGasId = 5;
        const float strayMoles = 0.5f;
        var config = SimTestHelpers.CreateEulerConfig();
        using var simulation = SimTestHelpers.CreateEulerSimulation(config, width, 1, 1, out var first);
        var second = SimTestHelpers.CreateOpenChunk(simulation, Int3.PosX);
        var third = SimTestHelpers.CreateOpenChunk(simulation, 2 * Int3.PosX);
        float moles = SimTestHelpers.MolesFor(config, Atmosphere, Temperature);
        foreach (var chunk in new[] { first, second, third })
        {
            for (int x = 0; x < width; x++)
                simulation.AddGasToVoxel(chunk, x, 0, 0, SimTestHelpers.EulerAirGasName, moles, Temperature);
        }

        // A channel for an id the registry never saw, which a checkpoint can carry. The merge lists it in the
        // middle chunk, whose sweep then reads the first chunk's rows with it. That chunk's table only ever grew
        // to the registry size.
        simulation.RestoreCheckpoint(WithExtraChannel(simulation.CaptureCheckpoint(), third, strayGasId, strayMoles));
        double initialStray = TotalMoles(simulation, strayGasId, first, second, third);

        Assert.That(
            () =>
            {
                for (int tick = 0; tick < 5; tick++)
                    simulation.Tick();
            },
            Throws.Nothing);

        var secondSnapshot = simulation.GetChunkSnapshot(second);
        TestContext.Out.WriteLine(
            $"stray gas: {initialStray:E6} -> {TotalMoles(simulation, strayGasId, first, second, third):E6}; " +
            $"second chunk holds {TotalMoles(simulation, strayGasId, second):E3}; {simulation.LastFlowDiagnostics}");
        Assert.Multiple(() =>
        {
            Assert.That(TotalMoles(simulation, strayGasId, first, second, third), Is.EqualTo(initialStray).Within(1e-4).Percent);
            Assert.That(secondSnapshot.Gases.Select(gas => gas.GasId), Does.Contain(strayGasId), "the stray gas crossed the face");
        });
    }

    /// <summary>
    ///     The checkpoint with one chunk given a full channel of <paramref name="gasId" />, whether the registry
    ///     knows the id or not.
    /// </summary>
    private static AtmosSimulationCheckpoint WithExtraChannel(
        AtmosSimulationCheckpoint checkpoint, AtmosChunkHandle target, int gasId, float moles)
    {
        var chunks = checkpoint.Chunks.Select(chunk =>
        {
            if (chunk.Position != target.Position)
                return chunk;

            var gases = chunk.Gases
                .Select(gas => new AtmosGasChannelCheckpoint(gas.GasId, gas.Moles.ToArray()))
                .Append(new AtmosGasChannelCheckpoint(gasId, Enumerable.Repeat(moles, chunk.Temperatures.Count).ToArray()))
                .ToArray();

            return new AtmosChunkCheckpoint(
                chunk.Position, chunk.Dimensions, chunk.IsAwake, chunk.SleepTimer,
                chunk.Classifications.ToArray(), chunk.Temperatures.ToArray(), chunk.Pressures.ToArray(),
                chunk.HeatCapacities.ToArray(), chunk.MomentumX.ToArray(), chunk.MomentumY.ToArray(),
                chunk.MomentumZ.ToArray(), chunk.ActiveAirIndices.ToArray(), gases);
        }).ToArray();

        return new AtmosSimulationCheckpoint(checkpoint.Dimensions, chunks);
    }

    /// <summary>
    ///     The Euler test configuration with eight diffusing gases instead of two.
    /// </summary>
    private static AtmosConfig CreateConfig()
    {
        var config = new AtmosConfig
        {
            DefaultTemperatureFallback = AtmosPhysicalConstants.RoomTemperature,
            DefaultMolarHeatCapacityAtConstantVolume =
                AtmosPhysicalConstants.IdealDiatomicMolarHeatCapacityAtConstantVolume,
            VoxelVolume = 1f,
            VacuumThreshold = 0f,
            SleepThreshold = int.MaxValue,
            SleepEpsilon = 0f
        };

        for (int gasId = 0; gasId < RegisteredGases; gasId++)
        {
            config.GasRegistry.Add(new GasProperties
            {
                Name = $"Gas{gasId}",
                MolarMass = SimTestHelpers.EulerAirMolarMass + 0.004f * gasId,
                MolarHeatCapacityAtConstantVolume = AtmosPhysicalConstants.IdealDiatomicMolarHeatCapacityAtConstantVolume,
                DiffusionCoefficient = 0.02f
            });
        }

        config.SolverConfigurations.Add(new EulerFlowConfig { MaxSubstepsPerTick = 0 });
        return config;
    }

    private static void Fill(AtmosSimulation simulation, AtmosChunkHandle chunk, int size, string gas, float moles)
    {
        for (int z = 0; z < size; z++)
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
            simulation.AddGasToVoxel(chunk, x, y, z, gas, moles, Temperature);
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
}
