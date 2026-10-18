using Numos.API.Generators.Common;

namespace Numos.API.Generators.SolverDependency;

/// <summary>
///     How the generated <c>InjectSolverDependencies</c> hooks into the type's base classes.
/// </summary>
/// <param name="IsOverride">
///     Whether a base class already provides an overridable hook, so the type overrides it instead of implementing
///     the interface.
/// </param>
/// <param name="Accessibility">The hook's accessibility, as C# modifiers.</param>
/// <param name="CallsBase">Whether the override calls the base hook first; <see langword="false" /> when it is abstract.</param>
internal sealed record DependencyHookModel(bool IsOverride, string Accessibility, bool CallsBase);

/// <summary>
///     Everything the emitter needs about one <c>[SolverDependency]</c> property, as plain equatable data.
/// </summary>
/// <remarks>
///     Like the chunk data models, this must never hold symbols or syntax.
/// </remarks>
internal sealed record SolverDependencyPropertyModel(
    string PropertyName,
    string PropertyModifiers,
    string PropertyTypeDisplay,
    bool PropertyTypeIsAnnotated,
    string ResolveTypeDisplay,
    bool Optional);

/// <summary>
///     Facts about the declaring type that need its base chain, worked out once per type.
/// </summary>
/// <param name="Hook">How the type's injection hook relates to its base classes.</param>
/// <param name="Diagnostics">
///     Type-level diagnostics without a location; the output step places them on the type's first declaration.
/// </param>
internal sealed record DependencyTypeAnalysis(DependencyHookModel Hook, EquatableArray<DiagnosticInfo> Diagnostics);

/// <summary>
///     A <c>[SolverDependency]</c> property with where it was declared and what the per-property checks found.
/// </summary>
/// <param name="Type">The declaring type.</param>
/// <param name="Property">The location-free part that reaches the emitter.</param>
/// <param name="Location">The property's identifier.</param>
/// <param name="TypeLocation">The identifier of the type declaration that contains the property.</param>
/// <param name="Diagnostics">Problems with the property itself.</param>
/// <param name="TypeDiagnostics">
///     Checks on the containing types that name the property, so they're reported once per property.
/// </param>
/// <param name="TypeAnalysis">Set only on the type's first <c>[SolverDependency]</c> property.</param>
internal sealed record SolverDependencyDeclaration(
    PartialTypeModel Type,
    SolverDependencyPropertyModel Property,
    LocationInfo? Location,
    LocationInfo? TypeLocation,
    EquatableArray<DiagnosticInfo> Diagnostics,
    EquatableArray<DiagnosticInfo> TypeDiagnostics,
    DependencyTypeAnalysis? TypeAnalysis);

/// <summary>
///     The generated file for one class.
/// </summary>
/// <param name="Type">The class to reopen.</param>
/// <param name="Hook">How the generated hook relates to the class's base classes.</param>
/// <param name="Members">The class's properties that passed every check, in source order.</param>
internal sealed record SolverDependencyTypeModel(
    PartialTypeModel Type,
    DependencyHookModel Hook,
    EquatableArray<SolverDependencyPropertyModel> Members);