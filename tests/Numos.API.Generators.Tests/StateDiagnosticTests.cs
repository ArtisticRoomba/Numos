using Microsoft.CodeAnalysis;

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
}
