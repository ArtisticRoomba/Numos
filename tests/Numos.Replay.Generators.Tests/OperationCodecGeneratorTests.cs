using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Numos.Replay.Generators.Tests;

[TestFixture]
public sealed class OperationCodecGeneratorTests
{
    private const string SimHint = "Numos.CoreSim.Replay.AtmosReplayCodec.Operations.g.cs";
    private const string WorldHint = "Numos.API.AtmosWorldReplayCodec.Operations.g.cs";

    private const string Primitives = """
                                      namespace Numos.Maths
                                      {
                                          public readonly record struct Int3(int X, int Y, int Z);
                                      }

                                      namespace Numos.CoreSim.Datatypes.Primitives
                                      {
                                          public readonly record struct VoxelClassification(int RoomId);
                                      }
                                      """;

    private const string StandardOperations = """
                                              public sealed record CreateChunkOperation(Numos.Maths.Int3 Position) : AtmosOperation
                                              {
                                                  public override AtmosOperationCode Code => AtmosOperationCode.CreateChunk;
                                              }

                                              public sealed record SetVoxelClassificationOperation(
                                                  Numos.Maths.Int3 Position,
                                                  ushort LocalVoxelIndex,
                                                  Numos.CoreSim.Datatypes.Primitives.VoxelClassification Classification) : AtmosOperation
                                              {
                                                  public override AtmosOperationCode Code => AtmosOperationCode.SetVoxelClassification;
                                              }

                                              public sealed record SetSolverEnabledOperation(string Name, bool Enabled) : AtmosOperation
                                              {
                                                  public override AtmosOperationCode Code => AtmosOperationCode.SetSolverEnabled;
                                              }
                                              """;

    [Test]
    public void ConcreteOperations_GenerateCodecFromTheirOwnDeclarations()
    {
        Compilation output = RunSim(out var diagnostics, StandardOperations);

        Assert.That(diagnostics, Is.Empty);
        Assert.That(GeneratorHarness.Errors(output), Is.Empty, string.Join("\n", GeneratorHarness.Errors(output)));

        string codec = GeneratorHarness.Generated(output, SimHint);
        Assert.Multiple(() =>
        {
            Assert.That(codec, Does.Contain("case global::Numos.CoreSim.Replay.CreateChunkOperation op:"));
            Assert.That(codec, Does.Contain("global::Numos.CoreSim.Replay.AtmosReplayCodec.WriteInt3(writer, op.Position);"));
            Assert.That(codec, Does.Contain("writer.Write(op.LocalVoxelIndex);"));
            Assert.That(codec, Does.Contain("global::Numos.CoreSim.Replay.AtmosReplayCodec.WriteVoxelClassification(writer, op.Classification);"));
            Assert.That(codec, Does.Contain("global::Numos.CoreSim.Replay.AtmosReplayCodec.ReadString(reader, maxStringBytes), reader.ReadBoolean()"));
            Assert.That(codec, Does.Contain("case global::Numos.CoreSim.Replay.AtmosOperationCode.SetSolverEnabled:"));
        });
    }

    [Test]
    public void QuantityAliasField_IsEncodedAsItsCatalogStorageType()
    {
        // Inside Numos.CoreSim the alias is still unresolved when generators run, so the catalog is the only source
        // of its storage type.
        const string operation = """
                                 public sealed record CreateChunkOperation(Kelvin Temperature) : AtmosOperation
                                 {
                                     public override AtmosOperationCode Code => AtmosOperationCode.CreateChunk;
                                 }

                                 public sealed record SetVoxelClassificationOperation(Mole64 Amount) : AtmosOperation
                                 {
                                     public override AtmosOperationCode Code => AtmosOperationCode.SetVoxelClassification;
                                 }

                                 public sealed record SetSolverEnabledOperation() : AtmosOperation
                                 {
                                     public override AtmosOperationCode Code => AtmosOperationCode.SetSolverEnabled;
                                 }
                                 """;

        const string catalog = """
                               # id | alias | storage | dimensions
                               temperature | Kelvin | global::System.Single | temperature=1
                               amount64 | Mole64 | global::System.Double | amount=1
                               """;

        Compilation output = GeneratorHarness.Run(new OperationCodecGenerator(),
            GeneratorHarness.CreateCompilation("Sim", [], Primitives, SimDomain(operation)),
            out var diagnostics, GeneratorHarness.Catalog(catalog));

        Assert.That(diagnostics, Is.Empty);
        string codec = GeneratorHarness.Generated(output, SimHint);
        Assert.Multiple(() =>
        {
            Assert.That(codec, Does.Contain("new global::Numos.CoreSim.Replay.CreateChunkOperation(reader.ReadSingle())"));
            Assert.That(codec, Does.Contain("new global::Numos.CoreSim.Replay.SetVoxelClassificationOperation(reader.ReadDouble())"));
        });
    }

    [Test]
    public void FieldWithoutEncoding_ReportsUnsupportedFieldType()
    {
        const string operation = """
                                 public sealed record CreateChunkOperation(System.Guid Id, Kelvin Temperature) : AtmosOperation
                                 {
                                     public override AtmosOperationCode Code => AtmosOperationCode.CreateChunk;
                                 }
                                 """;

        RunSim(out var diagnostics, operation, members: "CreateChunk = 2");

        // Guid has no WriteGuid/ReadGuid helper, and Kelvin has no catalog entry in this compilation.
        Assert.That(diagnostics.Select(static diagnostic => diagnostic.Id), Has.Exactly(1).EqualTo("NUMOSREPLAYGEN001"));
        Assert.That(diagnostics.Single().GetMessage(), Does.Contain("System.Guid"));

        RunSim(out diagnostics, operation.Replace("System.Guid Id, ", ""), members: "CreateChunk = 2");
        Assert.That(diagnostics.Select(static diagnostic => diagnostic.Id), Is.EqualTo(new[] { "NUMOSREPLAYGEN001" }));
    }

    [Test]
    public void SharedOpcode_ReportsDuplicateCode()
    {
        const string duplicate = """
                                 public sealed record WakeChunkOperation(Numos.Maths.Int3 Position) : AtmosOperation
                                 {
                                     public override AtmosOperationCode Code => AtmosOperationCode.CreateChunk;
                                 }
                                 """;

        RunSim(out var diagnostics, StandardOperations + duplicate);

        Assert.That(diagnostics.Select(static diagnostic => diagnostic.Id), Is.EqualTo(new[] { "NUMOSREPLAYGEN004" }));
    }

    [Test]
    public void OpcodeWithoutOperation_ReportsMissingOperation()
    {
        RunSim(out var diagnostics, StandardOperations,
            members: "CreateChunk = 2, SetVoxelClassification = 6, SetSolverEnabled = 11, SleepChunk = 10");

        Assert.That(diagnostics.Select(static diagnostic => diagnostic.Id), Is.EqualTo(new[] { "NUMOSREPLAYGEN008" }));
        Assert.That(diagnostics.Single().GetMessage(), Does.Contain("AtmosOperationCode.SleepChunk"));
    }

    [Test]
    public void ComputedCode_ReportsNonConstantCode()
    {
        const string computed = """
                                public sealed record CreateChunkOperation(Numos.Maths.Int3 Position) : AtmosOperation
                                {
                                    private readonly AtmosOperationCode _code = AtmosOperationCode.CreateChunk;

                                    public override AtmosOperationCode Code => _code;
                                }
                                """;

        RunSim(out var diagnostics, computed, members: "CreateChunk = 2");

        // The opcode stays unclaimed too, so the enum member is reported as well.
        Assert.That(diagnostics.Select(static diagnostic => diagnostic.Id),
            Is.EquivalentTo(new[] { "NUMOSREPLAYGEN011", "NUMOSREPLAYGEN008" }));
    }

    [Test]
    public void HandWrittenCodecMethods_ReplaceTheGeneratedBody()
    {
        const string codecMembers = """
                                    private static void WriteSetVoxelClassificationOperation(
                                        System.IO.BinaryWriter writer, SetVoxelClassificationOperation operation) { }

                                    private static SetVoxelClassificationOperation ReadSetVoxelClassificationOperation(
                                        System.IO.BinaryReader reader) => null!;
                                    """;

        Compilation output = RunSim(out var diagnostics, StandardOperations, codecMembers: codecMembers);

        Assert.That(diagnostics, Is.Empty);
        Assert.That(GeneratorHarness.Errors(output), Is.Empty, string.Join("\n", GeneratorHarness.Errors(output)));
        string codec = GeneratorHarness.Generated(output, SimHint);
        Assert.Multiple(() =>
        {
            Assert.That(codec, Does.Contain("AtmosReplayCodec.WriteSetVoxelClassificationOperation(writer, op);"));
            Assert.That(codec, Does.Contain("return global::Numos.CoreSim.Replay.AtmosReplayCodec.ReadSetVoxelClassificationOperation(reader);"));
        });
    }

    [Test]
    public void HandWrittenCodecWithWrongReader_ReportsMismatch()
    {
        const string codecMembers = """
                                    private static void WriteSetVoxelClassificationOperation(
                                        System.IO.BinaryWriter writer, SetVoxelClassificationOperation operation) { }

                                    private static SetVoxelClassificationOperation ReadSetVoxelClassificationOperation(
                                        System.IO.BinaryReader reader, string unexpected) => null!;
                                    """;

        RunSim(out var diagnostics, StandardOperations, codecMembers: codecMembers);

        Assert.That(diagnostics.Select(static diagnostic => diagnostic.Id), Is.EqualTo(new[] { "NUMOSREPLAYGEN007" }));
        Assert.That(diagnostics.Single().GetMessage(), Does.Contain("ReadSetVoxelClassificationOperation"));
    }

    [Test]
    public void OperationWithSeveralConstructors_ReportsAmbiguousConstructor()
    {
        const string operation = """
                                 public sealed record CreateChunkOperation(Numos.Maths.Int3 Position) : AtmosOperation
                                 {
                                     public CreateChunkOperation() : this(default(Numos.Maths.Int3)) { }

                                     public override AtmosOperationCode Code => AtmosOperationCode.CreateChunk;
                                 }
                                 """;

        RunSim(out var diagnostics, operation, members: "CreateChunk = 2");

        Assert.That(diagnostics.Select(static diagnostic => diagnostic.Id), Is.EqualTo(new[] { "NUMOSREPLAYGEN002" }));
    }

    [Test]
    public void WorldFamily_UsesSimulationCodecHelpersAcrossAssemblies()
    {
        // Numos.CoreSim is compiled first and referenced as metadata, the way Numos.API sees it.
        CSharpCompilation sim = GeneratorHarness.CreateCompilation("Sim", [],
            "[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"World\")]", Primitives,
            SimDomain(StandardOperations));
        Compilation simOutput = GeneratorHarness.Run(new OperationCodecGenerator(), sim, out var simDiagnostics);
        Assert.That(simDiagnostics, Is.Empty);
        MetadataReference simReference = Emit(simOutput);

        const string world = """
                             namespace Numos.API
                             {
                                 public enum AtmosWorldOperationCode : ushort
                                 {
                                     SimulationOperation = 1,
                                     CreateSimulation = 3,
                                 }

                                 public abstract record AtmosWorldOperation
                                 {
                                     public abstract AtmosWorldOperationCode Code { get; }
                                 }

                                 public sealed record AtmosWorldSimulationOperation(
                                     int Simulation, Numos.CoreSim.Replay.AtmosOperation Operation) : AtmosWorldOperation
                                 {
                                     public override AtmosWorldOperationCode Code => AtmosWorldOperationCode.SimulationOperation;
                                 }

                                 public sealed record CreateAtmosSimulationOperation(int Simulation, Numos.Maths.Int3 ChunkDimensions)
                                     : AtmosWorldOperation
                                 {
                                     public override AtmosWorldOperationCode Code => AtmosWorldOperationCode.CreateSimulation;
                                 }

                                 internal static partial class AtmosWorldReplayCodec
                                 {
                                     private static void WriteAtmosOperation(
                                         System.IO.BinaryWriter writer, Numos.CoreSim.Replay.AtmosOperation operation) { }

                                     private static Numos.CoreSim.Replay.AtmosOperation ReadAtmosOperation(
                                         System.IO.BinaryReader reader, int maxStringBytes) => null!;
                                 }
                             }
                             """;

        Compilation output = GeneratorHarness.Run(new OperationCodecGenerator(),
            GeneratorHarness.CreateCompilation("World", [simReference], world), out var diagnostics);

        Assert.That(diagnostics, Is.Empty);
        Assert.That(GeneratorHarness.Errors(output), Is.Empty, string.Join("\n", GeneratorHarness.Errors(output)));
        Assert.That(output.SyntaxTrees.Any(static tree => tree.FilePath.EndsWith(SimHint, StringComparison.Ordinal)),
            Is.False, "the simulation codec belongs to the compilation that declares AtmosOperation");

        string codec = GeneratorHarness.Generated(output, WorldHint);
        Assert.Multiple(() =>
        {
            Assert.That(codec, Does.Contain("global::Numos.CoreSim.Replay.AtmosReplayCodec.WriteInt3(writer, op.ChunkDimensions);"));
            Assert.That(codec, Does.Contain("global::Numos.API.AtmosWorldReplayCodec.ReadAtmosOperation(reader, maxStringBytes)"));
            Assert.That(codec, Does.Contain("World replay opcode {code} is unsupported."));
        });
    }

    private static Compilation RunSim(out ImmutableArray<Diagnostic> diagnostics, string operations,
        string members = "CreateChunk = 2, SetVoxelClassification = 6, SetSolverEnabled = 11", string codecMembers = "")
    {
        return GeneratorHarness.Run(new OperationCodecGenerator(), out diagnostics, Primitives,
            SimDomain(operations, members, codecMembers));
    }

    private static string SimDomain(string operations,
        string members = "CreateChunk = 2, SetVoxelClassification = 6, SetSolverEnabled = 11", string codecMembers = "")
    {
        return $$"""
                 namespace Numos.CoreSim.Replay
                 {
                     using Numos.Maths;
                     using Numos.CoreSim.Datatypes.Primitives;

                     public enum AtmosOperationCode : ushort
                     {
                         {{members}}
                     }

                     public abstract record AtmosOperation
                     {
                         public abstract AtmosOperationCode Code { get; }
                     }

                     {{operations}}

                     internal static partial class AtmosReplayCodec
                     {
                         internal static void WriteInt3(System.IO.BinaryWriter writer, Int3 value) { }
                         internal static Int3 ReadInt3(System.IO.BinaryReader reader) => default;
                         internal static void WriteString(System.IO.BinaryWriter writer, string value) { }
                         internal static string ReadString(System.IO.BinaryReader reader, int maxStringBytes) => "";
                         private static void WriteVoxelClassification(System.IO.BinaryWriter writer, VoxelClassification value) { }
                         private static VoxelClassification ReadVoxelClassification(System.IO.BinaryReader reader) => default;

                         {{codecMembers}}
                     }
                 }
                 """;
    }

    private static MetadataReference Emit(Compilation compilation)
    {
        using var image = new MemoryStream();
        var result = compilation.Emit(image);
        Assert.That(result.Success, Is.True, string.Join("\n", result.Diagnostics));
        return MetadataReference.CreateFromImage(image.ToArray());
    }
}
