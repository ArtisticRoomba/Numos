using Microsoft.CodeAnalysis;

namespace Numos.API.Generators.Solver;

internal static class AtmosSolverDiagnostics
{
    private const string Category = "Numos.API.Solver";

    public static readonly DiagnosticDescriptor TypeNotPartial = new(
        "NUMOSSOLVER010",
        "Type containing a solver class must be partial",
        "Type '{0}' must be partial so the generator can add the [AtmosSolver] descriptor of '{1}'",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor InvalidShape = new(
        "NUMOSSOLVER011",
        "Solver class can't have a generated descriptor",
        "Solver class '{0}' {1}, so the generator can't build or register its descriptor",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor NotWorldSolver = new(
        "NUMOSSOLVER012",
        "Solver class does not implement IAtmosWorldSolver",
        "Class '{0}' is marked [AtmosSolver] but does not implement Numos.API.IAtmosWorldSolver",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor NoConstructor = new(
        "NUMOSSOLVER013",
        "Solver class has no parameterless constructor",
        "Solver class '{0}' has no public or internal parameterless constructor for the generated factory to call",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor BlankName = new(
        "NUMOSSOLVER014",
        "Solver name is blank",
        "The [AtmosSolver] name of '{0}' is blank; give the stage a stable, namespaced name",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor AfterAndBefore = new(
        "NUMOSSOLVER015",
        "Solver sets both After and Before",
        "Solver class '{0}' sets both After and Before; place the stage relative to one stage only",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor InvalidRate = new(
        "NUMOSSOLVER016",
        "Solver rate is invalid",
        "Solver class '{0}' {1}",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor NoRate = new(
        "NUMOSSOLVER017",
        "Solver runs every tick",
        "Solver class '{0}' sets neither RunsPerSecond nor Interval, so the stage runs every tick; set one of them " +
        "to choose its rate",
        Category,
        DiagnosticSeverity.Warning,
        true);

    public static readonly DiagnosticDescriptor DuplicateName = new(
        "NUMOSSOLVER018",
        "Solver name is declared more than once",
        "Solver name '{0}' on '{1}' is also declared by '{2}'; each [AtmosSolver] name must be unique",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor InvalidSelection = new(
        "NUMOSSOLVER019",
        "Solver neighbor selection is invalid",
        "'{0}' {1}",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor AlreadyImplemented = new(
        "NUMOSSOLVER020",
        "Solver class implements its descriptor by hand",
        "Solver class '{0}' already declares Descriptor or implements Numos.API.IAtmosSolverDefinition<T> itself; " +
        "the generator supplies both for [AtmosSolver] classes",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor SelectorWithoutSolver = new(
        "NUMOSSOLVER021",
        "Neighbor selector is not in a solver class",
        "Neighbor selector '{0}' is declared in '{1}', which is not marked [AtmosSolver], so nothing uses it",
        Category,
        DiagnosticSeverity.Error,
        true);
}
