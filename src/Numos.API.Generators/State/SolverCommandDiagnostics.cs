using Microsoft.CodeAnalysis;

namespace Numos.API.Generators.State;

internal static class SolverCommandDiagnostics
{
    private const string Category = "Numos.API.SolverCommand";

    public static readonly DiagnosticDescriptor NotSolverClass = new(
        "NUMOSCOMMAND001",
        "Solver command must be declared in a partial class solver",
        "Solver command '{0}' is declared in '{1}', which {2}",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor NotPartialDefinition = new(
        "NUMOSCOMMAND002",
        "Solver command must be an instance partial void declaration",
        "Solver command '{0}' must be a non-generic instance 'partial void' method declaration with no body; the " +
        "generator supplies the body, and Apply{1} holds the command's logic",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor UnsupportedParameter = new(
        "NUMOSCOMMAND003",
        "Solver command parameter is not supported",
        "Parameter '{0}' of solver command '{1}' {2}",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor MissingApply = new(
        "NUMOSCOMMAND004",
        "Solver command has no Apply method",
        "Solver command '{0}' needs an instance method '{1}({2})' or '{1}({3})' with the same parameter types, which " +
        "holds the command's logic; the second form receives the world that holds the solver",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor DuplicateId = new(
        "NUMOSCOMMAND005",
        "Two solver commands have the same command id",
        "Solver commands '{0}' and '{1}' of '{2}' have the same command id; give one a different wire name with " +
        "[SolverCommand(\"...\")]",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor BlankWireName = new(
        "NUMOSCOMMAND006",
        "Solver command wire name is blank",
        "Solver command '{0}' has a blank wire name; pass a nonblank name or none to use the method name",
        Category,
        DiagnosticSeverity.Error,
        true);
}
