using Microsoft.CodeAnalysis;
using Numos.API.Generators.Common;

namespace Numos.API.Generators.ChunkData;

/// <summary>
///     One field of a <c>[ChunkComponent]</c> struct, as the chunk field it lowers to.
/// </summary>
/// <param name="Name">The field's name, unescaped.</param>
/// <param name="Key">The registry key, <c>{component key}/{wire name}</c>.</param>
/// <param name="ElementTypeDisplay">The element type, fully qualified, for generated code.</param>
/// <param name="Scratch">Whether the field is <c>[NumosScratch]</c> and registers as transient.</param>
/// <param name="Length">The <c>[PerChunk]</c> length, or null for one value per voxel.</param>
internal sealed record ChunkComponentFieldModel(
    string Name,
    string Key,
    string ElementTypeDisplay,
    bool Scratch,
    int? Length);

/// <summary>
///     Everything the generator needs about one <c>[ChunkComponent]</c> struct, as plain equatable data.
/// </summary>
/// <remarks>
///     Like <see cref="ChunkDataPropertyModel" />, this holds no symbols, syntax, or locations. <c>DisplayName</c> is
///     the struct's name qualified by its containing types, for messages. <c>AccessorName</c> is the name in
///     <c>Get{Name}</c> and the other accessors. <c>IsPublic</c> says whether the struct and every type around it are
///     public. <c>HasAccessors</c> says whether the extension members survived the language and name checks.
///     <c>Fields</c> are the valid fields in declaration order.
/// </remarks>
internal sealed record ChunkComponentModel(
    PartialTypeModel Type,
    string TypeDisplay,
    string DisplayName,
    string AccessorClassName,
    string AccessorName,
    bool IsPublic,
    bool ClearOnSleep,
    bool ClearOnVacuum,
    EquatableArray<ChunkComponentFieldModel> Fields,
    bool HasAccessors);

/// <summary>
///     A component field with where it was declared and the identity of its element type, for the key checks.
/// </summary>
/// <remarks>
///     <c>ElementTypeIdentity</c> is the element type without nullability, which registration compares.
/// </remarks>
internal sealed record ChunkComponentFieldDeclaration(
    ChunkComponentFieldModel Field,
    string ElementTypeIdentity,
    LocationInfo? Location);

/// <summary>
///     A <c>[ChunkComponent]</c> struct with where it was declared and what the per-struct checks found.
/// </summary>
internal sealed record ChunkComponentDeclaration(
    ChunkComponentModel Component,
    EquatableArray<ChunkComponentFieldDeclaration> Fields,
    LocationInfo? Location,
    EquatableArray<DiagnosticInfo> Diagnostics)
{
    /// <summary>
    ///     Gets whether a per-struct check reported an error. A warning leaves the component usable.
    /// </summary>
    public bool HasErrors => Diagnostics.Any(static diagnostic => diagnostic.Descriptor.DefaultSeverity == DiagnosticSeverity.Error);
}
