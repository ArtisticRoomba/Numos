using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Numos.Units.Analyzers.Tests;

[TestFixture]
public sealed class DimensionalAnalysisAnalyzerTests
{
    private const string Catalog = """
                                   amount | Mole | global::System.Single | amount=1
                                   temperature | Kelvin | global::System.Single | temperature=1
                                   pressure | Pascal | global::System.Single | mass=1,length=-1,time=-2
                                   pressurePerMoleKelvin | PascalPerMoleKelvin | global::System.Single | mass=1,length=-1,time=-2,temperature=-1,amount=-1
                                   """;

    [Test]
    public async Task CompatibleDerivedExpressionHasNoDiagnostics()
    {
        const string source = """
                              internal static class Physics
                              {
                                  internal static Pascal Pressure(Mole moles, Kelvin temperature,
                                      PascalPerMoleKelvin factor)
                                  {
                                      Pascal result = moles * temperature * factor;
                                      return result;
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public async Task IncompatibleAssignmentIsAnError()
    {
        const string source = """
                              internal static class Physics
                              {
                                  internal static void Invalid(Pascal pressure)
                                  {
                                      Kelvin temperature = pressure;
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Diagnostic diagnostic = diagnostics.Single(item => item.Id == "NUMOSUNIT002");
        Assert.That(diagnostic.GetMessage(),
            Is.EqualTo("Cannot assign 'length^-1 mass^1 time^-2' to 'temperature'"));
    }

    [Test]
    public async Task IncompatibleArgumentAndAdditionAreErrors()
    {
        const string source = """
                              internal static class Physics
                              {
                                  private static void AcceptTemperature(Kelvin value) { }

                                  internal static void Invalid(Pascal pressure, Kelvin temperature)
                                  {
                                      AcceptTemperature(pressure);
                                      _ = pressure + temperature;
                                  }
                              }
                              """;

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, Catalog);

        Assert.Multiple(() =>
        {
            Assert.That(diagnostics.Select(diagnostic => diagnostic.Id),
                Does.Contain("NUMOSUNIT001"));
            Assert.That(diagnostics.Select(diagnostic => diagnostic.Id),
                Does.Contain("NUMOSUNIT003"));
        });
    }

    [Test]
    public async Task CatalogCanAddAnIndependentBaseDimension()
    {
        const string source = """
                              internal static class Simulation
                              {
                                  internal static PerTick Scale(PerTick value, float ratio) => value * ratio;
                              }
                              """;
        const string catalog = "perTick | PerTick | global::System.Single | tick=-1";

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, catalog);

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public async Task GeneratedConversionCarriesItsCanonicalQuantity()
    {
        const string source = """
                              using Numos.Units.Generated;

                              internal static class Inputs
                              {
                                  internal static Kelvin Valid() => UnitConversions.FromCelsius(20f);
                                  internal static Pascal Invalid() => UnitConversions.FromCelsius(20f);
                              }
                              """;
        string catalog = Catalog + Environment.NewLine +
                         "convert | temperature | Celsius | global::System.Single | 1 | 273.15";

        ImmutableArray<Diagnostic> diagnostics = await Analyze(source, catalog);

        Assert.That(diagnostics.Select(diagnostic => diagnostic.Id),
            Is.EquivalentTo(new[] { "NUMOSUNIT004" }));
    }

    [Test]
    public void GeneratedAliasRetainsPrimitiveClrType()
    {
        const string source = """
                              internal static class Storage
                              {
                                  internal static Kelvin Value;
                                  internal static Mole[] Values = new Mole[4];
                              }
                              """;

        Compilation compilation = GenerateAliases(CreateCompilation(source), Catalog, out _);
        INamedTypeSymbol storage = compilation.GetTypeByMetadataName("Storage")!;

        Assert.Multiple(() =>
        {
            Assert.That(((IFieldSymbol)storage.GetMembers("Value").Single()).Type.SpecialType,
                Is.EqualTo(SpecialType.System_Single));
            var array = (IArrayTypeSymbol)((IFieldSymbol)storage.GetMembers("Values").Single()).Type;
            Assert.That(array.ElementType.SpecialType, Is.EqualTo(SpecialType.System_Single));
        });
    }

    private static async Task<ImmutableArray<Diagnostic>> Analyze(string source, string catalog)
    {
        var additionalText = new InMemoryAdditionalText("Test.numosunits", catalog);
        Compilation compilation = GenerateAliases(CreateCompilation(source), catalog,
            out ImmutableArray<Diagnostic> generatorDiagnostics);
        Assert.That(generatorDiagnostics, Is.Empty);
        Assert.That(compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error),
            Is.Empty);

        var options = new AnalyzerOptions(ImmutableArray.Create<AdditionalText>(additionalText));
        var analyzers = ImmutableArray.Create<DiagnosticAnalyzer>(new DimensionalAnalysisAnalyzer());
        return await compilation.WithAnalyzers(analyzers, options).GetAnalyzerDiagnosticsAsync();
    }

    private static Compilation GenerateAliases(CSharpCompilation compilation, string catalog,
        out ImmutableArray<Diagnostic> diagnostics)
    {
        var additionalText = new InMemoryAdditionalText("Test.numosunits", catalog);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new QuantityAliasGenerator().AsSourceGenerator()],
            [additionalText],
            (CSharpParseOptions)compilation.SyntaxTrees.Single().Options);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out Compilation output, out diagnostics);
        return output;
    }

    private static CSharpCompilation CreateCompilation(string source)
    {
        string[] trustedAssemblies = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator);
        IEnumerable<MetadataReference> references = trustedAssemblies
            .Append(typeof(QuantityAttribute).Assembly.Location)
            .Distinct(StringComparer.Ordinal)
            .Select(path => MetadataReference.CreateFromFile(path));
        return CSharpCompilation.Create("AnalyzerTest",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private sealed class InMemoryAdditionalText : AdditionalText
    {
        private readonly SourceText _text;

        internal InMemoryAdditionalText(string path, string text)
        {
            Path = path;
            _text = SourceText.From(text);
        }

        public override string Path { get; }
        public override SourceText GetText(CancellationToken cancellationToken = default) => _text;
    }
}