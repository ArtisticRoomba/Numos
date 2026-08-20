namespace Numos.API.Dangerous.Tests;

[TestFixture]
public sealed class AtmosDangerousApiTests
{
    [Test]
    public void Dangerous_WithNullSimulation_Throws()
    {
        AtmosSimulation? simulation = null;

        Assert.That(() => simulation!.Dangerous(),
            Throws.TypeOf<ArgumentNullException>()
                .With.Property(nameof(ArgumentNullException.ParamName)).EqualTo("simulation"));
    }

    [Test]
    public void Dangerous_WithLiveSimulation_ReturnsEntryPoint()
    {
        using var simulation = new AtmosSimulation();

        var dangerous = simulation.Dangerous();

        Assert.That(dangerous, Is.TypeOf<AtmosDangerousApi>());
    }

    [Test]
    public void Dangerous_WithDisposedSimulation_Throws()
    {
        var simulation = new AtmosSimulation();
        simulation.Dispose();

        Assert.That(simulation.Dangerous, Throws.TypeOf<ObjectDisposedException>());
    }

    [Test]
    public void RetainedDangerousApi_RejectsRegistrationAfterSimulationIsDisposed()
    {
        var simulation = new AtmosSimulation();
        var dangerous = simulation.Dangerous();
        simulation.Dispose();

        Assert.That(() => dangerous.Solvers.Register("late", _ => { }),
            Throws.TypeOf<ObjectDisposedException>());
    }

    [Test]
    public void DangerousSolver_ReceivesLiveChunkAndGasSpans()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        simulation.SetChunkClassification(chunk, new Numos.CoreSim.Datatypes.Primitives.VoxelClassification(7));
        simulation.AddGasToVoxel(chunk, 0, 2, 1f, 300f);
        simulation.Dangerous().Solvers.RegisterAfter(AtmosBuiltInSolvers.ThermalBoundary, "raw-write", context =>
        {
            var rawChunk = context.GetChunk(0);
            rawChunk.GetGasChannel(0).Moles[0] = 4f;
            rawChunk.MarkChanged();
        });

        simulation.Tick();

        Assert.That(simulation.GetChunkSnapshot(chunk).Gases.Single().Moles[0], Is.EqualTo(4f));
        Assert.That(simulation.Solvers.Steps.Single(step => step.Name == "raw-write").Kind,
            Is.EqualTo(AtmosSolverKind.Dangerous));
    }

    [Test]
    public void DangerousInjection_UsesCurrentVoxelShcFromTickSnapshot()
    {
        var config = new Numos.CoreSim.AtmosConfig
        {
            GasRegistry =
            [
                new Numos.CoreSim.GasProperties { MolarHeatCapacityAtConstantVolume = 10f },
                new Numos.CoreSim.GasProperties { MolarHeatCapacityAtConstantVolume = 30f }
            ]
        };
        using var simulation = new AtmosSimulation(config, 1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        simulation.SetChunkClassification(chunk, new Numos.CoreSim.Datatypes.Primitives.VoxelClassification(7));
        simulation.AddGasToVoxel(chunk, 0, 0, 1f, 300f);
        simulation.Dangerous().Solvers.RegisterBefore(AtmosBuiltInSolvers.Advection, "inject", context =>
            context.InjectGasToVoxel(0, 0, 1, 1f, 600f));

        simulation.Tick();

        var snapshot = simulation.GetChunkSnapshot(chunk);
        Assert.That(snapshot.Temperature[0], Is.EqualTo(525f).Within(0.0001f));
    }

    [Test]
    public void DangerousInjection_IntoActivePassableClosureDoesNotConsumeAnotherRoomSlot()
    {
        var config = new Numos.CoreSim.AtmosConfig
        {
            BulkFlowCoefficient = 0f,
            DefaultDiffusionCoefficient = 0f,
            VoxelSnappingEnabled = false,
            SleepThreshold = int.MaxValue
        };
        using var simulation = new AtmosSimulation(config, 2, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default, maxActiveRooms: 1);
        simulation.SetChunkClassification(chunk,
            Numos.CoreSim.Datatypes.Primitives.VoxelClassification.RoomSolid);
        simulation.SetVoxelClassification(chunk, 0,
            new Numos.CoreSim.Datatypes.Primitives.VoxelClassification(1));
        simulation.SetVoxelClassification(chunk, 1,
            new Numos.CoreSim.Datatypes.Primitives.VoxelClassification(2));
        simulation.AddGasToVoxel(chunk, 0, 0, 1f, 300f);
        simulation.Dangerous().Solvers.RegisterBefore(AtmosBuiltInSolvers.Advection, "inject-closure",
            context => context.InjectGasToVoxel(0, 1, 0, 1f, 300f));

        Assert.That(simulation.Tick, Throws.Nothing);
        Assert.That(simulation.GetVoxelSnapshot(chunk, 1).Gases.Single().Moles, Is.EqualTo(1f));
    }

    [Test]
    public void ConfiguredDangerousSolver_RetainsEditableTypedConfiguration()
    {
        using var simulation = new AtmosSimulation(1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        simulation.SetChunkClassification(chunk,
            new Numos.CoreSim.Datatypes.Primitives.VoxelClassification(7));
        var solver = new ConfiguredDangerousInjectionSolver();
        simulation.Dangerous().Solvers.RegisterBefore(
            AtmosBuiltInSolvers.Advection, "configured-injection", solver);

        solver.Config.Moles = 3f;
        simulation.Tick();

        Assert.That(simulation.GetChunkSnapshot(chunk).Gases.Single().Moles[0], Is.EqualTo(3f));
    }

    [Test]
    public void DangerousSolver_ConfigReferenceIsStableForTheTick()
    {
        var original = new Numos.CoreSim.AtmosConfig();
        var replacement = new Numos.CoreSim.AtmosConfig();
        using var simulation = new AtmosSimulation(original);
        var observed = new List<Numos.CoreSim.AtmosConfig>();
        simulation.Dangerous().Solvers.RegisterBefore(AtmosBuiltInSolvers.Advection, "replace-config", context =>
        {
            if (context.TickCount == 1)
                simulation.SetAtmosConfig(replacement);
        });
        simulation.Dangerous().Solvers.RegisterAfter("replace-config", "observe-config",
            context => observed.Add(context.Config));

        simulation.Tick();
        simulation.Tick();

        Assert.That(observed, Is.EqualTo(new[] { original, replacement }));
    }

    private sealed class ConfiguredDangerousInjectionSolver :
        IAtmosDangerousSolver<DangerousInjectionSolverConfig>
    {
        public DangerousInjectionSolverConfig Config { get; } = new();

        public void Solve(AtmosDangerousSolverContext context)
        {
            context.InjectGasToVoxel(0, 0, 0, Config.Moles, 300f);
        }
    }

    private sealed class DangerousInjectionSolverConfig
    {
        internal float Moles { get; set; }
    }
}
