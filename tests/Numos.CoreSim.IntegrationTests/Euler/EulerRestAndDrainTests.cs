using Numos.API;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.CoreSim.EulerFlow;

namespace Numos.CoreSim.IntegrationTests.Euler;

/// <summary>
///     The Euler stage's behaviour at rest, next to void, and around trace amounts of gas.
/// </summary>
[TestFixture]
public sealed class EulerRestAndDrainTests
{
    private const float Temperature = 293f;
    private const float Pressure = AtmosPhysicalConstants.StandardAtmosphericPressure;

    [Test]
    public void UniformRoomAtRest_IsBitwiseStatic()
    {
        var config = SimTestHelpers.CreateEulerConfig();
        using var simulation = SimTestHelpers.CreateEulerSimulation(config, 6, 5, 4, out var chunk);
        FillRoom(simulation, chunk, 6, 5, 4, SimTestHelpers.MolesFor(config, Pressure, Temperature));
        var before = simulation.GetChunkSnapshot(chunk);

        for (int tick = 0; tick < 10; tick++)
            simulation.Tick();

        var after = simulation.GetChunkSnapshot(chunk);
        AtmosFlowDiagnostics flow = simulation.LastFlowDiagnostics;
        TestContext.Out.WriteLine($"rest: {flow}");
        Assert.Multiple(() =>
        {
            Assert.That(flow.Substeps, Is.GreaterThan(1));
            for (int index = 0; index < before.Temperature.Length; index++)
            {
                Assert.That(
                    BitConverter.SingleToInt32Bits(SimTestHelpers.Moles(after, SimTestHelpers.EulerAirGasId, index)),
                    Is.EqualTo(BitConverter.SingleToInt32Bits(SimTestHelpers.Moles(before, SimTestHelpers.EulerAirGasId, index))),
                    $"moles at {index}");

                Assert.That(
                    BitConverter.SingleToInt32Bits(after.TotalPressure[index]),
                    Is.EqualTo(BitConverter.SingleToInt32Bits(before.TotalPressure[index])), $"pressure at {index}");

                Assert.That(
                    BitConverter.SingleToInt32Bits(after.Temperature[index]),
                    Is.EqualTo(BitConverter.SingleToInt32Bits(before.Temperature[index])), $"temperature at {index}");

                Assert.That(after.VelocityX[index], Is.Zero);
                Assert.That(after.VelocityY[index], Is.Zero);
                Assert.That(after.VelocityZ[index], Is.Zero);
            }
        });
    }

    [Test]
    public void UniformRoomAtRest_FallsAsleep()
    {
        var config = SimTestHelpers.CreateEulerConfig();
        config.SleepThreshold = 5;
        config.SleepEpsilon = 3.5f;
        using var simulation = SimTestHelpers.CreateEulerSimulation(config, 4, 4, 4, out var chunk);
        FillRoom(simulation, chunk, 4, 4, 4, SimTestHelpers.MolesFor(config, Pressure, Temperature));

        for (int tick = 0; tick < 5; tick++)
        {
            simulation.Tick();
            Assert.That(simulation.GetChunkSnapshot(chunk).IsAwake, Is.True, $"awake after tick {tick + 1}");
        }

        simulation.Tick();
        simulation.Tick();

        var snapshot = simulation.GetChunkSnapshot(chunk);
        Assert.That(snapshot.IsAwake, Is.False);
        Assert.That(simulation.LastFlowDiagnostics.Substeps, Is.Zero, "a sleeping chunk is not swept");
    }

    [Test]
    public void VoidAtTheEnd_DrainsTheTubeMonotonically()
    {
        const int length = 20;
        var config = SimTestHelpers.CreateEulerConfig();
        using var simulation = SimTestHelpers.CreateEulerSimulation(config, length, 1, 1, out var chunk);
        simulation.SetVoxelClassification(chunk, length - 1, 0, 0, VoxelClassification.RoomVoid);
        float moles = SimTestHelpers.MolesFor(config, Pressure, Temperature);
        for (int x = 0; x < length - 1; x++)
            simulation.AddGasToVoxel(chunk, x, 0, 0, SimTestHelpers.EulerAirGasName, moles, Temperature);

        double previousTotal = SimTestHelpers.TotalMoles(simulation.GetChunkSnapshot(chunk));
        double initialTotal = previousTotal;
        for (int tick = 0; tick < 150; tick++)
        {
            simulation.Tick();
            var snapshot = simulation.GetChunkSnapshot(chunk);
            double total = SimTestHelpers.TotalMoles(snapshot);
            AssertFinite(snapshot);
            Assert.That(total, Is.LessThanOrEqualTo(previousTotal * (1d + 1e-6)), $"moles rose on tick {tick}");
            for (int x = 0; x < length; x++)
                Assert.That(SimTestHelpers.Moles(snapshot, SimTestHelpers.EulerAirGasId, x), Is.GreaterThanOrEqualTo(0f));

            if (tick % 25 == 24)
            {
                TestContext.Out.WriteLine(
                    $"tick {tick + 1}: total={total:F5} mol ({total / initialTotal:P2}) " +
                    $"last cell={SimTestHelpers.Moles(snapshot, SimTestHelpers.EulerAirGasId, length - 2):E3} {simulation.LastFlowDiagnostics}");
            }

            previousTotal = total;
        }

        var final = simulation.GetChunkSnapshot(chunk);
        Assert.Multiple(() =>
        {
            Assert.That(previousTotal, Is.LessThan(0.5d * initialTotal));
            Assert.That(
                SimTestHelpers.Moles(final, SimTestHelpers.EulerAirGasId, length - 2),
                Is.LessThan(AtmosSolverConstants.MinimumTrackedMoles));

            for (int x = 0; x < length; x++)
            {
                if (SimTestHelpers.Moles(final, SimTestHelpers.EulerAirGasId, x) >= AtmosSolverConstants.MinimumTrackedMoles)
                    continue;

                Assert.That(final.VelocityX[x], Is.Zero, $"emptied cell {x} still moves");
            }
        });
    }

    [Test]
    public void TraceCellNextToAir_StaysFiniteAndKeepsTheTimeStep()
    {
        const int length = 10;
        var config = SimTestHelpers.CreateEulerConfig();
        float moles = SimTestHelpers.MolesFor(config, Pressure, Temperature);

        using var reference = SimTestHelpers.CreateEulerSimulation(config, length, 1, 1, out var referenceChunk);
        for (int x = 0; x < length; x++)
            reference.AddGasToVoxel(referenceChunk, x, 0, 0, SimTestHelpers.EulerAirGasName, moles, Temperature);

        reference.Tick();
        int airSubsteps = reference.LastFlowDiagnostics.Substeps;

        using var simulation = SimTestHelpers.CreateEulerSimulation(config, length, 1, 1, out var chunk);
        for (int x = 0; x < length - 1; x++)
            simulation.AddGasToVoxel(chunk, x, 0, 0, SimTestHelpers.EulerAirGasName, moles, Temperature);

        simulation.AddGasToVoxel(chunk, length - 1, 0, 0, SimTestHelpers.EulerAirGasName, 1e-5f, Temperature);

        for (int tick = 0; tick < 10; tick++)
        {
            simulation.Tick();
            AtmosFlowDiagnostics flow = simulation.LastFlowDiagnostics;
            var snapshot = simulation.GetChunkSnapshot(chunk);
            AssertFinite(snapshot);
            TestContext.Out.WriteLine(
                $"tick {tick + 1}: substeps={flow.Substeps} (air {airSubsteps}) lambda={flow.MaxSignalSpeed:F1} " +
                $"last cell={SimTestHelpers.Moles(snapshot, SimTestHelpers.EulerAirGasId, length - 1):F4} mol");

            Assert.That(flow.SubstepCapEngaged, Is.False);
            Assert.That(flow.Substeps, Is.LessThanOrEqualTo(2 * airSubsteps), $"substeps on tick {tick + 1}");
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

    private static void AssertFinite(AtmosChunkSnapshot snapshot)
    {
        for (int index = 0; index < snapshot.Temperature.Length; index++)
        {
            Assert.That(float.IsFinite(snapshot.Temperature[index]), $"temperature at {index}");
            Assert.That(float.IsFinite(snapshot.TotalPressure[index]), $"pressure at {index}");
            Assert.That(float.IsFinite(snapshot.VelocityX[index]), $"velocity at {index}");
            foreach (var gas in snapshot.Gases)
                Assert.That(float.IsFinite(gas.Moles[index]), $"moles of {gas.GasId} at {index}");
        }
    }
}
