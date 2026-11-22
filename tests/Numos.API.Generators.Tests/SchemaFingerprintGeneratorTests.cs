using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;

namespace Numos.API.Generators.Tests;

/// <summary>
///     The schema fingerprint the state generator writes into every state type.
/// </summary>
[TestFixture]
public sealed class SchemaFingerprintGeneratorTests
{
    private static readonly Regex FingerprintPattern = new(@"__numosSchemaFingerprint = 0x([0-9A-F]{16})UL;");

    /// <summary>
    ///     Generates <paramref name="members" /> on a class solver, and on a settings class when
    ///     <paramref name="settings" /> is true, and returns their fingerprints.
    /// </summary>
    private static (ulong Solver, ulong Settings) Fingerprints(string members, string? catalog = null, bool settings = false)
    {
        string settingsType = settings
            ? $$"""
                public sealed partial class FireSettings
                {
                    {{members}}
                }
                """
            : "";

        string source = $$"""
                          using System.Collections.Generic;
                          using Numos.API;
                          using Numos.CoreSim;
                          using Numos.CoreSim.State;

                          namespace MyMod;

                          public enum Mode : byte { Off, On }
                          public enum OtherMode : byte { Low, High }

                          public sealed partial class FireSolver : IAtmosWorldSolver
                          {
                              {{members}}

                              public void Solve(AtmosWorldSolverContext context) { }
                          }

                          {{settingsType}}
                          """;

        GeneratorRunResult result = GeneratorHarness.RunState(out Compilation output, out var diagnostics, catalog, source);
        Assert.That(diagnostics, Is.Empty);

        // Without the Magnitudo generator a quantity alias stays an error type, as it does inside Numos.CoreSim.
        if (catalog is null)
            Assert.That(GeneratorHarness.Errors(output), Is.Empty);

        return (
            Read(GeneratorHarness.GeneratedSource(result, "MyMod.FireSolver.NumosState.g.cs")),
            settings ? Read(GeneratorHarness.GeneratedSource(result, "MyMod.FireSettings.NumosState.g.cs")) : 0);
    }

    private static ulong Read(string generated)
    {
        Match match = FingerprintPattern.Match(generated);
        Assert.That(match.Success, Is.True, "The generated type has no fingerprint constant.");
        return ulong.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    [Test]
    public void Fingerprint_IsFnv1aOverWireNamesAndShapesInIdOrder()
    {
        // Ids order the fields, so the expected text lists them by id, not by name.
        string[] names = ["Count", "Weights"];
        string[] shapes = ["Int32", "List(Single)"];
        int[] order = names.Select((name, index) => (Id: FieldId(name), index)).OrderBy(static pair => pair.Id)
            .Select(static pair => pair.index).ToArray();

        ulong expected = 14695981039346656037;
        foreach (int index in order)
        {
            foreach (string text in new[] { names[index], shapes[index] })
            {
                foreach (byte value in Encoding.UTF8.GetBytes(text + "\0"))
                {
                    expected ^= value;
                    expected *= 1099511628211;
                }
            }
        }

        var (solver, settings) = Fingerprints(
            "[NumosSerialize] public int Count; [NumosSerialize] public float[] Weights = [];", settings: true);
        Assert.Multiple(() =>
        {
            Assert.That(solver, Is.EqualTo(expected));
            Assert.That(settings, Is.EqualTo(expected), "a value type and a type restored in place agree");
        });
    }

    [Test]
    public void Fingerprint_IgnoresSourceOrderAndMemberRenamesThatKeepTheWireName()
    {
        var (original, _) = Fingerprints("[NumosSerialize] public float Heat; [NumosSerialize] public int Count;");
        var (reordered, _) = Fingerprints("[NumosSerialize] public int Count; [NumosSerialize] public float Heat;");
        var (renamed, _) = Fingerprints("[NumosSerialize(\"Heat\")] public float Temperature; [NumosSerialize] public int Count;");

        Assert.Multiple(() =>
        {
            Assert.That(reordered, Is.EqualTo(original));
            Assert.That(renamed, Is.EqualTo(original));
        });
    }

    [Test]
    public void Fingerprint_ChangesWithTheWireNameOrTheShape()
    {
        var (original, _) = Fingerprints("[NumosSerialize] public float Heat;");
        var (renamed, _) = Fingerprints("[NumosSerialize] public float Warmth;");
        var (retyped, _) = Fingerprints("[NumosSerialize] public double Heat;");
        var (nullable, _) = Fingerprints("[NumosSerialize] public float? Heat;");
        var (added, _) = Fingerprints("[NumosSerialize] public float Heat; [NumosSerialize] public int Count;");

        Assert.That(new[] { original, renamed, retyped, nullable, added }, Is.Unique);
    }

    [Test]
    public void Fingerprint_KeepsShapesThatWriteTheSameBytes()
    {
        var (array, _) = Fingerprints("[NumosSerialize] public float[] Weights = [];");
        var (list, _) = Fingerprints("[NumosSerialize] public List<float> Weights = [];");
        var (mode, _) = Fingerprints("[NumosSerialize] public Mode Setting;");
        var (otherMode, _) = Fingerprints("[NumosSerialize] public OtherMode Setting;");
        var (plain, _) = Fingerprints("[NumosSerialize] public float Ignition;");
        var (alias, _) = Fingerprints(
            "[NumosSerialize] public Kelvin Ignition;", "temperature | Kelvin | global::System.Single | temperature=1");

        Assert.Multiple(() =>
        {
            Assert.That(list, Is.EqualTo(array));
            Assert.That(otherMode, Is.EqualTo(mode), "an enum contributes its underlying type");
            Assert.That(alias, Is.EqualTo(plain), "a quantity alias contributes its storage type");
        });
    }

    [Test]
    public void Fingerprint_IsReadableThroughTheInterface()
    {
        const string source = """
                              using Numos.API;
                              using Numos.CoreSim.State;

                              namespace MyMod;

                              [SimulationComponent("my-mod/empty")]
                              public sealed partial class EmptyComponent
                              {
                              }

                              public readonly partial record struct Point([property: NumosSerialize] int X);
                              """;

        GeneratorRunResult result = GeneratorHarness.RunState(out Compilation output, out var diagnostics, null, source);
        Assert.That(diagnostics, Is.Empty);
        Assert.That(GeneratorHarness.Errors(output), Is.Empty);
        string component = GeneratorHarness.GeneratedSource(result, "MyMod.EmptyComponent.NumosState.g.cs");
        string point = GeneratorHarness.GeneratedSource(result, "MyMod.Point.NumosState.g.cs");

        Assert.Multiple(() =>
        {
            const string member = "ulong global::Numos.CoreSim.State.Advanced.INumosStateSchema.SchemaFingerprint => __numosSchemaFingerprint;";
            Assert.That(component, Does.Contain(", global::Numos.CoreSim.State.Advanced.INumosStateSchema").And.Contain(member));
            Assert.That(point, Does.Contain(", global::Numos.CoreSim.State.Advanced.INumosStateSchema").And.Contain(member));
            Assert.That(Read(component), Is.EqualTo(14695981039346656037UL), "no fields hash to the offset basis");
        });
    }

    private static int FieldId(string name)
    {
        uint hash = 2166136261;
        foreach (byte value in Encoding.UTF8.GetBytes(name))
        {
            hash ^= value;
            hash *= 16777619;
        }

        int id = (int)(hash & 0x7FFFFFFF);
        return id == 0 ? 1 : id;
    }
}
