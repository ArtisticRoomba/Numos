using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Numos.API.Generators.State;

namespace Numos.API.Generators.Tests;

/// <summary>
///     Generator warnings sit in a syntax tree, so <c>#pragma warning disable</c> silences them like a compiler warning.
/// </summary>
[TestFixture]
public sealed class GeneratorDiagnosticLocationTests
{
    private const string Disable = "/*disable*/";
    private const string Restore = "/*restore*/";

    private static IEnumerable<TestCaseData> Warnings()
    {
        yield return new TestCaseData(
            "NUMOSSTATE016",
            "state",
            $$"""
              using Numos.CoreSim.State;

              namespace MyMod;

              public sealed partial class Settings
              {
                  [NumosSerialize] public float Rate { get; init; }
              {{Disable}}
                  public float Loose { get; set; }
              {{Restore}}
              }
              """).SetArgDisplayNames("NUMOSSTATE016");

        yield return new TestCaseData(
            "NUMOSCHUNK016",
            "chunk",
            $$"""
              public static partial class Data
              {
              {{Disable}}
                  [Numos.API.ChunkData("tests/struct")]
                  public static partial Numos.API.ChunkField<System.Numerics.Vector2> Value { get; }
              {{Restore}}
              }
              """).SetArgDisplayNames("NUMOSCHUNK016");

        yield return new TestCaseData(
            "NUMOSDEP007",
            "dependency",
            $$"""
              namespace MyMod;

              {{Disable}}
              public sealed partial class NotASolver
              {
                  [Numos.API.SolverDependency]
                  private partial System.IServiceProvider First { get; }
              }
              {{Restore}}
              """).SetArgDisplayNames("NUMOSDEP007");

        yield return new TestCaseData(
            "NUMOSSOLVER017",
            "solver",
            $$"""
              using Numos.API;

              namespace MyMod;

              {{Disable}}
              [AtmosSolver("my-mod/fire")]
              public sealed partial class Solver : IAtmosWorldSolver
              {
                  public void Solve(AtmosWorldSolverContext context)
                  {
                  }
              }
              {{Restore}}
              """).SetArgDisplayNames("NUMOSSOLVER017");

        yield return new TestCaseData(
            "NUMOSCOMPONENT004",
            "component",
            $$"""
              using Numos.API;
              using Numos.CoreSim.State;

              [ChunkComponent("tests/fire")]
              public partial struct Fire
              {
                  [NumosSerialize] public float Exposure;
              {{Disable}}
                  public float Loose;
              {{Restore}}
              }
              """).SetArgDisplayNames("NUMOSCOMPONENT004");
    }

    [TestCaseSource(nameof(Warnings))]
    public void Warning_IsReportedInTheDeclaringSyntaxTree(string id, string generator, string source)
    {
        ImmutableArray<Diagnostic> diagnostics = Run(generator, out Compilation output, Strip(source));

        Diagnostic diagnostic = diagnostics.Single(diagnostic => diagnostic.Id == id);
        SyntaxTree declaring = output.SyntaxTrees.Single(static tree => tree.FilePath == "Source0.cs");
        Assert.That(diagnostic.Location.SourceTree, Is.SameAs(declaring));
        Assert.That(diagnostic.IsSuppressed, Is.False);
    }

    [TestCaseSource(nameof(Warnings))]
    public void PragmaWarningDisable_SuppressesTheWarning(string id, string generator, string source)
    {
        string suppressed = source
            .Replace(Disable, "#pragma warning disable " + id)
            .Replace(Restore, "#pragma warning restore " + id);

        ImmutableArray<Diagnostic> diagnostics = Run(generator, out _, suppressed);

        // The driver keeps a pragma-suppressed diagnostic but marks it, and the compiler skips marked ones.
        Diagnostic[] matching = diagnostics.Where(diagnostic => diagnostic.Id == id).ToArray();
        Assert.That(matching, Has.Length.EqualTo(1), string.Join("\n", diagnostics));
        Assert.That(matching[0].IsSuppressed, Is.True);
    }

    [Test]
    public void UnrelatedEdit_WithWarnings_RerunsReportingAndLeavesSourceOutputsCached()
    {
        const string source = """
                              using Numos.CoreSim.State;

                              namespace MyMod;

                              public sealed partial class Settings
                              {
                                  [NumosSerialize] public float Rate { get; init; }
                                  public float Loose { get; set; }
                              }
                              """;

        CSharpParseOptions parseOptions = GeneratorHarness.ParseOptions();
        CSharpCompilation compilation = GeneratorHarness.CreateCompilation(parseOptions, source);
        GeneratorDriver driver = GeneratorHarness.CreateDriver(parseOptions, trackSteps: true, generator: new StateGenerator());
        driver = driver.RunGenerators(compilation);

        CSharpCompilation edited = compilation.AddSyntaxTrees(
            CSharpSyntaxTree.ParseText(
                "namespace Other; internal static class Unrelated { }",
                parseOptions,
                path: "Unrelated.cs"));

        driver = driver.RunGenerators(edited);

        GeneratorRunResult result = driver.GetRunResult().Results.Single();
        Assert.That(GeneratorHarness.Ids(result.Diagnostics), Is.EqualTo(new[] { "NUMOSSTATE016" }));
        Assert.That(result.GeneratedSources, Has.Length.EqualTo(1));
        Assert.That(
            GeneratorHarness.SourceOutputReasons(result),
            Is.Not.Empty.And.All.EqualTo(IncrementalStepRunReason.Cached));

        Assert.That(
            GeneratorHarness.ReportingOutputReasons(result),
            Has.Some.Not.EqualTo(IncrementalStepRunReason.Cached));
    }

    private static string Strip(string source)
    {
        return source.Replace(Disable, "").Replace(Restore, "");
    }

    private static ImmutableArray<Diagnostic> Run(string generator, out Compilation output, string source)
    {
        ImmutableArray<Diagnostic> diagnostics;
        switch (generator)
        {
            case "state":
                GeneratorHarness.RunState(out output, out diagnostics, null, source);
                break;
            case "chunk":
                GeneratorHarness.Run(out output, out diagnostics, sources: source);
                break;
            case "dependency":
                GeneratorHarness.RunDependencies(out output, out diagnostics, source);
                break;
            case "solver":
                AtmosSolverGeneratorTests.RunSolvers(out output, out diagnostics, source);
                break;
            case "component":
                GeneratorHarness.RunComponents(out output, out diagnostics, sources: source);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(generator), generator, null);
        }

        return diagnostics;
    }
}
