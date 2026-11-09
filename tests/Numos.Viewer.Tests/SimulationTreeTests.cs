using System.Diagnostics;
using Numos.API;
using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.Maths;
using Numos.Viewer.Workspace;
using Numos.Workbench;

namespace Numos.Viewer.Tests;

[TestFixture]
public sealed class SimulationTreeTests
{
    private const string Culture = "en";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private WorkbenchHost _host = null!;

    [SetUp]
    public void SetUp()
    {
        Locale.SetCulture(Culture);
        var session = new WorkbenchSession();
        session.CreateProject(new WorkbenchProjectOptions("Test", 4, 4, 1, true, TickRate: 20));
        session.AddChunk(new Int3(0, 0, 0), 1);
        _host = new WorkbenchHost(session);
    }

    [TearDown]
    public void TearDown()
    {
        _host.Dispose();
    }

    /// <summary>
    ///     Runs work on the host thread and waits for its result.
    /// </summary>
    private T Call<T>(Func<WorkbenchSession, T> work)
    {
        HostResult<T>? result = null;
        _host.Submit(work, completed => result = completed);
        var watch = Stopwatch.StartNew();
        while (result == null)
        {
            Assert.That(watch.Elapsed, Is.LessThan(Timeout), "The host did not answer in time.");
            _host.WaitForCompletions(TimeSpan.FromMilliseconds(50));
            _host.DrainCompletions();
        }

        if (result.Value.Exception is { } exception)
            Assert.Fail(exception.ToString());

        return result.Value.Value!;
    }

    private void Run(Action<WorkbenchSession> work)
    {
        Call(session =>
        {
            work(session);
            return 0;
        });
    }

    /// <summary>
    ///     Waits for a frame published after every queued command.
    /// </summary>
    private WorkbenchFrame NextFrame()
    {
        Run(static _ => { });
        long sequence = _host.Frame.Sequence;
        _host.RequestFrame(AtmosChunkSnapshotFields.Pressure);
        Assert.That(SpinWait.SpinUntil(() => _host.Frame.Sequence > sequence, Timeout), Is.True);
        return _host.Frame;
    }

    private SimulationTree RefreshedTree(out WorkbenchFrame frame)
    {
        var tree = new SimulationTree();
        frame = NextFrame();
        Assert.That(tree.Refresh(frame, Culture), Is.True);
        return tree;
    }

    private static TreeNodeId[] ChildIds(SimulationTree tree, TreeNodeId parent)
    {
        return tree.GetChildren(parent).Select(static node => node.Id).ToArray();
    }

    [Test]
    public void NewProject_HasTheDiagramShape()
    {
        var tree = RefreshedTree(out var frame);
        var simulation = frame.Simulations[0].Id;
        var timeline = frame.Timeline!;

        Assert.Multiple(() =>
        {
            Assert.That(tree.Root?.Id, Is.EqualTo(TreeNodeId.Project));
            Assert.That(tree.Root?.Label, Is.EqualTo("Test"));
            Assert.That(
                ChildIds(tree, TreeNodeId.Project),
                Is.EqualTo(new[] { TreeNodeId.World, TreeNodeId.Timeline, TreeNodeId.Visualization }));

            Assert.That(
                ChildIds(tree, TreeNodeId.World),
                Is.EqualTo(
                    new[]
                    {
                        TreeNodeId.ForSimulation(simulation),
                        TreeNodeId.Topology,
                        TreeNodeId.Solvers,
                        TreeNodeId.Gases,
                        TreeNodeId.AtmosConfiguration
                    }));

            Assert.That(
                ChildIds(tree, TreeNodeId.ForSimulation(simulation)),
                Is.EqualTo(new[] { TreeNodeId.ForChunk(simulation, new Int3(0, 0, 0)) }));

            Assert.That(tree.GetChildren(TreeNodeId.Topology), Is.Empty);
            Assert.That(
                ChildIds(tree, TreeNodeId.Solvers),
                Is.EqualTo(frame.SolverSteps.Select(static step => TreeNodeId.ForSolverStage(step.Name))));

            Assert.That(tree.GetChildren(TreeNodeId.Gases), Has.Count.EqualTo(frame.Gases.Count).And.Count.GreaterThan(0));
            Assert.That(
                ChildIds(tree, TreeNodeId.Timeline),
                Is.EqualTo(timeline.Branches.Select(static branch => TreeNodeId.ForBranch(branch.Id))));

            Assert.That(
                tree.GetChildren(TreeNodeId.ForBranch(timeline.SelectedBranchId)),
                Has.Count.EqualTo(timeline.Checkpoints.Count));

            Assert.That(
                ChildIds(tree, TreeNodeId.Visualization),
                Is.EqualTo(new[] { TreeNodeId.Views3D, TreeNodeId.Slice2D }));
        });
    }

    [Test]
    public void EveryNode_HasItsParentAndAnInfoRow()
    {
        var tree = RefreshedTree(out _);
        var stack = new Stack<TreeNode>([tree.Root!]);
        int visited = 0;
        while (stack.TryPop(out var node))
        {
            visited++;
            Assert.That(TreeNodeKinds.Info.ContainsKey(node.Id.Kind), node.Id.Key);
            foreach (var child in tree.GetChildren(node.Id))
            {
                Assert.That(child.Parent, Is.EqualTo(node.Id), child.Id.Key);
                stack.Push(child);
            }
        }

        Assert.That(visited, Is.EqualTo(tree.Count));
    }

    [Test]
    public void EveryKind_HasAnInfoRow()
    {
        foreach (var kind in Enum.GetValues<TreeNodeKind>())
        {
            Assert.That(TreeNodeKinds.Info.TryGetValue(kind, out var info), Is.True, kind.ToString());
            Assert.That(info!.Icon, Is.Not.Empty, kind.ToString());
        }
    }

    [Test]
    public void Rename_KeepsTheSimulationId()
    {
        var tree = RefreshedTree(out var frame);
        var simulation = frame.Simulations[0].Id;
        var id = TreeNodeId.ForSimulation(simulation);
        var chunks = tree.GetChildren(id);

        Run(session => session.SetSimulationName(simulation, "Renamed"));
        Assert.That(tree.Refresh(NextFrame(), Culture), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(tree.TryGetNode(id, out var node), Is.True);
            Assert.That(node.Label, Is.EqualTo("Renamed"));
            Assert.That(tree.GetChildren(id), Is.SameAs(chunks));
        });
    }

    [Test]
    public void SameChunkPosition_InTwoSimulations_GivesTwoIds()
    {
        var second = Call(session =>
        {
            var added = session.AddSimulation(4, 4, 1);
            session.AddChunk(new Int3(0, 0, 0), 1, added.Id);
            return added.Id;
        });

        var tree = RefreshedTree(out var frame);
        var first = frame.Simulations[0].Id;
        var firstChunk = TreeNodeId.ForChunk(first, new Int3(0, 0, 0));
        var secondChunk = TreeNodeId.ForChunk(second, new Int3(0, 0, 0));

        Assert.Multiple(() =>
        {
            Assert.That(firstChunk, Is.Not.EqualTo(secondChunk));
            Assert.That(tree.TryGetNode(firstChunk, out var a), Is.True);
            Assert.That(tree.TryGetNode(secondChunk, out var b), Is.True);
            Assert.That(a.Parent, Is.EqualTo(TreeNodeId.ForSimulation(first)));
            Assert.That(b.Parent, Is.EqualTo(TreeNodeId.ForSimulation(second)));
        });
    }

    [Test]
    public void UnchangedRefresh_ReturnsFalseAndReusesArrays()
    {
        var tree = RefreshedTree(out var frame);
        var simulation = TreeNodeId.ForSimulation(frame.Simulations[0].Id);
        var world = tree.GetChildren(TreeNodeId.World);
        var chunks = tree.GetChildren(simulation);
        var stages = tree.GetChildren(TreeNodeId.Solvers);
        var gases = tree.GetChildren(TreeNodeId.Gases);
        var branches = tree.GetChildren(TreeNodeId.Timeline);
        tree.TryGetNode(simulation, out var node);

        var next = NextFrame();
        Assert.That(next.Sequence, Is.GreaterThan(frame.Sequence));
        Assert.Multiple(() =>
        {
            Assert.That(tree.Refresh(next, Culture), Is.False);
            Assert.That(tree.Refresh(next, Culture), Is.False);
            Assert.That(tree.GetChildren(TreeNodeId.World), Is.SameAs(world));
            Assert.That(tree.GetChildren(simulation), Is.SameAs(chunks));
            Assert.That(tree.GetChildren(TreeNodeId.Solvers), Is.SameAs(stages));
            Assert.That(tree.GetChildren(TreeNodeId.Gases), Is.SameAs(gases));
            Assert.That(tree.GetChildren(TreeNodeId.Timeline), Is.SameAs(branches));
            Assert.That(tree.TryGetNode(simulation, out var after) && ReferenceEquals(after, node), Is.True);
        });
    }

    [Test]
    public void CultureChange_RebuildsLabels()
    {
        var tree = RefreshedTree(out var frame);
        try
        {
            Locale.SetCulture("ru");
            Assert.That(tree.Refresh(frame, "ru"), Is.True);
            Assert.That(tree.TryGetNode(TreeNodeId.World, out var world), Is.True);
            Assert.That(world.Label, Is.EqualTo(Locale.GetString("tree-kind-world")));
        }
        finally
        {
            Locale.SetCulture();
        }
    }

    [Test]
    public void PortalAndDock_AppearAfterTheyAreCreated()
    {
        var tree = RefreshedTree(out _);
        var (portal, dock) = Call(session =>
        {
            var first = session.ActiveSimulationId!.Value;
            var second = session.AddSimulation(4, 4, 1, activate: false).Id;
            session.AddChunk(new Int3(0, 0, 0), 1, second);
            var created = session.CreatePortal(
                new AtmosCellRef(first, new AtmosChunkHandle(new Int3(0, 0, 0)), 0),
                new AtmosCellRef(first, new AtmosChunkHandle(new Int3(0, 0, 0)), 5),
                ExplicitLinkFlags.All);

            var docked = session.CreateDock(
                new DockDefinition(
                    new DockEndpoint(first, new Int3(0, 0, 0), DockFace.PositiveX),
                    new DockEndpoint(second, new Int3(0, 0, 0), DockFace.NegativeX),
                    0,
                    false,
                    false,
                    ExplicitLinkFlags.All));

            return (created.Links, docked.Links);
        });

        Assert.That(tree.Refresh(NextFrame(), Culture), Is.True);
        var portalId = TreeNodeId.ForLinkSet(ExplicitLinkSetKind.Portal, portal);
        var dockId = TreeNodeId.ForLinkSet(ExplicitLinkSetKind.Dock, dock);

        Assert.Multiple(() =>
        {
            Assert.That(ChildIds(tree, TreeNodeId.Topology), Is.EquivalentTo(new[] { portalId, dockId }));
            Assert.That(portalId.Kind, Is.EqualTo(TreeNodeKind.Portal));
            Assert.That(dockId.Kind, Is.EqualTo(TreeNodeKind.Dock));
            Assert.That(tree.TryGetNode(portalId, out var node) && node.LinkSet == portal, Is.True);
            Assert.That(node!.Label, Is.EqualTo($"Portal {portal.Index + 1}"));
        });
    }

    [Test]
    public void GasAndStageEdits_UpdateTheirNodes()
    {
        var tree = RefreshedTree(out var frame);
        int before = frame.Gases.Count;
        string stage = frame.SolverSteps[0].Name;

        int gasId = Call(session => session.AddGas(
            new GasProperties
            {
                Name = "Tree gas",
                MolarHeatCapacityAtConstantVolume = AtmosPhysicalConstants.IdealDiatomicMolarHeatCapacityAtConstantVolume,
                LiquidId = -1,
                DiffusionCoefficient = AtmosConfigDefaults.DefaultDiffusionCoefficient
            }));

        Run(session => session.SetSolverEnabled(stage, false));
        Assert.That(tree.Refresh(NextFrame(), Culture), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(tree.GetChildren(TreeNodeId.Gases), Has.Count.EqualTo(before + 1));
            Assert.That(tree.TryGetNode(TreeNodeId.ForGas(gasId), out var gas), Is.True);
            Assert.That(gas.Label, Is.EqualTo("Tree gas"));
            Assert.That(tree.TryGetNode(TreeNodeId.ForSolverStage(stage), out var node), Is.True);
            Assert.That(node.IsMuted, Is.True);
            Assert.That(node.Detail, Is.Not.Null);
        });
    }

    [Test]
    public void NewBranch_AppearsUnderTimeline()
    {
        var tree = RefreshedTree(out var frame);
        int before = frame.Timeline!.Branches.Count;
        Run(static session => session.Tick(3));
        var branch = Call(static session =>
        {
            session.SeekTick(1);
            return session.CreateBranch();
        });

        var next = NextFrame();
        Assert.That(tree.Refresh(next, Culture), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(tree.GetChildren(TreeNodeId.Timeline), Has.Count.EqualTo(before + 1));
            Assert.That(tree.TryGetNode(TreeNodeId.ForBranch(branch.Id), out var node), Is.True);
            Assert.That(node.BranchId, Is.EqualTo(branch.Id));
            Assert.That(
                tree.GetChildren(TreeNodeId.ForBranch(next.Timeline!.SelectedBranchId)),
                Has.Count.EqualTo(next.Timeline.Checkpoints.Count));
        });
    }

    [Test]
    public void RemovedChunk_FallsBackToItsSimulation()
    {
        var tree = RefreshedTree(out var frame);
        var simulation = frame.Simulations[0].Id;
        var chunk = TreeNodeId.ForChunk(simulation, new Int3(0, 0, 0));
        var selection = new WorkspaceSelection();
        Assert.That(selection.Select(chunk, tree), Is.True);
        Assert.That(selection.Ancestors[^1], Is.EqualTo(TreeNodeId.ForSimulation(simulation)));

        Run(static session => session.RemoveChunk(new Int3(0, 0, 0)));
        Assert.That(tree.Refresh(NextFrame(), Culture), Is.True);
        long revision = selection.Revision;

        Assert.Multiple(() =>
        {
            Assert.That(selection.Reconcile(tree, inspecting: false), Is.True);
            Assert.That(selection.Current, Is.EqualTo(TreeNodeId.ForSimulation(simulation)));
            Assert.That(selection.IsMissing, Is.False);
            Assert.That(selection.Revision, Is.GreaterThan(revision));
        });
    }

    [Test]
    public void RemovedSimulation_FallsBackToWorld()
    {
        var second = Call(session =>
        {
            var added = session.AddSimulation(4, 4, 1);
            session.AddChunk(new Int3(2, 0, 0), 1, added.Id);
            return added.Id;
        });

        var tree = RefreshedTree(out _);
        var selection = new WorkspaceSelection();
        selection.Select(TreeNodeId.ForChunk(second, new Int3(2, 0, 0)), tree);

        Run(session => session.RemoveSimulation(second));
        tree.Refresh(NextFrame(), Culture);

        Assert.Multiple(() =>
        {
            Assert.That(tree.Contains(TreeNodeId.ForSimulation(second)), Is.False);
            Assert.That(tree.Contains(TreeNodeId.ForChunk(second, new Int3(2, 0, 0))), Is.False);
            Assert.That(selection.Reconcile(tree, inspecting: false), Is.True);
            Assert.That(selection.Current, Is.EqualTo(TreeNodeId.World));
        });
    }

    [Test]
    public void MissingNode_IsKeptWhileInspecting()
    {
        Run(static session =>
        {
            session.Tick(2);
            session.AddChunk(new Int3(1, 0, 0), 1);
            session.Tick(1);
        });

        var tree = RefreshedTree(out var frame);
        var chunk = TreeNodeId.ForChunk(frame.Simulations[0].Id, new Int3(1, 0, 0));
        var selection = new WorkspaceSelection();
        selection.Select(chunk, tree);

        Run(static session => session.SeekTick(1));
        var inspected = NextFrame();
        Assert.That(inspected.Timeline!.IsInspecting, Is.True);
        tree.Refresh(inspected, Culture);

        Assert.Multiple(() =>
        {
            Assert.That(tree.Contains(chunk), Is.False);
            Assert.That(selection.Reconcile(tree, inspected.Timeline.IsInspecting), Is.False);
            Assert.That(selection.Current, Is.EqualTo(chunk));
            Assert.That(selection.IsMissing, Is.True);
            Assert.That(selection.Node?.Chunk, Is.EqualTo(new Int3(1, 0, 0)));
        });

        Run(static session => session.ReturnToHead());
        tree.Refresh(NextFrame(), Culture);

        Assert.Multiple(() =>
        {
            Assert.That(selection.Reconcile(tree, inspecting: false), Is.False);
            Assert.That(selection.Current, Is.EqualTo(chunk));
            Assert.That(selection.IsMissing, Is.False);
        });
    }

    [Test]
    public void PendingSelection_WaitsForALaterFrame()
    {
        var tree = RefreshedTree(out var frame);
        var simulation = frame.Simulations[0].Id;
        var chunk = TreeNodeId.ForChunk(simulation, new Int3(3, 0, 0));
        var selection = new WorkspaceSelection();

        Run(static session => session.AddChunk(new Int3(3, 0, 0), 1));
        selection.Select(chunk, tree, _host.Frame.Sequence);

        Assert.Multiple(() =>
        {
            Assert.That(selection.IsMissing, Is.True);
            Assert.That(selection.Reconcile(tree, inspecting: false), Is.False);
            Assert.That(selection.Current, Is.EqualTo(chunk));
        });

        tree.Refresh(NextFrame(), Culture);
        Assert.Multiple(() =>
        {
            Assert.That(selection.Reconcile(tree, inspecting: false), Is.False);
            Assert.That(selection.Current, Is.EqualTo(chunk));
            Assert.That(selection.IsMissing, Is.False);
            Assert.That(
                selection.Ancestors,
                Is.EqualTo(new[] { TreeNodeId.Project, TreeNodeId.World, TreeNodeId.ForSimulation(simulation) }));
        });
    }

    [Test]
    public void CanonicalParent_MatchesTheTreeOutsideCheckpoints()
    {
        var tree = RefreshedTree(out _);
        var stack = new Stack<TreeNode>([tree.Root!]);
        while (stack.TryPop(out var node))
        {
            if (node.Id.Kind != TreeNodeKind.Checkpoint)
                Assert.That(node.Id.GetCanonicalParent(), Is.EqualTo(node.Parent), node.Id.Key);

            foreach (var child in tree.GetChildren(node.Id))
                stack.Push(child);
        }
    }

    [Test]
    public void ClosedProject_EmptiesTheTree()
    {
        var tree = RefreshedTree(out _);
        Run(static session => session.CloseProject());

        Assert.Multiple(() =>
        {
            Assert.That(tree.Refresh(NextFrame(), Culture), Is.True);
            Assert.That(tree.Root, Is.Null);
            Assert.That(tree.Count, Is.Zero);
        });
    }
}
