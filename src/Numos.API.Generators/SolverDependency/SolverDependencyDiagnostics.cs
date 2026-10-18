using Microsoft.CodeAnalysis;

namespace Numos.API.Generators.SolverDependency;

internal static class SolverDependencyDiagnostics
{
    private const string Category = "Numos.API.SolverDependency";

    public static readonly DiagnosticDescriptor TypeNotPartial = new(
        "NUMOSDEP001",
        "Type containing solver dependencies must be partial",
        "Type '{0}' must be partial because it contains solver dependency '{1}'",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor NotClass = new(
        "NUMOSDEP002",
        "Solver dependencies must be declared in a class",
        "Solver dependency '{0}' is declared in '{1}', which is not a class; solvers are injected by reference, " +
        "so only classes can hold dependencies",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor NotPartialDefinition = new(
        "NUMOSDEP003",
        "Solver dependency must be an unimplemented partial property",
        "Solver dependency '{0}' must be a partial property declaration with only a get accessor and no body; " +
        "the generator supplies the implementation",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor Static = new(
        "NUMOSDEP004",
        "Solver dependency must not be static",
        "Solver dependency '{0}' must be an instance property; dependencies are injected per solver instance",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor NotReferenceType = new(
        "NUMOSDEP005",
        "Solver dependency must have a reference type",
        "Solver dependency '{0}' has type '{1}', which is not a reference type; services and solvers are " +
        "resolved as class or interface types",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor OptionalMismatch = new(
        "NUMOSDEP006",
        "Solver dependency nullability disagrees with Optional",
        "Solver dependency '{0}' {1}",
        Category,
        DiagnosticSeverity.Warning,
        true);

    public static readonly DiagnosticDescriptor NotWorldSolver = new(
        "NUMOSDEP007",
        "Type with solver dependencies is not a world solver",
        "Type '{0}' declares solver dependencies but does not implement Numos.API.IAtmosWorldSolver, so the " +
        "pipeline will never inject them",
        Category,
        DiagnosticSeverity.Warning,
        true);

    public static readonly DiagnosticDescriptor BaseNotOverridable = new(
        "NUMOSDEP008",
        "Base class injects dependencies without an overridable hook",
        "Base type '{0}' of '{1}' implements Numos.API.IAtmosSolverDependencies but has no overridable " +
        "InjectSolverDependencies(AtmosSolverDependencyResolver) method, so generated injection would hide the " +
        "base's; add a protected virtual InjectSolverDependencies to the base",
        Category,
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor AlreadyImplemented = new(
        "NUMOSDEP009",
        "Type implements dependency injection by hand",
        "Type '{0}' already declares InjectSolverDependencies or implements " +
        "Numos.API.IAtmosSolverDependencies.InjectDependencies itself; the generator supplies both for " +
        "[SolverDependency] properties, so move those dependencies into the hand-written method instead",
        Category,
        DiagnosticSeverity.Error,
        true);
}