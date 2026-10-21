using Numos.API.Generators.Common;

namespace Numos.API.Generators.State;

/// <param name="Id">The field's <c>[StateField]</c> id.</param>
/// <param name="MemberName">The escaped member name to read the value from.</param>
/// <param name="Shape">The member's type; aliases are resolved by the time a field reaches the emitter.</param>
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

/// <param name="Type">The type to reopen.</param>
/// <param name="TypeName">The fully qualified type, used as the interface's type argument.</param>
/// <param name="IsReferenceType">Whether generated equality and ordering need null checks.</param>
/// <param name="IsSolverConfiguration">Whether to implement <c>IAtmosSolverConfiguration</c>'s equality and hash.</param>
/// <param name="StateKey">The <c>[NumosState(Key)]</c>, or null.</param>
/// <param name="Fields">The state fields in ascending id order.</param>
/// <param name="Read">How the reader constructs the value.</param>
internal sealed record StateTypeModel(
    PartialTypeModel Type,
    string TypeName,
    bool IsReferenceType,
    bool IsSolverConfiguration,
    string? StateKey,
    EquatableArray<StateFieldModel> Fields,
    StateReadPlan Read);

/// <summary>
///     A state field before alias resolution, with the location its type diagnostic points at.
/// </summary>
internal sealed record StateFieldDeclaration(StateFieldModel Field, string DisplayName, LocationInfo? Location);

/// <summary>
///     What the attribute transform knows about one <c>[NumosState]</c> type. <see cref="Fields" /> is empty when a
///     diagnostic already ruled out generating anything.
/// </summary>
internal sealed record StateTypeDeclaration(
    PartialTypeModel Type,
    string TypeName,
    string DisplayName,
    LocationInfo? Location,
    bool IsReferenceType,
    bool IsSolverConfiguration,
    string? StateKey,
    bool CanGenerate,
    EquatableArray<StateFieldDeclaration> Fields,
    EquatableArray<StateConstructorModel> Constructors,
    EquatableArray<DiagnosticInfo> Diagnostics);

/// <summary>
///     A resolved declaration: the diagnostics to report, and the model to emit when there were no errors.
/// </summary>
internal sealed record StateTypeResult(StateTypeModel? Model, EquatableArray<DiagnosticInfo> Diagnostics);
