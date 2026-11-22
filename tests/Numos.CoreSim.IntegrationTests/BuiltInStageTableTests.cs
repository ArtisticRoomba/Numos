using System.Reflection;
using Numos.API;
using Numos.Tests;

namespace Numos.CoreSim.IntegrationTests;

/// <summary>
///     <see cref="BuiltInStageTable" /> is the one list of built-in stages. These tests fail when a stage exists in the
///     public names, the schedule, or a default layout without a matching table entry.
/// </summary>
[TestFixture]
public sealed class BuiltInStageTableTests
{
    private static IEnumerable<string> PublicNames()
    {
        return typeof(AtmosBuiltInSolvers)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(static field => field is { IsLiteral: true } && field.FieldType == typeof(string))
            .Select(static field => (string)field.GetRawConstantValue()!);
    }

    [Test]
    public void EveryPublicName_IsATableEntry_AndTheTableHasNoOthers()
    {
        string[] names = PublicNames().ToArray();
        string[] table = BuiltInStageTable.All.Select(static stage => stage.Name).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(names, Is.Unique);
            Assert.That(table, Is.Unique);
            Assert.That(table, Is.EquivalentTo(names));
            Assert.That(AtmosWorldSolverPipeline.BuiltInStageNames, Is.EqualTo(table));
        });
    }

    [Test]
    public void EveryTableEntry_HasTheScheduleRate()
    {
        Assert.Multiple(() =>
        {
            foreach (BuiltInStage stage in BuiltInStageTable.All)
            {
                Assert.That(stage.RunsPerSecond, Is.Positive, stage.Name);
                Assert.That(BuiltInStageSchedule.TryGetRunsPerSecond(stage.Name, out int rate), Is.True, stage.Name);
                Assert.That(rate, Is.EqualTo(stage.RunsPerSecond), stage.Name);
            }
        });
    }

    [TestCase(AtmosTransportModel.Euler)]
    [TestCase(AtmosTransportModel.Legacy)]
    public void DefaultLayout_NamesOnlyTableEntries_AtContiguousPositions(AtmosTransportModel model)
    {
        var layout = AtmosWorldSolverPipeline.GetDefaultLayout(model);
        Assert.Multiple(() =>
        {
            Assert.That(layout, Is.Unique);
            for (int index = 0; index < layout.Count; index++)
            {
                Assert.That(BuiltInStageTable.TryGet(layout[index], out BuiltInStage? stage), Is.True, layout[index]);
                Assert.That(stage!.PositionIn(model), Is.EqualTo(index), layout[index]);
            }

            int members = BuiltInStageTable.All.Count(stage => stage.PositionIn(model) != BuiltInStageTable.Absent);
            Assert.That(layout, Has.Count.EqualTo(members));
        });
    }

    [Test]
    public void OnlyEulerFlowAndVacuumCleanup_MarkTheirOwnChanges()
    {
        string[] marking = BuiltInStageTable.All
            .Where(static stage => stage.MarksOwnChanges)
            .Select(static stage => stage.Name)
            .ToArray();

        Assert.That(marking, Is.EquivalentTo(new[] { AtmosBuiltInSolvers.EulerFlow, AtmosBuiltInSolvers.VacuumCleanup }));
    }

    [Test]
    public void OnlyTheExplicitLinkStages_LackAKernelSolver()
    {
        string[] worldOnly = BuiltInStageTable.All
            .Where(static stage => stage.SolveKernel is null)
            .Select(static stage => stage.Name)
            .ToArray();

        Assert.That(worldOnly, Is.EquivalentTo(new[]
        {
            AtmosBuiltInSolvers.ExplicitGasTransport, AtmosBuiltInSolvers.ExplicitThermalTransport
        }));
    }

    [Test]
    public void AWorld_CanInstallEveryTableEntry_WithItsDefaultInterval()
    {
        using var world = new AtmosWorld(new TestAtmosConfig());
        world.Solvers.SetLayout(AtmosWorldSolverPipeline.BuiltInStageNames
            .Select(static name => new AtmosWorldSolverLayoutEntry(name, false))
            .ToArray());

        var steps = world.Solvers.Steps;
        Assert.Multiple(() =>
        {
            Assert.That(steps.Select(static step => step.Name), Is.EqualTo(AtmosWorldSolverPipeline.BuiltInStageNames));
            foreach (var step in steps)
            {
                Assert.That(step.Kind, Is.EqualTo(AtmosWorldSolverKind.BuiltIn), step.Name);
                Assert.That(step.Interval,
                    Is.EqualTo(BuiltInStageSchedule.GetInterval(step.Name, world.TickRate)), step.Name);
            }
        });
    }
}
