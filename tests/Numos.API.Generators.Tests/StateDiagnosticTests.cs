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

                 namespace MyMod;

                 [NumosState]
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
    public void NonPartialType_ReportsNumosState001AndGeneratesNothing()
    {
        Diagnostic[] diagnostics = Run(
            out GeneratorRunResult result,
            State("[StateField(1)] public float Rate { get; init; }", "public sealed class Settings"));

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
                                  [NumosState]
                                  public partial struct Inner
                                  {
                                      [StateField(1)] public int Value;
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
            Ids(State("[StateField(1)] public T Value { get; init; } = default!;", "public sealed partial class Settings<T>")),
            Is.EqualTo(new[] { "NUMOSSTATE002" }));
    }

    [Test]
    public void UntaggedStorage_ReportsNumosState003ForEachMember()
    {
        Diagnostic[] diagnostics = Run(
            out GeneratorRunResult result,
            State(
                """
                private float _field;
                public float Init { get; init; }
                private float GetOnly { get; }
                public float Computed => _field + Init + GetOnly;
                [StateField(1)] public float Tagged { get; init; }
                """));

        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSTATE003", "NUMOSSTATE003", "NUMOSSTATE003" }));
        Assert.That(
            diagnostics.Select(static diagnostic => diagnostic.GetMessage()),
            Has.Some.Contains("'MyMod.Settings._field'")
                .And.Some.Contains("'MyMod.Settings.Init'")
                .And.Some.Contains("'MyMod.Settings.GetOnly'"));

        Assert.That(result.GeneratedSources, Is.Empty);
    }

    [Test]
    public void UntaggedPositionalRecordProperty_ReportsNumosState003()
    {
        const string source = """
                              using Numos.CoreSim.State;

                              namespace MyMod;

                              [NumosState]
                              public sealed partial record Settings([property: StateField(1)] float Rate, float Spread);
                              """;

        Diagnostic[] diagnostics = Run(out _, source);
        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSTATE003" }));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain("'MyMod.Settings.Spread'"));
    }

    [TestCase(0)]
    [TestCase(-3)]
    public void NonPositiveId_ReportsNumosState004(int id)
    {
        Assert.That(Ids(State($"[StateField({id})] public float Rate {{ get; init; }}")), Is.EqualTo(new[] { "NUMOSSTATE004" }));
    }

    [Test]
    public void DuplicateId_ReportsNumosState005()
    {
        Diagnostic[] diagnostics = Run(
            out _,
            State(
                """
                [StateField(2)] public float Rate { get; init; }
                [StateField(2)] public float Spread { get; init; }
                """));

        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSTATE005" }));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain("'MyMod.Settings.Spread'").And.Contain("'Rate'"));
    }

    [Test]
    public void RetiredId_ReportsNumosState006()
    {
        Assert.That(
            Ids(
                State(
                    "[StateField(4)] public float Rate { get; init; }",
                    "[RetiredStateFields(3, 4)] public sealed partial class Settings")),
            Is.EqualTo(new[] { "NUMOSSTATE006" }));
    }

    [TestCase("object")]
    [TestCase("System.Func<int>")]
    [TestCase("System.Collections.Generic.List<int>")]
    [TestCase("System.Collections.Generic.IReadOnlyList<object>")]
    [TestCase("System.Collections.Generic.Dictionary<float[], int>")]
    [TestCase("int[,]")]
    public void UnsupportedType_ReportsNumosState007(string type)
    {
        Assert.That(Ids(State($"[StateField(1)] public {type} Value {{ get; init; }} = default!;")), Is.EqualTo(new[] { "NUMOSSTATE007" }));
    }

    [Test]
    public void UnknownAlias_ReportsNumosState007()
    {
        GeneratorHarness.RunState(
            out _,
            out var diagnostics,
            "temperature | Kelvin | global::System.Single | temperature=1",
            State("[StateField(1)] public Furlong Distance { get; init; }"));

        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSTATE007" }));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain("'Furlong'"));
    }

    [Test]
    public void HandWrittenSolverConfigurationMembers_ReportNumosState008()
    {
        string source = State(
            """
            public string Key => "my-mod/fire";
            [StateField(1)] public float Rate { get; init; }
            public IAtmosSolverConfiguration CreateSnapshot(IGasRegistry gases) => this;
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
    public void HandWrittenAppendHash_ReportsNumosState008()
    {
        Assert.That(
            Ids(
                State(
                    """
                    [StateField(1)] public float Rate { get; init; }
                    internal void AppendHash(ref ulong hash) { }
                    """)),
            Is.EqualTo(new[] { "NUMOSSTATE008" }));
    }

    [Test]
    public void StateFieldOnWorldSolver_ReportsNumosState009()
    {
        const string source = """
                              using Numos.API;
                              using Numos.CoreSim.State;

                              namespace MyMod;

                              public sealed class FireSolver : IAtmosWorldSolver
                              {
                                  [StateField(1)] public float IgnitionTemperature { get; set; }

                                  public void Solve(AtmosWorldSolverContext context)
                                  {
                                  }
                              }
                              """;

        Diagnostic[] diagnostics = Run(out _, source);
        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSTATE009" }));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain("solver configuration").And.Contain("rollback chunk field"));
    }

    [Test]
    public void NumosStateWorldSolver_ReportsNumosState009Once()
    {
        const string source = """
                              using Numos.API;
                              using Numos.CoreSim.State;

                              namespace MyMod;

                              [NumosState]
                              public sealed partial class FireSolver : IAtmosWorldSolver
                              {
                                  private readonly int _scratch;

                                  [StateField(1)] public float IgnitionTemperature { get; set; }

                                  public void Solve(AtmosWorldSolverContext context)
                                  {
                                  }
                              }
                              """;

        Diagnostic[] diagnostics = Run(out GeneratorRunResult result, source);
        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSTATE009" }));
        Assert.That(result.GeneratedSources, Is.Empty);
    }

    [Test]
    public void StateFieldOutsideStateType_ReportsNumosState010()
    {
        const string source = """
                              using Numos.CoreSim.State;

                              namespace MyMod;

                              public sealed class Settings
                              {
                                  [StateField(1)] public float Rate { get; init; }
                                  [StateField(2)] public float Spread;
                              }
                              """;

        Assert.That(Ids(source), Is.EqualTo(new[] { "NUMOSSTATE010", "NUMOSSTATE010" }));
    }

    [Test]
    public void BothTags_ReportsNumosState011()
    {
        Assert.That(Ids(State("[StateField(1), NotState] public float Rate { get; init; }")), Is.EqualTo(new[] { "NUMOSSTATE011" }));
    }

    [Test]
    public void StaticStateField_ReportsNumosState012()
    {
        Assert.That(
            Ids(
                State(
                    """
                    [StateField(1)] public static float Shared { get; set; }
                    [StateField(2)] public const int Version = 1;
                    private static float s_ignored;
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

                              [NumosState]
                              public sealed partial class Settings : BaseSettings
                              {
                                  [StateField(1)] public float Rate { get; init; }
                              }
                              """;

        Assert.That(Ids(source), Is.EqualTo(new[] { "NUMOSSTATE013" }));
    }

    [Test]
    public void NotStateAndComputedMembers_ProduceNoDiagnostics()
    {
        const string source = """
                              using Numos.CoreSim.State;

                              namespace MyMod;

                              [NumosState]
                              public sealed partial record Settings([property: StateField(1)] float Low, [property: StateField(2)] float High)
                              {
                                  [NotState] private readonly float _range = High - Low;

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
            State("[StateField(1)] public float Rate { get; }"));

        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSTATE014" }));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain("'Rate' isn't a constructor parameter"));
        Assert.That(result.GeneratedSources, Is.Empty);
    }

    [Test]
    public void ReadonlyFieldWithNoConstructorParameter_ReportsNumosState014()
    {
        Assert.That(Ids(State("[StateField(1)] public readonly int Count;")), Is.EqualTo(new[] { "NUMOSSTATE014" }));
    }

    [Test]
    public void UnmatchedConstructorParameter_ReportsNumosState014()
    {
        Diagnostic[] diagnostics = Run(
            out _,
            State(
                """
                public Settings(float rate, int extra) => Rate = rate;
                [StateField(1)] public float Rate { get; }
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
                [StateField(1)] public float[] Rates { get; }
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
            [StateField(1)] public float Rate { get; init; }
            [StateField(2)] public int Count { get; init; }
            """);

        GeneratorRunResult result = GeneratorHarness.RunState(out Compilation output, out var diagnostics, null, source);
        Assert.That(diagnostics, Is.Empty);
        Assert.That(GeneratorHarness.Errors(output), Is.Empty);
        Assert.That(
            GeneratorHarness.GeneratedSource(result, "MyMod.Settings.NumosState.g.cs"),
            Does.Contain("return new global::MyMod.Settings(has1 ? field1 : ").And.Not.Contain("Rate = "));
    }

    [Test]
    public void BlankKey_ReportsNumosState015()
    {
        Assert.That(
            Ids(State("[StateField(1)] public float Rate { get; init; }").Replace("[NumosState]", "[NumosState(Key = \" \")]")),
            Is.EqualTo(new[] { "NUMOSSTATE015" }));
    }

    [Test]
    public void HandWrittenKeyNextToADeclaredKey_ReportsNumosState008()
    {
        string source = State(
                """
                public string Key => "my-mod/other";
                [StateField(1)] public float Rate { get; init; }
                public IAtmosSolverConfiguration CreateSnapshot(IGasRegistry gases) => this;
                """,
                "public sealed partial class Settings : IAtmosSolverConfiguration")
            .Replace("[NumosState]", "[NumosState(Key = \"my-mod/fire\")]");

        Diagnostic[] diagnostics = Run(out _, source);
        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSTATE008" }));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain("'Key'"));
    }
}
