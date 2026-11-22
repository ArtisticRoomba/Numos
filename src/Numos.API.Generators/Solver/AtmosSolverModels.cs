using Numos.API.Generators.Common;

namespace Numos.API.Generators.Solver;

/// <summary>
///     The neighbor selection a generated descriptor passes to <c>AtmosNeighborSelection</c>.
/// </summary>
/// <param name="Key">The selection key, as written into the generated code.</param>
/// <param name="IncludeCartesian">Whether Cartesian neighbors participate.</param>
/// <param name="Selector">The C# expression for the explicit link selector: a lambda or a method group.</param>
internal sealed record AtmosSolverNeighborsModel(string Key, bool IncludeCartesian, string Selector);

/// <summary>
///     Everything the emitter needs about one <c>[AtmosSolver]</c> class, as plain equatable data.
/// </summary>
/// <remarks>
///     Like the other generator models, this must never hold symbols or syntax.
/// </remarks>
/// <param name="Type">The class to reopen.</param>
/// <param name="TypeDisplay">The fully qualified class name, with <c>global::</c>.</param>
/// <param name="Name">The stage name.</param>
/// <param name="After">The stage to run after, or null.</param>
/// <param name="Before">The stage to run before, or null.</param>
/// <param name="RunsPerSecond">Runs per second, or zero.</param>
/// <param name="Interval">The interval in ticks, or zero.</param>
/// <param name="Neighbors">The neighbor selection, or null for an empty view.</param>
/// <param name="HidesInherited">Whether a base class has a visible <c>Descriptor</c> that the generated one hides.</param>
internal sealed record AtmosSolverModel(
    PartialTypeModel Type,
    string TypeDisplay,
    string Name,
    string? After,
    string? Before,
    int RunsPerSecond,
    int Interval,
    AtmosSolverNeighborsModel? Neighbors,
    bool HidesInherited);

/// <summary>
///     An <c>[AtmosSolver]</c> class with where it was declared and what the per-class checks found.
/// </summary>
/// <param name="Model">The location-free part that reaches the emitter. It is built even when a check fails.</param>
/// <param name="DisplayName">The class name for diagnostics.</param>
/// <param name="Location">The class identifier.</param>
/// <param name="Diagnostics">Problems with the class and its attribute.</param>
internal sealed record AtmosSolverDeclaration(
    AtmosSolverModel Model,
    string DisplayName,
    LocationInfo? Location,
    EquatableArray<DiagnosticInfo> Diagnostics);

/// <summary>
///     The cross-class pass's result: the generated files, and the classes the module initializer registers.
/// </summary>
/// <param name="Output">Every diagnostic, and one model per class that passed its own checks.</param>
/// <param name="Registrations">
///     The fully qualified classes to register, in ordinal order. A class whose name another class also declares is
///     left out, although it still gets its partial.
/// </param>
internal sealed record AtmosSolverOutput(
    GeneratorOutput<AtmosSolverModel> Output,
    EquatableArray<string> Registrations);
