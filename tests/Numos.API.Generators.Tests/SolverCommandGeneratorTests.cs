using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Numos.API.Generators.Solver;
using Numos.API.Generators.SolverDependency;
using Numos.API.Generators.State;

namespace Numos.API.Generators.Tests;

[TestFixture]
public sealed class SolverCommandGeneratorTests
{
    private const string HintName = "MyMod.Solver.NumosCommands.g.cs";

    private static string Solver(string members, string declaration = "public sealed partial class Solver : IAtmosWorldSolver")
    {
        return $$"""
                 using System.Collections.Generic;
                 using Numos.API;
                 using Numos.CoreSim;
                 using Numos.Maths;

                 namespace MyMod;

                 {{declaration}}
                 {
                     {{members}}

                     public void Solve(AtmosWorldSolverContext context)
                     {
                     }
                 }
                 """;
    }

    private static GeneratorRunResult[] Run(
        out Compilation output, out ImmutableArray<Diagnostic> diagnostics, string? unitsCatalog, params string[] sources)
    {
        CSharpParseOptions parseOptions = GeneratorHarness.ParseOptions();
        CSharpCompilation compilation = GeneratorHarness.CreateCompilation(parseOptions, sources);
        ImmutableArray<AdditionalText> additionalTexts = unitsCatalog is null
            ? []
            : [new InMemoryAdditionalText("Test.magnitudo", unitsCatalog)];

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [
                new SolverCommandGenerator().AsSourceGenerator(),
                new StateGenerator().AsSourceGenerator(),
                new SolverDependencyGenerator().AsSourceGenerator(),
                new AtmosSolverGenerator().AsSourceGenerator()
            ],
            additionalTexts,
            parseOptions);

        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out output, out diagnostics);
        return driver.GetRunResult().Results.ToArray();
    }

    private static string[] CommandIds(params string[] sources)
    {
        Run(out _, out var diagnostics, null, sources);
        return GeneratorHarness.Ids(diagnostics.Where(static diagnostic => diagnostic.Id.StartsWith("NUMOSCOMMAND", StringComparison.Ordinal)));
    }

    private static string CommandSource(GeneratorRunResult[] results)
    {
        return GeneratorHarness.GeneratedSource(results[0], HintName);
    }

    [Test]
    public void EverySupportedParameterKind_CompilesAndRoundTrips()
    {
        const string source = """
                              using System.Collections.Generic;
                              using Numos.API;
                              using Numos.CoreSim;
                              using Numos.CoreSim.State;
                              using Numos.Maths;

                              namespace MyMod;

                              public enum Mode : short { Off, On }

                              public sealed partial class Settings
                              {
                                  [NumosSerialize] public float Rate { get; set; }
                              }

                              public sealed partial class Solver : IAtmosWorldSolver
                              {
                                  [SolverCommand]
                                  public partial void Everything(
                                      bool flag, byte b, short s, int i, long l, uint u, float f, double d, string text,
                                      string? maybe, int? count, Int3 position, Mode mode, GasProperties gas,
                                      AtmosCellRef cell, AtmosChunkHandle chunk, AtmosSimulationId simulation, Settings settings,
                                      int[] numbers, IReadOnlyList<string> names, IReadOnlyDictionary<string, float> weights,
                                      IReadOnlySet<int> set);

                                  [SolverCommand("Old")]
                                  internal partial void Renamed();

                                  public void Solve(AtmosWorldSolverContext context)
                                  {
                                  }

                                  private void ApplyEverything(
                                      bool flag, byte b, short s, int i, long l, uint u, float f, double d, string text,
                                      string? maybe, int? count, Int3 position, Mode mode, GasProperties gas,
                                      AtmosCellRef cell, AtmosChunkHandle chunk, AtmosSimulationId simulation, Settings settings,
                                      int[] numbers, IReadOnlyList<string> names, IReadOnlyDictionary<string, float> weights,
                                      IReadOnlySet<int> set)
                                  {
                                  }

                                  private void ApplyRenamed()
                                  {
                                  }
                              }
                              """;

        GeneratorRunResult[] results = Run(out Compilation output, out var diagnostics, null, source);
        string generated = CommandSource(results);
        Assert.Multiple(() =>
        {
            Assert.That(diagnostics, Is.Empty);
            Assert.That(GeneratorHarness.Errors(output), Is.Empty);
            Assert.That(generated, Does.Contain("internal partial void Renamed()"));
            Assert.That(generated, Does.Contain("__numosScope.Complete(" + FieldIds.Compute("Old") + ", __numosPayload);"));
            Assert.That(generated, Does.Contain("case " + FieldIds.Compute("Everything") + ":"));
            Assert.That(generated, Does.Contain("NumosState.RegisterFieldNames("));
        });
    }

    [Test]
    public void QuantityAliasParameter_ResolvesThroughTheUnitsCatalog()
    {
        // Without the Magnitudo generator the alias stays an error type, as it does inside Numos.CoreSim.
        string source = Solver(
            """
            [SolverCommand]
            public partial void Heat(Kelvin temperature);

            private void ApplyHeat(Kelvin temperature)
            {
            }
            """);

        const string catalog = """
                               temperature | Kelvin | global::System.Single | temperature=1
                               """;

        GeneratorRunResult[] results = Run(out _, out var diagnostics, catalog, source);
        string generated = CommandSource(results);
        Assert.Multiple(() =>
        {
            Assert.That(diagnostics.Where(static diagnostic => diagnostic.Id.StartsWith("NUMOS", StringComparison.Ordinal)), Is.Empty);
            Assert.That(generated, Does.Contain("public partial void Heat(Kelvin temperature)"));
            Assert.That(generated, Does.Contain("global::Numos.CoreSim.State.Advanced.StateCodecs.Single.Write(ref __numosSink, temperature);"));
            Assert.That(generated, Does.Contain("float field" + FieldIds.Compute("temperature") + " = default!;"));
        });
    }

    [Test]
    public void SolverWithEveryOtherGenerator_Compiles()
    {
        const string source = """
                              using Numos.API;
                              using Numos.CoreSim.State;

                              namespace MyMod;

                              public interface IEvents
                              {
                              }

                              [AtmosSolver("my-mod/solver", Interval = 2)]
                              public sealed partial class Solver : IAtmosWorldSolver
                              {
                                  [NumosSerialize] public int Count;

                                  [SolverDependency(Optional = true)]
                                  private partial IEvents? Events { get; }

                                  [SolverCommand]
                                  public partial void Bump(int amount);

                                  public void Solve(AtmosWorldSolverContext context)
                                  {
                                  }

                                  private void ApplyBump(int amount)
                                  {
                                      Count += amount;
                                  }
                              }
                              """;

        Run(out Compilation output, out var diagnostics, null, source);
        Assert.Multiple(() =>
        {
            Assert.That(diagnostics, Is.Empty);
            Assert.That(GeneratorHarness.Errors(output), Is.Empty);
        });
    }

    [Test]
    public void NonPartialClass_ReportsNumosCommand001AndGeneratesNothing()
    {
        GeneratorRunResult[] results = Run(
            out _,
            out var diagnostics,
            null,
            Solver(
                "[SolverCommand] public partial void Go(); private void ApplyGo() { }",
                "public sealed class Solver : IAtmosWorldSolver"));

        Assert.Multiple(() =>
        {
            Assert.That(GeneratorHarness.Ids(diagnostics.Where(static d => d.Id.StartsWith("NUMOSCOMMAND", StringComparison.Ordinal))),
                Is.EqualTo(new[] { "NUMOSCOMMAND001" }));
            Assert.That(results[0].GeneratedSources, Is.Empty);
        });
    }

    [Test]
    public void NonPartialContainingType_ReportsNumosCommand001()
    {
        const string source = """
                              using Numos.API;

                              namespace MyMod;

                              public class Outer
                              {
                                  public sealed partial class Solver : IAtmosWorldSolver
                                  {
                                      [SolverCommand] public partial void Go();
                                      private void ApplyGo() { }
                                      public void Solve(AtmosWorldSolverContext context) { }
                                  }
                              }
                              """;

        Run(out _, out var diagnostics, null, source);
        Diagnostic diagnostic = diagnostics.Single(static d => d.Id == "NUMOSCOMMAND001");
        Assert.That(diagnostic.GetMessage(), Does.Contain("'MyMod.Outer'").And.Contain("not partial"));
    }

    [Test]
    public void ClassThatIsNotASolver_ReportsNumosCommand001()
    {
        string[] ids = CommandIds(
            Solver("[SolverCommand] public partial void Go(); private void ApplyGo() { }", "public sealed partial class Solver"));

        Assert.That(ids, Is.EqualTo(new[] { "NUMOSCOMMAND001" }));
    }

    [Test]
    public void Struct_ReportsNumosCommand001()
    {
        const string source = """
                              using Numos.API;

                              namespace MyMod;

                              public partial struct Solver
                              {
                                  [SolverCommand] public partial void Go();
                                  private void ApplyGo() { }
                              }
                              """;

        Assert.That(CommandIds(source), Is.EqualTo(new[] { "NUMOSCOMMAND001" }));
    }

    [TestCase("[SolverCommand] public static partial void Go(); private static void ApplyGo() { }")]
    [TestCase("[SolverCommand] public partial int Go(); private void ApplyGo() { }")]
    [TestCase("[SolverCommand] public partial void Go<T>(); private void ApplyGo() { }")]
    [TestCase("[SolverCommand] public void Go() { } private void ApplyGo() { }")]
    [TestCase("[SolverCommand] public partial void Go(); public partial void Go() { } private void ApplyGo() { }")]
    public void MethodThatIsNotAPartialVoidDeclaration_ReportsNumosCommand002(string members)
    {
        Assert.That(CommandIds(Solver(members)), Does.Contain("NUMOSCOMMAND002"));
    }

    [TestCase("ref int value", "by ref")]
    [TestCase("out int value", "by ref")]
    [TestCase("in int value", "by ref")]
    [TestCase("params int[] value", "params")]
    [TestCase("System.IO.Stream value", "state schema")]
    [TestCase("System.Action value", "state schema")]
    public void UnsupportedParameter_ReportsNumosCommand003WithTheParameterName(string parameter, string reason)
    {
        string source = Solver($"[SolverCommand] public partial void Go({parameter}); private void ApplyGo({parameter}) {{ value = default!; }}");
        Run(out _, out var diagnostics, null, source);
        Diagnostic diagnostic = diagnostics.First(static d => d.Id == "NUMOSCOMMAND003");
        Assert.That(diagnostic.GetMessage(), Does.Contain("'value'").And.Contain(reason));
    }

    [TestCase("")]
    [TestCase("private void ApplyGo(float amount) { }")]
    [TestCase("private void ApplyGo(int amount, int extra) { }")]
    [TestCase("private static void ApplyGo(int amount) { }")]
    [TestCase("private void ApplyGo(ref int amount) { }")]
    public void MissingOrMismatchedApply_ReportsNumosCommand004(string apply)
    {
        string[] ids = CommandIds(Solver("[SolverCommand] public partial void Go(int amount); " + apply));
        Assert.That(ids, Is.EqualTo(new[] { "NUMOSCOMMAND004" }));
    }

    [TestCase("private void ApplyGo(AtmosWorld world, float amount) { }")]
    [TestCase("private void ApplyGo(int amount, AtmosWorld world) { }")]
    [TestCase("private void ApplyGo(in AtmosWorld world, int amount) { }")]
    [TestCase("private void ApplyGo(AtmosConfig world, int amount) { }")]
    public void ApplyWithAMisplacedOrWrongWorld_ReportsNumosCommand004(string apply)
    {
        string[] ids = CommandIds(Solver("[SolverCommand] public partial void Go(int amount); " + apply));
        Assert.That(ids, Is.EqualTo(new[] { "NUMOSCOMMAND004" }));
    }

    [Test]
    public void MissingApply_NamesBothAcceptedShapes()
    {
        Run(out _, out var diagnostics, null, Solver("[SolverCommand] public partial void Go(int amount, float rate);"));
        Diagnostic diagnostic = diagnostics.Single(static d => d.Id == "NUMOSCOMMAND004");
        Assert.That(
            diagnostic.GetMessage(),
            Does.Contain("'ApplyGo(int, float)'").And.Contain("'ApplyGo(AtmosWorld, int, float)'"));
    }

    [Test]
    public void ApplyWithoutTheWorld_GetsOnlyTheArguments()
    {
        string source = Solver("[SolverCommand] public partial void Go(int amount); private void ApplyGo(int amount) { }");
        GeneratorRunResult[] results = Run(out Compilation output, out var diagnostics, null, source);
        string generated = CommandSource(results);
        Assert.Multiple(() =>
        {
            Assert.That(diagnostics, Is.Empty);
            Assert.That(GeneratorHarness.Errors(output), Is.Empty);
            Assert.That(generated, Does.Contain("this.ApplyGo(amount);"));
            Assert.That(generated, Does.Contain("this.ApplyGo(field" + FieldIds.Compute("amount") + ");"));
            Assert.That(generated, Does.Not.Contain("__numosScope.World"));
        });
    }

    [Test]
    public void ApplyWithALeadingWorld_GetsTheScopeWorldLiveAndOnReplay()
    {
        string source = Solver(
            """
            [SolverCommand] public partial void Go(int amount);
            [SolverCommand] public partial void Stop();
            private void ApplyGo(AtmosWorld world, int amount) { }
            private void ApplyStop(AtmosWorld world) { }
            """);

        GeneratorRunResult[] results = Run(out Compilation output, out var diagnostics, null, source);
        string generated = CommandSource(results);
        Assert.Multiple(() =>
        {
            Assert.That(diagnostics, Is.Empty);
            Assert.That(GeneratorHarness.Errors(output), Is.Empty);
            Assert.That(generated, Does.Contain("this.ApplyGo(__numosScope.World, amount);"));
            Assert.That(generated, Does.Contain("this.ApplyGo(__numosScope.World, field" + FieldIds.Compute("amount") + ");"));
            Assert.That(generated, Does.Contain("this.ApplyStop(__numosScope.World);"));

            // The world is not an argument on the wire.
            Assert.That(generated, Does.Not.Contain("Write(ref __numosSink, __numosScope"));
        });
    }

    [Test]
    public void ApplyInBothShapes_PrefersTheOneWithTheWorld()
    {
        string source = Solver(
            """
            [SolverCommand] public partial void Go(int amount);
            private void ApplyGo(int amount) { }
            private void ApplyGo(AtmosWorld world, int amount) { }
            """);

        GeneratorRunResult[] results = Run(out Compilation output, out var diagnostics, null, source);
        Assert.Multiple(() =>
        {
            Assert.That(diagnostics, Is.Empty);
            Assert.That(GeneratorHarness.Errors(output), Is.Empty);
            Assert.That(CommandSource(results), Does.Contain("this.ApplyGo(__numosScope.World, amount);"));
        });
    }

    [Test]
    public void TwoCommandsWithOneWireName_ReportNumosCommand005AndGenerateNeither()
    {
        string source = Solver(
            """
            [SolverCommand] public partial void Go();
            [SolverCommand("Go")] public partial void Run();
            [SolverCommand] public partial void Stay();
            private void ApplyGo() { }
            private void ApplyRun() { }
            private void ApplyStay() { }
            """);

        GeneratorRunResult[] results = Run(out _, out var diagnostics, null, source);
        string generated = CommandSource(results);
        Assert.Multiple(() =>
        {
            Assert.That(GeneratorHarness.Ids(diagnostics.Where(static d => d.Id.StartsWith("NUMOSCOMMAND", StringComparison.Ordinal))),
                Is.EqualTo(new[] { "NUMOSCOMMAND005" }));
            Assert.That(generated, Does.Contain("partial void Stay()"));
            Assert.That(generated, Does.Not.Contain("partial void Go()"));
        });
    }

    [TestCase("\"\"")]
    [TestCase("\"  \"")]
    [TestCase("null!")]
    public void BlankWireName_ReportsNumosCommand006(string wireName)
    {
        string[] ids = CommandIds(Solver($"[SolverCommand({wireName})] public partial void Go(); private void ApplyGo() {{ }}"));
        Assert.That(ids, Is.EqualTo(new[] { "NUMOSCOMMAND006" }));
    }

    private const string BaseSolver = """
                                      using Numos.API;

                                      namespace MyMod;

                                      public partial class BurnerBase : IAtmosWorldSolver
                                      {
                                          [SolverCommand] public partial void Heat(int amount);

                                          public void Solve(AtmosWorldSolverContext context)
                                          {
                                          }

                                          private void ApplyHeat(int amount) { }
                                      }
                                      """;

    private const string DerivedSolver = """
                                         using Numos.API;

                                         namespace MyMod;

                                         public sealed partial class Burner : BurnerBase
                                         {
                                             [SolverCommand] public partial void Cool(int amount);

                                             private void ApplyCool(int amount) { }
                                         }
                                         """;

    [Test]
    public void ClassWithoutCommandsInItsBases_ImplementsTheInterfaceAndIntroducesTheHook()
    {
        GeneratorRunResult[] results = Run(out Compilation output, out var diagnostics, null, BaseSolver);
        string generated = GeneratorHarness.GeneratedSource(results[0], "MyMod.BurnerBase.NumosCommands.g.cs");
        Assert.Multiple(() =>
        {
            Assert.That(diagnostics, Is.Empty);
            Assert.That(GeneratorHarness.Errors(output), Is.Empty);
            Assert.That(generated, Does.Contain(": global::Numos.API.IAtmosSolverCommands"));
            Assert.That(generated, Does.Contain("protected virtual bool InvokeSolverCommand("));
            Assert.That(generated, Does.Contain("return false;"));
        });

        GeneratorRunResult[] sealedResults = Run(out _, out _, null, Solver("[SolverCommand] public partial void Go(); private void ApplyGo() { }"));
        Assert.That(CommandSource(sealedResults), Does.Contain("private bool InvokeSolverCommand("));
    }

    [Test]
    public void DerivedClassWithCommands_OverridesTheBaseDispatchAndFallsBackToIt()
    {
        GeneratorRunResult[] results = Run(out Compilation output, out var diagnostics, null, BaseSolver, DerivedSolver);
        string generated = GeneratorHarness.GeneratedSource(results[0], "MyMod.Burner.NumosCommands.g.cs");
        Assert.Multiple(() =>
        {
            Assert.That(diagnostics, Is.Empty);
            Assert.That(GeneratorHarness.Errors(output), Is.Empty);
            Assert.That(generated, Does.Not.Contain("IAtmosSolverCommands"));
            Assert.That(generated, Does.Contain("protected override bool InvokeSolverCommand("));
            Assert.That(generated, Does.Contain("case " + FieldIds.Compute("Cool") + ":"));
            Assert.That(generated, Does.Contain("return base.InvokeSolverCommand(command, payload, gases);"));
        });
    }

    [Test]
    public void DerivedFromAReferencedAssembly_OverridesItsGeneratedHook()
    {
        CSharpParseOptions parseOptions = GeneratorHarness.ParseOptions();
        Compilation library = GeneratorHarness.CreateCompilation(parseOptions, BaseSolver);
        CSharpGeneratorDriver.Create([new SolverCommandGenerator().AsSourceGenerator()], parseOptions: parseOptions)
            .RunGeneratorsAndUpdateCompilation(library, out library, out var libraryDiagnostics);
        Assert.That(libraryDiagnostics, Is.Empty);
        MetadataReference reference = RoslynTestSupport.EmitToReference(library);

        GeneratorRunResult RunDerived(string source, out Compilation output, out ImmutableArray<Diagnostic> diagnostics)
        {
            CSharpCompilation compilation = GeneratorHarness.CreateCompilation(parseOptions, [reference], source);
            return CSharpGeneratorDriver.Create([new SolverCommandGenerator().AsSourceGenerator()], parseOptions: parseOptions)
                .RunGeneratorsAndUpdateCompilation(compilation, out output, out diagnostics)
                .GetRunResult().Results[0];
        }

        GeneratorRunResult result = RunDerived(DerivedSolver, out Compilation output, out var diagnostics);
        Assert.Multiple(() =>
        {
            Assert.That(diagnostics, Is.Empty);
            Assert.That(GeneratorHarness.Errors(output), Is.Empty);
            Assert.That(
                GeneratorHarness.GeneratedSource(result, "MyMod.Burner.NumosCommands.g.cs"),
                Does.Contain("protected override bool InvokeSolverCommand("));
        });

        string clashing = DerivedSolver.Replace(
            "[SolverCommand] public partial void Cool(int amount);",
            "[SolverCommand] public partial void Cool(int amount); [SolverCommand(\"Heat\")] public partial void Warm(); private void ApplyWarm() { }");

        RunDerived(clashing, out _, out diagnostics);
        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSCOMMAND005" }));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain("'MyMod.BurnerBase.Heat'").And.Contain("'MyMod.Burner.Warm'"));
    }

    [Test]
    public void DerivedCommandWithABaseCommandId_ReportsNumosCommand005AndKeepsTheBaseCommand()
    {
        string derived = DerivedSolver.Replace(
            "[SolverCommand] public partial void Cool(int amount);",
            "[SolverCommand] public partial void Cool(int amount); [SolverCommand(\"Heat\")] public partial void Warm(); private void ApplyWarm() { }");

        GeneratorRunResult[] results = Run(out _, out var diagnostics, null, BaseSolver, derived);
        Diagnostic[] commands = diagnostics.Where(static d => d.Id.StartsWith("NUMOSCOMMAND", StringComparison.Ordinal)).ToArray();
        string generated = GeneratorHarness.GeneratedSource(results[0], "MyMod.Burner.NumosCommands.g.cs");
        string baseGenerated = GeneratorHarness.GeneratedSource(results[0], "MyMod.BurnerBase.NumosCommands.g.cs");
        Assert.Multiple(() =>
        {
            Assert.That(GeneratorHarness.Ids(commands), Is.EqualTo(new[] { "NUMOSCOMMAND005" }));
            Assert.That(commands[0].GetMessage(), Does.Contain("'MyMod.BurnerBase.Heat'").And.Contain("'MyMod.Burner.Warm'"));
            Assert.That(commands[0].Location.SourceTree!.GetText().ToString(commands[0].Location.SourceSpan), Is.EqualTo("Warm"));
            Assert.That(generated, Does.Contain("partial void Cool(int amount)"));
            Assert.That(generated, Does.Not.Contain("partial void Warm()"));
            Assert.That(baseGenerated, Does.Contain("partial void Heat(int amount)"));
        });
    }
}
