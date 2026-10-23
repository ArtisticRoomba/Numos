using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Numos.Replay.Generators.Tests;

/// <summary>
///     Runs a replay generator over in-memory sources that stand in for Numos.CoreSim or Numos.API.
/// </summary>
internal static class GeneratorHarness
{
    private static readonly MetadataReference[] FrameworkReferences =
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator)
        .Where(static path => !Path.GetFileName(path).StartsWith("Numos.", StringComparison.Ordinal))
        .Distinct(StringComparer.Ordinal)
        .Select(static path => (MetadataReference)MetadataReference.CreateFromFile(path))
        .ToArray();

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    public static CSharpCompilation CreateCompilation(
        string assemblyName, IEnumerable<MetadataReference> references,
        params string[] sources)
    {
        return CSharpCompilation.Create(
            assemblyName,
            sources.Select((source, index) => CSharpSyntaxTree.ParseText(source, ParseOptions, path: $"Source{index}.cs")),
            FrameworkReferences.Concat(references),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
    }

    public static Compilation Run(
        IIncrementalGenerator generator, CSharpCompilation compilation,
        out ImmutableArray<Diagnostic> diagnostics, params AdditionalText[] additionalTexts)
    {
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [generator.AsSourceGenerator()],
            additionalTexts,
            ParseOptions);

        driver.RunGeneratorsAndUpdateCompilation(compilation, out Compilation output, out diagnostics);
        return output;
    }

    public static Compilation Run(
        IIncrementalGenerator generator, out ImmutableArray<Diagnostic> diagnostics,
        params string[] sources)
    {
        return Run(generator, CreateCompilation("ReplayGeneratorTest", [], sources), out diagnostics);
    }

    public static string Generated(Compilation compilation, string hintName)
    {
        return compilation.SyntaxTrees.Single(tree => tree.FilePath.EndsWith(hintName, StringComparison.Ordinal)).ToString();
    }

    public static IEnumerable<Diagnostic> Errors(Compilation compilation)
    {
        return compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    public static AdditionalText Catalog(string text)
    {
        return new InMemoryText("Test.magnitudo", text);
    }

    private sealed class InMemoryText(string path, string text) : AdditionalText
    {
        public override string Path { get; } = path;

        public override SourceText GetText(CancellationToken cancellationToken = default)
        {
            return SourceText.From(text);
        }
    }
}