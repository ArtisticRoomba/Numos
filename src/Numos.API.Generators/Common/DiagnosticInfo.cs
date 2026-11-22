using Microsoft.CodeAnalysis;

namespace Numos.API.Generators.Common;

/// <summary>
///     A diagnostic captured during a generator transform and reported later from a source output step.
/// </summary>
/// <remarks>
///     Descriptors are static singletons with value equality, so holding one keeps the model equatable.
/// </remarks>
internal sealed record DiagnosticInfo(
    DiagnosticDescriptor Descriptor,
    LocationInfo? Location,
    EquatableArray<string> Arguments)
{
    public static DiagnosticInfo Create(DiagnosticDescriptor descriptor, LocationInfo? location, params string[] arguments)
    {
        return new DiagnosticInfo(descriptor, location, new EquatableArray<string>(arguments));
    }

    /// <summary>
    ///     Builds the diagnostic, at a location in <paramref name="tree" /> when there is one.
    /// </summary>
    /// <param name="tree">The compilation's tree for <see cref="LocationInfo.FilePath" />, if it has one.</param>
    /// <remarks>
    ///     Use <see cref="DiagnosticReporter" /> to report, because only a tree location honors
    ///     <c>#pragma warning disable</c>.
    /// </remarks>
    public Diagnostic ToDiagnostic(SyntaxTree? tree)
    {
        return Diagnostic.Create(
            Descriptor,
            Location?.ToLocation(tree) ?? Microsoft.CodeAnalysis.Location.None,
            Arguments.Cast<object?>().ToArray());
    }
}