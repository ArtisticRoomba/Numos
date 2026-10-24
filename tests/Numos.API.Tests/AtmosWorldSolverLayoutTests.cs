using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.Maths;

namespace Numos.API.Tests;

[TestFixture]
public sealed class AtmosWorldSolverLayoutTests
{
    private static readonly AtmosWorldSolverLayoutEntry[] LegacyLayout = AtmosWorldSolverPipeline
        .GetDefaultLayout(AtmosTransportModel.Legacy)
        .Select(static name => new AtmosWorldSolverLayoutEntry(name, true))
        .ToArray();

    [Test]
    public void SetLayout_RejectsBadListsAndChangesNothing()
    {
        using var world = new AtmosWorld(CreateConfig());
        world.Solvers.Register("custom/stage", static _ => { });
        var before = world.Solvers.Steps;
        var valid = before.Select(static step => new AtmosWorldSolverLayoutEntry(step.Name, step.IsEnabled)).ToList();

        var duplicate = valid.Append(valid[0]).ToArray();
        var unknown = valid.Append(new AtmosWorldSolverLayoutEntry("not-a-stage", true)).ToArray();
        var missingCustom = valid.Where(static entry => entry.Name != "custom/stage").ToArray();
        var blank = valid.Append(new AtmosWorldSolverLayoutEntry(" ", true)).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(() => world.Solvers.SetLayout(duplicate), Throws.ArgumentException.With.Message.Contain("more than once"));
            Assert.That(() => world.Solvers.SetLayout(unknown), Throws.ArgumentException.With.Message.Contain("not-a-stage"));
            Assert.That(() => world.Solvers.SetLayout(missingCustom), Throws.ArgumentException.With.Message.Contain("custom/stage"));
            Assert.That(() => world.Solvers.SetLayout(blank), Throws.ArgumentException.With.Message.Contain("blank"));
            Assert.That(world.Solvers.Steps, Is.EqualTo(before));
        });
    }

    [Test]
    public void SetLayout_RecordsOnlyAnActualChange()
    {
        using var world = new AtmosWorld(CreateConfig());
        world.StartRecording();
        var current = world.Solvers.Steps
            .Select(static step => new AtmosWorldSolverLayoutEntry(step.Name, step.IsEnabled))
            .ToArray();

        Assert.That(world.Solvers.SetLayout(current), Is.False);
        Assert.That(world.Solvers.SetLayout(LegacyLayout), Is.True);
        var recording = world.StopRecording();

        var operation = (SetAtmosWorldSolverLayoutOperation)recording.Operations.Single().Operation;
        Assert.That(operation.Stages, Is.EqualTo(LegacyLayout));
    }

    [Test]
    public void RecordedLayoutChange_ReplaysToTheLiveStateHash()
    {
        using var world = new AtmosWorld(CreateConfig());
        var simulation = world.CreateSimulation(4, 1, 1);
        var chunk = CreateOpenChunk(simulation, default);
        simulation.AddGasToVoxel(chunk, 0, 0, 8f, 320f);
        world.Tick();
        var checkpoint = world.CaptureCheckpoint();
        world.StartRecording();

        world.Tick();
        var legacy = LegacyLayout.ToArray();
        legacy[^1] = legacy[^1] with { Enabled = false };
        world.Solvers.SetLayout(legacy);
        world.Tick();
        world.Tick();
        world.Solvers.SetLayout(AtmosWorldSolverPipeline.GetDefaultLayout(AtmosTransportModel.Euler)
            .Select(static name => new AtmosWorldSolverLayoutEntry(name, true))
            .ToArray());

        world.Tick();
        var recording = world.StopRecording();
        var live = world.ComputeStateHash();

        world.ReplayTo(checkpoint, recording.Operations, recording.Head);

        Assert.Multiple(() =>
        {
            Assert.That(world.ComputeStateHash(), Is.EqualTo(live));
            Assert.That(world.Solvers.Steps.First().Name, Is.EqualTo(AtmosBuiltInSolvers.EulerFlow));
        });
    }

    [Test]
    public void Registration_StaysRejectedWhileRecordingButSetLayoutWorks()
    {
        using var world = new AtmosWorld(CreateConfig());
        world.StartRecording();

        Assert.Multiple(() =>
        {
            Assert.That(() => world.Solvers.Register("custom/y", static _ => { }), Throws.InvalidOperationException);
            Assert.That(() => world.Solvers.ResetToDefaults(), Throws.InvalidOperationException);
            Assert.That(() => world.Solvers.SetLayout(LegacyLayout), Throws.Nothing);
        });
    }

    [Test]
    public void SetLayout_KeepsTheCustomSolverInstanceAcrossAReorder()
    {
        using var world = new AtmosWorld(CreateConfig());
        var solver = new CountingSolver();
        world.Solvers.Register("custom/counter", solver);
        var restoredBefore = solver.RestoredCount;
        world.Solvers.SetLayout(
            world.Solvers.Steps
                .Reverse()
                .Select(static step => new AtmosWorldSolverLayoutEntry(step.Name, step.IsEnabled))
                .ToArray());

        world.CreateSimulation(1, 1, 1);
        world.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(world.Solvers.Steps.First().Name, Is.EqualTo("custom/counter"));
            Assert.That(solver.SolveCount, Is.EqualTo(1));
            Assert.That(solver.RestoredCount, Is.EqualTo(restoredBefore));
        });
    }

    [Test]
    public void Restore_RequiresTheSameCustomStagesButAnyOrder()
    {
        using var source = new AtmosWorld(CreateConfig());
        source.CreateSimulation(1, 1, 1);
        source.Solvers.Register("custom/a", static _ => { });
        source.Solvers.SetLayout(source.Solvers.Steps.Reverse()
            .Select(static step => new AtmosWorldSolverLayoutEntry(step.Name, step.IsEnabled))
            .ToArray());

        var checkpoint = source.CaptureCheckpoint();

        using var matching = new AtmosWorld(CreateConfig());
        matching.CreateSimulation(1, 1, 1);
        matching.Solvers.Register("custom/a", static _ => { });
        matching.RestoreCheckpoint(checkpoint);

        using var other = new AtmosWorld(CreateConfig());
        other.CreateSimulation(1, 1, 1);
        other.Solvers.Register("custom/b", static _ => { });

        using var none = new AtmosWorld(CreateConfig());
        none.CreateSimulation(1, 1, 1);

        Assert.Multiple(() =>
        {
            Assert.That(
                matching.Solvers.Steps.Select(static step => step.Name),
                Is.EqualTo(source.Solvers.Steps.Select(static step => step.Name)));

            Assert.That(matching.ComputeStateHash(), Is.EqualTo(source.ComputeStateHash()));
            Assert.That(() => other.RestoreCheckpoint(checkpoint), Throws.ArgumentException.With.Message.Contain("custom"));
            Assert.That(() => none.RestoreCheckpoint(checkpoint), Throws.ArgumentException);
        });
    }

    [Test]
    public void ConvertTransportModel_SwapsOnlyTheTransportStages()
    {
        var euler = AtmosWorldSolverPipeline.GetDefaultLayout(AtmosTransportModel.Euler)
            .Select(static name => new AtmosWorldSolverLayoutEntry(name, name != AtmosBuiltInSolvers.Thermodynamics))
            .ToArray();

        var legacy = AtmosWorldSolverPipeline.ConvertTransportModel(euler, AtmosTransportModel.Legacy);
        var back = AtmosWorldSolverPipeline.ConvertTransportModel(legacy, AtmosTransportModel.Euler);

        Assert.Multiple(() =>
        {
            Assert.That(
                legacy.Select(static entry => entry.Name),
                Is.EqualTo(AtmosWorldSolverPipeline.GetDefaultLayout(AtmosTransportModel.Legacy)));

            Assert.That(legacy.Single(static entry => entry.Name == AtmosBuiltInSolvers.Thermodynamics).Enabled, Is.False);
            Assert.That(back, Is.EqualTo(euler));
        });
    }

    private static AtmosConfig CreateConfig()
    {
        return new AtmosConfig
        {
            GasRegistry = [new GasProperties { Name = "Air", DiffusionCoefficient = 0.02f }]
        };
    }

    private static AtmosChunkHandle CreateOpenChunk(AtmosSimulation simulation, Int3 position)
    {
        var chunk = simulation.CreateAndRegisterChunk(position);
        simulation.SetChunkClassification(chunk, new VoxelClassification(1));
        return chunk;
    }

    private sealed class CountingSolver : IAtmosWorldSolver
    {
        public int SolveCount { get; private set; }
        public int RestoredCount { get; private set; }

        public void Solve(AtmosWorldSolverContext context) => SolveCount++;

        public void OnRestored() => RestoredCount++;
    }
}
