using Numos.API.Dangerous;

namespace Numos.API.Tests;

[TestFixture]
public sealed class AtmosUnrecordedAccessTests
{
    // The registry is process-wide and shared by every test in this assembly, so keys are namespaced to this fixture.
    private readonly static ChunkField<int> Scratch =
        ChunkField<int>.Register("tests/unrecorded-access/scratch");

    [Test]
    public void GetChunk_BetweenRecordedTicks_RaisesCountOnWorldAndRecording()
    {
        using var simulation = new AtmosSimulation(2, 2, 2);
        var chunk = simulation.CreateAndRegisterChunk(default);
        var world = simulation.World;
        world.StartRecording();
        simulation.Tick();

        _ = simulation.Dangerous().GetChunk(chunk);
        _ = simulation.Dangerous().GetChunk(chunk);

        Assert.That(world.UnrecordedAccessCount, Is.EqualTo(2));
        Assert.That(world.CaptureRecording().UnrecordedAccessCount, Is.EqualTo(2));
        Assert.That(world.StopRecording().UnrecordedAccessCount, Is.EqualTo(2));
        Assert.That(world.UnrecordedAccessCount, Is.EqualTo(2));
    }

    [Test]
    public void GetChunkField_BetweenRecordedTicks_RaisesCountOnWorldAndRecording()
    {
        using var simulation = new AtmosSimulation(2, 2, 2);
        var chunk = simulation.CreateAndRegisterChunk(default);
        var world = simulation.World;
        world.StartRecording();
        simulation.Tick();

        _ = simulation.Dangerous().GetChunkField(chunk, Scratch);

        Assert.That(world.UnrecordedAccessCount, Is.EqualTo(1));
        Assert.That(world.StopRecording().UnrecordedAccessCount, Is.EqualTo(1));
    }

    [Test]
    public void SolverCallbackAccess_DoesNotCount()
    {
        using var simulation = new AtmosSimulation(2, 2, 2);
        var chunk = simulation.CreateAndRegisterChunk(default);
        var world = simulation.World;
        world.Solvers.Register(
            "unrecorded-access-probe",
            context =>
            {
                _ = simulation.Dangerous().GetChunk(chunk);
                _ = simulation.Dangerous().GetChunkField(chunk, Scratch);
            });

        world.StartRecording();
        simulation.Tick();
        simulation.Tick();

        Assert.That(world.UnrecordedAccessCount, Is.Zero);
        Assert.That(world.StopRecording().UnrecordedAccessCount, Is.Zero);
    }

    [Test]
    public void AccessWithoutLiveRecording_DoesNotCount()
    {
        using var simulation = new AtmosSimulation(2, 2, 2);
        var chunk = simulation.CreateAndRegisterChunk(default);
        var world = simulation.World;

        _ = simulation.Dangerous().GetChunk(chunk);
        _ = simulation.Dangerous().GetChunkField(chunk, Scratch);
        Assert.That(world.UnrecordedAccessCount, Is.Zero);

        world.StartRecording();
        simulation.Tick();
        var recording = world.StopRecording();
        _ = simulation.Dangerous().GetChunk(chunk);
        _ = simulation.Dangerous().GetChunkField(chunk, Scratch);

        Assert.That(world.UnrecordedAccessCount, Is.Zero);
        Assert.That(recording.UnrecordedAccessCount, Is.Zero);
    }

    [Test]
    public void StartRecording_ResetsCountAndResumeKeepsIt()
    {
        using var simulation = new AtmosSimulation(2, 2, 2);
        var chunk = simulation.CreateAndRegisterChunk(default);
        var world = simulation.World;
        world.StartRecording();
        _ = simulation.Dangerous().GetChunk(chunk);
        world.StopRecording();

        world.ResumeRecording();
        _ = simulation.Dangerous().GetChunk(chunk);
        Assert.That(world.UnrecordedAccessCount, Is.EqualTo(2));

        world.StopRecording();
        world.StartRecording();
        Assert.That(world.UnrecordedAccessCount, Is.Zero);
        Assert.That(world.StopRecording().UnrecordedAccessCount, Is.Zero);
    }

    [Test]
    public void Count_DoesNotChangeStateHash()
    {
        using var simulation = new AtmosSimulation(2, 2, 2);
        var chunk = simulation.CreateAndRegisterChunk(default);
        var world = simulation.World;
        world.StartRecording();
        var before = world.ComputeStateHash();

        _ = simulation.Dangerous().GetChunk(chunk);

        Assert.That(world.ComputeStateHash(), Is.EqualTo(before));
    }
}
