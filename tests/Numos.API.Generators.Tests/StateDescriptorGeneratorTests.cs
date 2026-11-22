using Microsoft.CodeAnalysis;

namespace Numos.API.Generators.Tests;

/// <summary>
///     The <c>INumosStateDescribed</c> member list the state generator writes for types restored in place.
/// </summary>
[TestFixture]
public sealed class StateDescriptorGeneratorTests
{
    private const string Hint = "MyMod.FireSolver.NumosState.g.cs";
    private const string Constructor = "new global::Numos.CoreSim.State.AtmosStateMember(";

    private const string EveryKindSource = """
                                           using System.Collections.Generic;
                                           using Numos.API;
                                           using Numos.CoreSim;
                                           using Numos.CoreSim.State;
                                           using Numos.Maths;

                                           namespace MyMod;

                                           public enum Mode : byte
                                           {
                                               Off,
                                               On,
                                           }

                                           public sealed partial class Leaf
                                           {
                                               [NumosSerialize] public string Name { get; init; } = "";
                                           }

                                           public sealed partial class FireSolver : IAtmosWorldSolver
                                           {
                                               [NumosSerialize] public bool Bool;
                                               [NumosSerialize] public byte Byte;
                                               [NumosSerialize] public int Int;
                                               [NumosSerialize] public ulong ULong;
                                               [NumosSerialize] public float Float;
                                               [NumosSerialize] public double Double;
                                               [NumosSerialize] public Mode Mode;
                                               [NumosSerialize] public string Text = "";
                                               [NumosSerialize] public string? MaybeText;
                                               [NumosSerialize] public int? MaybeInt;
                                               [NumosSerialize] public Mode? MaybeMode;
                                               [NumosSerialize] private float _private;
                                               [NumosSerialize] public float PrivateSet { get; private set; }
                                               [NumosSerialize("Wire")] public float Renamed;
                                               [NumosSerialize] public Int3 Position;
                                               [NumosSerialize] public GasProperties? Fuel;
                                               [NumosSerialize] public Leaf? Nested;
                                               [NumosSerialize] public readonly List<string?> Names = [];
                                               [NumosSerialize] public float[] Weights = [];
                                               [NumosSerialize] public Dictionary<string, int> Counts = new();
                                               [NumosSerialize(HostEditable = false)] public int Bulk;

                                               public void Solve(AtmosWorldSolverContext context) => _private++;
                                           }
                                           """;

    /// <summary>
    ///     Splits the generated list into one constructor call per member, keyed by the member name.
    /// </summary>
    private static Dictionary<string, string> Descriptors(string generated)
    {
        var descriptors = new Dictionary<string, string>(StringComparer.Ordinal);
        string[] parts = generated.Split(Constructor);
        foreach (string part in parts.Skip(1))
        {
            int end = part.IndexOf("});", StringComparison.Ordinal);
            string call = (end < 0 ? part : part[..end]).TrimEnd().TrimEnd(',');
            string name = call.Split('"')[1];
            descriptors.Add(name, call);
        }

        return descriptors;
    }

    [Test]
    public void EveryMemberKind_GetsADescriptorThatCompilesWithoutWarnings()
    {
        GeneratorRunResult result = GeneratorHarness.RunState(out Compilation output, out var diagnostics, null, EveryKindSource);
        Assert.That(diagnostics, Is.Empty);
        Assert.That(GeneratorHarness.Errors(output), Is.Empty);
        Assert.That(output.GetDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Warning), Is.Empty);

        string generated = GeneratorHarness.GeneratedSource(result, Hint);
        var descriptors = Descriptors(generated);
        string[] writable =
        [
            "Bool", "Byte", "Int", "ULong", "Float", "Double", "Mode", "Text", "MaybeText", "MaybeInt", "MaybeMode", "_private",
            "PrivateSet", "Renamed",
        ];

        string[] readOnly = ["Position", "Fuel", "Nested", "Names", "Weights", "Counts", "Bulk"];
        Assert.Multiple(() =>
        {
            Assert.That(generated, Does.Contain(", global::Numos.CoreSim.State.INumosStateDescribed"));
            Assert.That(
                generated,
                Does.Contain(
                    "global::System.Collections.Generic.IReadOnlyList<global::Numos.CoreSim.State.AtmosStateMember> " +
                    "global::Numos.CoreSim.State.INumosStateDescribed.StateMembers =>"));

            Assert.That(descriptors.Keys, Is.EquivalentTo(writable.Concat(readOnly)));
            foreach (string name in writable)
                Assert.That(descriptors[name], Does.Contain("static (target, value) => ((global::MyMod.FireSolver)target)."), name);

            foreach (string name in readOnly)
                Assert.That(descriptors[name], Does.EndWith("null)"), name);

            Assert.That(descriptors["Float"], Does.Contain("typeof(float)").And.Contain("= (float)(value ?? throw"));
            Assert.That(descriptors["Mode"], Does.Contain("typeof(global::MyMod.Mode)"));
            Assert.That(descriptors["MaybeText"], Does.Contain("typeof(string)").And.Contain("= (string?)value"));
            Assert.That(descriptors["MaybeInt"], Does.Contain("typeof(int?)").And.Contain("= (int?)value"));
            Assert.That(descriptors["MaybeMode"], Does.Contain("= (global::MyMod.Mode?)value"));
            Assert.That(descriptors["Renamed"], Does.Contain("\"Renamed\",").And.Contain("\"Wire\","));
            Assert.That(descriptors["Names"], Does.Contain("typeof(global::System.Collections.Generic.List<string>)"));
            Assert.That(descriptors["Fuel"], Does.Contain("typeof(global::Numos.CoreSim.GasProperties?)"));
            Assert.That(descriptors["Nested"], Does.Contain("typeof(global::MyMod.Leaf)"));
            Assert.That(descriptors["_private"], Does.Contain("static target => ((global::MyMod.FireSolver)target)._private,"));
        });
    }

    [Test]
    public void Descriptors_FollowDeclarationOrderNotIdOrder()
    {
        const string source = """
                              using Numos.API;
                              using Numos.CoreSim.State;

                              namespace MyMod;

                              public sealed partial class FireSolver : IAtmosWorldSolver
                              {
                                  [NumosSerialize] public float Last;
                                  [NumosSerialize] public float First;
                                  [NumosSerialize] public float Middle;

                                  public void Solve(AtmosWorldSolverContext context) { }
                              }
                              """;

        GeneratorRunResult result = GeneratorHarness.RunState(out Compilation output, out var diagnostics, null, source);
        Assert.That(diagnostics, Is.Empty);
        Assert.That(GeneratorHarness.Errors(output), Is.Empty);
        Assert.That(Descriptors(GeneratorHarness.GeneratedSource(result, Hint)).Keys, Is.EqualTo(new[] { "Last", "First", "Middle" }));
    }

    [Test]
    public void QuantityAlias_KeepsItsNameAsTheUnit()
    {
        // Without the Magnitudo generator the alias stays an error type, as it does inside Numos.CoreSim.
        const string source = """
                              using Numos.API;
                              using Numos.CoreSim.State;

                              namespace MyMod;

                              public sealed partial class FireSolver : IAtmosWorldSolver
                              {
                                  [NumosSerialize] public Kelvin Ignition;
                                  [NumosSerialize] public float Plain;

                                  public void Solve(AtmosWorldSolverContext context) { }
                              }
                              """;

        const string catalog = "temperature | Kelvin | global::System.Single | temperature=1";
        GeneratorRunResult result = GeneratorHarness.RunState(out _, out var diagnostics, catalog, source);
        Assert.That(diagnostics, Is.Empty);

        var descriptors = Descriptors(GeneratorHarness.GeneratedSource(result, Hint));
        Assert.Multiple(() =>
        {
            Assert.That(descriptors["Ignition"], Does.Contain("typeof(float),").And.Contain("\"Kelvin\","));
            Assert.That(descriptors["Ignition"], Does.Contain("= (float)(value ?? throw"));
            Assert.That(descriptors["Plain"], Does.Contain("typeof(float),").And.Not.Contain("Kelvin"));
        });
    }

    [Test]
    public void Component_GetsDescriptorsAndOneWithoutMarkedMembersGetsNone()
    {
        const string marked = """
                              using System.Collections.Generic;
                              using Numos.API;
                              using Numos.CoreSim.State;

                              namespace MyMod;

                              [SimulationComponent("my-mod/pipes")]
                              public sealed partial class PipeNetwork
                              {
                                  [NumosSerialize] public int NextId;
                                  [NumosSerialize(HostEditable = false)] public readonly List<int> Nodes = [];
                              }

                              [SimulationComponent("my-mod/empty")]
                              public sealed partial class Empty
                              {
                              }
                              """;

        GeneratorRunResult result = GeneratorHarness.RunState(out Compilation output, out var diagnostics, null, marked);
        Assert.That(diagnostics, Is.Empty);
        Assert.That(GeneratorHarness.Errors(output), Is.Empty);

        string pipes = GeneratorHarness.GeneratedSource(result, "MyMod.PipeNetwork.NumosState.g.cs");
        var descriptors = Descriptors(pipes);
        Assert.Multiple(() =>
        {
            Assert.That(pipes, Does.Contain(", global::Numos.CoreSim.State.INumosStateDescribed"));
            Assert.That(descriptors["NextId"], Does.Contain("static (target, value) =>"));
            Assert.That(descriptors["Nodes"], Does.EndWith("null)"));
            Assert.That(
                GeneratorHarness.GeneratedSource(result, "MyMod.Empty.NumosState.g.cs"),
                Does.Not.Contain("INumosStateDescribed"));
        });
    }

    [Test]
    public void StateBuiltFromData_GetsNoDescriptors()
    {
        const string source = """
                              using Numos.CoreSim.State;

                              namespace MyMod;

                              public partial struct Settings
                              {
                                  [NumosSerialize] public float Rate;
                              }
                              """;

        GeneratorRunResult result = GeneratorHarness.RunState(out Compilation output, out var diagnostics, null, source);
        Assert.That(diagnostics, Is.Empty);
        Assert.That(GeneratorHarness.Errors(output), Is.Empty);
        Assert.That(GeneratorHarness.GeneratedSource(result, "MyMod.Settings.NumosState.g.cs"), Does.Not.Contain("AtmosStateMember"));
    }
}
