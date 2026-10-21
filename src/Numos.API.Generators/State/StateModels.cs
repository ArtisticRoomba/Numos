using Numos.API.Generators.Common;

namespace Numos.API.Generators.State;

/// <param name="Id">The field's <c>[StateField]</c> id.</param>
/// <param name="MemberName">The escaped member name to read the value from.</param>
/// <param name="Shape">The member's type; aliases are resolved by the time a field reaches the emitter.</param>
internal sealed record StateFieldModel(int Id, string MemberName, StateShape Shape);

/// <param name="Type">The type to reopen.</param>
/// <param name="TypeName">The fully qualified type, used as the interface's type argument.</param>
/// <param name="IsReferenceType">Whether generated equality and ordering need null checks.</param>
/// <param name="IsSolverConfiguration">Whether to implement <c>IAtmosSolverConfiguration</c>'s equality and hash.</param>
/// <param name="Fields">The state fields in ascending id order.</param>
internal sealed record StateTypeModel(
    PartialTypeModel Type,
    string TypeName,
    bool IsReferenceType,
    bool IsSolverConfiguration,
    EquatableArray<StateFieldModel> Fields);

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
    bool IsReferenceType,
    bool IsSolverConfiguration,
    bool CanGenerate,
    EquatableArray<StateFieldDeclaration> Fields,
    EquatableArray<DiagnosticInfo> Diagnostics);

/// <summary>
///     A resolved declaration: the diagnostics to report, and the model to emit when there were no errors.
/// </summary>
internal sealed record StateTypeResult(StateTypeModel? Model, EquatableArray<DiagnosticInfo> Diagnostics);
