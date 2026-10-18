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
    ///     An unresolved identifier, presumably a Numos.Units quantity alias, still to be looked up in the catalog.
    /// </summary>
    Alias,

    Other
}

/// <summary>
///     A field type reduced to what the replay generators need to pick its encoding.
/// </summary>
internal sealed record WireType(WireTypeKind Kind, string Name)
{
    private static readonly Dictionary<SpecialType, string> Primitives = new()
    {
        [SpecialType.System_Boolean] = "Boolean",
        [SpecialType.System_Byte] = "Byte",
        [SpecialType.System_SByte] = "SByte",
        [SpecialType.System_Int16] = "Int16",
        [SpecialType.System_UInt16] = "UInt16",
        [SpecialType.System_Int32] = "Int32",
        [SpecialType.System_UInt32] = "UInt32",
        [SpecialType.System_Int64] = "Int64",
        [SpecialType.System_UInt64] = "UInt64",
        [SpecialType.System_Single] = "Single",
        [SpecialType.System_Double] = "Double",
    };

    private static readonly Dictionary<string, string> Keywords = new(StringComparer.Ordinal)
    {
        ["Boolean"] = "bool",
        ["Byte"] = "byte",
        ["SByte"] = "sbyte",
        ["Int16"] = "short",
        ["UInt16"] = "ushort",
        ["Int32"] = "int",
        ["UInt32"] = "uint",
        ["Int64"] = "long",
        ["UInt64"] = "ulong",
        ["Single"] = "float",
        ["Double"] = "double",
    };

    /// <summary>
    ///     Gets the C# keyword for a primitive, such as <c>float</c>.
    /// </summary>
    public string Keyword => Kind == WireTypeKind.String ? "string" : Keywords[Name];

    public static WireType From(ITypeSymbol type)
    {
        if (Primitives.TryGetValue(type.SpecialType, out string? name))
            return new WireType(WireTypeKind.Primitive, name);

        if (type.SpecialType == SpecialType.System_String)
            return new WireType(WireTypeKind.String, "String");

        // Numos.Units emits its quantity aliases (Kelvin, Mole, ...) from a generator in the same pass, and one
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

        foreach (QuantityAlias alias in aliases)
        {
            if (alias.Alias == Name)
                return new WireType(WireTypeKind.Primitive, alias.Storage);
        }

        return this;
    }

    /// <summary>
    ///     Reads the alias-to-storage table from the compilation's <c>*.numosunits</c> catalogs.
    /// </summary>
    public static IncrementalValueProvider<EquatableArray<QuantityAlias>> QuantityAliases(
        IncrementalGeneratorInitializationContext context)
    {
        return context.AdditionalTextsProvider
            .Where(static file => file.Path.EndsWith(".numosunits", StringComparison.OrdinalIgnoreCase))
            .SelectMany(static (file, cancellationToken) => ParseCatalog(file.GetText(cancellationToken)?.ToString() ?? ""))
            .Collect()
            .Select(static (aliases, _) => new EquatableArray<QuantityAlias>(aliases));
    }

    private static IEnumerable<QuantityAlias> ParseCatalog(string text)
    {
        // Lines look like "temperature | Kelvin | global::System.Single | temperature=1".
        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#')
                continue;

            string[] parts = trimmed.Split('|');
            if (parts.Length < 3)
                continue;

            string storage = parts[2].Trim();
            if (storage.StartsWith("global::", StringComparison.Ordinal))
                storage = storage.Substring("global::".Length);
            if (storage.StartsWith("System.", StringComparison.Ordinal))
                storage = storage.Substring("System.".Length);

            if (Keywords.ContainsKey(storage))
                yield return new QuantityAlias(parts[1].Trim(), storage);
        }
    }
}

/// <param name="Alias">The alias identifier, such as <c>Kelvin</c>.</param>
/// <param name="Storage">The CLR name of its storage primitive, such as <c>Single</c>.</param>
internal sealed record QuantityAlias(string Alias, string Storage);
