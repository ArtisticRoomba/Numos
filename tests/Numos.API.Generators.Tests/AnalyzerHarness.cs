using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Numos.API.Generators.Authoring;

namespace Numos.API.Generators.Tests;

/// <summary>
///     Runs an analyzer over in-memory sources compiled against the real Numos.API assemblies.
/// </summary>
internal static class AnalyzerHarness
{
    /// <summary>
    ///     Runs <see cref="SolverAuthoringAnalyzer"/> and returns its diagnostics in source order. The test fails if the
    ///     sources do not compile.
    /// </summary>
    public static ImmutableArray<Diagnostic> RunAuthoring(params string[] sources)
    {
        return Run(new SolverAuthoringAnalyzer(), [], sources);
    }

    /// <summary>
    ///     Runs <see cref="SolverAuthoringAnalyzer"/> in a compilation that leaves out the named Numos assemblies.
    /// </summary>
    public static ImmutableArray<Diagnostic> RunAuthoringWithout(
        IReadOnlyCollection<string> excludedAssemblies, params string[] sources)
    {
        return Run(new SolverAuthoringAnalyzer(), [], excludedAssemblies, sources);
    }

    public static ImmutableArray<Diagnostic> Run(
        DiagnosticAnalyzer analyzer,
        IEnumerable<MetadataReference> extraReferences, params string[] sources)
    {
        return Run(analyzer, extraReferences, [], sources);
    }

    private static ImmutableArray<Diagnostic> Run(
        DiagnosticAnalyzer analyzer,
        IEnumerable<MetadataReference> extraReferences, IReadOnlyCollection<string> excludedAssemblies,
        params string[] sources)
    {
        CSharpCompilation compilation = GeneratorHarness.CreateCompilation(
            GeneratorHarness.ParseOptions(), extraReferences, excludedAssemblies, sources);
        Assert.That(GeneratorHarness.Errors(compilation), Is.Empty);

        CompilationWithAnalyzers withAnalyzers = compilation.WithAnalyzers([analyzer]);
        return [..withAnalyzers.GetAnalyzerDiagnosticsAsync().GetAwaiter().GetResult()
            .OrderBy(static diagnostic => diagnostic.Location.SourceSpan.Start)];
    }

    /// <summary>
    ///     Returns the source text each diagnostic points at.
    /// </summary>
    public static string[] Spans(IEnumerable<Diagnostic> diagnostics)
    {
        return diagnostics
            .Select(static diagnostic => diagnostic.Location.SourceTree!.GetText()
                .ToString(diagnostic.Location.SourceSpan))
            .ToArray();
    }
}
