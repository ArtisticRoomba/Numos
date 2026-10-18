using Microsoft.CodeAnalysis;

namespace Numos.API.Generators.Tests;

[TestFixture]
public sealed class ChunkDataDiagnosticTests
{
    [TestCase("public static Numos.API.ChunkField<float> Value { get; } = null!;")]
    [TestCase("public static partial Numos.API.ChunkField<float> Value { get; set; }")]
    public void NotAPartialGetOnlyDefinition_Reports001(string declaration)
    {
        AssertReports("NUMOSCHUNK001", DiagnosticSeverity.Error, Wrap(declaration));
    }

    [Test]
    public void AlreadyImplemented_Reports001()
    {
        const string source = """
                              public static partial class Data
                              {
                                  [Numos.API.ChunkData]
                                  public static partial Numos.API.ChunkField<float> Value { get; }

                                  public static partial Numos.API.ChunkField<float> Value =>
                                      Numos.API.ChunkField<float>.CreatePrivate("value");
                              }
                              """;

        AssertReports("NUMOSCHUNK001", DiagnosticSeverity.Error, source);
    }

    [Test]
    public void InstanceProperty_Reports002()
    {
        const string source = """
                              public partial class Data
                              {
                                  [Numos.API.ChunkData]
                                  public partial Numos.API.ChunkField<float> Value { get; }
                              }
                              """;

        AssertReports("NUMOSCHUNK002", DiagnosticSeverity.Error, source);
    }

    [Test]
    public void WrongPropertyType_Reports003()
    {
        AssertReports("NUMOSCHUNK003", DiagnosticSeverity.Error,
            Wrap("public static partial int Value { get; }"));
    }

    [Test]
    public void ContainingTypeNotPartial_Reports004()
    {
        const string source = """
                              public static class Outer
                              {
                                  public static partial class Inner
                                  {
                                      [Numos.API.ChunkData]
                                      public static partial Numos.API.ChunkField<float> Value { get; }
                                  }
                              }
                              """;

        Diagnostic diagnostic = AssertReports("NUMOSCHUNK004", DiagnosticSeverity.Error, source);
        Assert.That(diagnostic.GetMessage(), Does.Contain("'Outer'"));
    }

    [Test]
    public void GenericContainingType_Reports005()
    {
        const string source = """
                              public static partial class Data<T>
                              {
                                  [Numos.API.ChunkData]
                                  public static partial Numos.API.ChunkField<float> Value { get; }
                              }
                              """;

        AssertReports("NUMOSCHUNK005", DiagnosticSeverity.Error, source);
    }

    [TestCase("\"\"")]
    [TestCase("\"   \"")]
    [TestCase("null!")]
    public void BlankKey_Reports006(string key)
    {
        AssertReports("NUMOSCHUNK006", DiagnosticSeverity.Error,
            Wrap($"[Numos.API.ChunkData({key})] public static partial Numos.API.ChunkField<float> Value {{ get; }}",
                attribute: false));
    }

    [Test]
    public void RollbackWithoutKey_Reports007()
    {
        AssertReports("NUMOSCHUNK007", DiagnosticSeverity.Error,
            Wrap("[Numos.API.ChunkData(Rollback = true)] public static partial Numos.API.ChunkField<float> Value { get; }",
                attribute: false));
    }

    [Test]
    public void RollbackWithManagedElement_Reports008()
    {
        AssertReports("NUMOSCHUNK008", DiagnosticSeverity.Error,
            Wrap("[Numos.API.ChunkData(\"tests/managed\", Rollback = true)] " +
                 "public static partial Numos.API.ChunkField<string> Value { get; }", attribute: false));
    }

    [Test]
    public void TransientManagedElement_IsAllowed()
    {
        GeneratorHarness.Run(out Compilation output, out var diagnostics,
            sources: Wrap("public static partial Numos.API.ChunkField<string?> Value { get; }"));

        Assert.That(diagnostics, Is.Empty);
        Assert.That(GeneratorHarness.Errors(output), Is.Empty);
    }

    [Test]
    public void NegativeLength_Reports009()
    {
        AssertReports("NUMOSCHUNK009", DiagnosticSeverity.Error,
            Wrap("[Numos.API.ChunkData(Length = -1)] public static partial Numos.API.ChunkField<float> Value { get; }",
                attribute: false));
    }

    [TestCase("private static partial Numos.API.ChunkField<float> Value { get; }", "public")]
    [TestCase("public static partial Numos.API.ChunkField<float> Value { get; }", "private")]
    public void InaccessibleProperty_Reports010(string declaration, string nestedAccessibility)
    {
        string source = $$"""
                          public static partial class Outer
                          {
                              {{nestedAccessibility}} static partial class Data
                              {
                                  [Numos.API.ChunkData]
                                  {{declaration}}
                              }
                          }
                          """;

        AssertReports("NUMOSCHUNK010", DiagnosticSeverity.Error, source);
    }

    [Test]
    public void ConflictingKey_Reports011OnEveryDeclaration()
    {
        const string source = """
                              public static partial class Data
                              {
                                  [Numos.API.ChunkData("tests/shared")]
                                  public static partial Numos.API.ChunkField<float> First { get; }

                                  [Numos.API.ChunkData("tests/shared")]
                                  public static partial Numos.API.ChunkField<int> Second { get; }
                              }
                              """;

        GeneratorHarness.Run(out _, out var diagnostics, sources: [source]);

        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSCHUNK011", "NUMOSCHUNK011" }));
        Assert.That(diagnostics.All(static d => d.Severity == DiagnosticSeverity.Error), Is.True);
    }

    [Test]
    public void DuplicateIdenticalKey_Reports012AndStillCompiles()
    {
        const string source = """
                              public static partial class Data
                              {
                                  [Numos.API.ChunkData("tests/shared-identical", AccessorName = "A")]
                                  public static partial Numos.API.ChunkField<float> First { get; }
                              }

                              public static partial class Other
                              {
                                  [Numos.API.ChunkData("tests/shared-identical", AccessorName = "B")]
                                  public static partial Numos.API.ChunkField<float> Second { get; }
                              }
                              """;

        GeneratorHarness.Run(out Compilation output, out var diagnostics, sources: [source]);

        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSCHUNK012" }));
        Assert.That(diagnostics[0].Severity, Is.EqualTo(DiagnosticSeverity.Warning));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain("Other.Second").And.Contain("Data.First"));
        Assert.That(GeneratorHarness.Errors(output), Is.Empty);
    }

    [Test]
    public void OldLanguageVersion_Reports013()
    {
        GeneratorHarness.Run(out Compilation output, out var diagnostics,
            version: Microsoft.CodeAnalysis.CSharp.LanguageVersion.CSharp13,
            sources: Wrap("public static partial Numos.API.ChunkField<float> Value { get; }"));

        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSCHUNK013" }));
        Assert.That(diagnostics[0].Severity, Is.EqualTo(DiagnosticSeverity.Info));
        Assert.That(GeneratorHarness.Errors(output), Is.Empty);
    }

    [TestCase("Position")]
    [TestCase("Dangerous")]
    [TestCase("Temperature")]
    public void AccessorNamedLikeViewMember_Reports014(string name)
    {
        GeneratorRunResult result = GeneratorHarness.Run(out Compilation output, out var diagnostics,
            sources: Wrap($"[Numos.API.ChunkData(AccessorName = \"{name}\")] " +
                          "public static partial Numos.API.ChunkField<float> Value { get; }", attribute: false));

        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSCHUNK014" }));
        Assert.That(diagnostics[0].Severity, Is.EqualTo(DiagnosticSeverity.Warning));
        Assert.That(GeneratorHarness.Errors(output), Is.Empty);

        // Temperature only exists on the Dangerous view, so the plain view still gets its accessor.
        string generated = GeneratorHarness.GeneratedSource(result, "Data.ChunkData.g.cs");
        Assert.That(generated.Contains("extension(global::Numos.API.AtmosSolverChunk chunk)"),
            Is.EqualTo(name == "Temperature"));
    }

    [Test]
    public void TemperatureWithoutDangerous_DoesNotReport014()
    {
        GeneratorHarness.Run(out _, out var diagnostics, withDangerous: false,
            sources: Wrap("public static partial Numos.API.ChunkField<float> Temperature { get; }"));

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public void SameAccessorAcrossTypesInNamespace_Reports014()
    {
        const string source = """
                              namespace MyMod
                              {
                                  public static partial class Fire
                                  {
                                      [Numos.API.ChunkData]
                                      public static partial Numos.API.ChunkField<float> Exposure { get; }
                                  }

                                  public static partial class Radiation
                                  {
                                      [Numos.API.ChunkData]
                                      public static partial Numos.API.ChunkField<float> Exposure { get; }
                                  }
                              }
                              """;

        GeneratorHarness.Run(out Compilation output, out var diagnostics, sources: [source]);

        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSCHUNK014" }));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain("Radiation.Exposure").And.Contain("Fire.Exposure"));
        Assert.That(GeneratorHarness.Errors(output), Is.Empty);
    }

    [Test]
    public void SameAccessorWithinType_Reports014AndSkipsDuplicate()
    {
        const string source = """
                              public static partial class Data
                              {
                                  [Numos.API.ChunkData]
                                  public static partial Numos.API.ChunkField<float> Exposure { get; }

                                  [Numos.API.ChunkData(AccessorName = "Exposure")]
                                  public static partial Numos.API.ChunkField<int> Other { get; }
                              }
                              """;

        GeneratorHarness.Run(out Compilation output, out var diagnostics, sources: [source]);

        Assert.That(GeneratorHarness.Ids(diagnostics), Is.EqualTo(new[] { "NUMOSCHUNK014" }));
        Assert.That(GeneratorHarness.Errors(output), Is.Empty);
    }

    [Test]
    public void InvalidAccessorName_Reports015()
    {
        AssertReports("NUMOSCHUNK015", DiagnosticSeverity.Error,
            Wrap("[Numos.API.ChunkData(AccessorName = \"not valid\")] " +
                 "public static partial Numos.API.ChunkField<float> Value { get; }", attribute: false));
    }

    [Test]
    public void ErrorOnOneProperty_StillGeneratesTheOthers()
    {
        const string source = """
                              public static partial class Data
                              {
                                  [Numos.API.ChunkData(Length = -1)]
                                  public static partial Numos.API.ChunkField<float> Broken { get; }

                                  [Numos.API.ChunkData]
                                  public static partial Numos.API.ChunkField<float> Fine { get; }
                              }
                              """;

        GeneratorRunResult result = GeneratorHarness.Run(out Compilation output, out _, sources: [source]);

        string generated = GeneratorHarness.GeneratedSource(result, "Data.ChunkData.g.cs");
        Assert.That(generated, Does.Contain("Fine =>"));
        Assert.That(generated, Does.Not.Contain("Broken"));

        // Only the unimplemented partial is left; the generator itself adds nothing that fails to compile.
        Assert.That(GeneratorHarness.Errors(output).Select(static d => d.Id).Distinct(),
            Is.EqualTo(new[] { "CS9248" }));
    }

    private static string[] Wrap(string declaration, bool attribute = true)
    {
        string prefix = attribute ? "[Numos.API.ChunkData] " : "";
        return
        [
            $$"""
              public static partial class Data
              {
                  {{prefix}}{{declaration}}
              }
              """,
        ];
    }

    private static Diagnostic AssertReports(string id, DiagnosticSeverity severity, params string[] sources)
    {
        GeneratorHarness.Run(out _, out var diagnostics, sources: sources);

        Diagnostic[] matching = diagnostics.Where(diagnostic => diagnostic.Id == id).ToArray();
        Assert.That(matching, Has.Length.EqualTo(1), string.Join("\n", diagnostics));
        Assert.That(matching[0].Severity, Is.EqualTo(severity));
        // Generator diagnostics carry a path and span rather than a syntax tree; see LocationInfo.
        Assert.That(matching[0].Location.GetLineSpan().Path, Does.StartWith("Source"));
        return matching[0];
    }
}
