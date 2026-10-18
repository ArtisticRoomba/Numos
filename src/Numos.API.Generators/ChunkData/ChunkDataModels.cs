using Numos.API.Generators.Common;

namespace Numos.API.Generators.ChunkData;

/// <summary>
///     One type in the chain enclosing a chunk data property, outermost first.
/// </summary>
/// <param name="Keyword">The declaration keyword(s) to reopen the type with, such as <c>class</c> or <c>record struct</c>.</param>
/// <param name="Name">The type's simple name.</param>
internal sealed record ContainingTypeModel(string Keyword, string Name);

/// <summary>
///     Everything the generator needs about one <c>[ChunkData]</c> property, as plain equatable data.
/// </summary>
/// <remarks>
///     This must never hold symbols, syntax, or the attribute context. The generator runs in downstream IDEs on
///     every keystroke, and a model that doesn't compare equal across runs defeats caching for everything after it.
/// </remarks>
internal sealed record ChunkDataPropertyModel(
    string Namespace,
    string TypeMetadataName,
    string TypeDisplay,
    string AccessorClassName,
    EquatableArray<ContainingTypeModel> ContainingTypes,
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
    string AccessorName,
    LocationInfo? Location,
    EquatableArray<DiagnosticInfo> Diagnostics)
{
    /// <summary>
    ///     Gets the property's name qualified by its containing types, for diagnostic messages.
    /// </summary>
    public string DisplayName
    {
        get
        {
            string types = string.Join(".", ContainingTypes.Select(static type => type.Name));
            return types + "." + PropertyName;
        }
    }

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
