using Microsoft.CodeAnalysis;

namespace Numos.Units.Analyzers;

internal static class Diagnostics
{
    private const string Category = "DimensionalAnalysis";

    internal readonly static DiagnosticDescriptor InvalidCatalog = new(
        "NUMOSUNIT000",
        "Invalid units catalog",
        "Invalid units catalog: {0}",
        Category,
        DiagnosticSeverity.Error,
        true);

    internal readonly static DiagnosticDescriptor IncompatibleOperands = new(
        "NUMOSUNIT001",
        "Incompatible quantities",
        "Operator '{0}' cannot combine '{1}' and '{2}'",
        Category,
        DiagnosticSeverity.Error,
        true);

    internal readonly static DiagnosticDescriptor IncompatibleAssignment = new(
        "NUMOSUNIT002",
        "Incompatible quantity assignment",
        "Cannot assign '{0}' to '{1}'",
        Category,
        DiagnosticSeverity.Error,
        true);

    internal readonly static DiagnosticDescriptor IncompatibleArgument = new(
        "NUMOSUNIT003",
        "Incompatible argument quantity",
        "Argument has quantity '{0}', but parameter '{1}' requires '{2}'",
        Category,
        DiagnosticSeverity.Error,
        true);

    internal readonly static DiagnosticDescriptor IncompatibleReturn = new(
        "NUMOSUNIT004",
        "Incompatible return quantity",
        "Returned expression has quantity '{0}', but the method returns '{1}'",
        Category,
        DiagnosticSeverity.Error,
        true);
}