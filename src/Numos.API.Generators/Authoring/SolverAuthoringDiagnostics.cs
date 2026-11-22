using Microsoft.CodeAnalysis;

namespace Numos.API.Generators.Authoring;

internal static class SolverAuthoringDiagnostics
{
    private const string Category = "Numos.API.Authoring";

    public static readonly DiagnosticDescriptor CapturedVariableWritten = new(
        "NUMOSSOLVER001",
        "Solver lambda writes a captured variable",
        "The solver lambda writes captured variable '{0}', which replay can't see or restore; keep state that " +
        "outlives a tick in a rollback chunk field or in a class solver's [NumosSerialize] member",
        Category,
        DiagnosticSeverity.Warning,
        true);

    public static readonly DiagnosticDescriptor TopologyWithoutNeighbors = new(
        "NUMOSSOLVER002",
        "Solver reads a topology view that is always empty",
        "The solver {0} reads AtmosWorldSolverContext.Topology, but {1}, so the view is empty; {2}",
        Category,
        DiagnosticSeverity.Warning,
        true);

    public static readonly DiagnosticDescriptor NondeterministicApi = new(
        "NUMOSSOLVER003",
        "Solver code uses a value that differs on replay",
        "'{0}' differs between a run and its replay, or keeps state outside the checkpoint; derive the value from " +
        "world state, or draw random numbers from AtmosWorldSolverContext.Random(stream)",
        Category,
        DiagnosticSeverity.Warning,
        true);

    public static readonly DiagnosticDescriptor ViewStoredInMember = new(
        "NUMOSSOLVER004",
        "Solver view is stored in a field or property",
        "'{0}' stores a {1}, which is valid only during the tick that handed it out; keep it in a local or a " +
        "parameter",
        Category,
        DiagnosticSeverity.Warning,
        true);

    public static readonly DiagnosticDescriptor DangerousAccessOutsideSolver = new(
        "NUMOSSOLVER005",
        "Dangerous chunk view used outside solver code",
        "AtmosDangerousApi.{0} hands out live chunk storage, and writes through it bypass recording, so a replay " +
        "reaches a different state; from host code, write with SetChunkData or WriteChunkData, or send a " +
        "[SolverCommand] that the solver applies",
        Category,
        DiagnosticSeverity.Warning,
        true);
}
