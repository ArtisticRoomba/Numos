using Numos.API.Generators.Common;

namespace Numos.API.Generators.State;

/// <param name="Id">The field's wire id, hashed from its wire name.</param>
/// <param name="MemberName">The escaped member name to read the value from.</param>
/// <param name="Shape">The member's type. Aliases are resolved by the time a field reaches the emitter.</param>
/// <param name="Name">The unescaped member name, which constructor parameters bind to.</param>
/// <param name="Settable">Whether an object initializer can assign the member (a mutable field, or a set/init accessor).</param>
/// <param name="InitOnly">
///     Whether the member can only be assigned in an object initializer. Other settable members are assigned after
///     construction, and only when the data contains them, so a missing one keeps whatever the constructor set.
/// </param>
internal sealed record StateFieldModel(int Id, string MemberName, StateShape Shape, string Name, bool Settable, bool InitOnly);

/// <param name="Name">The parameter name.</param>
/// <param name="Shape">The parameter type, classified with <see cref="StateShape.FromParameter" />.</param>
/// <param name="TypeDisplay">The parameter type as written, for diagnostics.</param>
/// <param name="DefaultLiteral">
///     The parameter's default value as a C# literal (<c>default</c> for null), or null when it has none.
/// </param>
internal sealed record StateParameterModel(string Name, StateShape Shape, string TypeDisplay, string? DefaultLiteral);

/// <param name="Parameters">The constructor's parameters in order.</param>
internal sealed record StateConstructorModel(EquatableArray<StateParameterModel> Parameters);

/// <summary>
///     How the generated reader builds a value: constructor arguments, then initializer assignments, both as complete
///     expressions over the <c>field{id}</c>/<c>has{id}</c> locals.
/// </summary>
internal sealed record StateReadPlan(EquatableArray<string> Arguments, EquatableArray<StateInitializerModel> Initializers);

/// <param name="MemberName">The escaped member to assign.</param>
/// <param name="Expression">The value to assign.</param>
/// <param name="Id">The field id, whose <c>has{id}</c> local guards an assignment after construction.</param>
/// <param name="AfterConstruction">
///     Whether to assign after construction when the field was read, rather than in the object initializer.
/// </param>
internal sealed record StateInitializerModel(string MemberName, string Expression, int Id, bool AfterConstruction);

/// <summary>
///     How a solver configuration's generated <c>CreateSnapshot</c> works.
/// </summary>
internal enum SnapshotKind
{
    /// <summary>
    ///     Not generated: the type isn't a solver configuration, or it writes its own.
    /// </summary>
    None,

    /// <summary>
    ///     Every member is provably immutable, so the snapshot is the instance itself.
    /// </summary>
    Self,

    /// <summary>
    ///     A round trip through the wire format, which deep-copies and re-resolves gas references.
    /// </summary>
    Copy,
}

/// <param name="Type">The type to reopen.</param>
/// <param name="TypeName">The fully qualified type, used as the interface's type argument.</param>
/// <param name="IsReferenceType">Whether generated equality and ordering need null checks.</param>
/// <param name="IsSolverConfiguration">Whether to implement <c>IAtmosSolverConfiguration</c>'s equality and hash.</param>
/// <param name="IsWorldSolver">
///     Whether the type is a class solver, which gets <c>INumosStateTarget</c> rather than <c>INumosState&lt;T&gt;</c>.
/// </param>
/// <param name="StateKey">The constant the type's <c>Key</c> property returns, or null.</param>
/// <param name="Snapshot">Whether and how to generate <c>CreateSnapshot</c>.</param>
/// <param name="Fields">The state fields in ascending id order.</param>
/// <param name="Read">How the reader constructs the value.</param>
internal sealed record StateTypeModel(
    PartialTypeModel Type,
    string TypeName,
    bool IsReferenceType,
    bool IsSolverConfiguration,
    bool IsWorldSolver,
    string? StateKey,
    SnapshotKind Snapshot,
    EquatableArray<StateFieldModel> Fields,
    StateReadPlan Read);

/// <summary>
///     A state field before alias resolution, with the location its type diagnostic points at.
/// </summary>
internal sealed record StateFieldDeclaration(StateFieldModel Field, string DisplayName, LocationInfo? Location);

/// <summary>
///     What the transform knows about one type with <c>[NumosSerialize]</c> members. <see cref="Fields" /> is empty when a
///     diagnostic already ruled out generating anything.
/// </summary>
internal sealed record StateTypeDeclaration(
    PartialTypeModel Type,
    string TypeName,
    string DisplayName,
    LocationInfo? Location,
    bool IsReferenceType,
    bool IsSolverConfiguration,
    bool IsWorldSolver,
    string? StateKey,
    SnapshotKind Snapshot,
    string? Registration,
    bool CanGenerate,
    EquatableArray<StateFieldDeclaration> Fields,
    EquatableArray<StateConstructorModel> Constructors,
    EquatableArray<DiagnosticInfo> Diagnostics);

/// <summary>
///     A resolved declaration. It holds the diagnostics to report and the model to emit when there were no errors. It
///     also holds the fully qualified type to register for loading, if it is a solver configuration that the module
///     initializer can name.
/// </summary>
internal sealed record StateTypeResult(StateTypeModel? Model, string? Registration, EquatableArray<DiagnosticInfo> Diagnostics);
