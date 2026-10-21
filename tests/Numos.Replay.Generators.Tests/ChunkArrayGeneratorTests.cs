using Microsoft.CodeAnalysis;

namespace Numos.Replay.Generators.Tests;

[TestFixture]
public sealed class ChunkArrayGeneratorTests
{
    private const string Hint = "Numos.CoreSim.Replay.AtmosChunkCheckpoint.LiveChunkArrays.g.cs";

    private const string Attribute = """
                                     namespace Numos.CoreSim.Replay
                                     {
                                         [System.AttributeUsage(System.AttributeTargets.Property)]
                                         internal sealed class LiveChunkArrayAttribute(string liveMember) : System.Attribute;
                                     }
                                     """;

    [Test]
    public void TaggedArrays_GetCaptureAndRestore()
    {
        const string domain = """
                              namespace Numos.CoreSim.Replay
                              {
                                  public sealed partial class AtmosChunkCheckpoint
                                  {
                                      [LiveChunkArray("Temperature")]
                                      public System.Collections.Generic.IReadOnlyList<float> Temperatures { get; private set; } = [];

                                      [LiveChunkArray("VoxelRoomMap")]
                                      public System.Collections.Generic.IReadOnlyList<int> Classifications { get; private set; } = [];

                                      private partial void CaptureLiveArrays(Numos.CoreSim.AtmosChunk chunk);

                                      private partial void RestoreLiveArrays(Numos.CoreSim.AtmosChunk chunk);
                                  }
                              }
                              """;

        Compilation output = GeneratorHarness.Run(
            new ChunkArrayGenerator(),
            GeneratorHarness.CreateCompilation("Sim", [], Attribute, domain),
            out var diagnostics);

        Assert.That(diagnostics, Is.Empty);
        string generated = GeneratorHarness.Generated(output, Hint);
        Assert.Multiple(() =>
        {
            Assert.That(generated, Does.Contain("partial class AtmosChunkCheckpoint"));
            Assert.That(generated, Does.Contain("Temperatures = chunk.Temperature.ToArray();"));
            Assert.That(generated, Does.Contain("Classifications = chunk.VoxelRoomMap.ToArray();"));
            Assert.That(
                generated,
                Does.Contain("global::Numos.CoreSim.Replay.CheckpointArray.CopyTo(Classifications, chunk.VoxelRoomMap.AsSpan());"));
        });
    }

    [Test]
    public void ArrayWithoutASetterOrListType_IsReported()
    {
        const string domain = """
                              namespace Numos.CoreSim.Replay
                              {
                                  public sealed partial class AtmosChunkCheckpoint
                                  {
                                      [LiveChunkArray("Temperature")]
                                      public System.Collections.Generic.IReadOnlyList<float> Temperatures { get; } = [];

                                      [LiveChunkArray("SleepTimer")]
                                      public int SleepTimer { get; private set; }
                                  }
                              }
                              """;

        GeneratorHarness.Run(
            new ChunkArrayGenerator(),
            GeneratorHarness.CreateCompilation("Sim", [], Attribute, domain),
            out var diagnostics);

        Assert.That(
            diagnostics.Select(static diagnostic => diagnostic.Id),
            Is.EquivalentTo(new[] { "NUMOSCHECKPTGEN001", "NUMOSCHECKPTGEN001" }));
    }
}
