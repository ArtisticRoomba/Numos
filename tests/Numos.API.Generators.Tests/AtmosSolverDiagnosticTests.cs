using Microsoft.CodeAnalysis;

namespace Numos.API.Generators.Tests;

[TestFixture]
public sealed class AtmosSolverDiagnosticTests
{
    private static string Solver(
        string attribute = "[AtmosSolver(\"my-mod/fire\", RunsPerSecond = 20)]",
        string declaration = "public sealed partial class Solver : IAtmosWorldSolver",
        string members = "")
    {
        return $$"""
                 using Numos.API;

                 namespace MyMod;

                 {{attribute}}
                 {{declaration}}
                 {
                     {{members}}

                     public void Solve(AtmosWorldSolverContext context)
                     {
                     }
                 }
                 """;
    }

    private static (string[] Ids, GeneratorRunResult Result, Diagnostic[] Diagnostics) Run(params string[] sources)
    {
        GeneratorRunResult result = AtmosSolverGeneratorTests.RunSolvers(out _, out var diagnostics, sources);
        return (GeneratorHarness.Ids(diagnostics), result, [..diagnostics]);
    }

    private static string GeneratedFor(GeneratorRunResult result, string typeHintName)
    {
        return string.Join(
            ",",
            result.GeneratedSources
                .Select(static source => source.HintName)
                .Where(hintName => hintName == typeHintName + ".AtmosSolver.g.cs"));
    }

    [Test]
    public void NonPartialClass_ReportsNumosSolver010AndGeneratesNothing()
    {
        var (ids, result, _) = Run(Solver(declaration: "public sealed class Solver : IAtmosWorldSolver"));

        Assert.That(ids, Is.EqualTo(new[] { "NUMOSSOLVER010" }));
        Assert.That(result.GeneratedSources, Is.Empty);
    }

    [Test]
    public void NonPartialContainingType_ReportsNumosSolver010()
    {
        const string source = """
                              using Numos.API;

                              namespace MyMod;

                              public class Outer
                              {
                                  [AtmosSolver("my-mod/fire", RunsPerSecond = 20)]
                                  public sealed partial class Solver : IAtmosWorldSolver
                                  {
                                      public void Solve(AtmosWorldSolverContext context)
                                      {
                                      }
                                  }
                              }
                              """;

        var (ids, result, diagnostics) = Run(source);
        Assert.That(ids, Is.EqualTo(new[] { "NUMOSSOLVER010" }));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain("'MyMod.Outer'"));
        Assert.That(result.GeneratedSources, Is.Empty);
    }

    [TestCase("public abstract partial class Solver : IAtmosWorldSolver", "is abstract")]
    [TestCase("public sealed partial class Solver<T> : IAtmosWorldSolver", "is generic")]
    [TestCase("file sealed partial class Solver : IAtmosWorldSolver", "is file-local")]
    public void InvalidShape_ReportsNumosSolver011(string declaration, string reason)
    {
        var (ids, result, diagnostics) = Run(Solver(declaration: declaration));

        Assert.That(ids, Is.EqualTo(new[] { "NUMOSSOLVER011" }));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain(reason));
        Assert.That(result.GeneratedSources, Is.Empty);
    }

    [Test]
    public void StaticClass_ReportsNumosSolver011()
    {
        const string source = """
                              using Numos.API;

                              namespace MyMod;

                              [AtmosSolver("my-mod/fire", RunsPerSecond = 20)]
                              public static partial class Solver
                              {
                              }
                              """;

        var (ids, _, diagnostics) = Run(source);

        Assert.That(ids, Does.Contain("NUMOSSOLVER011"));
        Assert.That(diagnostics.Single(static d => d.Id == "NUMOSSOLVER011").GetMessage(), Does.Contain("is static"));
    }

    [TestCase("public partial class Outer<T>", "public", "is nested in generic type 'MyMod.Outer<T>'")]
    [TestCase("public partial class Outer", "private", "is private or protected")]
    [TestCase("public partial class Outer", "protected", "is private or protected")]
    public void NestingProblem_ReportsNumosSolver011(string outer, string accessibility, string reason)
    {
        string source = $$"""
                          using Numos.API;

                          namespace MyMod;

                          {{outer}}
                          {
                              [AtmosSolver("my-mod/fire", RunsPerSecond = 20)]
                              {{accessibility}} sealed partial class Solver : IAtmosWorldSolver
                              {
                                  public void Solve(AtmosWorldSolverContext context)
                                  {
                                  }
                              }
                          }
                          """;

        var (ids, result, diagnostics) = Run(source);

        Assert.That(ids, Is.EqualTo(new[] { "NUMOSSOLVER011" }));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain(reason));
        Assert.That(result.GeneratedSources, Is.Empty);
    }

    [Test]
    public void NotWorldSolver_ReportsNumosSolver012()
    {
        var (ids, result, _) = Run(Solver(declaration: "public sealed partial class Solver"));

        Assert.That(ids, Is.EqualTo(new[] { "NUMOSSOLVER012" }));
        Assert.That(result.GeneratedSources, Is.Empty);
    }

    [TestCase("private Solver() { }")]
    [TestCase("public Solver(int seed) { }")]
    [TestCase("protected Solver() { }")]
    public void NoCallableConstructor_ReportsNumosSolver013(string constructor)
    {
        var (ids, result, _) = Run(Solver(members: constructor));

        Assert.That(ids, Is.EqualTo(new[] { "NUMOSSOLVER013" }));
        Assert.That(result.GeneratedSources, Is.Empty);
    }

    [TestCase("internal Solver() { }")]
    [TestCase("public Solver() { }\n    public Solver(int seed) { }")]
    public void CallableConstructor_IsAccepted(string constructor)
    {
        var (ids, _, _) = Run(Solver(members: constructor));

        Assert.That(ids, Is.Empty);
    }

    [TestCase("\"\"")]
    [TestCase("\"  \"")]
    public void BlankName_ReportsNumosSolver014(string name)
    {
        var (ids, result, _) = Run(Solver(attribute: "[AtmosSolver(" + name + ", RunsPerSecond = 20)]"));

        Assert.That(ids, Is.EqualTo(new[] { "NUMOSSOLVER014" }));
        Assert.That(result.GeneratedSources, Is.Empty);
    }

    [Test]
    public void AfterAndBefore_ReportsNumosSolver015()
    {
        var (ids, _, _) = Run(
            Solver(attribute: "[AtmosSolver(\"my-mod/fire\", After = \"a\", Before = \"b\", RunsPerSecond = 20)]"));

        Assert.That(ids, Is.EqualTo(new[] { "NUMOSSOLVER015" }));
    }

    [TestCase("RunsPerSecond = 20, Interval = 4", "sets both RunsPerSecond and Interval")]
    [TestCase("RunsPerSecond = -1", "negative RunsPerSecond")]
    [TestCase("Interval = -3", "negative Interval")]
    public void InvalidRate_ReportsNumosSolver016(string arguments, string reason)
    {
        var (ids, result, diagnostics) = Run(Solver(attribute: "[AtmosSolver(\"my-mod/fire\", " + arguments + ")]"));

        Assert.That(ids, Is.EqualTo(new[] { "NUMOSSOLVER016" }));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain(reason));
        Assert.That(result.GeneratedSources, Is.Empty);
    }

    [TestCase("")]
    [TestCase(", Interval = 0")]
    public void NoRate_ReportsNumosSolver017WarningAndStillGenerates(string arguments)
    {
        var (ids, result, diagnostics) = Run(Solver(attribute: "[AtmosSolver(\"my-mod/fire\"" + arguments + ")]"));

        Assert.That(ids, Is.EqualTo(new[] { "NUMOSSOLVER017" }));
        Assert.That(diagnostics[0].Severity, Is.EqualTo(DiagnosticSeverity.Warning));
        Assert.That(GeneratedFor(result, "MyMod.Solver"), Is.Not.Empty);
    }

    [Test]
    public void DuplicateName_ReportsNumosSolver018OnBothAndRegistersNeither()
    {
        string first = Solver();
        string second = Solver(declaration: "public sealed partial class Other : IAtmosWorldSolver");

        var (ids, result, diagnostics) = Run(first, second);

        Assert.That(ids, Is.EqualTo(new[] { "NUMOSSOLVER018", "NUMOSSOLVER018" }));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain("'MyMod.Solver'").And.Contain("'MyMod.Other'"));
        Assert.That(
            result.GeneratedSources.Select(static source => source.HintName),
            Is.EquivalentTo(new[] { "MyMod.Solver.AtmosSolver.g.cs", "MyMod.Other.AtmosSolver.g.cs" }));
    }

    [TestCase("private bool Select(AtmosExplicitLinkInfo link) => true;", "must have the signature")]
    [TestCase("private static int Select(AtmosExplicitLinkInfo link) => 0;", "must have the signature")]
    [TestCase("private static bool Select(in AtmosExplicitLinkInfo link) => true;", "must have the signature")]
    [TestCase("private static bool Select(AtmosExplicitLinkInfo link, int extra) => true;", "must have the signature")]
    [TestCase("private static bool Select<T>(AtmosExplicitLinkInfo link) => true;", "must have the signature")]
    public void BadSelectorSignature_ReportsNumosSolver019(string method, string reason)
    {
        var (ids, result, diagnostics) = Run(
            Solver(members: "[SolverNeighborSelector(\"my-mod/fire#links\")]\n    " + method));

        Assert.That(ids, Is.EqualTo(new[] { "NUMOSSOLVER019" }));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain(reason));
        Assert.That(result.GeneratedSources, Is.Empty);
    }

    [Test]
    public void BlankSelectorKey_ReportsNumosSolver019()
    {
        var (ids, _, diagnostics) = Run(
            Solver(members: "[SolverNeighborSelector(\" \")] private static bool Select(AtmosExplicitLinkInfo link) => true;"));

        Assert.That(ids, Is.EqualTo(new[] { "NUMOSSOLVER019" }));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain("blank selection key"));
    }

    [Test]
    public void TwoSelectors_ReportsNumosSolver019OnTheSecond()
    {
        const string members = """
                               [SolverNeighborSelector("my-mod/fire#a")] private static bool A(AtmosExplicitLinkInfo link) => true;
                                   [SolverNeighborSelector("my-mod/fire#b")] private static bool B(AtmosExplicitLinkInfo link) => true;
                               """;

        var (ids, _, diagnostics) = Run(Solver(members: members));

        Assert.That(ids, Is.EqualTo(new[] { "NUMOSSOLVER019" }));
        Assert.That(diagnostics[0].GetMessage(), Does.StartWith("'MyMod.Solver.B'"));
    }

    [Test]
    public void SelectorAndNeighbors_ReportsNumosSolver019()
    {
        var (ids, _, diagnostics) = Run(
            Solver(
                attribute: "[AtmosSolver(\"my-mod/fire\", RunsPerSecond = 20, Neighbors = AtmosSolverNeighbors.All)]",
                members: "[SolverNeighborSelector(\"my-mod/fire#a\")] private static bool A(AtmosExplicitLinkInfo link) => true;"));

        Assert.That(ids, Is.EqualTo(new[] { "NUMOSSOLVER019" }));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain("also sets Neighbors"));
    }

    [Test]
    public void UndefinedNeighborsValue_ReportsNumosSolver019()
    {
        var (ids, _, _) = Run(
            Solver(attribute: "[AtmosSolver(\"my-mod/fire\", RunsPerSecond = 20, Neighbors = (AtmosSolverNeighbors)9)]"));

        Assert.That(ids, Is.EqualTo(new[] { "NUMOSSOLVER019" }));
    }

    [Test]
    public void SelectorAsLocalFunction_ReportsNumosSolver019()
    {
        const string members = """
                               private void Helper()
                                   {
                                       [SolverNeighborSelector("my-mod/fire#local")]
                                       static bool Local(AtmosExplicitLinkInfo link) => true;
                                       _ = Local(default);
                                   }
                               """;

        var (ids, _, diagnostics) = Run(Solver(members: members));

        Assert.That(ids, Is.EqualTo(new[] { "NUMOSSOLVER019" }));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain("local function"));
    }

    [TestCase("public static Numos.API.AtmosSolverDescriptor Descriptor => null!;")]
    [TestCase("private static int Descriptor;")]
    public void OwnDescriptor_ReportsNumosSolver020(string member)
    {
        var (ids, result, _) = Run(Solver(members: member));

        Assert.That(ids, Is.EqualTo(new[] { "NUMOSSOLVER020" }));
        Assert.That(result.GeneratedSources, Is.Empty);
    }

    [Test]
    public void HandWrittenDefinitionInterface_ReportsNumosSolver020()
    {
        var (ids, _, _) = Run(
            Solver(declaration: "public sealed partial class Solver : IAtmosWorldSolver, IAtmosSolverDefinition<Solver>"));

        Assert.That(ids, Is.EqualTo(new[] { "NUMOSSOLVER020" }));
    }

    [Test]
    public void SelectorOutsideSolverClass_ReportsNumosSolver021()
    {
        const string source = """
                              using Numos.API;

                              namespace MyMod;

                              public static class Selectors
                              {
                                  [SolverNeighborSelector("my-mod/stray")]
                                  public static bool Select(AtmosExplicitLinkInfo link) => true;
                              }
                              """;

        var (ids, _, diagnostics) = Run(source);

        Assert.That(ids, Is.EqualTo(new[] { "NUMOSSOLVER021" }));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain("'MyMod.Selectors.Select'"));
    }
}
