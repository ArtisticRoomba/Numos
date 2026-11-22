using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Numos.API.Generators.State;

namespace Numos.API.Generators.Tests;

/// <summary>
///     Class solvers and simulation components that keep part of their state in a base class.
/// </summary>
[TestFixture]
public sealed class StateInheritanceGeneratorTests
{
    private const string Prelude = """
                                   using System.Collections.Generic;
                                   using Numos.API;
                                   using Numos.CoreSim.State;

                                   namespace MyMod;

                                   """;

    private const string Target = "global::Numos.CoreSim.State.Advanced.INumosStateTarget";

    private static readonly Regex FingerprintPattern = new(@"__numosSchemaFingerprint = 0x([0-9A-F]{16})UL;");

    private static GeneratorRunResult Run(
        out Compilation output, out ImmutableArray<Diagnostic> diagnostics, params string[] sources)
    {
        return GeneratorHarness.RunState(out output, out diagnostics, null, sources.Select(static source => Prelude + source).ToArray());
    }

    private static string Source(GeneratorRunResult result, string type, string kind = "NumosState")
    {
        return GeneratorHarness.GeneratedSource(result, "MyMod." + type + "." + kind + ".g.cs");
    }

    private static ulong Fingerprint(string generated)
    {
        Match match = FingerprintPattern.Match(generated);
        Assert.That(match.Success, Is.True, "The generated type has no fingerprint constant.");
        return ulong.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    private const string PlainBase = """
                                     public abstract partial class FireBase
                                     {
                                         [NumosSerialize] private int _ignitions;
                                         [NumosScratch] private int _cache = 3;
                                     }
                                     """;

    private const string DerivedSolver = """
                                         public sealed partial class FireSolver : FireBase, IAtmosWorldSolver
                                         {
                                             [NumosSerialize] public float Threshold;

                                             public void Solve(AtmosWorldSolverContext context) { }
                                         }
                                         """;

    [Test]
    public void PlainBase_GetsTheHooksAndTheSolverOverridesThem()
    {
        GeneratorRunResult result = Run(out Compilation output, out var diagnostics, PlainBase, DerivedSolver);
        string baseState = Source(result, "FireBase");
        string baseScratch = Source(result, "FireBase", "NumosScratch");
        string derived = Source(result, "FireSolver");
        string derivedScratch = Source(result, "FireSolver", "NumosScratch");

        Assert.Multiple(() =>
        {
            Assert.That(diagnostics, Is.Empty);
            Assert.That(GeneratorHarness.Errors(output), Is.Empty);
            Assert.That(baseState, Does.Contain("partial class FireBase\n"));
            Assert.That(baseState, Does.Contain("protected virtual void WriteNumosState<TSink>("));
            Assert.That(baseState, Does.Contain("protected virtual bool ReadNumosStateField("));
            Assert.That(baseScratch, Does.Contain("protected virtual void ResetNumosScratch()"));
            Assert.That(derived, Does.Contain("partial class FireSolver : " + Target));
            Assert.That(derived, Does.Contain("protected override void WriteNumosState<TSink>("));
            Assert.That(derived, Does.Contain("base.WriteNumosState(ref sink, afterId, "));
            Assert.That(derived, Does.Contain("return base.ReadNumosStateField(ref reader, id, gases, ref state.Base);"));
            Assert.That(derived, Does.Contain("base.ApplyNumosState(state.Base);"));
            Assert.That(derivedScratch, Does.Contain("ResetScratch() => this.ResetNumosScratch();"));
        });
    }

    [Test]
    public void SolverWithoutMarksOfItsOwn_ImplementsTheInterfacesForAPlainBase()
    {
        const string solver = """
                              public sealed partial class FireSolver : FireBase, IAtmosWorldSolver
                              {
                                  public void Solve(AtmosWorldSolverContext context) { }
                              }
                              """;

        GeneratorRunResult result = Run(out Compilation output, out var diagnostics, PlainBase, solver);
        string derived = Source(result, "FireSolver");

        Assert.Multiple(() =>
        {
            Assert.That(diagnostics, Is.Empty);
            Assert.That(GeneratorHarness.Errors(output), Is.Empty);
            Assert.That(derived, Does.Contain("partial class FireSolver : " + Target));
            Assert.That(derived, Does.Not.Contain("protected override"));
            Assert.That(Source(result, "FireSolver", "NumosScratch"), Does.Contain("ResetScratch() => this.ResetNumosScratch();"));
        });
    }

    [Test]
    public void SolverBase_EmitsItsOwnCodeAndTheDerivedSolverOnlyOverrides()
    {
        const string sources = """
                               public partial class BurnerBase : IAtmosWorldSolver
                               {
                                   [NumosSerialize] public int Heats;

                                   public void Solve(AtmosWorldSolverContext context) { }
                               }

                               public sealed partial class Burner : BurnerBase
                               {
                                   [NumosSerialize] public float Rate;
                               }

                               public sealed partial class QuietBurner : BurnerBase
                               {
                               }
                               """;

        GeneratorRunResult result = Run(out Compilation output, out var diagnostics, sources);
        string baseState = Source(result, "BurnerBase");
        string derived = Source(result, "Burner");

        Assert.Multiple(() =>
        {
            Assert.That(diagnostics, Is.Empty);
            Assert.That(GeneratorHarness.Errors(output), Is.Empty);
            Assert.That(baseState, Does.Contain("partial class BurnerBase : " + Target));
            Assert.That(baseState, Does.Contain("protected const string __numosSchemaFields = \"Heats\\0Int32\\0\";"));
            Assert.That(derived, Does.Not.Contain(Target + ","));
            Assert.That(derived, Does.Contain("global::Numos.CoreSim.State.INumosStateDescribed"));
            Assert.That(derived, Does.Contain("protected override void WriteNumosState<TSink>("));
            Assert.That(derived, Does.Not.Contain("__numosSchemaFields"), "a sealed class publishes no schema entries");
            Assert.That(
                result.GeneratedSources.Select(static source => source.HintName),
                Has.None.Contains("QuietBurner"),
                "a derived solver that adds nothing inherits every generated member");
        });
    }

    [Test]
    public void OpenDerivedClass_HidesTheBaseSchemaConstant()
    {
        const string sources = """
                               public partial class BurnerBase : IAtmosWorldSolver
                               {
                                   [NumosSerialize] public int Heats;

                                   public void Solve(AtmosWorldSolverContext context) { }
                               }

                               public partial class Burner : BurnerBase
                               {
                                   [NumosSerialize] public float Rate;
                               }
                               """;

        GeneratorRunResult result = Run(out Compilation output, out var diagnostics, sources);
        string derived = Source(result, "Burner");

        Assert.Multiple(() =>
        {
            Assert.That(diagnostics, Is.Empty);
            Assert.That(GeneratorHarness.Errors(output), Is.Empty);
            Assert.That(derived, Does.Contain("protected new const string __numosSchemaFields = "));
        });
    }

    [Test]
    public void BaseWithoutMarkedMembers_NeedsNoPartialAndReportsItsUnmarkedFields()
    {
        const string sources = """
                               public abstract class SolverBase
                               {
                                   protected int Counter;

                                   public abstract float Rate { get; }
                               }

                               public sealed partial class FireSolver : SolverBase, IAtmosWorldSolver
                               {
                                   [NumosSerialize] public float Heat;

                                   public override float Rate => Heat;

                                   public void Solve(AtmosWorldSolverContext context) => Counter++;
                               }
                               """;

        GeneratorRunResult result = Run(out Compilation output, out var diagnostics, sources);

        Assert.Multiple(() =>
        {
            Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSTATE016" }));
            Assert.That(diagnostics[0].GetMessage(), Does.Contain("'MyMod.SolverBase.Counter'"));
            Assert.That(GeneratorHarness.Errors(output), Is.Empty);
            Assert.That(Source(result, "FireSolver"), Does.Not.Contain("WriteNumosState"), "nothing to chain, so it stays inline");
        });
    }

    [Test]
    public void SharedPlainBase_IsEmittedAndReportedOnce()
    {
        const string sources = """
                               public abstract partial class SharedBase
                               {
                                   [NumosSerialize] public int Shared;
                                   public int Forgotten;
                               }

                               public sealed partial class First : SharedBase, IAtmosWorldSolver
                               {
                                   public void Solve(AtmosWorldSolverContext context) { }
                               }

                               public sealed partial class Second : SharedBase, IAtmosWorldSolver
                               {
                                   [NumosSerialize] public int Own;

                                   public void Solve(AtmosWorldSolverContext context) { }
                               }
                               """;

        GeneratorRunResult result = Run(out Compilation output, out var diagnostics, sources);

        Assert.Multiple(() =>
        {
            Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSTATE016" }));
            Assert.That(GeneratorHarness.Errors(output), Is.Empty);
            Assert.That(
                result.GeneratedSources.Count(static source => source.HintName == "MyMod.SharedBase.NumosState.g.cs"),
                Is.EqualTo(1));
        });
    }

    [Test]
    public void PlainBaseThatIsNotPartial_ReportsNumosState001()
    {
        const string sources = """
                               public abstract class FireBase
                               {
                                   [NumosSerialize] public int Ignitions;
                               }

                               public sealed partial class FireSolver : FireBase, IAtmosWorldSolver
                               {
                                   [NumosSerialize] public float Threshold;

                                   public void Solve(AtmosWorldSolverContext context) { }
                               }
                               """;

        GeneratorRunResult result = Run(out _, out var diagnostics, sources);

        Assert.Multiple(() =>
        {
            Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSTATE001" }));
            Assert.That(diagnostics[0].GetMessage(), Does.Contain("Type 'MyMod.FireBase' must be partial"));
            Assert.That(result.GeneratedSources, Is.Empty);
        });
    }

    [Test]
    public void IdCollisionAcrossTheChain_ReportsNumosState005OnTheDerivedMember()
    {
        const string solver = """
                              public sealed partial class FireSolver : FireBase, IAtmosWorldSolver
                              {
                                  [NumosSerialize("_ignitions")] public float Warmth;

                                  public void Solve(AtmosWorldSolverContext context) { }
                              }
                              """;

        GeneratorRunResult result = Run(out _, out var diagnostics, PlainBase, solver);

        Assert.Multiple(() =>
        {
            Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSTATE005" }));
            Assert.That(
                diagnostics[0].GetMessage(),
                Does.Contain("'MyMod.FireSolver.Warmth'").And.Contain("'MyMod.FireBase._ignitions'"));

            Assert.That(
                result.GeneratedSources.Select(static source => source.HintName),
                Has.None.Contains("FireSolver"));
        });
    }

    [Test]
    public void DataTypeWithABaseClass_StillReportsNumosState013()
    {
        const string sources = """
                               public abstract partial class SettingsBase
                               {
                                   [NumosSerialize] public float Low { get; init; }
                               }

                               public sealed partial class Settings : SettingsBase
                               {
                                   [NumosSerialize] public float High { get; init; }
                               }
                               """;

        Run(out _, out var diagnostics, sources);

        Assert.That(GeneratorHarness.Ids(diagnostics), Does.Contain("NUMOSSTATE013"));
        Assert.That(
            diagnostics.Single(static diagnostic => diagnostic.Id == "NUMOSSTATE013").GetMessage(),
            Does.Contain("only a class solver or a [SimulationComponent] class can derive from a class"));
    }

    [Test]
    public void ComponentWithABaseClass_GetsItsDefinitionAndTheChain()
    {
        const string sources = """
                               public abstract partial class NetworkBase
                               {
                                   [NumosSerialize] public int NextId;
                               }

                               [SimulationComponent("my-mod/pipes")]
                               public sealed partial class PipeNetwork : NetworkBase
                               {
                                   [NumosSerialize] public readonly List<int> Nodes = [];
                               }
                               """;

        GeneratorRunResult result = Run(out Compilation output, out var diagnostics, sources);
        string component = Source(result, "PipeNetwork");

        Assert.Multiple(() =>
        {
            Assert.That(diagnostics, Is.Empty);
            Assert.That(GeneratorHarness.Errors(output), Is.Empty);
            Assert.That(component, Does.Contain("global::Numos.API.ISimulationComponentDefinition<global::MyMod.PipeNetwork>"));
            Assert.That(component, Does.Contain("partial class PipeNetwork : " + Target));
            Assert.That(component, Does.Contain("protected override void WriteNumosState<TSink>("));
            Assert.That(Source(result, "NetworkBase"), Does.Contain("protected virtual void WriteNumosState<TSink>("));
            Assert.That(
                GeneratorHarness.GeneratedSource(result, StateGenerator.ComponentModuleInitializerHintName),
                Does.Contain("Register<global::MyMod.PipeNetwork>()").And.Not.Contain("NetworkBase"));
        });
    }

    [Test]
    public void DerivedFingerprint_CoversBaseMembersAsOneWireObject()
    {
        static ulong DerivedFingerprint(string baseMember)
        {
            string sources = $$"""
                               public abstract partial class FireBase
                               {
                                   {{baseMember}}
                               }

                               {{DerivedSolver}}
                               """;

            GeneratorRunResult result = Run(out _, out var diagnostics, sources);
            Assert.That(diagnostics, Is.Empty);
            return Fingerprint(Source(result, "FireSolver"));
        }

        const string flat = """
                            public sealed partial class FlatSolver : IAtmosWorldSolver
                            {
                                [NumosSerialize] public int Ignitions;
                                [NumosSerialize] public float Threshold;

                                public void Solve(AtmosWorldSolverContext context) { }
                            }
                            """;

        GeneratorRunResult flatResult = Run(out _, out _, flat);
        ulong original = DerivedFingerprint("[NumosSerialize] public int Ignitions;");

        Assert.Multiple(() =>
        {
            Assert.That(original, Is.EqualTo(Fingerprint(Source(flatResult, "FlatSolver"))), "the fields of the chain share one object");
            Assert.That(DerivedFingerprint("[NumosSerialize] public long Ignitions;"), Is.Not.EqualTo(original));
            Assert.That(DerivedFingerprint("[NumosSerialize] public int Sparks;"), Is.Not.EqualTo(original));
            Assert.That(DerivedFingerprint("[NumosSerialize(\"Ignitions\")] public int Count;"), Is.EqualTo(original));
        });
    }

    [Test]
    public void DescribeHook_AddsBaseMembersFirst()
    {
        GeneratorRunResult result = Run(out _, out _, PlainBase, DerivedSolver);
        string derived = Source(result, "FireSolver");
        int baseCall = derived.IndexOf("base.DescribeNumosState(members, bulkMemberNames);", StringComparison.Ordinal);
        int ownMember = derived.IndexOf("\"Threshold\",", StringComparison.Ordinal);

        Assert.That(baseCall, Is.GreaterThan(0).And.LessThan(ownMember));
    }

    private const string LibrarySource = """
                                         using Numos.API;
                                         using Numos.CoreSim.State;

                                         namespace Library;

                                         public abstract partial class LibrarySolver : IAtmosWorldSolver
                                         {
                                             [NumosSerialize] private float _heat;
                                             [NumosScratch] private int _cache;

                                             public void Solve(AtmosWorldSolverContext context) { }
                                         }

                                         public class Untracked
                                         {
                                             public int Value;
                                         }
                                         """;

    private static GeneratorRunResult RunAgainstLibrary(
        string source, out Compilation output, out ImmutableArray<Diagnostic> diagnostics)
    {
        CSharpParseOptions parseOptions = GeneratorHarness.ParseOptions();
        Compilation library = GeneratorHarness.CreateCompilation(parseOptions, LibrarySource);
        CSharpGeneratorDriver.Create([new StateGenerator().AsSourceGenerator()], parseOptions: parseOptions)
            .RunGeneratorsAndUpdateCompilation(library, out library, out var libraryDiagnostics);

        Assert.That(libraryDiagnostics, Is.Empty);
        MetadataReference reference = RoslynTestSupport.EmitToReference(library);
        CSharpCompilation compilation = GeneratorHarness.CreateCompilation(parseOptions, [reference], Prelude + source);
        return CSharpGeneratorDriver.Create([new StateGenerator().AsSourceGenerator()], parseOptions: parseOptions)
            .RunGeneratorsAndUpdateCompilation(compilation, out output, out diagnostics)
            .GetRunResult().Results[0];
    }

    [Test]
    public void BaseInAnotherAssemblyWithoutGeneratedState_ReportsNumosState013()
    {
        const string source = """
                              public sealed partial class FireSolver : Library.Untracked, IAtmosWorldSolver
                              {
                                  [NumosSerialize] public float Threshold;

                                  public void Solve(AtmosWorldSolverContext context) { }
                              }
                              """;

        GeneratorRunResult result = RunAgainstLibrary(source, out _, out var diagnostics);

        Assert.Multiple(() =>
        {
            Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSTATE013" }));
            Assert.That(
                diagnostics[0].GetMessage(),
                Does.Contain("'MyMod.FireSolver' derives from 'Library.Untracked'").And.Contain("in another assembly"));

            Assert.That(result.GeneratedSources, Is.Empty);
        });
    }

    [Test]
    public void SolverWithoutMarksDerivedFromAnUntrackedForeignBase_ReportsNothingNew()
    {
        const string source = """
                              public sealed class QuietSolver : Library.Untracked, IAtmosWorldSolver
                              {
                                  public void Solve(AtmosWorldSolverContext context) { }
                              }
                              """;

        GeneratorRunResult result = RunAgainstLibrary(source, out _, out var diagnostics);

        Assert.Multiple(() =>
        {
            Assert.That(diagnostics, Is.Empty);
            Assert.That(result.GeneratedSources, Is.Empty);
        });
    }

    [Test]
    public void BaseInAnotherAssemblyWithGeneratedState_ChainsThroughItsHooks()
    {
        const string source = """
                              public sealed partial class FireSolver : Library.LibrarySolver
                              {
                                  [NumosSerialize] public float Threshold;
                                  [NumosScratch] private int _seen;
                              }
                              """;

        GeneratorRunResult result = RunAgainstLibrary(source, out Compilation output, out var diagnostics);
        string derived = Source(result, "FireSolver");

        Assert.Multiple(() =>
        {
            Assert.That(diagnostics, Is.Empty);
            Assert.That(GeneratorHarness.Errors(output), Is.Empty);
            Assert.That(derived, Does.Not.Contain(Target + ","));
            Assert.That(derived, Does.Contain("protected override void WriteNumosState<TSink>("));
            Assert.That(Source(result, "FireSolver", "NumosScratch"), Does.Contain("protected override void ResetNumosScratch()"));
            Assert.That(Fingerprint(derived), Is.EqualTo(UnionFingerprint(("_heat", "Single"), ("Threshold", "Single"))));
        });

        // The library's private field is out of sight, but its published schema entries still claim the id.
        const string clashing = """
                                public sealed partial class FireSolver : Library.LibrarySolver
                                {
                                    [NumosSerialize("_heat")] public float Warmth;
                                }
                                """;

        RunAgainstLibrary(clashing, out _, out diagnostics);
        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSTATE005" }));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain("'Library.LibrarySolver._heat'"));
    }

    /// <summary>
    ///     Computes the documented fingerprint of one wire object with these fields.
    /// </summary>
    private static ulong UnionFingerprint(params (string WireName, string Shape)[] fields)
    {
        ulong hash = 14695981039346656037;
        foreach ((string wireName, string shape) in fields.OrderBy(static field => FieldIds.Compute(field.WireName)))
        {
            foreach (byte value in Encoding.UTF8.GetBytes(wireName + "\0" + shape + "\0"))
            {
                hash ^= value;
                hash *= 1099511628211;
            }
        }

        return hash;
    }
}
