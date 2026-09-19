using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Numos.Replay.SourceGen.Tests;

[TestFixture]
public sealed class ReplayCodecGeneratorTests
{
    // Mirrors the real Numos.CoreSim.Replay shapes closely enough to exercise every wire-kind the generator
    // supports (Int3, VoxelClassification, ushort, int, float, string, bool) without depending on Numos.CoreSim.
    private const string DomainSource = """
                                         namespace Numos.Maths
                                         {
                                             public readonly record struct Int3(int X, int Y, int Z);
                                         }

                                         namespace Numos.CoreSim.Datatypes.Primitives
                                         {
                                             public readonly record struct VoxelClassification(int RoomId);
                                         }

                                         namespace Numos.CoreSim.Replay
                                         {
                                             public enum AtmosOperationCode : ushort
                                             {
                                                 CreateChunk = 2,
                                                 SetVoxelClassification = 6,
                                                 SetSolverEnabled = 11,
                                             }

                                             public abstract record AtmosOperation
                                             {
                                                 public abstract AtmosOperationCode Code { get; }
                                             }

                                             public sealed record CreateChunkOperation(global::Numos.Maths.Int3 Position) : AtmosOperation
                                             {
                                                 public override AtmosOperationCode Code => AtmosOperationCode.CreateChunk;
                                             }

                                             public sealed record SetVoxelClassificationOperation(
                                                 global::Numos.Maths.Int3 Position,
                                                 ushort LocalVoxelIndex,
                                                 global::Numos.CoreSim.Datatypes.Primitives.VoxelClassification Classification) : AtmosOperation
                                             {
                                                 public override AtmosOperationCode Code => AtmosOperationCode.SetVoxelClassification;
                                             }

                                             public sealed record SetSolverEnabledOperation(string Name, bool Enabled) : AtmosOperation
                                             {
                                                 public override AtmosOperationCode Code => AtmosOperationCode.SetSolverEnabled;
                                             }

                                             public sealed record UnsupportedFieldOperation(double Fraction) : AtmosOperation
                                             {
                                                 public override AtmosOperationCode Code => AtmosOperationCode.CreateChunk;
                                             }
                                         }
                                         """;

    // A minimal stand-in for the hand-written half of NumosReplaySerializer -- just enough for generated code
    // calling WriteInt3/ReadInt3/WriteString/ReadString (unqualified, as same-partial-class members) to compile.
    // Registrations attach via a separate partial declaration in each test, mirroring how the real
    // NumosReplaySerializer.cs carries the [WireOperation] attribute list directly on its own class declaration.
    private const string SerializerStubSource = """
                                                  namespace Numos.Serialization
                                                  {
                                                      public sealed class NumosReplayReadOptions;

                                                      public static partial class NumosReplaySerializer
                                                      {
                                                          internal static void WriteInt3(System.IO.BinaryWriter writer, Numos.Maths.Int3 value) { }
                                                          internal static Numos.Maths.Int3 ReadInt3(System.IO.BinaryReader reader) => default;
                                                          internal static void WriteString(System.IO.BinaryWriter writer, string value) { }
                                                          internal static string ReadString(System.IO.BinaryReader reader, NumosReplayReadOptions options) => "";

                                                          // Stand-ins for the hand-written delegation wrappers a world operation's nested-operation field
                                                          // calls into -- these are distinct from the generated TryWriteGeneratedOperation/TryReadGeneratedOperation.
                                                          internal static void WriteOperation(System.IO.BinaryWriter writer, Numos.CoreSim.Replay.AtmosOperation operation) { }
                                                          internal static Numos.CoreSim.Replay.AtmosOperation ReadOperation(
                                                              System.IO.BinaryReader reader, ushort rawCode, NumosReplayReadOptions options) => null!;
                                                      }
                                                  }
                                                  """;

    [Test]
    public void Initialize_EmitsWireOperationAttributeWithNoDiagnostics()
    {
        var compilation = RunGenerator(out var diagnostics, "namespace Placeholder;");

        Assert.That(diagnostics, Is.Empty);

        var attribute = compilation.GetTypeByMetadataName("Numos.Replay.SourceGen.WireOperationAttribute");
        Assert.That(attribute, Is.Not.Null);
        Assert.That(attribute!.BaseType?.ToDisplayString(), Is.EqualTo("System.Attribute"));

        var constructor = attribute.Constructors.Single(static ctor => !ctor.IsStatic);
        var usage = attribute.GetAttributes().Single(
            static data => data.AttributeClass?.Name == nameof(System.AttributeUsageAttribute));

        Assert.Multiple(() =>
        {
            Assert.That(constructor.Parameters, Has.Length.EqualTo(2));
            Assert.That(constructor.Parameters[0].Type.ToDisplayString(), Is.EqualTo("System.Type"));
            Assert.That(constructor.Parameters[1].Type.SpecialType, Is.EqualTo(SpecialType.System_Object));

            var custom = (IPropertySymbol)attribute.GetMembers("Custom").Single();
            Assert.That(custom.Type.SpecialType, Is.EqualTo(SpecialType.System_Boolean));

            // Class-only: every registration (mechanical or Custom) stacks directly on the class that receives the
            // generated codec. AllowMultiple is required since one class carries one attribute per opcode.
            var validOn = (AttributeTargets)usage.ConstructorArguments.Single().Value!;
            Assert.That(validOn, Is.EqualTo(AttributeTargets.Class));
            Assert.That(usage.NamedArguments.Single(static arg => arg.Key == "AllowMultiple").Value.Value, Is.EqualTo(true));
        });
    }

    [Test]
    public void AssemblyLevelRegistration_IsRejectedByAttributeUsage()
    {
        const string registration =
            "[assembly: Numos.Replay.SourceGen.WireOperation(" +
            "typeof(Numos.CoreSim.Replay.CreateChunkOperation), Numos.CoreSim.Replay.AtmosOperationCode.CreateChunk)]";

        var compilation = RunGenerator(out var diagnostics, DomainSource, SerializerStubSource, registration);

        // Now a plain C# compiler error (CS0592, "attribute is not valid on this declaration type"), not a
        // generator diagnostic -- the AttributeUsage restriction makes the wrong-placement mistake impossible to
        // reach generator logic at all, rather than needing the generator to detect and report it itself.
        Assert.That(diagnostics, Is.Empty);
        Assert.That(compilation.GetDiagnostics().Select(static d => d.Id), Does.Contain("CS0592"));
    }

    [Test]
    public void RegisteredOperations_GenerateCodecThatCompilesAgainstTheDomain()
    {
        const string registrations = """
                                      namespace Numos.Serialization
                                      {
                                          [Numos.Replay.SourceGen.WireOperation(
                                              typeof(Numos.CoreSim.Replay.CreateChunkOperation), Numos.CoreSim.Replay.AtmosOperationCode.CreateChunk)]
                                          [Numos.Replay.SourceGen.WireOperation(
                                              typeof(Numos.CoreSim.Replay.SetVoxelClassificationOperation), Numos.CoreSim.Replay.AtmosOperationCode.SetVoxelClassification)]
                                          [Numos.Replay.SourceGen.WireOperation(
                                              typeof(Numos.CoreSim.Replay.SetSolverEnabledOperation), Numos.CoreSim.Replay.AtmosOperationCode.SetSolverEnabled)]
                                          public static partial class NumosReplaySerializer;
                                      }
                                      """;

        var compilation = RunGenerator(out var diagnostics, DomainSource, SerializerStubSource, registrations);

        Assert.That(diagnostics, Is.Empty);

        // The strongest available check: the generated switch statements must type-check against the domain types
        // and the serializer stub, not merely look plausible as text.
        var compileErrors = compilation.GetDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.That(compileErrors, Is.Empty, string.Join("\n", compileErrors));

        var generatedCodec = compilation.SyntaxTrees.Single(static tree => tree.FilePath.Contains("SimulationOperationCodec"));
        string codecText = generatedCodec.ToString();
        Assert.Multiple(() =>
        {
            Assert.That(codecText, Does.Contain("case global::Numos.CoreSim.Replay.CreateChunkOperation op:"));
            Assert.That(codecText, Does.Contain("global::Numos.Serialization.NumosReplaySerializer.WriteInt3(writer, op.Position);"));
            Assert.That(codecText, Does.Contain("writer.Write(op.Classification.RoomId);"));
            Assert.That(codecText, Does.Contain("global::Numos.Serialization.NumosReplaySerializer.WriteString(writer, op.Name);"));
            Assert.That(codecText, Does.Contain("case global::Numos.CoreSim.Replay.AtmosOperationCode.SetSolverEnabled:"));
        });
    }

    [Test]
    public void WorldFamilyRegistration_GeneratesSeparateCodecOnItsOwnHostWithNestedOperationSupport()
    {
        const string worldDomain = """
                                    namespace Numos.API
                                    {
                                        public enum AtmosWorldOperationCode : ushort
                                        {
                                            SimulationOperation = 1,
                                            DestroySimulation = 4,
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

                                        public sealed record DestroyAtmosSimulationOperation(int Simulation) : AtmosWorldOperation
                                        {
                                            public override AtmosWorldOperationCode Code => AtmosWorldOperationCode.DestroySimulation;
                                        }
                                    }
                                    """;

        const string worldRegistrations = """
                                           namespace Numos.Serialization
                                           {
                                               [Numos.Replay.SourceGen.WireOperation(
                                                   typeof(Numos.API.AtmosWorldSimulationOperation), Numos.API.AtmosWorldOperationCode.SimulationOperation)]
                                               [Numos.Replay.SourceGen.WireOperation(
                                                   typeof(Numos.API.DestroyAtmosSimulationOperation), Numos.API.AtmosWorldOperationCode.DestroySimulation)]
                                               public static partial class NumosWorldReplaySerializer;
                                           }
                                           """;

        const string simRegistration = """
                                        namespace Numos.Serialization
                                        {
                                            [Numos.Replay.SourceGen.WireOperation(
                                                typeof(Numos.CoreSim.Replay.CreateChunkOperation), Numos.CoreSim.Replay.AtmosOperationCode.CreateChunk)]
                                            public static partial class NumosReplaySerializer;
                                        }
                                        """;

        var compilation = RunGenerator(
            out var diagnostics, DomainSource, worldDomain, SerializerStubSource, worldRegistrations, simRegistration);

        Assert.That(diagnostics, Is.Empty);

        var compileErrors = compilation.GetDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.That(compileErrors, Is.Empty, string.Join("\n", compileErrors));

        // Two distinct codec files, one per family, each landing in the class its own registrations were actually
        // stacked on -- proving codec emission is driven by the resolved host symbol, not a hardcoded class name.
        var simCodec = compilation.SyntaxTrees.Single(static tree => tree.FilePath.Contains("SimulationOperationCodec"));
        var worldCodec = compilation.SyntaxTrees.Single(static tree => tree.FilePath.Contains("WorldOperationCodec"));

        Assert.Multiple(() =>
        {
            Assert.That(simCodec.ToString(), Does.Contain("public static partial class NumosReplaySerializer"));
            Assert.That(worldCodec.ToString(), Does.Contain("public static partial class NumosWorldReplaySerializer"));
            Assert.That(worldCodec.ToString(), Does.Contain("switch ((global::Numos.API.AtmosWorldOperationCode)code)"));

            // Nested operation field: writes/reads its own opcode, then delegates into the sim family's codec for
            // the payload -- left-to-right argument evaluation on the read side consumes the opcode first (the
            // golden world-replay bytes are what actually proves the ordering; these just confirm both calls exist).
            Assert.That(worldCodec.ToString(), Does.Contain("writer.Write((ushort)op.Operation.Code);"));
            Assert.That(
                worldCodec.ToString(),
                Does.Contain("global::Numos.Serialization.NumosReplaySerializer.WriteOperation(writer, op.Operation);"));
            Assert.That(
                worldCodec.ToString(),
                Does.Contain("global::Numos.Serialization.NumosReplaySerializer.ReadOperation(reader, reader.ReadUInt16(), options)"));
        });
    }

    [Test]
    public void UnsupportedFieldType_ReportsDiagnosticAndExcludesTheOperation()
    {
        const string registration = """
                                     namespace Numos.Serialization
                                     {
                                         [Numos.Replay.SourceGen.WireOperation(
                                             typeof(Numos.CoreSim.Replay.UnsupportedFieldOperation), Numos.CoreSim.Replay.AtmosOperationCode.CreateChunk)]
                                         public static partial class NumosReplaySerializer;
                                     }
                                     """;

        var compilation = RunGenerator(out var diagnostics, DomainSource, SerializerStubSource, registration);

        Assert.That(diagnostics.Select(static d => d.Id), Does.Contain("NUMOSREPLAYGEN001"));
        Assert.That(
            compilation.SyntaxTrees.Any(static tree => tree.FilePath.Contains("SimulationOperationCodec")),
            Is.False,
            "no operations were successfully registered, so no codec file should be generated");
    }

    [Test]
    public void DuplicateCode_ReportsDiagnostic()
    {
        const string registrations = """
                                      namespace Numos.Serialization
                                      {
                                          [Numos.Replay.SourceGen.WireOperation(
                                              typeof(Numos.CoreSim.Replay.CreateChunkOperation), Numos.CoreSim.Replay.AtmosOperationCode.CreateChunk)]
                                          [Numos.Replay.SourceGen.WireOperation(
                                              typeof(Numos.CoreSim.Replay.SetSolverEnabledOperation), Numos.CoreSim.Replay.AtmosOperationCode.CreateChunk)]
                                          public static partial class NumosReplaySerializer;
                                      }
                                      """;

        var compilation = RunGenerator(out var diagnostics, DomainSource, SerializerStubSource, registrations);

        Assert.That(diagnostics.Select(static d => d.Id), Does.Contain("NUMOSREPLAYGEN004"));
        _ = compilation;
    }

    [Test]
    public void CustomRegistration_GeneratesDispatchCallingHandWrittenMethods()
    {
        const string customCodec = """
                                    namespace Numos.Serialization
                                    {
                                        [Numos.Replay.SourceGen.WireOperation(
                                            typeof(Numos.CoreSim.Replay.CreateChunkOperation), Numos.CoreSim.Replay.AtmosOperationCode.CreateChunk, Custom = true)]
                                        public static partial class NumosReplaySerializer
                                        {
                                            internal static void WriteCreateChunkOperation(
                                                System.IO.BinaryWriter writer, Numos.CoreSim.Replay.CreateChunkOperation operation) { }

                                            internal static Numos.CoreSim.Replay.CreateChunkOperation ReadCreateChunkOperation(
                                                System.IO.BinaryReader reader, NumosReplayReadOptions options) => null!;
                                        }
                                    }
                                    """;

        var compilation = RunGenerator(out var diagnostics, DomainSource, SerializerStubSource, customCodec);

        Assert.That(diagnostics, Is.Empty);

        var compileErrors = compilation.GetDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.That(compileErrors, Is.Empty, string.Join("\n", compileErrors));

        var generatedCodec = compilation.SyntaxTrees.Single(static tree => tree.FilePath.Contains("SimulationOperationCodec"));
        string codecText = generatedCodec.ToString();
        Assert.Multiple(() =>
        {
            Assert.That(codecText, Does.Contain("WriteCreateChunkOperation(writer, op);"));
            Assert.That(codecText, Does.Contain("operation = ReadCreateChunkOperation(reader, options);"));
        });
    }

    [Test]
    public void CustomRegistration_MissingHandWrittenMethod_ReportsDiagnostic()
    {
        const string customCodecWithoutMethods = """
                                                  namespace Numos.Serialization
                                                  {
                                                      [Numos.Replay.SourceGen.WireOperation(
                                                          typeof(Numos.CoreSim.Replay.CreateChunkOperation), Numos.CoreSim.Replay.AtmosOperationCode.CreateChunk, Custom = true)]
                                                      public static partial class NumosReplaySerializer;
                                                  }
                                                  """;

        var compilation = RunGenerator(out var diagnostics, DomainSource, SerializerStubSource, customCodecWithoutMethods);

        Assert.That(diagnostics.Select(static d => d.Id), Does.Contain("NUMOSREPLAYGEN007"));
        _ = compilation;
    }

    private static Compilation RunGenerator(out ImmutableArray<Diagnostic> diagnostics, params string[] sources)
    {
        var compilation = CreateCompilation(sources);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new ReplayCodecGenerator().AsSourceGenerator()],
            parseOptions: (CSharpParseOptions)compilation.SyntaxTrees.First().Options);

        driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out diagnostics);
        return output;
    }

    private static CSharpCompilation CreateCompilation(params string[] sources)
    {
        string[] trustedAssemblies = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator);

        IEnumerable<MetadataReference> references = trustedAssemblies
            .Distinct(StringComparer.Ordinal)
            .Select(path => MetadataReference.CreateFromFile(path));

        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
        return CSharpCompilation.Create(
            "ReplayCodecGeneratorTest",
            sources.Select(source => CSharpSyntaxTree.ParseText(source, parseOptions)),
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }
}
