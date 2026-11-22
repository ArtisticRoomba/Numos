using Microsoft.CodeAnalysis;
using Numos.API.Generators.State;

namespace Numos.API.Generators.Tests;

/// <summary>
///     What the state generator writes for a <c>[SimulationComponent]</c> class and for members marked
///     <c>[NumosSerialize(HostEditable = false)]</c>, and what it rejects.
/// </summary>
[TestFixture]
public sealed class SimulationComponentGeneratorTests
{
    private const string Hint = "MyMod.PipeNetwork.NumosState.g.cs";

    private static string Component(string members, string declaration = "public sealed partial class PipeNetwork", string key = "\"my-mod/pipes\"")
    {
        return $$"""
                 using System.Collections.Generic;
                 using Numos.API;
                 using Numos.CoreSim.State;

                 namespace MyMod;

                 [SimulationComponent({{key}})]
                 {{declaration}}
                 {
                     {{members}}
                 }
                 """;
    }

    [Test]
    public void Component_GetsItsDefinitionAnInPlaceReaderAndARegistration()
    {
        string source = Component(
            """
            [NumosSerialize] public int NextId;
            [NumosSerialize] public readonly List<int> Nodes = [];
            [NumosScratch] private readonly List<int> _scratch = [];
            """);

        GeneratorRunResult result = GeneratorHarness.RunState(out Compilation output, out var diagnostics, null, source);
        Assert.That(diagnostics, Is.Empty);
        Assert.That(GeneratorHarness.Errors(output), Is.Empty);
        Assert.That(output.GetDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Warning), Is.Empty);

        string generated = GeneratorHarness.GeneratedSource(result, Hint);
        string registration = GeneratorHarness.GeneratedSource(result, StateGenerator.ComponentModuleInitializerHintName);
        Assert.Multiple(() =>
        {
            Assert.That(
                generated,
                Does.Contain(
                    "partial class PipeNetwork : global::Numos.CoreSim.State.Advanced.INumosStateTarget, " +
                    "global::Numos.API.ISimulationComponentDefinition<global::MyMod.PipeNetwork>"));

            Assert.That(
                generated,
                Does.Contain(
                    "static string global::Numos.API.ISimulationComponentDefinition<global::MyMod.PipeNetwork>.ComponentKey => " +
                    "\"my-mod/pipes\";"));

            Assert.That(
                generated,
                Does.Contain(
                    "static global::MyMod.PipeNetwork global::Numos.API.ISimulationComponentDefinition<global::MyMod.PipeNetwork>" +
                    ".CreateComponent() => new global::MyMod.PipeNetwork();"));

            Assert.That(generated, Does.Contain("this.Nodes.AddRange("));
            Assert.That(generated, Does.Not.Contain("INumosBulkStateTarget"));
            Assert.That(result.GeneratedSources.Select(static file => file.HintName), Does.Contain("MyMod.PipeNetwork.NumosScratch.g.cs"));
            Assert.That(registration, Does.Contain("global::Numos.API.SimulationComponentCatalog.Register<global::MyMod.PipeNetwork>();"));
        });
    }

    [Test]
    public void ComponentWithoutMarkedMembers_StillGetsItsDefinition()
    {
        GeneratorRunResult result = GeneratorHarness.RunState(out Compilation output, out var diagnostics, null, Component(""));

        Assert.That(diagnostics, Is.Empty);
        Assert.That(GeneratorHarness.Errors(output), Is.Empty);
        Assert.That(GeneratorHarness.GeneratedSource(result, Hint), Does.Contain("ISimulationComponentDefinition<global::MyMod.PipeNetwork>"));
    }

    [Test]
    public void MemberThatIsNotHostEditable_IsLeftOutOfTheBoundaryWriter()
    {
        string source = Component(
            """
            [NumosSerialize] public int NextId;
            [NumosSerialize(HostEditable = false)] public readonly List<int> Nodes = [];
            [NumosSerialize(HostEditable = true)] public float Pressure;
            """);

        GeneratorRunResult result = GeneratorHarness.RunState(out Compilation output, out var diagnostics, null, source);
        Assert.That(diagnostics, Is.Empty);
        Assert.That(GeneratorHarness.Errors(output), Is.Empty);

        string generated = GeneratorHarness.GeneratedSource(result, Hint);
        int writer = generated.IndexOf(".WriteHostEditableState<TSink>(ref TSink sink)", StringComparison.Ordinal);
        int hashes = generated.IndexOf(".HashBulkMembers(global::System.Span<ulong> hashes)", StringComparison.Ordinal);
        string boundary = generated[writer..hashes];
        Assert.Multiple(() =>
        {
            Assert.That(generated, Does.Contain(", global::Numos.CoreSim.State.Advanced.INumosBulkStateTarget"));
            Assert.That(generated, Does.Contain("private static readonly string[] __numosBulkMemberNames = { \"Nodes\" };"));
            Assert.That(boundary, Does.Contain("this.NextId").And.Contain("this.Pressure").And.Not.Contain("this.Nodes"));
            Assert.That(generated[hashes..], Does.Contain("this.Nodes);").And.Contain("hashes[0] = sink.Value;"));
        });
    }

    [Test]
    public void ClassSolver_CanMarkAMemberThatIsNotHostEditable()
    {
        const string source = """
                              using System.Collections.Generic;
                              using Numos.API;
                              using Numos.CoreSim.State;

                              namespace MyMod;

                              public sealed partial class FireSolver : IAtmosWorldSolver
                              {
                                  [NumosSerialize(HostEditable = false)] public readonly List<int> Burning = [];

                                  public void Solve(AtmosWorldSolverContext context) => Burning.Add(1);
                              }
                              """;

        GeneratorRunResult result = GeneratorHarness.RunState(out Compilation output, out var diagnostics, null, source);
        Assert.That(diagnostics, Is.Empty);
        Assert.That(GeneratorHarness.Errors(output), Is.Empty);
        Assert.That(GeneratorHarness.GeneratedSource(result, "MyMod.FireSolver.NumosState.g.cs"), Does.Contain("INumosBulkStateTarget"));
    }

    [Test]
    public void HostEditableOutsideStateRestoredInPlace_ReportsNumosState041()
    {
        const string source = """
                              using Numos.CoreSim.State;

                              namespace MyMod;

                              public sealed partial class Entry
                              {
                                  [NumosSerialize(HostEditable = false)] public int Value { get; set; }
                              }
                              """;

        GeneratorRunResult result = GeneratorHarness.RunState(out Compilation output, out var diagnostics, null, source);
        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSTATE041" }));
        Assert.That(GeneratorHarness.Errors(output), Is.Empty);
        Assert.That(GeneratorHarness.GeneratedSource(result, "MyMod.Entry.NumosState.g.cs"), Does.Not.Contain("INumosBulkStateTarget"));
    }

    [TestCase("public abstract partial class PipeNetwork", "is abstract")]
    [TestCase("public sealed partial record PipeNetwork", "must be a class and not a record")]
    [TestCase("public sealed partial class PipeNetwork(int seed)", "has no public or internal parameterless constructor")]
    [TestCase("public sealed partial class PipeNetwork : IAtmosWorldSolver", "also implements IAtmosWorldSolver")]
    public void ComponentOfTheWrongShape_ReportsNumosState039(string declaration, string reason)
    {
        string members = declaration.Contains("IAtmosWorldSolver", StringComparison.Ordinal)
            ? "public void Solve(AtmosWorldSolverContext context) { }"
            : "";

        GeneratorRunResult result = GeneratorHarness.RunState(out _, out var diagnostics, null, Component(members, declaration));

        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSTATE039" }));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain(reason));
        Assert.That(result.GeneratedSources, Is.Empty);
    }

    [Test]
    public void PrivateNestedComponent_ReportsNumosState039()
    {
        const string source = """
                              using Numos.API;

                              namespace MyMod;

                              public static partial class Outer
                              {
                                  [SimulationComponent("my-mod/pipes")]
                                  private sealed partial class PipeNetwork;
                              }
                              """;

        GeneratorHarness.RunState(out _, out var diagnostics, null, source);

        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSTATE039" }));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain("module initializer can't register it"));
    }

    [Test]
    public void ComponentThatIsNotPartial_ReportsNumosState001()
    {
        GeneratorHarness.RunState(out _, out var diagnostics, null, Component("", "public sealed class PipeNetwork"));

        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSTATE001" }));
    }

    [TestCase("\"\"")]
    [TestCase("\"  \"")]
    public void BlankComponentKey_ReportsNumosState040(string key)
    {
        GeneratorRunResult result = GeneratorHarness.RunState(out _, out var diagnostics, null, Component("", key: key));

        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSSTATE040" }));
        Assert.That(result.GeneratedSources, Is.Empty);
    }

    [Test]
    public void UnmarkedMembersOfAComponent_ReportNumosState016AndReadonlyScalarsNumosState020()
    {
        string source = Component(
            """
            public int Loose;
            private readonly List<int> _hidden = [];
            [NumosSerialize] public readonly int Fixed;
            """);

        GeneratorHarness.RunState(out _, out var diagnostics, null, source);

        Assert.That(
            GeneratorHarness.Ids(diagnostics.OrderBy(static d => d.Location.SourceSpan.Start)),
            Is.EqualTo(new[] { "NUMOSSTATE016", "NUMOSSTATE016", "NUMOSSTATE020" }));

        Assert.That(diagnostics.First(static d => d.Id == "NUMOSSTATE016").GetMessage(), Does.Contain("[NumosScratch]"));
    }
}
