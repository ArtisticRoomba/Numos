using Microsoft.CodeAnalysis;

namespace Numos.Replay.Generators.Tests;

[TestFixture]
public sealed class CheckpointFieldGeneratorTests
{
    private const string Hint = "Numos.CoreSim.Replay.GeneratedCheckpointFields.g.cs";

    private const string Attributes = """
                                      namespace Numos.CoreSim.Replay
                                      {
                                          [System.AttributeUsage(System.AttributeTargets.Property)]
                                          internal sealed class ChunkCheckpointFieldAttribute(string liveMember, int order) : System.Attribute;

                                          [System.AttributeUsage(System.AttributeTargets.Property)]
                                          internal sealed class ConfigCheckpointFieldAttribute(int order) : System.Attribute;

                                          [System.AttributeUsage(System.AttributeTargets.Field)]
                                          internal sealed class GasCheckpointFieldAttribute(int order) : System.Attribute;
                                      }
                                      """;

    [Test]
    public void TaggedFields_DriveCaptureHashAndWireFromOneOrderedList()
    {
        const string domain = """
                              namespace Numos.CoreSim.Replay
                              {
                                  public sealed class AtmosChunkCheckpoint
                                  {
                                      [ChunkCheckpointField("Temperature", 1)]
                                      public System.Collections.Generic.IReadOnlyList<float> Temperatures { get; } = [];

                                      [ChunkCheckpointField("VoxelRoomMap", 0)]
                                      public System.Collections.Generic.IReadOnlyList<int> Classifications { get; } = [];
                                  }
                              }

                              namespace Numos.CoreSim
                              {
                                  public sealed class AtmosConfigSnapshot
                                  {
                                      [Numos.CoreSim.Replay.ConfigCheckpointField(1)]
                                      public int SleepThreshold { get; }

                                      [Numos.CoreSim.Replay.ConfigCheckpointField(0)]
                                      public Kelvin GlobalTemperature { get; }
                                  }

                                  public struct GasProperties
                                  {
                                      [Numos.CoreSim.Replay.GasCheckpointField(0)]
                                      public string Name;

                                      [Numos.CoreSim.Replay.GasCheckpointField(1)]
                                      public bool CondensationEnabled;
                                  }
                              }
                              """;

        Compilation output = GeneratorHarness.Run(new CheckpointFieldGenerator(),
            GeneratorHarness.CreateCompilation("Sim", [], Attributes, domain), out var diagnostics,
            GeneratorHarness.Catalog("temperature | Kelvin | global::System.Single | temperature=1"));

        Assert.That(diagnostics, Is.Empty);
        string generated = GeneratorHarness.Generated(output, Hint);
        Assert.Multiple(() =>
        {
            // Order comes from the attribute, not declaration order.
            Assert.That(generated, Does.Contain(
                "CaptureChunkArrays(global::Numos.CoreSim.AtmosChunk chunk)"));
            Assert.That(generated.IndexOf("chunk.VoxelRoomMap.ToArray()", StringComparison.Ordinal),
                Is.LessThan(generated.IndexOf("chunk.Temperature.ToArray()", StringComparison.Ordinal)));
            Assert.That(generated, Does.Contain("foreach (int value in chunk.Classifications) hash.Add(value);"));
            Assert.That(generated, Does.Contain("AtmosReplayCodec.ReadArray<float>(reader, voxels)"));

            Assert.That(generated.IndexOf("hash.Add(config.GlobalTemperature);", StringComparison.Ordinal),
                Is.LessThan(generated.IndexOf("hash.Add(config.SleepThreshold);", StringComparison.Ordinal)));
            Assert.That(generated, Does.Contain("config.GlobalTemperature = reader.ReadSingle();"));
            Assert.That(generated, Does.Contain("config.SleepThreshold = reader.ReadInt32();"));

            Assert.That(generated, Does.Contain("AtmosReplayCodec.WriteNullableString(writer, gas.Name);"));
            Assert.That(generated, Does.Contain("gas.Name = global::Numos.CoreSim.Replay.AtmosReplayCodec.ReadNullableString(reader, maxStringBytes)!;"));
            Assert.That(generated, Does.Contain("gas.CondensationEnabled = reader.ReadBoolean();"));
        });
    }

    [Test]
    public void DuplicateOrderAndUnsupportedType_AreReported()
    {
        const string domain = """
                              namespace Numos.CoreSim
                              {
                                  public sealed class AtmosConfigSnapshot
                                  {
                                      [Numos.CoreSim.Replay.ConfigCheckpointField(0)]
                                      public int SleepThreshold { get; }

                                      [Numos.CoreSim.Replay.ConfigCheckpointField(0)]
                                      public int AccumulatorMaxAliveTicks { get; }

                                      [Numos.CoreSim.Replay.ConfigCheckpointField(1)]
                                      public System.Guid Seed { get; }
                                  }
                              }
                              """;

        GeneratorHarness.Run(new CheckpointFieldGenerator(),
            GeneratorHarness.CreateCompilation("Sim", [], Attributes, domain), out var diagnostics);

        Assert.That(diagnostics.Select(static diagnostic => diagnostic.Id),
            Is.EquivalentTo(new[] { "NUMOSCHECKPTGEN002", "NUMOSCHECKPTGEN001" }));
    }
}
