using Microsoft.CodeAnalysis;

namespace Numos.API.Generators.Common;

/// <summary>
///     A diagnostic captured during a generator transform and reported later from the source output step.
/// </summary>
/// <remarks>
///     Holds the descriptor's ID rather than the descriptor so the model stays plain data; the generator maps it
///     back through its own descriptor table.
/// </remarks>
internal sealed record DiagnosticInfo(string Id, LocationInfo? Location, EquatableArray<string> Arguments)
{
    public static DiagnosticInfo Create(DiagnosticDescriptor descriptor, LocationInfo? location, params string[] arguments)
    {
        return new DiagnosticInfo(descriptor.Id, location, new EquatableArray<string>(arguments));
    }

    public Diagnostic ToDiagnostic(DiagnosticDescriptor descriptor)
    {
        return Diagnostic.Create(descriptor, Location?.ToLocation() ?? Microsoft.CodeAnalysis.Location.None,
            Arguments.Cast<object?>().ToArray());
    }
}
