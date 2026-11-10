using Numos.CoreSim;
using Numos.Maths;

namespace Numos.API.Dangerous.Tests;

[TestFixture]
public sealed class AtmosDangerousVacuumCleanupTests
{
    private const string StageName = "tests/dangerous-vacuum/cleanup";

    private static AtmosWorld CreateWorld(out AtmosSimulation simulation, out AtmosChunkHandle handle)
    {
        var config = new TestAtmosConfig { VacuumThreshold = 1000f };
        var world = new AtmosWorld(config);
        simulation = world.CreateSimulation(1, 1, 1);
        handle = simulation.CreateAndRegisterChunk(default);
        simulation.SetVoxelTemperature(handle, 0, 0, 0, 300f);

        // 0.01 mol at 300 K is a few tens of pascals, far under the 1000 Pa threshold.
        simulation.AddGasToVoxel(handle, 0, "TestGas0", 0.01f, 300f);
        world.Solvers.SetEnabled(AtmosBuiltInSolvers.VacuumCleanup, false);
        return world;
    }

    [Test]
    public void WithTheBuiltInStageDisabled_SubThresholdGasSurvives()
    {
        using var world = CreateWorld(out var simulation, out var handle);

        world.Tick();

        Assert.That(simulation.GetChunkSnapshot(handle).TotalPressure[0], Is.GreaterThan(0f));
    }

    [Test]
    public void ApplyVacuumCleanup_FromACustomStage_EmptiesSubThresholdVoxels()
    {
        using var world = CreateWorld(out var simulation, out var handle);
        world.Solvers.Register(StageName, context => context.Dangerous().ApplyVacuumCleanup(simulation));

        world.Tick();

        var snapshot = simulation.GetChunkSnapshot(handle);
        Assert.Multiple(() =>
        {
            Assert.That(snapshot.TotalPressure[0], Is.Zero);
            Assert.That(simulation.Dangerous().GetChunk(handle).GetGasChannel(0).Moles[0], Is.Zero);
        });
    }

    [Test]
    public void ApplyVacuumCleanup_WithASimulationFromAnotherWorld_Throws()
    {
        using var world = CreateWorld(out _, out _);
        using var otherWorld = new AtmosWorld(new TestAtmosConfig());
        var stranger = otherWorld.CreateSimulation(1, 1, 1);
        Exception? error = null;
        world.Solvers.Register(
            StageName,
            context =>
            {
                try
                {
                    context.Dangerous().ApplyVacuumCleanup(stranger);
                }
                catch (Exception exception)
                {
                    error = exception;
                }
            });

        world.Tick();

        Assert.That(error, Is.TypeOf<ArgumentException>());
    }

    [Test]
    public void ApplyVacuumCleanup_WithNullSimulation_Throws()
    {
        using var world = CreateWorld(out _, out _);
        Exception? error = null;
        world.Solvers.Register(
            StageName,
            context =>
            {
                try
                {
                    context.Dangerous().ApplyVacuumCleanup(null!);
                }
                catch (Exception exception)
                {
                    error = exception;
                }
            });

        world.Tick();

        Assert.That(error, Is.TypeOf<ArgumentNullException>());
    }
}
