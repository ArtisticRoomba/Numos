using Numos.API;
using Numos.CoreSim;

namespace Numos.Viewer.Tests;

[TestFixture]
public sealed class ReplayBranchSessionTests
{
    [Test]
    public void CreateBranch_StartsBeforeOperationsAtCurrentTickAndPreservesParentHead()
    {
        using var world = new AtmosWorld(new AtmosConfig());
        var simulation = world.CreateSimulation(1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        var timeline = new AtmosWorldReplayTimeline(world, 1);
        var branches = new ReplayBranchSession(world, timeline, 1);

        world.Tick();
        simulation.SetVoxelTemperature(chunk, 0, 310f);
        var parentHead = world.ComputeStateHash();

        var child = branches.CreateBranchFromCurrentTick();

        Assert.Multiple(() =>
        {
            Assert.That(child.Name, Is.EqualTo("branch-1"));
            Assert.That(child.ParentId, Is.EqualTo(0));
            Assert.That(child.Fork.Tick, Is.EqualTo(1));
            Assert.That(child.Fork.OperationSequence, Is.EqualTo(0));
            Assert.That(branches.Timeline.Operations, Is.Empty);
            Assert.That(branches.Timeline.IsInspecting, Is.False);
        });

        branches.SelectBranch(0);
        Assert.That(branches.Timeline.Position, Is.EqualTo(branches.SelectedBranch.Fork));
        branches.Timeline.ReturnToHead();
        Assert.That(world.ComputeStateHash(), Is.EqualTo(parentHead));
    }

    [Test]
    public void SwitchAndContinue_PreservesDivergentSiblingHeads()
    {
        using var world = new AtmosWorld(new AtmosConfig());
        var simulation = world.CreateSimulation(1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        var branches = new ReplayBranchSession(world, new AtmosWorldReplayTimeline(world, 1), 1);

        simulation.SetVoxelTemperature(chunk, 0, 300f);
        world.Tick();
        branches.CreateBranchFromCurrentTick();
        simulation.SetVoxelTemperature(chunk, 0, 320f);
        world.Tick();
        var childHead = world.ComputeStateHash();

        branches.SelectBranch(0);
        branches.Timeline.ReturnToHead();
        Assert.That(branches.CanContinueSelectedBranch, Is.True);
        branches.ContinueSelectedBranch();
        simulation.SetVoxelTemperature(chunk, 0, 340f);
        world.Tick();
        var mainHead = world.ComputeStateHash();

        branches.SelectBranch(1);
        Assert.That(branches.Timeline.Position, Is.EqualTo(branches.SelectedBranch.Fork));
        branches.Timeline.ReturnToHead();
        Assert.That(world.ComputeStateHash(), Is.EqualTo(childHead));

        branches.SelectBranch(0);
        branches.Timeline.ReturnToHead();
        Assert.That(world.ComputeStateHash(), Is.EqualTo(mainHead));
    }

    [Test]
    public void NestedBranches_KeepStableCreationOrderAndParentage()
    {
        using var world = new AtmosWorld(new AtmosConfig());
        world.CreateSimulation(1, 1, 1);
        var branches = new ReplayBranchSession(world, new AtmosWorldReplayTimeline(world, 1), 1);

        var first = branches.CreateBranchFromCurrentTick();
        world.Tick();
        var second = branches.CreateBranchFromCurrentTick();

        Assert.Multiple(() =>
        {
            Assert.That(
                branches.Branches.Select(static branch => branch.Name),
                Is.EqualTo(new[] { "main", "branch-1", "branch-2" }));

            Assert.That(first.ParentId, Is.EqualTo(0));
            Assert.That(second.ParentId, Is.EqualTo(first.Id));
            Assert.That(branches.SelectedBranch.Id, Is.EqualTo(second.Id));
            Assert.That(branches.MaximumHeadTick, Is.EqualTo(1));
        });
    }

    [Test]
    public void CreateBranch_RebasesTimelineAndCheckpointsAtFork()
    {
        using var world = new AtmosWorld(new AtmosConfig());
        world.CreateSimulation(1, 1, 1);
        var branches = new ReplayBranchSession(world, new AtmosWorldReplayTimeline(world, 1), 1);

        world.Tick();
        world.Tick();
        var child = branches.CreateBranchFromCurrentTick();

        Assert.Multiple(() =>
        {
            Assert.That(branches.Timeline.Start, Is.EqualTo(child.Fork));
            Assert.That(branches.Timeline.Checkpoints, Has.Count.EqualTo(1));
            Assert.That(branches.Timeline.Checkpoints[0].Checkpoint.Position, Is.EqualTo(child.Fork));
        });
    }

    [Test]
    public void SelectBranch_PositionsCursorAtRequestedTick()
    {
        using var world = new AtmosWorld(new AtmosConfig());
        world.CreateSimulation(1, 1, 1);
        var branches = new ReplayBranchSession(world, new AtmosWorldReplayTimeline(world, 1), 1);

        world.Tick();
        var child = branches.CreateBranchFromCurrentTick();
        world.Tick();
        world.Tick();
        branches.SelectBranch(0);

        branches.SelectBranch(child.Id, 2);

        Assert.Multiple(() =>
        {
            Assert.That(branches.SelectedBranch.Id, Is.EqualTo(child.Id));
            Assert.That(branches.Timeline.Position.Tick, Is.EqualTo(2));
            Assert.That(branches.Timeline.Start, Is.EqualTo(child.Fork));
            Assert.That(
                branches.Timeline.Checkpoints.Select(static point => point.Checkpoint.Position.Tick),
                Is.All.GreaterThanOrEqualTo(child.Fork.Tick));
        });
    }

    [Test]
    public void FailedCheckout_RestoresSelectedLiveBranchAndCursor()
    {
        using var world = new AtmosWorld(new AtmosConfig());
        var simulation = world.CreateSimulation(1, 1, 1);
        var chunk = simulation.CreateAndRegisterChunk(default);
        var timeline = new AtmosWorldReplayTimeline(world, 1);
        int restoreCalls = 0;
        var branches = new ReplayBranchSession(
            world,
            timeline,
            1,
            (targetWorld, archive, interval) =>
            {
                restoreCalls++;
                if (restoreCalls == 1)
                    throw new InvalidDataException("Injected checkout failure.");

                return AtmosWorldReplayTimeline.Restore(targetWorld, archive, interval);
            });

        branches.CreateBranchFromCurrentTick();
        simulation.SetVoxelTemperature(chunk, 0, 325f);
        world.Tick();
        var sourceHead = world.ComputeStateHash();

        Assert.That(() => branches.SelectBranch(0), Throws.TypeOf<InvalidDataException>());
        Assert.Multiple(() =>
        {
            Assert.That(branches.SelectedBranch.Name, Is.EqualTo("branch-1"));
            Assert.That(branches.Timeline.IsInspecting, Is.False);
            Assert.That(branches.Timeline.Position, Is.EqualTo(sourceHead.Position));
            Assert.That(world.ComputeStateHash(), Is.EqualTo(sourceHead));
            Assert.That(restoreCalls, Is.EqualTo(2));
        });
    }
}