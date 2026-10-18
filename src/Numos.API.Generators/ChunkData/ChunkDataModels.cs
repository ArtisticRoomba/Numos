using Numos.API.Generators.Common;

namespace Numos.API.Generators.ChunkData;

/// <summary>
///     Everything the generator needs about one <c>[ChunkData]</c> property, as plain equatable data.
/// </summary>
/// <remarks>
///     This must never hold symbols, syntax, or the attribute context. The generator runs in downstream IDEs on
///     every keystroke, and a model that doesn't compare equal across runs defeats caching for everything after it.
///     Locations live on <see cref="ChunkDataDeclaration" /> instead, so moving a declaration doesn't change this.
///     <c>DisplayName</c> is the property's name qualified by its containing types, for diagnostic messages.
/// </remarks>
internal sealed record ChunkDataPropertyModel(
    PartialTypeModel Type,
    string TypeDisplay,
    string AccessorClassName,
    string DisplayName,
    string PropertyName,
    string PropertyModifiers,
    string PropertyTypeDisplay,
    bool PropertyTypeIsAnnotated,
    string? ElementTypeDisplay,
    string? ElementTypeIdentity,
    bool AccessorIsPublic,
    string? Key,
    bool Rollback,
    int? Length,
    string AccessorName);

/// <summary>
///     A <c>[ChunkData]</c> property with where it was declared and what the per-property checks found.
/// </summary>
internal sealed record ChunkDataDeclaration(
    ChunkDataPropertyModel Property,
    LocationInfo? Location,
    EquatableArray<DiagnosticInfo> Diagnostics)
{
    /// <summary>
    ///     Gets whether a per-property check failed. Every check made in the transform is an error.
    /// </summary>
    public bool HasErrors => Diagnostics.Count > 0;
}

/// <summary>
///     Member names already taken on the chunk view types, plus whether the Dangerous view exists at all.
/// </summary>
internal sealed record ChunkViewInfo(
    bool HasDangerous,
    EquatableArray<string> SolverChunkMembers,
    EquatableArray<string> DangerousChunkMembers);

/// <summary>
///     Which chunk views get an accessor for one property, and under what member name.
/// </summary>
internal readonly record struct AccessorTargets(string MemberName, bool OnSolverChunk, bool OnDangerousChunk);

/// <summary>
///     One property in a generated file, with its accessor if one survived the collision checks.
/// </summary>
internal sealed record ChunkDataMemberModel(ChunkDataPropertyModel Property, AccessorTargets? Accessor);

/// <summary>
///     The generated file for one declaring type.
/// </summary>
/// <param name="Members">The type's properties that passed every check, in source order.</param>
/// <param name="UseExtensionBlocks">Whether C# 14 extension properties are available.</param>
/// <param name="HasDangerous">Whether <c>Numos.API.Dangerous</c> is referenced.</param>
internal sealed record ChunkDataTypeModel(
    EquatableArray<ChunkDataMemberModel> Members,
    bool UseExtensionBlocks,
    bool HasDangerous);
