using Numos.API.Generators.Common;

namespace Numos.API.Generators.SolverDependency;

/// <summary>
///     One type in the chain enclosing a solver dependency, outermost first.
/// </summary>
/// <param name="Keyword">The declaration keyword(s) to reopen the type with, such as <c>class</c> or <c>record</c>.</param>
/// <param name="Declaration">The name plus any type parameter list, such as <c>Cache&lt;TKey, TValue&gt;</c>.</param>
internal sealed record DependencyContainingTypeModel(string Keyword, string Declaration);

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
///     Everything the generator needs about one <c>[SolverDependency]</c> property, as plain equatable data.
/// </summary>
/// <remarks>
///     Like the chunk data models, this must never hold symbols or syntax. Type-level facts are repeated on every
///     property of the type, and type-level diagnostics are reported once per type when the output step groups them.
/// </remarks>
internal sealed record SolverDependencyPropertyModel(
    string Namespace,
    string TypeMetadataName,
    string TypeDisplayName,
    EquatableArray<DependencyContainingTypeModel> ContainingTypes,
    DependencyHookModel Hook,
    string PropertyName,
    string PropertyModifiers,
    string PropertyTypeDisplay,
    bool PropertyTypeIsAnnotated,
    string ResolveTypeDisplay,
    bool Optional,
    LocationInfo? Location,
    EquatableArray<DiagnosticInfo> Diagnostics,
    EquatableArray<DiagnosticInfo> TypeDiagnostics);
