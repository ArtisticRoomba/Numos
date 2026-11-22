using Microsoft.CodeAnalysis;

namespace Numos.API.Generators.Tests;

[TestFixture]
public sealed class ChunkComponentDiagnosticTests
{
    [TestCase("public struct Fire { [NumosSerialize] public float Exposure; }", "'Fire' is not partial")]
    [TestCase("public partial struct Fire<T> { [NumosSerialize] public float Exposure; }", "is generic")]
    [TestCase("file partial struct Fire { [NumosSerialize] public float Exposure; }", "is file-local")]
    public void NotAPartialNonGenericStruct_Reports001(string declaration, string reason)
    {
        Diagnostic diagnostic = AssertReports("NUMOSCOMPONENT001", DiagnosticSeverity.Error, Component(declaration));
        Assert.That(diagnostic.GetMessage(), Does.Contain(reason));
    }

    [Test]
    public void NonPartialContainingType_Reports001()
    {
        const string source = """
                              using Numos.API;
                              using Numos.CoreSim.State;

                              public class Outer
                              {
                                  [ChunkComponent("tests/fire")]
                                  public partial struct Fire { [NumosSerialize] public float Exposure; }
                              }
                              """;

        Diagnostic diagnostic = AssertReports("NUMOSCOMPONENT001", DiagnosticSeverity.Error, source);
        Assert.That(diagnostic.GetMessage(), Does.Contain("'Outer' is not partial"));
    }

    [Test]
    public void PrivateNestedStruct_Reports002()
    {
        const string source = """
                              using Numos.API;
                              using Numos.CoreSim.State;

                              public partial class Outer
                              {
                                  [ChunkComponent("tests/fire")]
                                  private partial struct Fire { [NumosSerialize] public float Exposure; }
                              }
                              """;

        AssertReports("NUMOSCOMPONENT002", DiagnosticSeverity.Error, source);
    }

    [TestCase("\"\"")]
    [TestCase("\"   \"")]
    [TestCase("null!")]
    public void BlankKey_Reports003(string key)
    {
        AssertReports(
            "NUMOSCOMPONENT003",
            DiagnosticSeverity.Error,
            Component("public partial struct Fire { [NumosSerialize] public float Exposure; }", key));
    }

    [Test]
    public void UnmarkedField_Reports004AndLeavesItOut()
    {
        GeneratorRunResult[] results = GeneratorHarness.RunComponents(
            out Compilation output,
            out var diagnostics,
            sources: Component("public partial struct Fire { [NumosSerialize] public float Exposure; public float Loose; }"));

        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSCOMPONENT004" }));
        Assert.That(diagnostics[0].Severity, Is.EqualTo(DiagnosticSeverity.Warning));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain("'Loose'"));
        Assert.That(RoslynTestSupport.Errors(output), Is.Empty);
        Assert.That(GeneratorHarness.GeneratedSource(results[0], "Fire.ChunkComponent.g.cs"), Does.Not.Contain("Loose"));
    }

    [TestCase("[NumosSerialize] public float Exposure = 1f;", "Exposure")]
    [TestCase("[NumosScratch] public int Ticks = 4;", "Ticks")]
    [TestCase("[NumosSerialize] public float Heat, Exposure = 1f;", "Exposure")]
    public void MarkedFieldInitializer_Reports015AndStillGenerates(string declaration, string field)
    {
        GeneratorRunResult[] results = GeneratorHarness.RunComponents(
            out Compilation output,
            out var diagnostics,
            sources: Component("public partial struct Fire { public Fire() { } " + declaration + " }"));

        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSCOMPONENT015" }));
        Assert.That(diagnostics[0].Severity, Is.EqualTo(DiagnosticSeverity.Warning));
        Assert.That(
            diagnostics[0].GetMessage(),
            Does.Contain("'" + field + "'").And.Contain("default value").And.Contain("remove the initializer"));
        Assert.That(diagnostics[0].Location.SourceTree!.GetText().ToString(diagnostics[0].Location.SourceSpan), Is.EqualTo(field));
        Assert.That(RoslynTestSupport.Errors(output), Is.Empty);
        Assert.That(GeneratorHarness.GeneratedSource(results[0], "Fire.ChunkComponent.g.cs"), Does.Contain(field));
    }

    [Test]
    public void MarkedFieldWithoutInitializer_DoesNotReport015()
    {
        GeneratorHarness.RunComponents(
            out _,
            out var diagnostics,
            sources: Component("public partial struct Fire { public Fire() { Exposure = 1f; } [NumosSerialize] public float Exposure; }"));

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public void StaticAndConstantMembersWithoutMarks_ReportNothing()
    {
        GeneratorHarness.RunComponents(
            out Compilation output,
            out var diagnostics,
            sources: Component(
                "public partial struct Fire { [NumosSerialize] public float Exposure; public static int Shared; " +
                "public const int Limit = 3; public float Twice => Exposure * 2; }"));

        Assert.That(diagnostics, Is.Empty);
        Assert.That(RoslynTestSupport.Errors(output), Is.Empty);
    }

    [TestCase("[NumosIgnore] public float Exposure;", "[NumosIgnore]")]
    [TestCase("[NumosSerialize] public static float Exposure;", "is static")]
    [TestCase("[NumosScratch] public const float Exposure = 1f;", "is a constant")]
    [TestCase("[NumosSerialize] public float Exposure { get; set; }", "is a property")]
    [TestCase("[field: NumosSerialize] public float Exposure { get; set; }", "is a property")]
    [TestCase("[NumosSerialize, NumosScratch] public float Exposure;", "both")]
    [TestCase("[NumosSerialize(\" \")] public float Exposure;", "blank wire name")]
    [TestCase("[NumosSerialize(Count = \"Other\")] public float Exposure;", "NumosSerialize.Count")]
    public void WrongMarkOrMarkedNonField_Reports005(string member, string reason)
    {
        Diagnostic diagnostic = AssertReports(
            "NUMOSCOMPONENT005",
            DiagnosticSeverity.Error,
            Component("public partial struct Fire { [NumosScratch] public float Heat; " + member + " }"));

        Assert.That(diagnostic.GetMessage(), Does.Contain(reason));
    }

    [TestCase("char")]
    [TestCase("decimal")]
    [TestCase("float?")]
    [TestCase("System.Numerics.Vector2")]
    [TestCase("string")]
    public void SavedFieldWithUnportableOrManagedType_Reports006(string type)
    {
        Diagnostic diagnostic = AssertReports(
            "NUMOSCOMPONENT006",
            DiagnosticSeverity.Error,
            Component($"public partial struct Fire {{ [NumosSerialize] public {type} Exposure; }}"));

        Assert.That(diagnostic.GetMessage(), Does.Contain("Exposure"));
    }

    [TestCase("bool")]
    [TestCase("byte")]
    [TestCase("ulong")]
    [TestCase("System.Half")]
    [TestCase("double")]
    [TestCase("Mode")]
    public void SavedFieldWithPortableType_ReportsNothing(string type)
    {
        GeneratorHarness.RunComponents(
            out Compilation output,
            out var diagnostics,
            sources:
            [
                "public enum Mode : short { None }",
                .. Component($"public partial struct Fire {{ [NumosSerialize] public {type} Exposure; }}"),
            ]);

        Assert.That(diagnostics, Is.Empty);
        Assert.That(RoslynTestSupport.Errors(output), Is.Empty);
    }

    [Test]
    public void ScratchFieldWithUnmanagedStruct_ReportsNothing()
    {
        GeneratorHarness.RunComponents(
            out Compilation output,
            out var diagnostics,
            sources: Component("public partial struct Fire { [NumosScratch] public System.Numerics.Vector2 Flow; }"));

        Assert.That(diagnostics, Is.Empty);
        Assert.That(RoslynTestSupport.Errors(output), Is.Empty);
    }

    [Test]
    public void ScratchFieldWithManagedType_Reports007()
    {
        AssertReports(
            "NUMOSCOMPONENT007",
            DiagnosticSeverity.Error,
            Component("public partial struct Fire { [NumosScratch] public string Name; }"));
    }

    [Test]
    public void UnresolvedQuantityAlias_IsTreatedAsManaged()
    {
        // A quantity alias such as Kelvin is an error type in a project that owns a units catalog. The chunk data
        // generator skips its portability check but still finds it not unmanaged, and components do the same.
        GeneratorHarness.RunComponents(
            out _,
            out var diagnostics,
            sources: Component(
                "public partial struct Fire { [NumosSerialize] public Kelvin Saved; [NumosScratch] public Kelvin Heat; }"));

        Assert.That(
            diagnostics.Where(static d => d.Id.StartsWith("NUMOSCOMPONENT", StringComparison.Ordinal)).Select(static d => d.Id),
            Is.EqualTo(new[] { "NUMOSCOMPONENT006", "NUMOSCOMPONENT007" }));
    }

    [Test]
    public void NegativePerChunkLength_Reports008()
    {
        AssertReports(
            "NUMOSCOMPONENT008",
            DiagnosticSeverity.Error,
            Component("public partial struct Fire { [NumosSerialize, PerChunk(Length = -2)] public int Count; }"));
    }

    [Test]
    public void ZeroPerChunkLength_IsAllowed()
    {
        GeneratorRunResult[] results = GeneratorHarness.RunComponents(
            out Compilation output,
            out var diagnostics,
            sources: Component("public partial struct Fire { [NumosSerialize, PerChunk(Length = 0)] public int Count; }"));

        Assert.That(diagnostics, Is.Empty);
        Assert.That(RoslynTestSupport.Errors(output), Is.Empty);
        Assert.That(GeneratorHarness.GeneratedSource(results[0], "Fire.ChunkComponent.g.cs"), Does.Contain("length: 0"));
    }

    [Test]
    public void InvalidAccessorName_Reports009()
    {
        AssertReports(
            "NUMOSCOMPONENT009",
            DiagnosticSeverity.Error,
            Component(
                "public partial struct Fire { [NumosSerialize] public float Exposure; }",
                arguments: ", AccessorName = \"not valid\""));
    }

    [TestCase("Chunk", "yields")]
    [TestCase("Volume", "AtmosSolverGasMixturePage member 'GetVolume'")]
    [TestCase("Slot", "AtmosSolverGasMixturePage member 'GetSlot'")]
    [TestCase("Gas", "AtmosSolverChunk member 'HasGas'")]
    [TestCase("CellRef", "AtmosSolverChunk member 'GetCellRef'")]
    public void AccessorNamedLikeViewMember_Reports010(string name, string collision)
    {
        GeneratorRunResult[] results = GeneratorHarness.RunComponents(
            out Compilation output,
            out var diagnostics,
            sources: Component(
                "public partial struct Fire { [NumosSerialize] public float Exposure; }",
                arguments: $", AccessorName = \"{name}\""));

        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSCOMPONENT010" }));
        Assert.That(diagnostics[0].Severity, Is.EqualTo(DiagnosticSeverity.Warning));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain(collision));
        Assert.That(RoslynTestSupport.Errors(output), Is.Empty);
        Assert.That(GeneratorHarness.GeneratedSource(results[0], "Fire.ChunkComponent.g.cs"), Does.Not.Contain("extension("));
    }

    [Test]
    public void TwoComponentsWithOneAccessorName_Report010OnTheSecond()
    {
        const string source = """
                              using Numos.API;
                              using Numos.CoreSim.State;

                              namespace MyMod;

                              [ChunkComponent("tests/first", AccessorName = "Burn")]
                              public partial struct First { [NumosSerialize] public float A; }

                              [ChunkComponent("tests/second", AccessorName = "Burn")]
                              public partial struct Second { [NumosSerialize] public float B; }
                              """;

        GeneratorHarness.RunComponents(out Compilation output, out var diagnostics, sources: [source]);

        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSCOMPONENT010" }));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain("'Second'").And.Contain("'First'"));
        Assert.That(RoslynTestSupport.Errors(output), Is.Empty);
    }

    [Test]
    public void TwoFieldsWithOneWireName_Report011()
    {
        Diagnostic diagnostic = AssertReports(
            "NUMOSCOMPONENT011",
            DiagnosticSeverity.Error,
            Component(
                "public partial struct Fire { [NumosSerialize] public float Exposure; " +
                "[NumosSerialize(\"Exposure\")] public float Renamed; }"));

        Assert.That(diagnostic.GetMessage(), Does.Contain("'tests/fire/Exposure'").And.Contain("'Renamed'"));
    }

    [Test]
    public void NoFields_Reports013()
    {
        AssertReports(
            "NUMOSCOMPONENT013",
            DiagnosticSeverity.Error,
            Component("public partial struct Fire { public static int Shared; }"));
    }

    [TestCase("[NumosSerialize] public float View;", "View")]
    [TestCase("[NumosScratch] public float Fields;", "Fields")]
    [TestCase("public void View() { }", "View")]
    [TestCase("public struct Fields { }", "Fields")]
    public void MemberNamedLikeAGeneratedType_Reports014(string member, string name)
    {
        GeneratorRunResult[] results = GeneratorHarness.RunComponents(
            out Compilation output,
            out var diagnostics,
            sources: Component("public partial struct Fire { [NumosSerialize] public float Exposure; " + member + " }"));

        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSCOMPONENT014" }));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain("'" + name + "'"));
        Assert.That(RoslynTestSupport.Errors(output), Is.Empty);
        Assert.That(
            results[0].GeneratedSources.Select(static generated => generated.HintName),
            Has.None.EqualTo("Fire.ChunkComponent.g.cs"));
    }

    [Test]
    public void ErrorOnOneComponent_StillGeneratesTheOthers()
    {
        const string source = """
                              using Numos.API;
                              using Numos.CoreSim.State;

                              [ChunkComponent("tests/broken")]
                              public partial struct Broken { [NumosSerialize] public string Name; }

                              [ChunkComponent("tests/fine")]
                              public partial struct Fine { [NumosSerialize] public float Value; }
                              """;

        GeneratorRunResult[] results = GeneratorHarness.RunComponents(out Compilation output, out _, sources: [source]);

        string[] hints = results[0].GeneratedSources.Select(static generated => generated.HintName).ToArray();
        Assert.That(hints, Does.Contain("Fine.ChunkComponent.g.cs"));
        Assert.That(hints, Does.Not.Contain("Broken.ChunkComponent.g.cs"));
        Assert.That(RoslynTestSupport.Errors(output), Is.Empty);
    }

    [Test]
    public void ScratchFieldsInAComponent_DrawNoStateDiagnostics()
    {
        // [NumosScratch] outside a class solver is NUMOSSTATE036, but a component field is the other legal place.
        GeneratorRunResult[] results = GeneratorHarness.RunComponents(
            out Compilation output,
            out var diagnostics,
            sources: Component(
                "public partial struct Fire { [NumosScratch] public float Heat; [NumosSerialize] public float Exposure; " +
                "[NumosIgnore] public float Ignored; }"));

        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSCOMPONENT005" }));
        Assert.That(results[1].GeneratedSources, Is.Empty);
        Assert.That(RoslynTestSupport.Errors(output), Is.Empty);
    }

    private static string[] Component(string declaration, string key = "\"tests/fire\"", string arguments = "")
    {
        return
        [
            $$"""
              using Numos.API;
              using Numos.CoreSim.State;

              [ChunkComponent({{key}}{{arguments}})]
              {{declaration}}
              """,
        ];
    }

    private static Diagnostic AssertReports(string id, DiagnosticSeverity severity, params string[] sources)
    {
        GeneratorHarness.RunComponents(out _, out var diagnostics, sources: sources);

        Diagnostic[] matching = diagnostics.Where(diagnostic => diagnostic.Id == id).ToArray();
        Assert.That(matching, Has.Length.EqualTo(1), string.Join("\n", diagnostics));
        Assert.That(matching[0].Severity, Is.EqualTo(severity));
        Assert.That(diagnostics.Where(static d => d.Id.StartsWith("NUMOSSTATE", StringComparison.Ordinal)), Is.Empty);
        Assert.That(matching[0].Location.GetLineSpan().Path, Does.StartWith("Source"));
        return matching[0];
    }
}
