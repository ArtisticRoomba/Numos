using Numos.API;
using Numos.Maths;
using Numos.Viewer.Workspace;

namespace Numos.Viewer.Tests;

[TestFixture]
public sealed class PortalEndpointCaptureTests
{
    private static readonly AtmosCellRef CellA = new(new AtmosSimulationId(0, 1), new AtmosChunkHandle(Int3.Zero), 3);
    private static readonly AtmosCellRef CellB = new(new AtmosSimulationId(1, 1), new AtmosChunkHandle(new Int3(1, 0, 0)), 7);

    [Test]
    public void Accept_FillsFirstThenMovesToTheEmptySecond()
    {
        var capture = new PortalEndpointCapture();
        var draft = new TopologyDraft();
        capture.Arm(PortalEndpoint.First);

        Assert.That(capture.Accept(CellA, draft), Is.EqualTo(PortalEndpoint.First));
        Assert.That(draft.PortalFirst, Is.EqualTo(CellA));
        Assert.That(capture.Target, Is.EqualTo(PortalEndpoint.Second));

        Assert.That(capture.Accept(CellB, draft), Is.EqualTo(PortalEndpoint.Second));
        Assert.That(draft.PortalSecond, Is.EqualTo(CellB));
        Assert.That(capture.IsArmed, Is.False);
    }

    [Test]
    public void Accept_EndsWhenTheOtherEndpointIsAlreadySet()
    {
        var capture = new PortalEndpointCapture();
        var draft = new TopologyDraft { PortalSecond = CellB };
        capture.Arm(PortalEndpoint.First);

        capture.Accept(CellA, draft);

        Assert.That(capture.IsArmed, Is.False);
        Assert.That(draft.PortalSecond, Is.EqualTo(CellB));
    }

    [Test]
    public void Accept_SecondMovesToAnEmptyFirst()
    {
        var capture = new PortalEndpointCapture();
        var draft = new TopologyDraft();
        capture.Arm(PortalEndpoint.Second);

        capture.Accept(CellB, draft);

        Assert.That(draft.PortalSecond, Is.EqualTo(CellB));
        Assert.That(capture.Target, Is.EqualTo(PortalEndpoint.First));
    }

    [Test]
    public void Accept_WhileDisarmed_LeavesTheDraftAlone()
    {
        var capture = new PortalEndpointCapture();
        var draft = new TopologyDraft();

        Assert.That(capture.Accept(CellA, draft), Is.EqualTo(PortalEndpoint.None));
        Assert.That(draft.PortalFirst, Is.Null);
        Assert.That(draft.PortalSecond, Is.Null);
    }

    [Test]
    public void Reconcile_KeepsThePickWhileTopologyStaysSelected()
    {
        var capture = new PortalEndpointCapture();
        capture.Arm(PortalEndpoint.First);

        Assert.That(capture.Reconcile(TreeNodeId.Topology, inspecting: false), Is.False);
        Assert.That(capture.Target, Is.EqualTo(PortalEndpoint.First));
    }

    [Test]
    public void Reconcile_EndsThePickWhenAnotherNodeIsSelected()
    {
        var capture = new PortalEndpointCapture();
        capture.Arm(PortalEndpoint.First);

        Assert.That(capture.Reconcile(TreeNodeId.ForChunk(new AtmosSimulationId(0, 1), Int3.Zero), false), Is.True);
        Assert.That(capture.IsArmed, Is.False);

        capture.Arm(PortalEndpoint.Second);
        Assert.That(capture.Reconcile(null, false), Is.True);
        Assert.That(capture.IsArmed, Is.False);
    }

    [Test]
    public void Reconcile_EndsThePickWhileInspecting()
    {
        var capture = new PortalEndpointCapture();
        capture.Arm(PortalEndpoint.Second);

        Assert.That(capture.Reconcile(TreeNodeId.Topology, inspecting: true), Is.True);
        Assert.That(capture.IsArmed, Is.False);
    }
}
