using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Numos.API.Generators.Tests;

/// <summary>
///     Runs a generator over in-memory sources compiled against the real Numos.API assemblies.
/// </summary>
internal static class GeneratorHarness
{
    private static readonly string[] FrameworkReferences = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator)
        .Where(static path => !Path.GetFileName(path).StartsWith("Numos.", StringComparison.Ordinal))
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    public static CSharpParseOptions ParseOptions(LanguageVersion version = LanguageVersion.CSharp14)
    {
        return new CSharpParseOptions(version);
    }

    public static CSharpCompilation CreateCompilation(bool withDangerous, CSharpParseOptions parseOptions,
        params string[] sources)
    {
        return CreateCompilation(withDangerous, parseOptions, [], sources);
    }

    public static CSharpCompilation CreateCompilation(bool withDangerous, CSharpParseOptions parseOptions,
        IEnumerable<MetadataReference> extraReferences, params string[] sources)
    {
        IEnumerable<MetadataReference> references = FrameworkReferences
            .Concat(NumosReferences(withDangerous))
            .Select(static path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .Concat(extraReferences);

        return CSharpCompilation.Create(
            "GeneratorTest_" + Guid.NewGuid().ToString("N"),
            sources.Select((source, index) =>
                CSharpSyntaxTree.ParseText(source, parseOptions, path: $"Source{index}.cs")),
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));
    }

    public static GeneratorRunResult Run(out Compilation output, out ImmutableArray<Diagnostic> diagnostics,
        bool withDangerous = true, LanguageVersion version = LanguageVersion.CSharp14, params string[] sources)
    {
        CSharpParseOptions parseOptions = ParseOptions(version);
        CSharpCompilation compilation = CreateCompilation(withDangerous, parseOptions, sources);
        GeneratorDriver driver = CreateDriver(parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out output, out diagnostics);
        return driver.GetRunResult().Results.Single();
    }

    public static GeneratorRunResult RunDependencies(out Compilation output,
        out ImmutableArray<Diagnostic> diagnostics, params string[] sources)
    {
        return RunDependencies(out output, out diagnostics, [], sources);
    }

    public static GeneratorRunResult RunDependencies(out Compilation output,
        out ImmutableArray<Diagnostic> diagnostics, IEnumerable<MetadataReference> extraReferences,
        params string[] sources)
    {
        CSharpParseOptions parseOptions = ParseOptions();
        CSharpCompilation compilation = CreateCompilation(true, parseOptions, extraReferences, sources);
        GeneratorDriver driver = CreateDriver(parseOptions, generator: new SolverDependency.SolverDependencyGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out output, out diagnostics);
        return driver.GetRunResult().Results.Single();
    }

    public static GeneratorDriver CreateDriver(CSharpParseOptions parseOptions, bool trackSteps = false,
        IIncrementalGenerator? generator = null)
    {
        return CSharpGeneratorDriver.Create(
            [(generator ?? new ChunkData.ChunkDataGenerator()).AsSourceGenerator()],
            parseOptions: parseOptions,
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackSteps));
    }

    public static string GeneratedSource(GeneratorRunResult result, string hintName)
    {
        GeneratedSourceResult source = result.GeneratedSources.Single(source => source.HintName == hintName);
        return source.SourceText.ToString();
    }

    public static Diagnostic[] Errors(Compilation compilation)
    {
        return compilation.GetDiagnostics()
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
    }

    public static string[] Ids(IEnumerable<Diagnostic> diagnostics)
    {
        return diagnostics.Select(static diagnostic => diagnostic.Id).ToArray();
    }

    private static IEnumerable<string> NumosReferences(bool withDangerous)
    {
        // The test project references Numos.API and Numos.API.Dangerous, so their closures sit next to this
        // assembly. Dangerous is opt-in to cover the generator's "no Dangerous referenced" path.
        return Directory.EnumerateFiles(AppContext.BaseDirectory, "Numos.*.dll")
            .Where(path =>
            {
                string name = Path.GetFileNameWithoutExtension(path);
                if (name.StartsWith("Numos.API.Generators", StringComparison.Ordinal))
                    return false;
                return withDangerous || name != "Numos.API.Dangerous";
            })
            .OrderBy(static path => path, StringComparer.Ordinal);
    }
}
