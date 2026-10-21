using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Numos.CoreSim.State.Advanced;

namespace Numos.API.Generators.Tests;

[TestFixture]
public sealed class StateGeneratorTests
{
    private const string EveryShapeSource = """
                                            using System.Collections.Frozen;
                                            using System.Collections.Generic;
                                            using System.Collections.Immutable;
                                            using Numos.CoreSim;
                                            using Numos.CoreSim.State;
                                            using Numos.Maths;

                                            namespace MyMod;

                                            public enum Mode : byte
                                            {
                                                Off,
                                                On,
                                            }

                                            [NumosState]
                                            public readonly partial record struct Point([property: StateField(1)] float X, [property: StateField(2)] float Y);

                                            [NumosState]
                                            public sealed partial class Leaf
                                            {
                                                [StateField(1)] public string Name { get; init; } = "";
                                            }

                                            [NumosState]
                                            public sealed partial class EveryShape
                                            {
                                                [StateField(1)] public bool Bool;
                                                [StateField(2)] public byte Byte;
                                                [StateField(3)] public sbyte SByte;
                                                [StateField(4)] public short Short;
                                                [StateField(5)] public ushort UShort;
                                                [StateField(6)] public int Int;
                                                [StateField(7)] public uint UInt;
                                                [StateField(8)] public long Long;
                                                [StateField(9)] public ulong ULong;
                                                [StateField(10)] public float Float;
                                                [StateField(11)] public double Double;
                                                [StateField(12)] public Mode Mode;
                                                [StateField(13)] public string Text = "";
                                                [StateField(14)] public string? MaybeText;
                                                [StateField(15)] public Int3 Position;
                                                [StateField(16)] public GasProperties Gas;
                                                [StateField(17)] public GasProperties? MaybeGas;
                                                [StateField(18)] public int? MaybeInt;
                                                [StateField(19)] public Point Point;
                                                [StateField(20)] public Leaf? MaybeLeaf;
                                                [StateField(21)] public float[] Floats = [];
                                                [StateField(22)] public IReadOnlyList<string?> Names = [];
                                                [StateField(23)] public ImmutableArray<Point> Points = [];
                                                [StateField(24)] public IReadOnlyDictionary<GasProperties, float> Amounts = new Dictionary<GasProperties, float>();
                                                [StateField(25)] public FrozenDictionary<string, Leaf> Leaves = FrozenDictionary<string, Leaf>.Empty;
                                                [StateField(26)] public Dictionary<Mode, int[]> ByMode = new();
                                                [StateField(27)] public FrozenSet<Point> PointSet = FrozenSet<Point>.Empty;
                                                [StateField(28)] public IReadOnlySet<string> NameSet = new HashSet<string>();
                                                [StateField(29)] public Mode? MaybeMode;
                                            }
                                            """;

    [Test]
    public void EverySupportedShape_GeneratesCompilingCodeWithoutWarnings()
    {
        GeneratorRunResult result = GeneratorHarness.RunState(out Compilation output, out var diagnostics, null, EveryShapeSource);

        Assert.That(diagnostics, Is.Empty);
        Assert.That(GeneratorHarness.Errors(output), Is.Empty);
        Assert.That(output.GetDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Warning), Is.Empty);
        Assert.That(
            result.GeneratedSources.Select(static source => source.HintName),
            Is.EquivalentTo(new[] { "MyMod.Point.NumosState.g.cs", "MyMod.Leaf.NumosState.g.cs", "MyMod.EveryShape.NumosState.g.cs" }));

        string source = GeneratorHarness.GeneratedSource(result, "MyMod.EveryShape.NumosState.g.cs");
        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("StateCodecs.Enum<global::MyMod.Mode, byte, global::Numos.CoreSim.State.Advanced.StateCodecs.Byte>"));
            Assert.That(
                source,
                Does.Contain(
                    "StateCodecs.Nullable<string, global::Numos.CoreSim.State.Advanced.StateCodecs.String>.Write(ref sink, value.MaybeText);"));

            Assert.That(
                source,
                Does.Contain(
                    "StateCodecs.Dictionary<global::System.Collections.Generic.IReadOnlyDictionary<global::Numos.CoreSim.GasProperties, float>, " +
                    "global::Numos.CoreSim.GasProperties, global::Numos.CoreSim.State.Advanced.StateCodecs.Gas, float, " +
                    "global::Numos.CoreSim.State.Advanced.StateCodecs.Single>"));

            Assert.That(source, Does.Contain("StateCodecs.Set<global::System.Collections.Frozen.FrozenSet<global::MyMod.Point>"));
            Assert.That(source, Does.Not.Contain("SemanticallyEquals"));
        });
    }

    [Test]
    public void Fields_AreWrittenInIdOrderNotDeclarationOrder()
    {
        const string source = """
                              using Numos.CoreSim.State;

                              namespace MyMod;

                              [NumosState]
                              public partial struct Settings
                              {
                                  [StateField(9)] public float Last;
                                  [StateField(2)] public float First;
                                  [StateField(5)] public float Middle;
                              }
                              """;

        GeneratorRunResult result = GeneratorHarness.RunState(out Compilation output, out var diagnostics, null, source);
        Assert.That(diagnostics, Is.Empty);
        Assert.That(GeneratorHarness.Errors(output), Is.Empty);

        string generated = GeneratorHarness.GeneratedSource(result, "MyMod.Settings.NumosState.g.cs");
        int first = generated.IndexOf("sink.BeginField(2);", StringComparison.Ordinal);
        int middle = generated.IndexOf("sink.BeginField(5);", StringComparison.Ordinal);
        int last = generated.IndexOf("sink.BeginField(9);", StringComparison.Ordinal);
        Assert.That(first, Is.GreaterThan(0));
        Assert.That(middle, Is.GreaterThan(first));
        Assert.That(last, Is.GreaterThan(middle));
    }

    [Test]
    public void SolverConfiguration_GetsSemanticallyEqualsAndComputeStateHash()
    {
        const string source = """
                              using Numos.CoreSim;
                              using Numos.CoreSim.State;

                              namespace MyMod;

                              /// <summary>
                              ///     Documented settings.
                              /// </summary>
                              [NumosState]
                              public sealed partial class FireSettings : IAtmosSolverConfiguration
                              {
                                  /// <inheritdoc />
                                  public string Key => "my-mod/fire";

                                  /// <summary>
                                  ///     Ignition temperature.
                                  /// </summary>
                                  [StateField(1)] public float IgnitionTemperature { get; init; }

                                  /// <inheritdoc />
                                  public IAtmosSolverConfiguration CreateSnapshot(IGasRegistry gases) => this;
                              }
                              """;

        // Documentation mode surfaces CS1591 on generated public members that lack docs.
        CSharpParseOptions parseOptions = GeneratorHarness.ParseOptions().WithDocumentationMode(DocumentationMode.Diagnose);
        CSharpCompilation compilation = GeneratorHarness.CreateCompilation(parseOptions, source);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new State.StateGenerator().AsSourceGenerator()],
            parseOptions: parseOptions);

        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out Compilation output, out var diagnostics);

        Assert.That(diagnostics, Is.Empty);
        Assert.That(output.GetDiagnostics().Where(static d => d.Severity >= DiagnosticSeverity.Warning), Is.Empty);

        string generated = GeneratorHarness.GeneratedSource(driver.GetRunResult().Results.Single(), "MyMod.FireSettings.NumosState.g.cs");
        Assert.Multiple(() =>
        {
            Assert.That(generated, Does.Contain("public bool SemanticallyEquals(global::Numos.CoreSim.IAtmosSolverConfiguration other)"));
            Assert.That(generated, Does.Contain("public ulong ComputeStateHash()"));
            Assert.That(generated, Does.Contain("sink.WriteString(((global::Numos.CoreSim.IAtmosSolverConfiguration)this).Key);"));
        });
    }

    [Test]
    public void QuantityAlias_ResolvesThroughTheUnitsCatalog()
    {
        // Without the Magnitudo generator the alias stays an error type, as it does inside Numos.CoreSim.
        const string source = """
                              using System.Collections.Generic;
                              using Numos.CoreSim;
                              using Numos.CoreSim.State;

                              namespace MyMod;

                              [NumosState]
                              public sealed partial class Settings
                              {
                                  [StateField(1)] public Kelvin Ignition { get; init; }
                                  [StateField(2)] public IReadOnlyDictionary<GasProperties, Mole64> Amounts { get; init; } = null!;
                              }
                              """;

        const string catalog = """
                               # stable id | alias | storage | dimensions
                               temperature | Kelvin | global::System.Single | temperature=1
                               amount64 | Mole64 | global::System.Double | amount=1
                               """;

        GeneratorRunResult result = GeneratorHarness.RunState(out _, out var diagnostics, catalog, source);
        Assert.That(diagnostics, Is.Empty);

        string generated = GeneratorHarness.GeneratedSource(result, "MyMod.Settings.NumosState.g.cs");
        Assert.Multiple(() =>
        {
            Assert.That(generated, Does.Contain("global::Numos.CoreSim.State.Advanced.StateCodecs.Single.Write(ref sink, value.Ignition);"));
            Assert.That(
                generated,
                Does.Contain(
                    "IReadOnlyDictionary<global::Numos.CoreSim.GasProperties, double>, global::Numos.CoreSim.GasProperties, " +
                    "global::Numos.CoreSim.State.Advanced.StateCodecs.Gas, double, global::Numos.CoreSim.State.Advanced.StateCodecs.Double>"));
        });
    }

    [Test]
    public void ReadonlyRecordStruct_ReopensWithoutReadonly()
    {
        const string source = """
                              using Numos.CoreSim.State;

                              namespace MyMod;

                              public readonly partial record struct Outer
                              {
                                  [NumosState]
                                  public readonly partial record struct Inner([property: StateField(1)] int Value);
                              }
                              """;

        GeneratorRunResult result = GeneratorHarness.RunState(out Compilation output, out var diagnostics, null, source);
        Assert.That(diagnostics, Is.Empty);
        Assert.That(GeneratorHarness.Errors(output), Is.Empty);
        Assert.That(
            GeneratorHarness.GeneratedSource(result, "MyMod.Outer+Inner.NumosState.g.cs"),
            Does.Contain("partial record struct Outer\n").And.Contain("partial record struct Inner : global::Numos.CoreSim.State.Advanced.INumosState<global::MyMod.Outer.Inner>"));
    }

    [Test]
    public void Reader_SkipsUnknownIdsAndDefaultsMissingFields()
    {
        GeneratorRunResult result = GeneratorHarness.RunState(out Compilation output, out var diagnostics, null, EveryShapeSource);
        Assert.That(diagnostics, Is.Empty);
        Assert.That(GeneratorHarness.Errors(output), Is.Empty);

        string source = GeneratorHarness.GeneratedSource(result, "MyMod.Point.NumosState.g.cs");
        Assert.Multiple(() =>
        {
            Assert.That(
                source,
                Does.Contain(
                    "static global::MyMod.Point global::Numos.CoreSim.State.Advanced.INumosState<global::MyMod.Point>.ReadState(" +
                    "ref global::Numos.CoreSim.State.Advanced.StateReader reader, global::Numos.CoreSim.IGasRegistry? gases)"));

            Assert.That(source, Does.Contain("field1 = global::Numos.CoreSim.State.Advanced.StateCodecs.Single.Read(ref reader, gases);"));
            Assert.That(source, Does.Match(@"default:\s+reader\.SkipField\(\);\s+continue;"));
            Assert.That(
                source,
                Does.Contain(
                    "return new global::MyMod.Point(has1 ? field1 : " +
                    "global::Numos.CoreSim.State.Advanced.StateCodecs.DefaultValue<float, global::Numos.CoreSim.State.Advanced.StateCodecs.Single>(), "));

            Assert.That(source, Does.Contain("static string? global::Numos.CoreSim.State.Advanced.INumosState<global::MyMod.Point>.StateKey => null;"));
            Assert.That(source, Does.Contain("void global::Numos.CoreSim.State.Advanced.INumosState.WriteState<TSink>(ref TSink sink)"));
        });

        // Mutable fields with no constructor are assigned after construction, and only when the data has them, so a
        // missing one keeps its initializer's value.
        Assert.That(
            GeneratorHarness.GeneratedSource(result, "MyMod.EveryShape.NumosState.g.cs"),
            Does.Match(@"var result = new global::MyMod\.EveryShape\(\);\s+if \(has1\)\s+result\.Bool = field1;"));
    }

    [Test]
    public void ConstructorParameters_GetCopiesOfCollectionsTheyCantTakeDirectly()
    {
        const string source = """
                              using System.Collections.Frozen;
                              using System.Collections.Generic;
                              using System.Collections.Immutable;
                              using System.Linq;
                              using Numos.CoreSim;
                              using Numos.CoreSim.State;

                              namespace MyMod;

                              [NumosState]
                              public sealed partial class Settings(
                                  IEnumerable<float>? rates,
                                  ISet<string> names,
                                  IDictionary<GasProperties, float> amounts,
                                  List<int> order,
                                  ImmutableArray<int> more,
                                  float spread = -0.5f)
                              {
                                  [StateField(1)] public IReadOnlyList<float> Rates { get; } = rates?.ToArray() ?? [];
                                  [StateField(2)] public IReadOnlySet<string> Names { get; } = names.ToFrozenSet();
                                  [StateField(3)] public FrozenDictionary<GasProperties, float> Amounts { get; } = amounts.ToFrozenDictionary();
                                  [StateField(4)] public int[] Order { get; } = order.ToArray();
                                  [StateField(5)] public IReadOnlyList<int> More { get; } = more;
                                  [StateField(6)] public float Spread { get; } = spread;
                              }
                              """;

        GeneratorRunResult result = GeneratorHarness.RunState(out Compilation output, out var diagnostics, null, source);
        Assert.That(diagnostics, Is.Empty);
        Assert.That(GeneratorHarness.Errors(output), Is.Empty, string.Join("\n", GeneratorHarness.Errors(output)));
        Assert.That(output.GetDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Warning), Is.Empty);

        string generated = GeneratorHarness.GeneratedSource(result, "MyMod.Settings.NumosState.g.cs");
        Assert.Multiple(() =>
        {
            Assert.That(generated, Does.Contain("has1 ? field1 : "), "IReadOnlyList already is an IEnumerable");
            Assert.That(generated, Does.Contain("has2 ? new global::System.Collections.Generic.HashSet<string>(field2) : "));
            Assert.That(generated, Does.Contain("has3 ? field3 : "), "FrozenDictionary already is an IDictionary");
            Assert.That(generated, Does.Contain("has4 ? new global::System.Collections.Generic.List<int>(field4) : "));
            Assert.That(generated, Does.Contain("has5 ? global::System.Collections.Immutable.ImmutableArray.CreateRange(field5) : "));
            Assert.That(generated, Does.Contain("has6 ? field6 : (float)(-0.5)"));
        });
    }

    [Test]
    public void QuantityAliasParameters_BindToTheirStoragePrimitive()
    {
        // The reaction types' shape: an alias-valued frozen field filled from a float dictionary parameter.
        const string source = """
                              using System.Collections.Frozen;
                              using System.Collections.Generic;
                              using Numos.CoreSim;
                              using Numos.CoreSim.State;

                              namespace MyMod;

                              [NumosState]
                              public readonly partial record struct Reaction
                              {
                                  public Reaction(IDictionary<GasProperties, float> input, Kelvin ignition)
                                  {
                                      Input = input.ToFrozenDictionary();
                                      Ignition = ignition;
                                  }

                                  [StateField(1)] private FrozenDictionary<GasProperties, Mole> Input { get; }
                                  [StateField(2)] private float Ignition { get; }
                              }
                              """;

        const string catalog = """
                               temperature | Kelvin | global::System.Single | temperature=1
                               amount | Mole | global::System.Single | amount=1
                               """;

        GeneratorRunResult result = GeneratorHarness.RunState(out _, out var diagnostics, catalog, source);
        Assert.That(diagnostics, Is.Empty);
        Assert.That(
            GeneratorHarness.GeneratedSource(result, "MyMod.Reaction.NumosState.g.cs"),
            Does.Contain("return new global::MyMod.Reaction(has1 ? field1 : "));
    }

    [Test]
    public void DeclaredKey_BecomesStateKeyAndSolverConfigurationKey()
    {
        const string source = """
                              using Numos.CoreSim;
                              using Numos.CoreSim.State;

                              namespace MyMod;

                              [NumosState(Key = "my-mod/fire")]
                              public sealed partial class FireSettings : IAtmosSolverConfiguration
                              {
                                  [StateField(1)] public float IgnitionTemperature { get; init; }

                                  public IAtmosSolverConfiguration CreateSnapshot(IGasRegistry gases) => this;
                              }
                              """;

        GeneratorRunResult result = GeneratorHarness.RunState(out Compilation output, out var diagnostics, null, source);
        Assert.That(diagnostics, Is.Empty);
        Assert.That(GeneratorHarness.Errors(output), Is.Empty);

        string generated = GeneratorHarness.GeneratedSource(result, "MyMod.FireSettings.NumosState.g.cs");
        Assert.Multiple(() =>
        {
            Assert.That(generated, Does.Contain(".StateKey => \"my-mod/fire\";"));
            Assert.That(generated, Does.Contain("public string Key => \"my-mod/fire\";"));
        });
    }
}
