using Numos.API;
using Numos.Tests;

namespace Numos.CoreSim.IntegrationTests;

/// <summary>
///     The public API registers every component class it creates, so only a recording built with the internal
///     constructor can name a key that <see cref="SimulationComponentCatalog" /> lacks.
/// </summary>
[TestFixture]
public sealed class ReplayArchiveComponentCatalogTests
{
    [Test]
    public void UnsupportedFeatures_ListsAComponentKeyTheCatalogLacks()
    {
        const string unknown = "numos-tests/archive/uncataloged-component";
        using var world = new AtmosWorld(new TestAtmosConfig());
        var simulation = world.CreateSimulation(1, 1, 1);
        var checkpoint = world.CaptureCheckpoint();
        var hash = checkpoint.ComputeStateHash();
        var operation = new AtmosWorldRecordedOperation(
            checkpoint.Position,
            new SetAtmosSimulationComponentStateOperation(simulation.Id, unknown, ReadOnlyMemory<byte>.Empty));
        var recording = new AtmosWorldRecording(checkpoint.Position, checkpoint.Position, [operation]);
        var archive = new AtmosWorldReplayArchive(checkpoint, recording, hash, hash);

        Assert.Multiple(() =>
        {
            Assert.That(SimulationComponentCatalog.Contains(unknown), Is.False);
            Assert.That(archive.RequiredSimulationComponents, Is.EqualTo(new[] { unknown }));
            Assert.That(
                archive.UnsupportedFeatures,
                Is.EqualTo(new[] { $"simulation component '{unknown}' (not in the component catalog)" }));
            Assert.That(archive.EnsurePortable, Throws.TypeOf<NotSupportedException>().With.Message.Contains(unknown));
        });
    }
}
