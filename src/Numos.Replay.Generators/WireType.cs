using Microsoft.CodeAnalysis;
using Numos.API.Generators.Common;

namespace Numos.Replay.Generators;

internal enum WireTypeKind
{
    /// <summary>
    ///     A <see cref="System.IO.BinaryWriter" /> primitive; the name is its CLR name, such as <c>Single</c>.
    /// </summary>
    Primitive,

    String,

    /// <summary>
    ///     An unresolved identifier, presumably a Magnitudo quantity alias, still to be looked up in the catalog.
    /// </summary>
    Alias,

    Other
}

/// <summary>
///     A field type reduced to what the replay generators need to pick its encoding.
/// </summary>
internal sealed record WireType(WireTypeKind Kind, string Name)
{
    /// <summary>
    ///     Gets the C# keyword for a primitive, such as <c>float</c>.
    /// </summary>
    public string Keyword => Kind == WireTypeKind.String ? "string" : QuantityAliasCatalog.PrimitiveKeywords[Name];

    public static WireType From(ITypeSymbol type)
    {
        if (QuantityAliasCatalog.PrimitiveNames.TryGetValue(type.SpecialType, out string? name))
            return new WireType(WireTypeKind.Primitive, name);

        if (type.SpecialType == SpecialType.System_String)
            return new WireType(WireTypeKind.String, "String");

        // Magnitudo emits its quantity aliases (Kelvin, Mole, ...) from a generator in the same pass, and one
        // generator never sees another's output, so inside Numos.CoreSim an alias is still an error type here.
        if (type.TypeKind == TypeKind.Error)
            return new WireType(WireTypeKind.Alias, type.Name);

        return new WireType(WireTypeKind.Other, type.ToDisplayString());
    }

    /// <summary>
    ///     Replaces a quantity alias with its storage primitive, or returns this unchanged.
    /// </summary>
    public WireType Resolve(EquatableArray<QuantityAlias> aliases)
    {
        if (Kind != WireTypeKind.Alias)
            return this;

        string? storage = QuantityAliasCatalog.Find(aliases, Name);
        return storage is null ? this : new WireType(WireTypeKind.Primitive, storage);
    }
}
