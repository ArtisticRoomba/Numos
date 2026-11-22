using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Numos.API.Generators.Common;

/// <summary>
///     Reports a generator's <see cref="DiagnosticInfo" /> values at locations inside the compilation's syntax trees.
/// </summary>
/// <remarks>
///     <para>
///         The compiler applies <c>#pragma warning disable</c>, <c>[SuppressMessage]</c>, and per-file
///         <c>.editorconfig</c> severities only to a location in a syntax tree. A path-based location from
///         <see cref="LocationInfo.ToLocation()" /> escapes all three. The models stay location-free, and this maps each
///         stored path back to its tree at report time.
///     </para>
///     <para>
///         The reporting node combines with <see cref="IncrementalGeneratorInitializationContext.CompilationProvider" />,
///         so it reruns on every compilation change. It only reports, so the source outputs stay cached.
///     </para>
/// </remarks>
internal static class DiagnosticReporter
{
    /// <summary>
    ///     The tracking name of every reporting node, so a cache test can tell it from the source outputs.
    /// </summary>
    internal const string StepName = "NumosDiagnosticReport";

    /// <summary>
    ///     Reports every diagnostic <paramref name="diagnostics" /> produces.
    /// </summary>
    /// <param name="context">The generator's initialization context.</param>
    /// <param name="diagnostics">The diagnostics to report.</param>
    public static void Register(
        IncrementalGeneratorInitializationContext context,
        IncrementalValueProvider<EquatableArray<DiagnosticInfo>> diagnostics)
    {
        context.RegisterSourceOutput(
            diagnostics.Combine(context.CompilationProvider).WithTrackingName(StepName),
            static (production, input) => Report(production, input.Right, input.Left));
    }

    /// <summary>
    ///     Reports every diagnostic <paramref name="diagnostics" /> produces, in provider order.
    /// </summary>
    /// <param name="context">The generator's initialization context.</param>
    /// <param name="diagnostics">The diagnostics to report, one batch per item.</param>
    public static void Register(
        IncrementalGeneratorInitializationContext context,
        IncrementalValuesProvider<EquatableArray<DiagnosticInfo>> diagnostics)
    {
        Register(context, diagnostics.Collect().Select(static (batches, _) => Flatten(batches)));
    }

    private static EquatableArray<DiagnosticInfo> Flatten(ImmutableArray<EquatableArray<DiagnosticInfo>> batches)
    {
        if (batches.Length == 1)
            return batches[0];

        return batches.SelectMany(static batch => batch).ToEquatableArray();
    }

    private static void Report(
        SourceProductionContext production, Compilation compilation, EquatableArray<DiagnosticInfo> diagnostics)
    {
        if (diagnostics.Count == 0)
            return;

        // Built once per run. Two trees with the same path would be a host bug, so the first one wins.
        Dictionary<string, SyntaxTree>? trees = null;
        foreach (DiagnosticInfo diagnostic in diagnostics)
        {
            SyntaxTree? tree = null;
            if (diagnostic.Location is { } location)
            {
                trees ??= IndexTrees(compilation);
                trees.TryGetValue(location.FilePath, out tree);
            }

            production.ReportDiagnostic(diagnostic.ToDiagnostic(tree));
        }
    }

    private static Dictionary<string, SyntaxTree> IndexTrees(Compilation compilation)
    {
        var trees = new Dictionary<string, SyntaxTree>(StringComparer.Ordinal);
        foreach (SyntaxTree tree in compilation.SyntaxTrees)
        {
            if (!trees.ContainsKey(tree.FilePath))
                trees.Add(tree.FilePath, tree);
        }

        return trees;
    }
}
