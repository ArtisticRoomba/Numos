using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Numos.API.Generators.State;

namespace Numos.API.Generators.Tests;

[TestFixture]
public sealed class StateGeneratorTests
{
    private static string HasLocal(string name) => "has" + FieldIds.Compute(name);

    private static string FieldLocal(string name) => "field" + FieldIds.Compute(name);

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

                                            public readonly partial record struct Point([property: NumosSerialize] float X, [property: NumosSerialize] float Y);

                                            public sealed partial class Leaf
                                            {
                                                [NumosSerialize] public string Name { get; init; } = "";
                                            }

                                            public sealed partial class EveryShape
                                            {
                                                [NumosSerialize] public bool Bool;
                                                [NumosSerialize] public byte Byte;
                                                [NumosSerialize] public sbyte SByte;
                                                [NumosSerialize] public short Short;
                                                [NumosSerialize] public ushort UShort;
                                                [NumosSerialize] public int Int;
                                                [NumosSerialize] public uint UInt;
                                                [NumosSerialize] public long Long;
                                                [NumosSerialize] public ulong ULong;
                                                [NumosSerialize] public float Float;
                                                [NumosSerialize] public double Double;
                                                [NumosSerialize] public Mode Mode;
                                                [NumosSerialize] public string Text = "";
                                                [NumosSerialize] public string? MaybeText;
                                                [NumosSerialize] public Int3 Position;
                                                [NumosSerialize] public GasProperties Gas;
                                                [NumosSerialize] public GasProperties? MaybeGas;
                                                [NumosSerialize] public int? MaybeInt;
                                                [NumosSerialize] public Point Point;
                                                [NumosSerialize] public Leaf? MaybeLeaf;
                                                [NumosSerialize] public float[] Floats = [];
                                                [NumosSerialize] public IReadOnlyList<string?> Names = [];
                                                [NumosSerialize] public ImmutableArray<Point> Points = [];
                                                [NumosSerialize] public IReadOnlyDictionary<GasProperties, float> Amounts = new Dictionary<GasProperties, float>();
                                                [NumosSerialize] public FrozenDictionary<string, Leaf> Leaves = FrozenDictionary<string, Leaf>.Empty;
                                                [NumosSerialize] public Dictionary<Mode, int[]> ByMode = new();
                                                [NumosSerialize] public FrozenSet<Point> PointSet = FrozenSet<Point>.Empty;
                                                [NumosSerialize] public IReadOnlySet<string> NameSet = new HashSet<string>();
                                                [NumosSerialize] public Mode? MaybeMode;
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

                              public partial struct Settings
                              {
                                  [NumosSerialize] public float Last;
                                  [NumosSerialize] public float First;
                                  [NumosSerialize] public float Middle;
                              }
                              """;

        GeneratorRunResult result = GeneratorHarness.RunState(out Compilation output, out var diagnostics, null, source);
        Assert.That(diagnostics, Is.Empty);
        Assert.That(GeneratorHarness.Errors(output), Is.Empty);

        string generated = GeneratorHarness.GeneratedSource(result, "MyMod.Settings.NumosState.g.cs");
        int[] positions = new[] { "Last", "First", "Middle" }
            .OrderBy(FieldIds.Compute)
            .Select(name => generated.IndexOf("sink.BeginField(" + FieldIds.Compute(name) + ");", StringComparison.Ordinal))
            .ToArray();

        Assert.That(positions, Has.All.GreaterThan(0));
        Assert.That(positions, Is.Ordered.Ascending);
    }

    [Test]
    public void FieldIds_AreFnv1aOfTheName()
    {
        Assert.Multiple(() =>
        {
            Assert.That(FieldIds.Compute("a"), Is.EqualTo(0x640c292c));
            Assert.That(FieldIds.Compute("Rate"), Is.EqualTo(783280263));
            Assert.That(FieldIds.Compute("GlobalTemperature"), Is.EqualTo(1407824132));
        });
    }

    [Test]
    public void WireName_KeepsTheOldIdAfterARename()
    {
        const string source = """
                              using Numos.CoreSim.State;

                              namespace MyMod;

                              public partial struct Settings
                              {
                                  [NumosSerialize("Rate")] public float SpreadRate;
                              }
                              """;

        GeneratorRunResult result = GeneratorHarness.RunState(out Compilation output, out var diagnostics, null, source);
        Assert.That(diagnostics, Is.Empty);
        Assert.That(GeneratorHarness.Errors(output), Is.Empty);
        Assert.That(
            GeneratorHarness.GeneratedSource(result, "MyMod.Settings.NumosState.g.cs"),
            Does.Contain("sink.BeginField(783280263);").And.Contain("value.SpreadRate"));
    }

    [Test]
    public void PartialTypeAcrossFiles_GeneratesOnce()
    {
        const string first = """
                             using Numos.CoreSim.State;

                             namespace MyMod;

                             public partial class Settings
                             {
                                 [NumosSerialize] public float Rate;
                             }
                             """;

        const string second = """
                              using Numos.CoreSim.State;

                              namespace MyMod;

                              public partial class Settings
                              {
                                  [NumosSerialize] public float Spread;
                              }
                              """;

        GeneratorRunResult result = GeneratorHarness.RunState(out Compilation output, out var diagnostics, null, first, second);
        Assert.That(diagnostics, Is.Empty);
        Assert.That(GeneratorHarness.Errors(output), Is.Empty);
        Assert.That(result.GeneratedSources, Has.Length.EqualTo(1));
        Assert.That(
            result.GeneratedSources[0].SourceText.ToString(),
            Does.Contain("value.Rate").And.Contain("value.Spread"));
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
                              public sealed partial class FireSettings : IAtmosSolverConfiguration
                              {
                                  /// <inheritdoc />
                                  public string Key => "my-mod/fire";

                                  /// <summary>
                                  ///     Ignition temperature.
                                  /// </summary>
                                  [NumosSerialize] public float IgnitionTemperature { get; init; }
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
            Assert.That(generated, Does.Contain("public global::Numos.CoreSim.IAtmosSolverConfiguration CreateSnapshot("));
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

                              public sealed partial class Settings
                              {
                                  [NumosSerialize] public Kelvin Ignition { get; init; }
                                  [NumosSerialize] public IReadOnlyDictionary<GasProperties, Mole64> Amounts { get; init; } = null!;
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
                                  public readonly partial record struct Inner([property: NumosSerialize] int Value);
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

            Assert.That(source, Does.Contain(FieldLocal("X") + " = global::Numos.CoreSim.State.Advanced.StateCodecs.Single.Read(ref reader, gases);"));
            Assert.That(source, Does.Match(@"default:\s+reader\.SkipField\(\);\s+continue;"));
            Assert.That(
                source,
                Does.Contain(
                    "return new global::MyMod.Point(" + HasLocal("X") + " ? " + FieldLocal("X") + " : " +
                    "global::Numos.CoreSim.State.Advanced.StateCodecs.DefaultValue<float, global::Numos.CoreSim.State.Advanced.StateCodecs.Single>(), "));

            Assert.That(source, Does.Contain("static string? global::Numos.CoreSim.State.Advanced.INumosState<global::MyMod.Point>.StateKey => null;"));
            Assert.That(source, Does.Contain("void global::Numos.CoreSim.State.Advanced.INumosState.WriteState<TSink>(ref TSink sink)"));
        });

        // Mutable fields with no constructor are assigned after construction, and only when the data has them, so a
        // missing one keeps its initializer's value.
        Assert.That(
            GeneratorHarness.GeneratedSource(result, "MyMod.EveryShape.NumosState.g.cs"),
            Does.Match(@"var result = new global::MyMod\.EveryShape\(\);\s+if \(has\d+\)").And.Match(@"if \(" + HasLocal("Bool") + @"\)\s+result\.Bool = " + FieldLocal("Bool") + ";"));
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

                              public sealed partial class Settings(
                                  IEnumerable<float>? rates,
                                  ISet<string> names,
                                  IDictionary<GasProperties, float> amounts,
                                  List<int> order,
                                  ImmutableArray<int> more,
                                  float spread = -0.5f)
                              {
                                  [NumosSerialize] public IReadOnlyList<float> Rates { get; } = rates?.ToArray() ?? [];
                                  [NumosSerialize] public IReadOnlySet<string> Names { get; } = names.ToFrozenSet();
                                  [NumosSerialize] public FrozenDictionary<GasProperties, float> Amounts { get; } = amounts.ToFrozenDictionary();
                                  [NumosSerialize] public int[] Order { get; } = order.ToArray();
                                  [NumosSerialize] public IReadOnlyList<int> More { get; } = more;
                                  [NumosSerialize] public float Spread { get; } = spread;
                              }
                              """;

        GeneratorRunResult result = GeneratorHarness.RunState(out Compilation output, out var diagnostics, null, source);
        Assert.That(diagnostics, Is.Empty);
        Assert.That(GeneratorHarness.Errors(output), Is.Empty, string.Join("\n", GeneratorHarness.Errors(output)));
        Assert.That(output.GetDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Warning), Is.Empty);

        string generated = GeneratorHarness.GeneratedSource(result, "MyMod.Settings.NumosState.g.cs");
        Assert.Multiple(() =>
        {
            Assert.That(generated, Does.Contain(HasLocal("Rates") + " ? " + FieldLocal("Rates") + " : "), "IReadOnlyList already is an IEnumerable");
            Assert.That(generated, Does.Contain(HasLocal("Names") + " ? new global::System.Collections.Generic.HashSet<string>(" + FieldLocal("Names") + ") : "));
            Assert.That(generated, Does.Contain(HasLocal("Amounts") + " ? " + FieldLocal("Amounts") + " : "), "FrozenDictionary already is an IDictionary");
            Assert.That(generated, Does.Contain(HasLocal("Order") + " ? new global::System.Collections.Generic.List<int>(" + FieldLocal("Order") + ") : "));
            Assert.That(generated, Does.Contain(HasLocal("More") + " ? global::System.Collections.Immutable.ImmutableArray.CreateRange(" + FieldLocal("More") + ") : "));
            Assert.That(generated, Does.Contain(HasLocal("Spread") + " ? " + FieldLocal("Spread") + " : (float)(-0.5)"));
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

                              public readonly partial record struct Reaction
                              {
                                  public Reaction(IDictionary<GasProperties, float> input, Kelvin ignition)
                                  {
                                      Input = input.ToFrozenDictionary();
                                      Ignition = ignition;
                                  }

                                  [NumosSerialize] private FrozenDictionary<GasProperties, Mole> Input { get; }
                                  [NumosSerialize] private float Ignition { get; }
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
            Does.Contain("return new global::MyMod.Reaction(" + HasLocal("Input") + " ? " + FieldLocal("Input") + " : "));
    }

    [Test]
    public void SolverConfigurations_AreRegisteredByOneModuleInitializer()
    {
        const string source = """
                              using Numos.CoreSim;
                              using Numos.CoreSim.State;

                              namespace MyMod;

                              public sealed partial class Smoke : IAtmosSolverConfiguration
                              {
                                  public string Key => "my-mod/smoke";
                                  [NumosSerialize] public float Density { get; init; }
                              }

                              internal sealed partial record Fire([property: NumosSerialize] float Ignition) : IAtmosSolverConfiguration
                              {
                                  public string Key => "my-mod/fire";
                              }

                              public partial struct NotAConfiguration
                              {
                                  [NumosSerialize] public float Value;
                              }
                              """;

        GeneratorRunResult result = GeneratorHarness.RunState(out Compilation output, out var diagnostics, null, source);
        Assert.That(diagnostics, Is.Empty);
        Assert.That(GeneratorHarness.Errors(output), Is.Empty);

        string initializer = GeneratorHarness.GeneratedSource(result, StateGenerator.ModuleInitializerHintName);
        Assert.Multiple(() =>
        {
            Assert.That(initializer, Does.Contain("[global::System.Runtime.CompilerServices.ModuleInitializer]"));
            Assert.That(initializer, Does.Contain("\"CA2255\""));
            Assert.That(initializer, Does.Not.Contain("NotAConfiguration"));
            Assert.That(
                initializer.IndexOf("RegisterDefault<global::MyMod.Fire>();", StringComparison.Ordinal),
                Is.LessThan(initializer.IndexOf("RegisterDefault<global::MyMod.Smoke>();", StringComparison.Ordinal))
                    .And.GreaterThan(0));
        });
    }

    [Test]
    public void NoSolverConfigurations_NoModuleInitializer()
    {
        GeneratorRunResult result = GeneratorHarness.RunState(out _, out _, null, EveryShapeSource);
        Assert.That(result.GeneratedSources.Select(static source => source.HintName), Has.None.EqualTo(StateGenerator.ModuleInitializerHintName));
    }

    [TestCase("[NumosSerialize] public float Rate { get; init; }", true)]
    [TestCase("[NumosSerialize] public readonly Numos.Maths.Int3 Size;\npublic Settings(Numos.Maths.Int3 size) => Size = size;", true)]
    [TestCase("[NumosSerialize] public System.Collections.Frozen.FrozenDictionary<GasProperties, float> Amounts { get; init; } = null!;", true)]
    [TestCase("[NumosSerialize] public System.Collections.Immutable.ImmutableArray<Inner> Inners { get; init; }", true)]
    [TestCase("[NumosSerialize] public float Rate { get; set; }", false)]
    [TestCase("[NumosSerialize] public float Rate;", false)]
    [TestCase("[NumosSerialize] public System.Collections.Generic.IReadOnlyList<float> Rates { get; init; } = [];", false)]
    [TestCase("[NumosSerialize] public float[] Rates { get; init; } = [];", false)]
    [TestCase("[NumosSerialize] public System.Collections.Immutable.ImmutableArray<Loose> Loose { get; init; }", false)]
    public void GeneratedSnapshot_IsTheInstanceOnlyWhenProvablyImmutable(string member, bool immutable)
    {
        string source = $$"""
                          using Numos.CoreSim;
                          using Numos.CoreSim.State;

                          namespace MyMod;

                          public readonly partial record struct Inner([property: NumosSerialize] float Value);

                          public partial struct Loose
                          {
                              [NumosSerialize] public float Value;
                          }

                          public sealed partial class Settings : IAtmosSolverConfiguration
                          {
                              public string Key => "my-mod/settings";

                              {{member}}
                          }
                          """;

        GeneratorRunResult result = GeneratorHarness.RunState(out Compilation output, out var diagnostics, null, source);
        Assert.That(diagnostics, Is.Empty);
        Assert.That(GeneratorHarness.Errors(output), Is.Empty);

        string generated = GeneratorHarness.GeneratedSource(result, "MyMod.Settings.NumosState.g.cs");
        Assert.That(generated, Does.Contain("partial void OnSnapshot(global::Numos.CoreSim.IGasRegistry gases);"));
        if (immutable)
            Assert.That(generated, Does.Match(@"OnSnapshot\(gases\);\s+return this;").And.Not.Contain("StateBinarySink"));
        else
            Assert.That(generated, Does.Contain("var snapshot = ").And.Contain("snapshot.OnSnapshot(gases);"));
    }

    [Test]
    public void HandWrittenCreateSnapshot_Wins()
    {
        const string source = """
                              using Numos.CoreSim;
                              using Numos.CoreSim.State;

                              namespace MyMod;

                              public sealed partial class Settings : IAtmosSolverConfiguration
                              {
                                  public string Key => "my-mod/settings";
                                  [NumosSerialize] public float Rate { get; set; }
                                  public IAtmosSolverConfiguration CreateSnapshot(IGasRegistry gases) => new Settings { Rate = Rate };
                              }
                              """;

        GeneratorRunResult result = GeneratorHarness.RunState(out Compilation output, out var diagnostics, null, source);
        Assert.That(diagnostics, Is.Empty);
        Assert.That(GeneratorHarness.Errors(output), Is.Empty);
        Assert.That(
            GeneratorHarness.GeneratedSource(result, "MyMod.Settings.NumosState.g.cs"),
            Does.Not.Contain("CreateSnapshot").And.Not.Contain("OnSnapshot"));
    }

    [Test]
    public void WorldSolver_GetsAnInPlaceReaderAndNoConstructedOne()
    {
        const string source = """
                              using Numos.API;
                              using Numos.CoreSim;
                              using Numos.CoreSim.State;

                              namespace MyMod;

                              public sealed partial class FireSolver : IAtmosWorldSolver
                              {
                                  private readonly System.Collections.Generic.List<int> _scratch = [];

                                  public FireSolver(int seed) => _ticks = seed;

                                  [NumosSerialize] public float IgnitionTemperature = 600f;
                                  [NumosSerialize] private int _ticks;
                                  [NumosSerialize] public GasProperties? Fuel { get; set; }

                                  public void Solve(AtmosWorldSolverContext context) => _ticks++;
                              }
                              """;

        GeneratorRunResult result = GeneratorHarness.RunState(out Compilation output, out var diagnostics, null, source);
        Assert.That(diagnostics, Is.Empty);
        Assert.That(GeneratorHarness.Errors(output), Is.Empty);

        string generated = GeneratorHarness.GeneratedSource(result, "MyMod.FireSolver.NumosState.g.cs");
        Assert.Multiple(() =>
        {
            Assert.That(generated, Does.Contain("partial class FireSolver : global::Numos.CoreSim.State.Advanced.INumosStateTarget"));
            Assert.That(generated, Does.Contain("void global::Numos.CoreSim.State.Advanced.INumosState.WriteState<TSink>(ref TSink sink)"));
            Assert.That(
                generated,
                Does.Contain(
                    "void global::Numos.CoreSim.State.Advanced.INumosStateTarget.ReadStateInto(" +
                    "ref global::Numos.CoreSim.State.Advanced.StateReader reader, global::Numos.CoreSim.IGasRegistry? gases)"));

            Assert.That(generated, Does.Contain("this._ticks);"));
            Assert.That(generated, Does.Match(@"if \(" + HasLocal("_ticks") + @"\)\s+this\._ticks = " + FieldLocal("_ticks") + ";"));
            Assert.That(generated, Does.Not.Contain("INumosState<").And.Not.Contain("new global::MyMod.FireSolver"));
        });

        // Every assignment comes after the read loop, so a payload that fails halfway changes nothing.
        int lastRead = generated.LastIndexOf("reader.EndObject();", StringComparison.Ordinal);
        int firstAssignment = generated.IndexOf("this._ticks = ", StringComparison.Ordinal);
        Assert.That(firstAssignment, Is.GreaterThan(lastRead));
    }
}
