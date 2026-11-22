using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Numos.API.Generators.Authoring;
using Numos.Tests.Roslyn;

namespace Numos.API.Generators.Tests;

[TestFixture]
public sealed class SolverAuthoringAnalyzerTests
{
    private static string Setup(string body, string parameters = "")
    {
        return $$"""
                 using System;
                 using System.Collections.Generic;
                 using System.Diagnostics;
                 using Numos.API;

                 namespace MyMod;

                 public static class Setup
                 {
                     public static void Run(AtmosWorldSolverPipeline solvers{{parameters}})
                     {
                         {{body}}
                     }

                     private static void Touch(ref int value)
                     {
                     }

                     private static void Produce(out int value)
                     {
                         value = 1;
                     }

                     private static void Read(in int value)
                     {
                     }
                 }
                 """;
    }

    private static string ClassSolver(string members, string declaration = "public sealed class Solver : IAtmosWorldSolver")
    {
        return $$"""
                 using System;
                 using System.Diagnostics;
                 using Numos.API;

                 namespace MyMod;

                 {{declaration}}
                 {
                     {{members}}
                 }
                 """;
    }

    private static ImmutableArray<Diagnostic> Run(params string[] sources)
    {
        return AnalyzerHarness.RunAuthoring(sources);
    }

    private static string[] Ids(IEnumerable<Diagnostic> diagnostics)
    {
        return GeneratorHarness.Ids(diagnostics);
    }

    // NUMOSSOLVER001

    [TestCase("count = 1;", "count")]
    [TestCase("count += 2;", "count")]
    [TestCase("maybe ??= 3;", "maybe")]
    [TestCase("count++;", "count")]
    [TestCase("--count;", "count")]
    [TestCase("Touch(ref count);", "count")]
    [TestCase("Produce(out count);", "count")]
    [TestCase("(count, _) = (1, 2);", "count")]
    public void CapturedLocalWrite_ReportsNumosSolver001AtTheVariable(string statement, string variable)
    {
        string source = Setup($$"""
                                int count = 0;
                                int? maybe = null;
                                solvers.Register("my-mod/count", context =>
                                {
                                    {{statement}}
                                });
                                """);

        ImmutableArray<Diagnostic> diagnostics = Run(source);

        Assert.That(Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSOLVER001" }));
        Assert.That(AnalyzerHarness.Spans(diagnostics), Is.EqualTo(new[] { variable }));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain($"'{variable}'"));
    }

    [Test]
    public void DeconstructionIntoTwoCapturedLocals_ReportsEach()
    {
        string source = Setup("""
                              int first = 0;
                              int second = 0;
                              solvers.Register("my-mod/pair", context => (first, second) = (1, 2));
                              """);

        ImmutableArray<Diagnostic> diagnostics = Run(source);

        Assert.That(Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSOLVER001", "NUMOSSOLVER001" }));
        Assert.That(AnalyzerHarness.Spans(diagnostics), Is.EqualTo(new[] { "first", "second" }));
    }

    [Test]
    public void CapturedParameterWrite_ReportsNumosSolver001()
    {
        string source = Setup(
            """solvers.Register("my-mod/ticks", context => ticks++);""",
            ", int ticks");

        ImmutableArray<Diagnostic> diagnostics = Run(source);

        Assert.That(Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSOLVER001" }));
        Assert.That(AnalyzerHarness.Spans(diagnostics), Is.EqualTo(new[] { "ticks" }));
    }

    [Test]
    public void WriteInsideLambdaNestedInSolverLambda_ReportsNumosSolver001()
    {
        string source = Setup("""
                              int total = 0;
                              solvers.Register("my-mod/nested", context =>
                              {
                                  Action add = () => total += 1;
                                  add();
                              });
                              """);

        Assert.That(Ids(Run(source)), Is.EqualTo(new[] { "NUMOSSOLVER001" }));
    }

    [Test]
    public void AnonymousMethodWrite_ReportsNumosSolver001()
    {
        string source = Setup("""
                              int count = 0;
                              solvers.Register("my-mod/count", delegate (AtmosWorldSolverContext context) { count++; });
                              """);

        Assert.That(Ids(Run(source)), Is.EqualTo(new[] { "NUMOSSOLVER001" }));
    }

    [Test]
    public void CapturedReadsAndCallsOnCapturedObjects_ReportNothing()
    {
        string source = Setup("""
                              int limit = 4;
                              var seen = new List<long>();
                              solvers.Register("my-mod/observe", context =>
                              {
                                  if (limit > 0)
                                      seen.Add(context.TickCount);
                                  Read(in limit);
                              });
                              """);

        Assert.That(Run(source), Is.Empty);
    }

    [Test]
    public void LambdaOwnLocalsAndParameters_ReportNothing()
    {
        string source = Setup("""
                              solvers.Register("my-mod/locals", context =>
                              {
                                  int local = 0;
                                  local++;
                                  Action bump = () => local += 1;
                                  bump();
                                  context = null!;
                              });
                              """);

        Assert.That(Run(source), Is.Empty);
    }

    [Test]
    public void CapturedWriteOutsideRegisterLambda_ReportsNothing()
    {
        string source = Setup("""
                              int count = 0;
                              AtmosWorldSolver solver = context => count++;
                              solvers.Register("my-mod/held", solver);
                              Action other = () => count++;
                              other();
                              """);

        Assert.That(Run(source), Is.Empty);
    }

    // NUMOSSOLVER002

    [TestCase("")]
    [TestCase(", neighbors: null")]
    [TestCase(", null")]
    public void TopologyWithoutNeighbors_ReportsNumosSolver002AtTheAccess(string arguments)
    {
        string source = Setup($$"""
                                solvers.Register("my-mod/topology", context =>
                                {
                                    _ = context.Topology;
                                }{{arguments}});
                                """);

        ImmutableArray<Diagnostic> diagnostics = Run(source);

        Assert.That(Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSOLVER002" }));
        Assert.That(AnalyzerHarness.Spans(diagnostics), Is.EqualTo(new[] { "context.Topology" }));
    }

    [Test]
    public void TopologyWithNeighbors_ReportsNothing()
    {
        string source = Setup("""
                              var selection = new AtmosNeighborSelection("my-mod/v1", true, static _ => true);
                              solvers.Register("my-mod/topology", context =>
                              {
                                  _ = context.Topology;
                              }, neighbors: selection);
                              solvers.Register("my-mod/topology-2", context => _ = context.Topology, selection);
                              """);

        Assert.That(Run(source), Is.Empty);
    }

    [TestCase("[AtmosSolver(\"my-mod/fire\", RunsPerSecond = 20)]")]
    [TestCase("[AtmosSolver(\"my-mod/fire\", RunsPerSecond = 20, Neighbors = AtmosSolverNeighbors.None)]")]
    public void AtmosSolverClassWithoutNeighbors_ReportsNumosSolver002AtTheAccess(string attribute)
    {
        string source = ClassSolver(
            """
            public void Solve(AtmosWorldSolverContext context)
                {
                    _ = context.Topology;
                    Action nested = () => _ = context.Topology;
                }
            """,
            attribute + "\npublic sealed partial class Solver : IAtmosWorldSolver");

        ImmutableArray<Diagnostic> diagnostics = Run(source);

        Assert.That(Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSOLVER002", "NUMOSSOLVER002" }));
        Assert.That(AnalyzerHarness.Spans(diagnostics), Is.EqualTo(new[] { "context.Topology", "context.Topology" }));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain("class 'Solver'"));
    }

    [TestCase("[AtmosSolver(\"my-mod/fire\", RunsPerSecond = 20, Neighbors = AtmosSolverNeighbors.Cartesian)]", "")]
    [TestCase(
        "[AtmosSolver(\"my-mod/fire\", RunsPerSecond = 20)]",
        "[SolverNeighborSelector(\"my-mod/fire#links\")] private static bool Select(AtmosExplicitLinkInfo link) => true;")]
    [TestCase("", "")]
    public void TopologyReadWithNeighborsOrWithoutAtmosSolver_ReportsNothing(string attribute, string selector)
    {
        string source = ClassSolver(
            selector +
            """

                public void Solve(AtmosWorldSolverContext context)
                {
                    _ = context.Topology;
                }
            """,
            attribute + "\npublic sealed partial class Solver : IAtmosWorldSolver");

        Assert.That(Run(source), Is.Empty);
    }

    [Test]
    public void TopologyReadInNestedType_IsNotChargedToTheSolverClass()
    {
        string source = ClassSolver(
            """
            public void Solve(AtmosWorldSolverContext context)
                {
                }

                private static class Helper
                {
                    public static object Read(AtmosWorldSolverContext context) => context.Topology;
                }
            """,
            "[AtmosSolver(\"my-mod/fire\", RunsPerSecond = 20)]\npublic sealed partial class Solver : IAtmosWorldSolver");

        Assert.That(Run(source), Is.Empty);
    }

    // NUMOSSOLVER003

    [TestCase("_ = new Random();", "new Random(...)")]
    [TestCase("_ = new Random(42);", "new Random(...)")]
    [TestCase("_ = Random.Shared;", "Random.Shared")]
    [TestCase("_ = Guid.NewGuid();", "Guid.NewGuid")]
    [TestCase("_ = DateTime.Now;", "DateTime.Now")]
    [TestCase("_ = DateTime.UtcNow;", "DateTime.UtcNow")]
    [TestCase("_ = DateTime.Today;", "DateTime.Today")]
    [TestCase("_ = DateTimeOffset.Now;", "DateTimeOffset.Now")]
    [TestCase("_ = DateTimeOffset.UtcNow;", "DateTimeOffset.UtcNow")]
    [TestCase("_ = Environment.TickCount;", "Environment.TickCount")]
    [TestCase("_ = Environment.TickCount64;", "Environment.TickCount64")]
    [TestCase("_ = HashCode.Combine(1, 2);", "HashCode.Combine")]
    [TestCase("_ = \"key\".GetHashCode();", "string.GetHashCode")]
    public void NondeterministicApiInClassSolver_ReportsNumosSolver003(string statement, string display)
    {
        string source = ClassSolver($$"""
                                      public void Solve(AtmosWorldSolverContext context)
                                      {
                                          {{statement}}
                                      }
                                      """);

        ImmutableArray<Diagnostic> diagnostics = Run(source);

        Assert.That(Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSOLVER003" }));
        Assert.That(diagnostics[0].GetMessage(), Does.StartWith($"'{display}'"));
    }

    [Test]
    public void HashCodeInstance_ReportsEachMember()
    {
        string source = ClassSolver("""
                                    public void Solve(AtmosWorldSolverContext context)
                                    {
                                        var hash = new HashCode();
                                        hash.Add(1);
                                        _ = hash.ToHashCode();
                                    }
                                    """);

        Assert.That(Ids(Run(source)), Is.EqualTo(new[] { "NUMOSSOLVER003", "NUMOSSOLVER003", "NUMOSSOLVER003" }));
    }

    [Test]
    public void NondeterministicApiInSolverMembers_ReportsInPropertiesHelpersAndLambdas()
    {
        string source = ClassSolver("""
                                    private long Stamp => DateTime.UtcNow.Ticks;

                                    public void Solve(AtmosWorldSolverContext context)
                                    {
                                        Func<int> roll = () => Random.Shared.Next();
                                        _ = roll();
                                    }

                                    private static Guid Helper() => Guid.NewGuid();
                                    """);

        Assert.That(
            AnalyzerHarness.Spans(Run(source)),
            Is.EqualTo(new[] { "DateTime.UtcNow", "Random.Shared", "Guid.NewGuid()" }));
    }

    [Test]
    public void NondeterministicApiInRegisterLambda_ReportsNumosSolver003()
    {
        string source = Setup("""
                              solvers.Register("my-mod/clock", context =>
                              {
                                  _ = DateTime.Now;
                                  Action nested = () => _ = Environment.TickCount64;
                                  nested();
                              });
                              """);

        Assert.That(
            AnalyzerHarness.Spans(Run(source)),
            Is.EqualTo(new[] { "DateTime.Now", "Environment.TickCount64" }));
    }

    [Test]
    public void Stopwatch_ReportsNothing()
    {
        string solver = ClassSolver("""
                                    public void Solve(AtmosWorldSolverContext context)
                                    {
                                        var watch = Stopwatch.StartNew();
                                        _ = watch.Elapsed;
                                        _ = Stopwatch.GetTimestamp();
                                    }
                                    """);
        string lambda = Setup("""solvers.Register("my-mod/timed", context => _ = Stopwatch.GetTimestamp());""");

        Assert.That(Run(solver, lambda), Is.Empty);
    }

    [Test]
    public void ContextRandom_ReportsNothing()
    {
        string solver = ClassSolver("""
                                    public void Solve(AtmosWorldSolverContext context)
                                    {
                                        AtmosRandom random = context.Random(stream: 3);
                                        _ = random.NextSingle();
                                    }
                                    """);

        Assert.That(Run(solver), Is.Empty);
    }

    [Test]
    public void NondeterministicApiOutsideSolverCode_ReportsNothing()
    {
        string host = Setup("""
                            _ = DateTime.Now;
                            solvers.Register("my-mod/helper", context => Helper.Roll());
                            AtmosWorldSolver held = context => _ = Guid.NewGuid();
                            solvers.Register("my-mod/held", held);
                            """);
        string helper = """
                        namespace MyMod;

                        public static class Helper
                        {
                            public static int Roll() => System.Random.Shared.Next();
                        }
                        """;

        Assert.That(Run(host, helper), Is.Empty);
    }

    [Test]
    public void NoPipelineInCompilation_ReportsNothing()
    {
        const string source = """
                              namespace Numos.API
                              {
                                  public interface IAtmosWorldSolver
                                  {
                                  }
                              }

                              namespace MyMod
                              {
                                  public sealed class Solver : Numos.API.IAtmosWorldSolver
                                  {
                                      public System.DateTime Now() => System.DateTime.Now;
                                  }
                              }
                              """;

        CSharpCompilation compilation = CSharpCompilation.Create(
            "NoPipeline",
            [CSharpSyntaxTree.ParseText(source, GeneratorHarness.ParseOptions())],
            RoslynTestSupport.FrameworkReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        ImmutableArray<Diagnostic> diagnostics = compilation
            .WithAnalyzers([new SolverAuthoringAnalyzer()])
            .GetAnalyzerDiagnosticsAsync().GetAwaiter().GetResult();

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public void GeneratedCode_ReportsNothing()
    {
        string source = "// <auto-generated/>\n" + ClassSolver("""
                                                                public void Solve(AtmosWorldSolverContext context)
                                                                {
                                                                    _ = DateTime.Now;
                                                                }
                                                                """);

        Assert.That(Run(source), Is.Empty);
    }

    // NUMOSSOLVER004

    [TestCase("AtmosWorldSolverContext", "AtmosWorldSolverContext")]
    [TestCase("AtmosSolverChunk", "AtmosSolverChunk")]
    [TestCase("AtmosSolverChunks", "AtmosSolverChunks")]
    [TestCase("AtmosSolverGasMixtures", "AtmosSolverGasMixtures")]
    [TestCase("AtmosSolverGasMixturePage", "AtmosSolverGasMixturePage")]
    [TestCase("AtmosSolverChunk?", "AtmosSolverChunk")]
    public void ViewField_ReportsNumosSolver004AtTheField(string type, string display)
    {
        string source = ClassSolver($$"""
                                      private {{type}} _view;

                                      public void Solve(AtmosWorldSolverContext context)
                                      {
                                      }
                                      """,
            "public sealed class Holder");

        ImmutableArray<Diagnostic> diagnostics = Run(source);

        Assert.That(Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSOLVER004" }));
        Assert.That(AnalyzerHarness.Spans(diagnostics), Is.EqualTo(new[] { "_view" }));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain($"stores a {display},"));
    }

    [Test]
    public void ViewAutoProperty_ReportsNumosSolver004AtTheProperty()
    {
        string source = ClassSolver("""
                                    public AtmosWorldSolverContext? Last { get; set; }
                                    public static AtmosSolverChunks Shared { get; } = default;
                                    """,
            "public sealed class Holder");

        ImmutableArray<Diagnostic> diagnostics = Run(source);

        Assert.That(Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSOLVER004", "NUMOSSOLVER004" }));
        Assert.That(AnalyzerHarness.Spans(diagnostics), Is.EqualTo(new[] { "Last", "Shared" }));
    }

    [Test]
    public void ViewInLocalParameterComputedPropertyOrRefStruct_ReportsNothing()
    {
        string solver = ClassSolver("""
                                    private readonly AtmosSolverChunks[] _unrelated = [];

                                    public AtmosSolverChunk First => default;

                                    public void Solve(AtmosWorldSolverContext context)
                                    {
                                        AtmosWorldSolverContext local = context;
                                        foreach (var simulation in local.Simulations)
                                        {
                                            AtmosSolverChunks chunks = local.Chunks(simulation);
                                            _ = chunks.Count;
                                        }
                                    }
                                    """);
        string refStruct = ClassSolver("""
                                       private readonly AtmosWorldSolverContext _context;

                                       public Scope(AtmosWorldSolverContext context) => _context = context;
                                       """,
            "public readonly ref struct Scope");

        Assert.That(Run(solver, refStruct.Replace("namespace MyMod;", "namespace MyMod.Scopes;")), Is.Empty);
    }

    // NUMOSSOLVER005

    private static string DangerousHost(string body, string declaration = "public static class Host")
    {
        return $$"""
                 using Numos.API;
                 using Numos.API.Dangerous;

                 namespace MyMod;

                 {{declaration}}
                 {
                     {{body}}
                 }
                 """;
    }

    [Test]
    public void DangerousViewFromHostCode_ReportsNumosSolver005AtTheCall()
    {
        string source = DangerousHost("""
                                      public static void Seed(AtmosSimulation simulation, AtmosChunkHandle chunk, ChunkField<float> field)
                                      {
                                          simulation.Dangerous().GetChunkField(chunk, field)[0] = 1f;
                                          AtmosDangerousApi api = simulation.Dangerous();
                                          _ = api.GetChunk(chunk);
                                      }
                                      """);

        ImmutableArray<Diagnostic> diagnostics = Run(source);

        Assert.That(Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSOLVER005", "NUMOSSOLVER005" }));
        Assert.That(AnalyzerHarness.Spans(diagnostics), Is.EqualTo(new[] { "GetChunkField", "GetChunk" }));
        Assert.That(diagnostics.Select(static diagnostic => diagnostic.Severity), Has.All.EqualTo(DiagnosticSeverity.Warning));
        Assert.That(
            diagnostics[0].GetMessage(),
            Does.Contain("AtmosDangerousApi.GetChunkField")
                .And.Contain("bypass recording")
                .And.Contain("SetChunkData")
                .And.Contain("WriteChunkData")
                .And.Contain("[SolverCommand]"));
    }

    [Test]
    public void DangerousViewInSolverCode_ReportsNothing()
    {
        string host = DangerousHost("""
                                    public static void Run(AtmosWorldSolverPipeline solvers, AtmosSimulation simulation, AtmosChunkHandle chunk)
                                    {
                                        solvers.Register("my-mod/seed", context => _ = simulation.Dangerous().GetChunk(chunk));
                                    }
                                    """);
        string solver = DangerousHost("""
                                      private readonly AtmosSimulation _simulation;
                                      private readonly AtmosChunkHandle _chunk;

                                      public Solver(AtmosSimulation simulation, AtmosChunkHandle chunk)
                                      {
                                          _simulation = simulation;
                                          _chunk = chunk;
                                      }

                                      public void Solve(AtmosWorldSolverContext context)
                                      {
                                          _ = _simulation.Dangerous().GetChunk(_chunk);
                                      }
                                      """,
            "public sealed class Solver : IAtmosWorldSolver");

        Assert.That(Run(host, solver.Replace("namespace MyMod;", "namespace MyMod.Solvers;")), Is.Empty);
    }

    [Test]
    public void DangerousViewInCommandApply_ReportsNothingButOtherMethodsReport()
    {
        // The class isn't a solver, so only the Apply{Name} rule keeps ApplyIgnite quiet.
        string source = DangerousHost("""
                                      private AtmosSimulation _simulation = null!;
                                      private AtmosChunkHandle _chunk;

                                      [SolverCommand]
                                      partial void Ignite(int strength);

                                      private void ApplyIgnite(int strength)
                                      {
                                          _ = _simulation.Dangerous().GetChunk(_chunk);
                                      }

                                      private void ApplyQuench(int strength)
                                      {
                                          _ = _simulation.Dangerous().GetChunk(_chunk);
                                      }
                                      """,
            "public sealed partial class Commands");

        ImmutableArray<Diagnostic> diagnostics = Run(source);

        Assert.That(Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSOLVER005" }));
        Assert.That(
            diagnostics[0].Location.SourceSpan.Start,
            Is.GreaterThan(source.IndexOf("ApplyQuench", StringComparison.Ordinal)));
    }

    [Test]
    public void NoDangerousReference_StillRunsTheOtherRules()
    {
        string source = ClassSolver("""
                                    public void Solve(AtmosWorldSolverContext context)
                                    {
                                        _ = DateTime.Now;
                                    }
                                    """);

        ImmutableArray<Diagnostic> diagnostics = AnalyzerHarness.RunAuthoringWithout(["Numos.API.Dangerous"], source);

        Assert.That(Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSOLVER003" }));
    }
}
