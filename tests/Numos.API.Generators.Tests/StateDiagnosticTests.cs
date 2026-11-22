using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Numos.API.Generators.State;

namespace Numos.API.Generators.Tests;

[TestFixture]
public sealed class StateDiagnosticTests
{
    private static string State(string members, string declaration = "public sealed partial class Settings")
    {
        return $$"""
                 using Numos.CoreSim;
                 using Numos.CoreSim.State;
                 using Numos.CoreSim.State.Advanced;

                 namespace MyMod;

                 {{declaration}}
                 {
                     {{members}}
                 }
                 """;
    }

    private static Diagnostic[] Run(out GeneratorRunResult result, params string[] sources)
    {
        result = GeneratorHarness.RunState(out _, out var diagnostics, null, sources);
        return diagnostics.ToArray();
    }

    private static string[] Ids(params string[] sources)
    {
        return GeneratorHarness.Ids(Run(out _, sources));
    }

    [Test]
    public void TypeWithoutMarkedMembers_IsLeftAlone()
    {
        Diagnostic[] diagnostics = Run(out GeneratorRunResult result, State("public float Rate;", "public sealed class Settings"));
        Assert.That(diagnostics, Is.Empty);
        Assert.That(result.GeneratedSources, Is.Empty);
    }

    [Test]
    public void NonPartialType_ReportsNumosState001AndGeneratesNothing()
    {
        Diagnostic[] diagnostics = Run(
            out GeneratorRunResult result,
            State("[NumosSerialize] public float Rate { get; init; }", "public sealed class Settings"));

        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSTATE001" }));
        Assert.That(result.GeneratedSources, Is.Empty);
    }

    [Test]
    public void NonPartialContainingType_ReportsNumosState001()
    {
        const string source = """
                              using Numos.CoreSim.State;

                              namespace MyMod;

                              public class Outer
                              {
                                  public partial struct Inner
                                  {
                                      [NumosSerialize] public int Value;
                                  }
                              }
                              """;

        Diagnostic[] diagnostics = Run(out _, source);
        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSTATE001" }));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain("'MyMod.Outer'"));
    }

    [Test]
    public void GenericType_ReportsNumosState002()
    {
        Assert.That(
            Ids(State("[NumosSerialize] public T Value { get; init; } = default!;", "public sealed partial class Settings<T>")),
            Is.EqualTo(new[] { "NUMOSSTATE002" }));
    }

    [Test]
    public void UnmarkedMutableMembers_ReportNumosState016ButStillGenerate()
    {
        Diagnostic[] diagnostics = Run(
            out GeneratorRunResult result,
            State(
                """
                private float _field;
                public float Init { get; init; }
                public float Set { get; set; }
                private readonly float _cache;
                private float GetOnly { get; }
                public float Computed => _field + Init + Set + GetOnly + _cache;
                [NumosSerialize] public float Marked { get; init; }
                """));

        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSTATE016", "NUMOSSTATE016", "NUMOSSTATE016" }));
        Assert.That(diagnostics.Select(static diagnostic => diagnostic.Severity), Has.All.EqualTo(DiagnosticSeverity.Warning));
        Assert.That(
            diagnostics.Select(static diagnostic => diagnostic.GetMessage()),
            Has.Some.Contains("'MyMod.Settings._field'")
                .And.Some.Contains("'MyMod.Settings.Init'")
                .And.Some.Contains("'MyMod.Settings.Set'"));

        Assert.That(result.GeneratedSources, Has.Length.EqualTo(1));
    }

    [Test]
    public void NumosIgnore_SilencesNumosState016()
    {
        Assert.That(
            Ids(
                State(
                    """
                    [NumosIgnore] private int _lookups;
                    [NumosIgnore] public float Scratch { get; set; }
                    [NumosSerialize] public float Rate { get; init; }
                    """)),
            Is.Empty);
    }

    [Test]
    public void UnmarkedPositionalRecordProperty_ReportsNumosState016()
    {
        // The constructor can't be bound without it either.
        const string source = """
                              using Numos.CoreSim.State;

                              namespace MyMod;

                              public sealed partial record Settings([property: NumosSerialize] float Rate, float Spread);
                              """;

        Diagnostic[] diagnostics = Run(out _, source);
        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSTATE016", "NUMOSSTATE014" }));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain("'MyMod.Settings.Spread'"));
    }

    [Test]
    public void IdCollision_ReportsNumosState005NamingBothMembers()
    {
        // Rate and the wire name "Rate" are the same id.
        Diagnostic[] diagnostics = Run(
            out _,
            State(
                """
                [NumosSerialize] public float Rate { get; init; }
                [NumosSerialize("Rate")] public float Spread { get; init; }
                """));

        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSTATE005" }));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain("'MyMod.Settings.Spread'").And.Contain("'MyMod.Settings.Rate'"));
    }

    [TestCase("\"\"")]
    [TestCase("\" \"")]
    public void BlankWireName_ReportsNumosState017(string name)
    {
        Assert.That(Ids(State($"[NumosSerialize({name})] public float Rate {{ get; init; }}")), Is.EqualTo(new[] { "NUMOSSTATE017" }));
    }

    [TestCase("object")]
    [TestCase("System.Func<int>")]
    [TestCase("System.Collections.Generic.List<int>")]
    [TestCase("System.Collections.Generic.HashSet<int>")]
    [TestCase("System.Collections.Generic.IReadOnlyList<object>")]
    [TestCase("System.Collections.Generic.Dictionary<float[], int>")]
    [TestCase("int[,]")]
    public void UnsupportedType_ReportsNumosState007(string type)
    {
        Assert.That(Ids(State($"[NumosSerialize] public {type} Value {{ get; init; }} = default!;")), Is.EqualTo(new[] { "NUMOSSTATE007" }));
    }

    [Test]
    public void UnknownAlias_ReportsNumosState007()
    {
        GeneratorHarness.RunState(
            out _,
            out var diagnostics,
            "temperature | Kelvin | global::System.Single | temperature=1",
            State("[NumosSerialize] public Furlong Distance { get; init; }"));

        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSTATE007" }));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain("'Furlong'"));
    }

    [Test]
    public void CodecHook_ReplacesTheTypesOwnCodec()
    {
        // object has no codec of its own, so this only builds because the hook supplies one.
        string source = State(
            """
            [NumosSerialize(Codec = typeof(Boxed))] public object Value { get; set; } = 0;

            public readonly struct Boxed : IStateCodec<object>
            {
                public static void Write<TSink>(ref TSink sink, object value) where TSink : struct, IStateSink => sink.WriteInt32((int)value);
                public static object Read(ref StateReader reader, IGasRegistry? gases) => reader.ReadInt32();
                public static bool StateEquals(object left, object right) => (int)left == (int)right;
                public static int Compare(object left, object right) => ((int)left).CompareTo((int)right);
            }
            """);

        GeneratorRunResult result = GeneratorHarness.RunState(out Compilation output, out var diagnostics, null, source);
        Assert.That(diagnostics, Is.Empty);
        Assert.That(GeneratorHarness.Errors(output), Is.Empty);
        Assert.That(GeneratorHarness.GeneratedSource(result, "MyMod.Settings.NumosState.g.cs"), Does.Contain("global::MyMod.Settings.Boxed.Write(ref sink, value.Value);"));
    }

    [Test]
    public void HandWrittenSolverConfigurationMembers_ReportNumosState008()
    {
        string source = State(
            """
            public string Key => "my-mod/fire";
            [NumosSerialize] public float Rate { get; init; }
            public bool SemanticallyEquals(IAtmosSolverConfiguration other) => false;
            ulong IAtmosSolverConfiguration.ComputeStateHash() => 0;
            """,
            "public sealed partial class Settings : IAtmosSolverConfiguration");

        Diagnostic[] diagnostics = Run(out GeneratorRunResult result, source);
        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSTATE008", "NUMOSSTATE008" }));
        Assert.That(
            diagnostics.Select(static diagnostic => diagnostic.GetMessage()),
            Has.Some.Contains("'SemanticallyEquals'").And.Some.Contains("'ComputeStateHash'"));

        Assert.That(result.GeneratedSources, Is.Empty);
    }

    [Test]
    public void SerializedMemberOnWorldSolverClass_GeneratesWithoutConstructorDiagnostics()
    {
        const string source = """
                              using Numos.API;
                              using Numos.CoreSim.State;

                              namespace MyMod;

                              public sealed partial class FireSolver : IAtmosWorldSolver
                              {
                                  private readonly int _cache;

                                  public FireSolver(int cache) => _cache = cache;

                                  [NumosSerialize] public float IgnitionTemperature { get; set; }
                                  [NumosSerialize] public float SpreadRate;
                                  [NumosSerialize] private int _ticks;

                                  public void Solve(AtmosWorldSolverContext context) => _ticks += _cache;
                              }
                              """;

        Diagnostic[] diagnostics = Run(out GeneratorRunResult result, source);
        Assert.That(diagnostics, Is.Empty);
        Assert.That(result.GeneratedSources, Has.Length.EqualTo(1));
    }

    [Test]
    public void SerializedMemberOnStructSolver_ReportsNumosState009Once()
    {
        const string source = """
                              using Numos.API;
                              using Numos.CoreSim.State;

                              namespace MyMod;

                              public partial struct FireSolver : IAtmosWorldSolver
                              {
                                  private int _scratch;

                                  [NumosSerialize] public float IgnitionTemperature { get; set; }

                                  public void Solve(AtmosWorldSolverContext context) => _scratch++;
                              }
                              """;

        Diagnostic[] diagnostics = Run(out GeneratorRunResult result, source);
        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSTATE009" }));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain("must be a class"));
        Assert.That(result.GeneratedSources, Is.Empty);
    }

    [Test]
    public void UnassignableSolverMembers_ReportNumosState020Each()
    {
        const string source = """
                              using Numos.API;
                              using Numos.CoreSim.State;

                              namespace MyMod;

                              public sealed partial class FireSolver : IAtmosWorldSolver
                              {
                                  [NumosSerialize] private readonly int _limit = 3;
                                  [NumosSerialize] public float Rate { get; } = 1f;
                                  [NumosSerialize] public float Spread { get; init; }
                                  [NumosSerialize] public float Fine { get; private set; }

                                  public void Solve(AtmosWorldSolverContext context)
                                  {
                                  }
                              }
                              """;

        Diagnostic[] diagnostics = Run(out GeneratorRunResult result, source);
        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSTATE020", "NUMOSSTATE020", "NUMOSSTATE020" }));
        Assert.That(
            diagnostics.Select(static diagnostic => diagnostic.GetMessage()),
            Has.Some.Contains("_limit").And.Some.Contains("Rate").And.Some.Contains("Spread"));

        Assert.That(result.GeneratedSources, Is.Empty);
    }

    [TestCase("private readonly System.Collections.Generic.List<int> _cells = [];")]
    [TestCase("public System.Collections.Generic.HashSet<int> Cells { get; } = [];")]
    [TestCase("public System.Collections.Generic.Dictionary<int, float> Cells { get; init; } = new();")]
    public void UnassignableRefillableSolverMember_IsAllowed(string member)
    {
        Diagnostic[] diagnostics = Run(out GeneratorRunResult result, Solver("[NumosSerialize] " + member, PartialSolver));
        Assert.That(diagnostics, Is.Empty);
        Assert.That(result.GeneratedSources, Has.Length.EqualTo(1));
    }

    [TestCase("private readonly System.Collections.Generic.List<int>? _cells = [];")]
    [TestCase("private readonly int[] _cells = [];")]
    [TestCase("public System.Collections.Generic.IReadOnlyList<int> Cells { get; } = [];")]
    [TestCase("public System.Collections.Generic.IReadOnlyDictionary<int, float> Cells { get; } = new System.Collections.Generic.Dictionary<int, float>();")]
    public void UnassignableSolverMemberARestoreCantRefill_ReportsNumosState020(string member)
    {
        Diagnostic[] diagnostics = Run(out _, Solver("[NumosSerialize] " + member, PartialSolver));
        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSTATE020" }));
    }

    [Test]
    public void HandWrittenReadStateIntoOnSolver_ReportsNumosState008()
    {
        const string source = """
                              using Numos.API;
                              using Numos.CoreSim;
                              using Numos.CoreSim.State;
                              using Numos.CoreSim.State.Advanced;

                              namespace MyMod;

                              public sealed partial class FireSolver : IAtmosWorldSolver
                              {
                                  [NumosSerialize] public float Rate;

                                  public void ReadStateInto(ref StateReader reader, IGasRegistry? gases)
                                  {
                                  }

                                  public void Solve(AtmosWorldSolverContext context)
                                  {
                                  }
                              }
                              """;

        Assert.That(Ids(source), Is.EqualTo(new[] { "NUMOSSTATE008" }));
    }

    [Test]
    public void BothTags_ReportsNumosState011()
    {
        Assert.That(Ids(State("[NumosSerialize, NumosIgnore] public float Rate { get; init; }")), Is.EqualTo(new[] { "NUMOSSTATE011" }));
    }

    [Test]
    public void StaticSerializedMember_ReportsNumosState012()
    {
        Assert.That(
            Ids(
                State(
                    """
                    [NumosSerialize] public static float Shared { get; set; }
                    [NumosSerialize] public const int Version = 1;
                    private static float s_ignored;
                    [NumosSerialize] public float Rate { get; init; }
                    """)),
            Is.EqualTo(new[] { "NUMOSSTATE012", "NUMOSSTATE012" }));
    }

    [Test]
    public void DerivedClass_ReportsNumosState013()
    {
        const string source = """
                              using Numos.CoreSim.State;

                              namespace MyMod;

                              public class BaseSettings
                              {
                                  public float Hidden;
                              }

                              public sealed partial class Settings : BaseSettings
                              {
                                  [NumosSerialize] public float Rate { get; init; }
                              }
                              """;

        Assert.That(Ids(source), Is.EqualTo(new[] { "NUMOSSTATE013" }));
    }

    [Test]
    public void DerivedAndComputedMembers_ProduceNoDiagnostics()
    {
        const string source = """
                              using Numos.CoreSim.State;

                              namespace MyMod;

                              public sealed partial record Settings([property: NumosSerialize] float Low, [property: NumosSerialize] float High)
                              {
                                  private readonly float _range = High - Low;

                                  public float Range => _range;

                                  public static Settings Default { get; } = new(0f, 1f);
                              }
                              """;

        Assert.That(Ids(source), Is.Empty);
    }

    [Test]
    public void GetOnlyFieldWithNoConstructorParameter_ReportsNumosState014()
    {
        Diagnostic[] diagnostics = Run(
            out GeneratorRunResult result,
            State("[NumosSerialize] public float Rate { get; }"));

        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSTATE014" }));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain("'Rate' isn't a constructor parameter"));
        Assert.That(result.GeneratedSources, Is.Empty);
    }

    [Test]
    public void ReadonlyFieldWithNoConstructorParameter_ReportsNumosState014()
    {
        Assert.That(Ids(State("[NumosSerialize] public readonly int Count;")), Is.EqualTo(new[] { "NUMOSSTATE014" }));
    }

    [Test]
    public void UnmatchedConstructorParameter_ReportsNumosState014()
    {
        Diagnostic[] diagnostics = Run(
            out _,
            State(
                """
                public Settings(float rate, int extra) => Rate = rate;
                [NumosSerialize] public float Rate { get; }
                """));

        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSTATE014" }));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain("parameter 'extra' doesn't match"));
    }

    [Test]
    public void ParameterTypeTheFieldCantFill_ReportsNumosState014()
    {
        Diagnostic[] diagnostics = Run(
            out _,
            State(
                """
                public Settings(System.Collections.Generic.ISet<float> rates) => Rates = [.. rates];
                [NumosSerialize] public float[] Rates { get; }
                """));

        // A list can't become a set without dropping duplicates, so it's not offered.
        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSTATE014" }));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain("can't take 'Rates'"));
    }

    [Test]
    public void LargestBindableConstructor_IsChosen()
    {
        string source = State(
            """
            public Settings() { }
            public Settings(float rate, int count) { Rate = rate; Count = count; }
            public Settings(float rate, int count, string missing) : this(rate, count) { }
            [NumosSerialize] public float Rate { get; init; }
            [NumosSerialize] public int Count { get; init; }
            """);

        GeneratorRunResult result = GeneratorHarness.RunState(out Compilation output, out var diagnostics, null, source);
        Assert.That(diagnostics, Is.Empty);
        Assert.That(GeneratorHarness.Errors(output), Is.Empty);
        Assert.That(
            GeneratorHarness.GeneratedSource(result, "MyMod.Settings.NumosState.g.cs"),
            Does.Contain("return new global::MyMod.Settings(has783280263 ? field783280263 : ").And.Not.Contain("Rate = "));
    }

    [Test]
    public void BlankKey_ReportsNumosState015()
    {
        Assert.That(
            Ids(
                State(
                    """
                    public string Key => " ";
                    [NumosSerialize] public float Rate { get; init; }
                    """,
                    "public sealed partial class Settings : IAtmosSolverConfiguration")),
            Is.EqualTo(new[] { "NUMOSSTATE015" }));
    }

    [TestCase("public string Key { get; } = \"my-mod/fire\";")]
    [TestCase("public string Key => _key;\nprivate readonly string _key = \"my-mod/fire\";")]
    [TestCase("public string Key { get; init; } = \"my-mod/fire\";")]
    public void NonConstantSolverConfigurationKey_ReportsNumosState018(string key)
    {
        Diagnostic[] diagnostics = Run(
            out GeneratorRunResult result,
            State(
                key + "\n[NumosSerialize] public float Rate { get; init; }",
                "public sealed partial class Settings : IAtmosSolverConfiguration"));

        Assert.That(GeneratorHarness.Ids(diagnostics), Does.Contain("NUMOSSTATE018"));
        Assert.That(result.GeneratedSources, Is.Empty);
    }

    [TestCase("public string Key => \"my-mod/fire\";")]
    [TestCase("public string Key => FireKey;\npublic const string FireKey = \"my-mod/\" + \"fire\";")]
    [TestCase("public string Key { get { return \"my-mod/fire\"; } }")]
    [TestCase("string IAtmosSolverConfiguration.Key => \"my-mod/fire\";")]
    public void ConstantSolverConfigurationKey_BecomesTheStateKey(string key)
    {
        string source = State(
            key + "\n[NumosSerialize] public float Rate { get; init; }",
            "public sealed partial class Settings : IAtmosSolverConfiguration");

        GeneratorRunResult result = GeneratorHarness.RunState(out Compilation output, out var diagnostics, null, source);
        Assert.That(diagnostics, Is.Empty);
        Assert.That(GeneratorHarness.Errors(output), Is.Empty);
        Assert.That(GeneratorHarness.GeneratedSource(result, "MyMod.Settings.NumosState.g.cs"), Does.Contain(".StateKey => \"my-mod/fire\";"));
    }

    [Test]
    public void PrivateNestedSolverConfiguration_ReportsNumosState019()
    {
        const string source = """
                              using Numos.CoreSim;
                              using Numos.CoreSim.State;

                              namespace MyMod;

                              public sealed partial class Outer
                              {
                                  private sealed partial class Hidden : IAtmosSolverConfiguration
                                  {
                                      public string Key => "my-mod/hidden";
                                      [NumosSerialize] public float Rate { get; init; }
                                  }
                              }
                              """;

        GeneratorRunResult result = GeneratorHarness.RunState(out Compilation output, out var diagnostics, null, source);
        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSTATE019" }));
        Assert.That(GeneratorHarness.Errors(output), Is.Empty);
        Assert.That(result.GeneratedSources.Select(static source => source.HintName), Has.None.Contains("ModuleInitializer"));
    }

    private const string PublicConfiguration = """
                                               public sealed partial class Visible : IAtmosSolverConfiguration
                                               {
                                                   public string Key => "my-mod/visible";
                                                   [NumosSerialize] public float Rate { get; init; }
                                               }
                                               """;

    private static string FileLocalSource(string hidden)
    {
        return $$"""
                 using Numos.API;
                 using Numos.CoreSim;
                 using Numos.CoreSim.State;

                 namespace MyMod;

                 {{PublicConfiguration}}

                 {{hidden}}
                 """;
    }

    [TestCase(
        """
        file sealed partial class Hidden : IAtmosSolverConfiguration
        {
            public string Key => "my-mod/hidden";
            [NumosSerialize] public float Rate { get; init; }
        }
        """)]
    [TestCase(
        """
        file sealed partial class Hidden : IAtmosWorldSolver
        {
            [NumosSerialize] public float Rate;

            public void Solve(AtmosWorldSolverContext context)
            {
            }
        }
        """)]
    [TestCase(
        """
        file partial class Holder
        {
            public sealed partial class Hidden : IAtmosSolverConfiguration
            {
                public string Key => "my-mod/hidden";
                [NumosSerialize] public float Rate { get; init; }
            }
        }
        """)]
    public void FileLocalType_ReportsNumosState032WithoutCrashing(string hidden)
    {
        GeneratorRunResult result = GeneratorHarness.RunState(
            out _, out var diagnostics, null, FileLocalSource(hidden));

        Assert.Multiple(() =>
        {
            Assert.That(result.Exception, Is.Null);
            Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSTATE032" }));
            Assert.That(diagnostics[0].GetMessage(), Does.Contain("Hidden"));
            Assert.That(
                result.GeneratedSources.Select(static source => source.HintName),
                Has.Some.EqualTo("MyMod.Visible.NumosState.g.cs"));

            string initializer = GeneratorHarness.GeneratedSource(result, StateGenerator.ModuleInitializerHintName);
            Assert.That(initializer, Does.Contain("MyMod.Visible").And.Not.Contain("Hidden"));
        });
    }

    [Test]
    public void FieldTargetOnProperty_ReportsNumosState033()
    {
        Diagnostic[] diagnostics = Run(
            out GeneratorRunResult result,
            State(
                """
                [NumosSerialize] public float Rate { get; set; }
                [field: NumosSerialize] public float Spread { get; set; }
                """));

        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSTATE033" }));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain("'MyMod.Settings.Spread'"));
        Assert.That(result.GeneratedSources, Is.Empty);
    }

    [Test]
    public void FieldTargetOnTheOnlyMark_ReportsNumosState033()
    {
        Assert.That(
            Ids(State("[field: NumosSerialize] public float Spread { get; set; }")),
            Is.EqualTo(new[] { "NUMOSSTATE033" }));
    }

    [Test]
    public void FieldTargetOnSolverProperty_ReportsNumosState033()
    {
        const string source = """
                              using Numos.API;
                              using Numos.CoreSim.State;

                              namespace MyMod;

                              public sealed partial class FireSolver : IAtmosWorldSolver
                              {
                                  [NumosSerialize] public float Rate { get; set; }
                                  [field: NumosSerialize] public float Spread { get; set; }

                                  public void Solve(AtmosWorldSolverContext context)
                                  {
                                  }
                              }
                              """;

        Diagnostic[] diagnostics = Run(out GeneratorRunResult result, source);
        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSTATE033" }));
        Assert.That(result.GeneratedSources, Is.Empty);
    }

    [Test]
    public void FieldTargetOnRecordParameter_ReportsNumosState033()
    {
        const string source = """
                              using Numos.CoreSim.State;

                              namespace MyMod;

                              public sealed partial record Settings(
                                  [property: NumosSerialize] float Rate,
                                  [field: NumosSerialize] float Spread);
                              """;

        Diagnostic[] diagnostics = Run(out GeneratorRunResult result, source);
        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSTATE033" }));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain("'MyMod.Settings.Spread'"));
        Assert.That(result.GeneratedSources, Is.Empty);
    }

    [Test]
    public void FieldTargetOnAField_IsAllowed()
    {
        Assert.That(Ids(State("[field: NumosSerialize] public float Rate;")), Is.Empty);
    }

    [Test]
    public void MarkedIndexerOnly_ReportsNumosState034()
    {
        Diagnostic[] diagnostics = Run(
            out GeneratorRunResult result,
            State("[NumosSerialize] public float this[int index] { get => 0f; set { } }"));

        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSTATE034" }));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain("'MyMod.Settings.this[int]'"));
        Assert.That(result.GeneratedSources, Is.Empty);
    }

    [Test]
    public void MarkedIndexerNextToMarkedField_ReportsNumosState034()
    {
        Diagnostic[] diagnostics = Run(
            out GeneratorRunResult result,
            State(
                """
                [NumosSerialize] public float Rate { get; set; }
                [NumosSerialize] public float this[int index] { get => 0f; set { } }
                """));

        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSTATE034" }));
        Assert.That(result.GeneratedSources, Is.Empty);
    }

    [Test]
    public void MarkedIndexerOnSolver_ReportsNumosState034()
    {
        const string source = """
                              using Numos.API;
                              using Numos.CoreSim.State;

                              namespace MyMod;

                              public sealed partial class FireSolver : IAtmosWorldSolver
                              {
                                  [NumosSerialize] public float Rate { get; set; }
                                  [NumosSerialize] public float this[int index] { get => 0f; set { } }

                                  public void Solve(AtmosWorldSolverContext context)
                                  {
                                  }
                              }
                              """;

        Diagnostic[] diagnostics = Run(out GeneratorRunResult result, source);
        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSTATE034" }));
        Assert.That(result.GeneratedSources, Is.Empty);
    }

    private static string Solver(string members, string declaration = "public sealed class FireSolver : IAtmosWorldSolver")
    {
        return $$"""
                 using Numos.API;
                 using Numos.CoreSim.State;

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

    [Test]
    public void UnmarkedFieldOnSolverWithoutMarks_ReportsNumosState016AtTheField()
    {
        string source = Solver("private int _ticks;");
        Diagnostic[] diagnostics = Run(out GeneratorRunResult result, source);

        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSTATE016" }));
        Assert.That(diagnostics[0].Severity, Is.EqualTo(DiagnosticSeverity.Warning));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain("'MyMod.FireSolver._ticks'"));

        int start = diagnostics[0].Location.SourceSpan.Start;
        Assert.That(source.Substring(start, "_ticks".Length), Is.EqualTo("_ticks"));
        Assert.That(result.GeneratedSources, Is.Empty);
    }

    [Test]
    public void NumosIgnoreOnSolverWithoutMarks_SilencesNumosState016()
    {
        Assert.That(Ids(Solver("[NumosIgnore] private int _ticks;")), Is.Empty);
    }

    [Test]
    public void ImmutableMembersOnSolverWithoutMarks_ProduceNoDiagnostics()
    {
        Assert.That(
            Ids(
                Solver(
                    """
                    private readonly int _limit = 3;
                    public float Rate { get; } = 1f;
                    private static int s_count;
                    private const int Max = 4;
                    public int Total => _limit + s_count + Max;
                    """)),
            Is.Empty);
    }

    [Test]
    public void PartialSolverWithoutMarks_ReportsEachFieldOnce()
    {
        const string first = """
                             using Numos.API;

                             namespace MyMod;

                             public sealed partial class FireSolver : IAtmosWorldSolver
                             {
                                 private int _ticks;

                                 public void Solve(AtmosWorldSolverContext context)
                                 {
                                 }
                             }
                             """;

        const string second = """
                              namespace MyMod;

                              public sealed partial class FireSolver : System.IDisposable
                              {
                                  private float _heat;

                                  public void Dispose()
                                  {
                                  }
                              }
                              """;

        Diagnostic[] diagnostics = Run(out GeneratorRunResult result, first, second);
        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSTATE016", "NUMOSSTATE016" }));
        Assert.That(
            diagnostics.Select(static diagnostic => diagnostic.GetMessage()),
            Has.One.Contains("'MyMod.FireSolver._ticks'").And.One.Contains("'MyMod.FireSolver._heat'"));

        Assert.That(result.GeneratedSources, Is.Empty);
    }

    [Test]
    public void SolverWithOneMark_ReportsEachUnmarkedFieldOnce()
    {
        Diagnostic[] diagnostics = Run(
            out GeneratorRunResult result,
            Solver(
                """
                [NumosSerialize] public float Rate;
                private int _ticks;
                private float _heat;
                """,
                "public sealed partial class FireSolver : IAtmosWorldSolver"));

        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSTATE016", "NUMOSSTATE016" }));
        Assert.That(
            diagnostics.Select(static diagnostic => diagnostic.GetMessage()),
            Has.One.Contains("'MyMod.FireSolver._ticks'").And.One.Contains("'MyMod.FireSolver._heat'"));

        Assert.That(result.GeneratedSources, Has.Length.EqualTo(1));
    }

    [Test]
    public void ClassThatIsNotASolver_ReportsNothing()
    {
        const string source = """
                              namespace MyMod;

                              public sealed class Tracker : System.IDisposable
                              {
                                  private int _ticks;

                                  public void Dispose() => _ticks++;
                              }
                              """;

        Diagnostic[] diagnostics = Run(out GeneratorRunResult result, source);
        Assert.That(diagnostics, Is.Empty);
        Assert.That(result.GeneratedSources, Is.Empty);
    }

    [Test]
    public void UnrelatedEdit_LeavesUnmarkedSolverStepCached()
    {
        CSharpParseOptions parseOptions = GeneratorHarness.ParseOptions();
        CSharpCompilation compilation = GeneratorHarness.CreateCompilation(parseOptions, Solver("private int _ticks;"));
        GeneratorDriver driver = GeneratorHarness.CreateDriver(parseOptions, trackSteps: true, generator: new StateGenerator());
        driver = driver.RunGenerators(compilation);

        CSharpCompilation edited = compilation.AddSyntaxTrees(
            CSharpSyntaxTree.ParseText(
                "namespace Other; internal static class Unrelated { }",
                parseOptions,
                path: "Unrelated.cs"));

        driver = driver.RunGenerators(edited);

        GeneratorRunResult result = driver.GetRunResult().Results.Single();
        var outputs = result.TrackedSteps[StateGenerator.UnmarkedSolversStepName]
            .SelectMany(static run => run.Outputs)
            .Select(static output => output.Reason)
            .ToArray();

        Assert.That(outputs, Is.Not.Empty);
        Assert.That(
            outputs,
            Is.All.Matches<IncrementalStepRunReason>(static reason =>
                reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged));

        Assert.That(
            GeneratorHarness.SourceOutputReasons(result),
            Is.All.EqualTo(IncrementalStepRunReason.Cached));
    }

    private const string PartialSolver = "public sealed partial class FireSolver : IAtmosWorldSolver";

    [Test]
    public void UnmarkedReadonlyCollectionsOnSolverWithoutMarks_ReportNumosState016()
    {
        Diagnostic[] diagnostics = Run(
            out _,
            Solver(
                """
                private readonly System.Collections.Generic.List<int> _hot = new();
                private readonly int[] _buffer = new int[4];
                public System.Collections.Generic.Dictionary<int, float> Heat { get; } = new();
                private readonly System.Collections.Generic.IReadOnlyList<int> _view = [];
                private readonly string _name = "fire";
                """));

        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSTATE016", "NUMOSSTATE016", "NUMOSSTATE016" }));
        Assert.That(
            diagnostics.Select(static diagnostic => diagnostic.GetMessage()),
            Has.One.Contains("'MyMod.FireSolver._hot'")
                .And.One.Contains("'MyMod.FireSolver._buffer'")
                .And.One.Contains("'MyMod.FireSolver.Heat'"));
    }

    [Test]
    public void UnmarkedReadonlyCollectionOnSolverWithMarks_ReportsNumosState016WithThreeChoices()
    {
        Diagnostic[] diagnostics = Run(
            out _,
            Solver(
                """
                [NumosSerialize] public float Rate;
                private readonly System.Collections.Generic.Queue<int> _pending = new();
                """,
                PartialSolver));

        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSTATE016" }));
        Assert.That(
            diagnostics[0].GetMessage(),
            Does.Contain("[NumosSerialize]").And.Contain("[NumosScratch]").And.Contain("[NumosIgnore]"));
    }

    [Test]
    public void ReadonlyCollectionOnSerializableType_ReportsNothing()
    {
        Assert.That(
            Ids(
                State(
                    """
                    [NumosSerialize] public float Rate { get; init; }
                    private readonly System.Collections.Generic.List<int> _cache = new();
                    """)),
            Is.Empty);
    }

    [Test]
    public void UnmarkedFieldOnSerializableType_DoesNotOfferNumosScratch()
    {
        Diagnostic[] diagnostics = Run(
            out _,
            State(
                """
                [NumosSerialize] public float Rate { get; init; }
                public int Count;
                """));

        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSTATE016" }));
        Assert.That(diagnostics[0].GetMessage(), Does.Not.Contain("NumosScratch"));
    }

    [Test]
    public void ScratchOnlySolver_ReportsEachUnmarkedFieldOnce()
    {
        Diagnostic[] diagnostics = Run(
            out GeneratorRunResult result,
            Solver(
                """
                [NumosScratch] private int _cursor;
                private int _ticks;
                """,
                PartialSolver));

        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSTATE016" }));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain("'MyMod.FireSolver._ticks'"));
        Assert.That(result.GeneratedSources, Has.Length.EqualTo(1));
    }

    [Test]
    public void NonPartialScratchOnlySolver_ReportsNumosState001()
    {
        Diagnostic[] diagnostics = Run(out GeneratorRunResult result, Solver("[NumosScratch] private int _cursor;"));

        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSTATE001" }));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain("[NumosScratch]"));
        Assert.That(result.GeneratedSources, Is.Empty);
    }

    [Test]
    public void HandWrittenResetScratch_ReportsNumosState008()
    {
        Diagnostic[] diagnostics = Run(
            out GeneratorRunResult result,
            Solver(
                """
                [NumosScratch] private int _cursor;
                public void ResetScratch() => _cursor = 0;
                """,
                PartialSolver));

        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSTATE008" }));
        Assert.That(result.GeneratedSources, Is.Empty);
    }

    [TestCase("[NumosScratch, NumosSerialize] private int _cursor;", "NumosSerialize")]
    [TestCase("[NumosScratch, NumosIgnore] private int _cursor;", "NumosIgnore")]
    public void ScratchWithAnotherMark_ReportsNumosState035(string member, string other)
    {
        Diagnostic[] diagnostics = Run(out GeneratorRunResult result, Solver(member, PartialSolver));

        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSTATE035" }));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain("[" + other + "]"));
        Assert.That(result.GeneratedSources, Is.Empty);
    }

    [Test]
    public void ScratchOnATypeThatIsNotASolver_ReportsNumosState036()
    {
        Diagnostic[] diagnostics = Run(
            out GeneratorRunResult result,
            State("[NumosScratch] private int _cursor;", "public sealed class Settings"));

        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSTATE036" }));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain("'MyMod.Settings._cursor'"));
        Assert.That(result.GeneratedSources, Is.Empty);
    }

    [Test]
    public void ScratchOnASolverConfiguration_ReportsNumosState036()
    {
        Diagnostic[] diagnostics = Run(
            out GeneratorRunResult result,
            State(
                """
                public string Key => "my-mod/fire";
                [NumosSerialize] public float Rate { get; init; }
                [NumosScratch] private int _cursor;
                """,
                "public sealed partial class Settings : IAtmosSolverConfiguration"));

        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSTATE036" }));
        Assert.That(result.GeneratedSources, Is.Empty);
    }

    [Test]
    public void ScratchOnAStructSolver_ReportsNumosState036()
    {
        Assert.That(
            Ids(Solver("[NumosScratch] private int _cursor;", "public partial struct FireSolver : IAtmosWorldSolver")),
            Is.EqualTo(new[] { "NUMOSSTATE036" }));
    }

    [TestCase("[NumosScratch] private static int s_cursor;")]
    [TestCase("[NumosScratch] private const int Max = 4;")]
    [TestCase("[field: NumosScratch] public int Cursor { get; set; }")]
    [TestCase("[NumosScratch] public int this[int index] { get => 0; set { } }")]
    public void ScratchOnAMemberWithNoInstanceValue_ReportsNumosState036(string member)
    {
        Diagnostic[] diagnostics = Run(out GeneratorRunResult result, Solver(member, PartialSolver));

        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSTATE036" }));
        Assert.That(result.GeneratedSources, Is.Empty);
    }

    [TestCase("[NumosScratch] private readonly int _cursor = 3;", "int")]
    [TestCase("[NumosScratch] private readonly int[] _buffer = new int[4];", "int[]")]
    [TestCase("[NumosScratch] public float Heat { get; init; }", "float")]
    [TestCase("[NumosScratch] private readonly Box _box = new();", "MyMod.Box")]
    public void ScratchThatCantBeReset_ReportsNumosState037(string member, string type)
    {
        string source = Solver(member, PartialSolver) + "\n\npublic struct Box { public void Clear() { } }\n";
        Diagnostic[] diagnostics = Run(out GeneratorRunResult result, source);

        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSTATE037" }));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain("'" + type + "'"));
        Assert.That(result.GeneratedSources, Is.Empty);
    }

    [Test]
    public void ScratchInitializerReadingAPrimaryConstructorParameter_ReportsNumosState038()
    {
        Diagnostic[] diagnostics = Run(
            out GeneratorRunResult result,
            Solver("[NumosScratch] private int _cursor = seed + 1;", "public sealed partial class FireSolver(int seed) : IAtmosWorldSolver"));

        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSTATE038" }));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain("'seed'").And.Contain("OnRestored").And.Contain("[NumosIgnore]"));
        Assert.That(result.GeneratedSources, Is.Empty);
    }

    [Test]
    public void ScratchArrayInitializer_ReportsNumosState038()
    {
        Diagnostic[] diagnostics = Run(
            out GeneratorRunResult result,
            Solver("[NumosScratch] private int[] _buffer = { 1, 2 };", PartialSolver));

        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSTATE038" }));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain("array initializer"));
        Assert.That(result.GeneratedSources, Is.Empty);
    }

    [Test]
    public void ScratchInitializerThatCapturesNothing_IsAllowedInAPrimaryConstructorSolver()
    {
        Diagnostic[] diagnostics = Run(
            out GeneratorRunResult result,
            Solver(
                """
                [NumosScratch] private System.Func<int, int> _step = seed => seed + 1;
                private readonly int _seed = seed;
                """,
                "public sealed partial class FireSolver(int seed) : IAtmosWorldSolver"));

        Assert.That(diagnostics, Is.Empty);
        Assert.That(result.GeneratedSources, Has.Length.EqualTo(1));
    }

    [Test]
    public void ScratchOnALiveType_ReportsNumosState036()
    {
        const string source = """
                              using Numos.CoreSim.State;

                              namespace MyMod;

                              public sealed class Live
                              {
                                  [NumosSerialize] internal int Ticks { get; set; }
                                  [NumosScratch] internal int Cursor { get; set; }
                              }

                              [NumosSnapshotOf(typeof(Live))]
                              public sealed partial class LiveSnapshot;
                              """;

        GeneratorHarness.RunSnapshots(out _, out var diagnostics, source);
        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSTATE036" }));
    }
}
