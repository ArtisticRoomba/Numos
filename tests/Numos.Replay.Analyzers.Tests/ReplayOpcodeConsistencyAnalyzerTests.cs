using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Numos.Replay.SourceGen.Tests;

[TestFixture]
public sealed class ReplayOpcodeConsistencyAnalyzerTests
{
    // Mirrors the WireOperationAttribute that ReplayCodecGenerator normally emits via
    // RegisterPostInitializationOutput -- this test project doesn't reference that generator (only the analyzer),
    // so the attribute is hand-declared here with the exact same metadata name and shape.
    private const string AttributeSource = """
                                            namespace Numos.Replay.SourceGen
                                            {
                                                [System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
                                                public sealed class WireOperationAttribute : System.Attribute
                                                {
                                                    public WireOperationAttribute(System.Type operationType, object code)
                                                    {
                                                        OperationType = operationType;
                                                        Code = code;
                                                    }

                                                    public System.Type OperationType { get; }
                                                    public object Code { get; }
                                                    public bool Custom { get; set; }
                                                }
                                            }
                                            """;

    // A minimal stand-in for Numos.CoreSim.Replay -- two opcodes, matching the real AtmosOperationCode/AtmosOperation shape.
    private const string DomainSource = """
                                         namespace Numos.CoreSim.Replay
                                         {
                                             public enum AtmosOperationCode : ushort
                                             {
                                                 CreateChunk = 2,
                                                 SetSolverEnabled = 11,
                                             }

                                             public abstract record AtmosOperation
                                             {
                                                 public abstract AtmosOperationCode Code { get; }
                                             }

                                             public sealed record CreateChunkOperation : AtmosOperation
                                             {
                                                 public override AtmosOperationCode Code => AtmosOperationCode.CreateChunk;
                                             }

                                             public sealed record SetSolverEnabledOperation : AtmosOperation
                                             {
                                                 public override AtmosOperationCode Code => AtmosOperationCode.SetSolverEnabled;
                                             }
                                         }
                                         """;

    [Test]
    public async Task ConsistentDomain_ReportsNoDiagnostics()
    {
        const string kernel = """
                              namespace Numos.CoreSim
                              {
                                  internal sealed class AtmosKernel
                                  {
                                      internal void ApplyRecordedOperation(Numos.CoreSim.Replay.AtmosOperation operation)
                                      {
                                          switch (operation)
                                          {
                                              case Numos.CoreSim.Replay.CreateChunkOperation op:
                                                  break;
                                              case Numos.CoreSim.Replay.SetSolverEnabledOperation op:
                                                  break;
                                              default:
                                                  throw new System.ArgumentException();
                                          }
                                      }
                                  }
                              }
                              """;

        const string registrations = """
                                      namespace Numos.Serialization
                                      {
                                          [Numos.Replay.SourceGen.WireOperation(
                                              typeof(Numos.CoreSim.Replay.CreateChunkOperation), Numos.CoreSim.Replay.AtmosOperationCode.CreateChunk)]
                                          [Numos.Replay.SourceGen.WireOperation(
                                              typeof(Numos.CoreSim.Replay.SetSolverEnabledOperation), Numos.CoreSim.Replay.AtmosOperationCode.SetSolverEnabled)]
                                          public static class NumosReplaySerializer;
                                      }
                                      """;

        var diagnostics = await Analyze(AttributeSource, DomainSource, kernel, registrations);

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public async Task MissingRegistration_ReportsUnregisteredCode()
    {
        const string kernel = """
                              namespace Numos.CoreSim
                              {
                                  internal sealed class AtmosKernel
                                  {
                                      internal void ApplyRecordedOperation(Numos.CoreSim.Replay.AtmosOperation operation)
                                      {
                                          switch (operation)
                                          {
                                              case Numos.CoreSim.Replay.CreateChunkOperation op:
                                                  break;
                                              case Numos.CoreSim.Replay.SetSolverEnabledOperation op:
                                                  break;
                                              default:
                                                  throw new System.ArgumentException();
                                          }
                                      }
                                  }
                              }
                              """;

        // SetSolverEnabled has an Apply case but no [WireOperation] registration.
        const string registrations = """
                                      namespace Numos.Serialization
                                      {
                                          [Numos.Replay.SourceGen.WireOperation(
                                              typeof(Numos.CoreSim.Replay.CreateChunkOperation), Numos.CoreSim.Replay.AtmosOperationCode.CreateChunk)]
                                          public static class NumosReplaySerializer;
                                      }
                                      """;

        var diagnostics = await Analyze(AttributeSource, DomainSource, kernel, registrations);

        var diagnostic = diagnostics.Single(static d => d.Id == "NUMOSREPLAYGEN008");
        Assert.Multiple(() =>
        {
            Assert.That(diagnostic.GetMessage(), Does.Contain("AtmosOperationCode.SetSolverEnabled"));
            Assert.That(diagnostic.Location, Is.Not.EqualTo(Location.None), "should anchor at the registration host, not nowhere");
        });
    }

    [Test]
    public async Task MissingApplyCase_ReportsMissingApplyCase()
    {
        // SetSolverEnabled has a [WireOperation] registration but no Apply-switch case.
        const string kernel = """
                              namespace Numos.CoreSim
                              {
                                  internal sealed class AtmosKernel
                                  {
                                      internal void ApplyRecordedOperation(Numos.CoreSim.Replay.AtmosOperation operation)
                                      {
                                          switch (operation)
                                          {
                                              case Numos.CoreSim.Replay.CreateChunkOperation op:
                                                  break;
                                              default:
                                                  throw new System.ArgumentException();
                                          }
                                      }
                                  }
                              }
                              """;

        const string registrations = """
                                      namespace Numos.Serialization
                                      {
                                          [Numos.Replay.SourceGen.WireOperation(
                                              typeof(Numos.CoreSim.Replay.CreateChunkOperation), Numos.CoreSim.Replay.AtmosOperationCode.CreateChunk)]
                                          [Numos.Replay.SourceGen.WireOperation(
                                              typeof(Numos.CoreSim.Replay.SetSolverEnabledOperation), Numos.CoreSim.Replay.AtmosOperationCode.SetSolverEnabled)]
                                          public static class NumosReplaySerializer;
                                      }
                                      """;

        var diagnostics = await Analyze(AttributeSource, DomainSource, kernel, registrations);

        var diagnostic = diagnostics.Single(static d => d.Id == "NUMOSREPLAYGEN009");
        Assert.Multiple(() =>
        {
            Assert.That(diagnostic.GetMessage(), Does.Contain("SetSolverEnabledOperation"));
            Assert.That(diagnostic.GetMessage(), Does.Contain("AtmosKernel.ApplyRecordedOperation"));
            Assert.That(diagnostic.Location, Is.Not.EqualTo(Location.None), "should anchor at the operation's Code override");
        });
    }

    [Test]
    public async Task NoRegistrationsAtAll_DoesNotReportUnregisteredCode()
    {
        // A compilation that merely sees the enum/operations (e.g. Numos.CoreSim itself) has no registrations to
        // check -- the registration completeness check must not fire there, since it structurally never will.
        const string kernel = """
                              namespace Numos.CoreSim
                              {
                                  internal sealed class AtmosKernel
                                  {
                                      internal void ApplyRecordedOperation(Numos.CoreSim.Replay.AtmosOperation operation)
                                      {
                                          switch (operation)
                                          {
                                              case Numos.CoreSim.Replay.CreateChunkOperation op:
                                                  break;
                                              case Numos.CoreSim.Replay.SetSolverEnabledOperation op:
                                                  break;
                                              default:
                                                  throw new System.ArgumentException();
                                          }
                                      }
                                  }
                              }
                              """;

        var diagnostics = await Analyze(AttributeSource, DomainSource, kernel);

        Assert.That(diagnostics.Select(static d => d.Id), Does.Not.Contain("NUMOSREPLAYGEN008"));
    }

    private static async Task<ImmutableArray<Diagnostic>> Analyze(params string[] sources)
    {
        var compilation = CreateCompilation(sources);
        Assert.That(
            compilation.GetDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error),
            Is.Empty,
            "synthetic domain must itself compile cleanly");

        var analyzers = ImmutableArray.Create<DiagnosticAnalyzer>(new ReplayOpcodeConsistencyAnalyzer());
        return await compilation.WithAnalyzers(analyzers).GetAnalyzerDiagnosticsAsync();
    }

    private static CSharpCompilation CreateCompilation(string[] sources)
    {
        string[] trustedAssemblies = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator);

        IEnumerable<MetadataReference> references = trustedAssemblies
            .Distinct(StringComparer.Ordinal)
            .Select(path => MetadataReference.CreateFromFile(path));

        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
        return CSharpCompilation.Create(
            "ReplayOpcodeConsistencyAnalyzerTest",
            sources.Select(source => CSharpSyntaxTree.ParseText(source, parseOptions)),
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }
}
