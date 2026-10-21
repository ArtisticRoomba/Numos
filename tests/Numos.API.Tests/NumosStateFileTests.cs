using Numos.CoreSim;
using Numos.CoreSim.GasReactions;
using Numos.CoreSim.State;
using Numos.Serialization;
using Numos.Serialization.FileSystem;
using static Numos.API.Tests.SolverConfigurationArchiveTests;

namespace Numos.API.Tests;

/// <summary>
///     Saving one <see cref="NumosStateAttribute" /> value on its own through <see cref="NumosStateSerializer" /> and
///     <see cref="NumosStateFile" />.
/// </summary>
[TestFixture]
public sealed class NumosStateFileTests
{
    [Test]
    public void StandaloneConfiguration_RoundTrips()
    {
        GasReactionConfig reactions = Reactions(2f);
        using var stream = new MemoryStream();
        NumosStateSerializer.Write(stream, reactions);
        stream.Position = 0;

        GasReactionConfig read = NumosStateSerializer.Read<GasReactionConfig>(stream, new GasRegistry { Air, Fuel });
        Assert.Multiple(() =>
        {
            Assert.That(read.SemanticallyEquals(reactions), Is.True);
            Assert.That(read.ComputeStateHash(), Is.EqualTo(reactions.ComputeStateHash()));
            Assert.That(stream.Position, Is.EqualTo(stream.Length));
        });
    }

    [Test]
    public void StandaloneConfiguration_RejectsTheWrongTypeAndMissingRegistry()
    {
        using var stream = new MemoryStream();
        NumosStateSerializer.Write(stream, Reactions(1f));

        Assert.Multiple(() =>
        {
            stream.Position = 0;
            Assert.That(
                () => NumosStateSerializer.Read<FireSettings>(stream),
                Throws.TypeOf<InvalidDataException>().With.Message.Contains("'numos/gas-reactions'"));

            stream.Position = 0;
            Assert.That(
                () => NumosStateSerializer.Read<GasReactionConfig>(stream),
                Throws.TypeOf<InvalidOperationException>().With.Message.Contains("gas registry"));

            stream.Position = 0;
            Assert.That(
                () => NumosStateSerializer.Read<GasReactionConfig>(new MemoryStream(stream.ToArray()[..^1]), new GasRegistry { Air, Fuel }),
                Throws.TypeOf<InvalidDataException>());

            Assert.That(() => NumosStateSerializer.Write(new MemoryStream(), new Scrubber("x", 1f)), Throws.InvalidOperationException);
        });
    }

    [Test]
    public void StateFile_SavesAtomicallyAndLoads()
    {
        string directory = Path.Combine(Path.GetTempPath(), "numos-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "fire.numosstate");
            var fire = new FireSettings(450f, Mode.Flame, ["wood"]);
            NumosStateFile.Save(path, fire);
            Assert.That(() => NumosStateFile.Save(path, fire), Throws.InstanceOf<IOException>());
            NumosStateFile.Save(path, new FireSettings(500f, Mode.Flame, ["wood"]), true);

            FireSettings loaded = NumosStateFile.Load<FireSettings>(path);
            Assert.Multiple(() =>
            {
                Assert.That(loaded.IgnitionTemperature, Is.EqualTo(500f));
                Assert.That(Directory.GetFiles(directory), Has.Length.EqualTo(1), "no temporary files left behind");
            });
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
