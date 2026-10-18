using Microsoft.CodeAnalysis;

namespace Numos.API.Generators.Common;

/// <summary>
///     What a generator's cross-property pass produces: every diagnostic, and one location-free model per output
///     file.
/// </summary>
/// <remarks>
///     The two halves go to separate source outputs. Diagnostics carry locations, so they change whenever code
///     above an annotated property moves; the per-type models don't, so their files stay cached through that.
/// </remarks>
internal sealed record GeneratorOutput<T>(EquatableArray<DiagnosticInfo> Diagnostics, EquatableArray<T> Types)
    where T : IEquatable<T>
{
    public static GeneratorOutput<T> Empty { get; } = new(EquatableArray<DiagnosticInfo>.Empty, EquatableArray<T>.Empty);
}

internal static class GeneratorOutput
{
    /// <summary>
    ///     Reports <paramref name="output" />'s diagnostics and emits one file per type model.
    /// </summary>
    /// <param name="context">The generator's initialization context.</param>
    /// <param name="output">The cross-property pass's result.</param>
    /// <param name="typesStepName">The tracking name for the per-type step.</param>
    /// <param name="emit">Builds the hint name and source for one type.</param>
    public static void Register<T>(
        IncrementalGeneratorInitializationContext context,
        IncrementalValueProvider<GeneratorOutput<T>> output, string typesStepName,
        Func<T, (string HintName, string Source)> emit)
        where T : IEquatable<T>
    {
        context.RegisterSourceOutput(
            output.Select(static (result, _) => result.Diagnostics),
            static (production, diagnostics) =>
            {
                foreach (DiagnosticInfo diagnostic in diagnostics)
                    production.ReportDiagnostic(diagnostic.ToDiagnostic());
            });

        IncrementalValuesProvider<T> types = output
            .SelectMany(static (result, _) => result.Types)
            .WithTrackingName(typesStepName);

        context.RegisterSourceOutput(
            types,
            (production, type) =>
            {
                (string hintName, string source) = emit(type);
                production.AddSource(hintName, source);
            });
    }
}