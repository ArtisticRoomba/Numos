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

    public Diagnostic ToDiagnostic()
    {
        return Diagnostic.Create(
            Descriptor,
            Location?.ToLocation() ?? Microsoft.CodeAnalysis.Location.None,
            Arguments.Cast<object?>().ToArray());
    }
}