using Microsoft.CodeAnalysis;

namespace Numos.API.Generators.Common;

/// <param name="Alias">The alias identifier, such as <c>Kelvin</c>.</param>
/// <param name="Storage">The CLR name of its storage primitive, such as <c>Single</c>.</param>
internal sealed record QuantityAlias(string Alias, string Storage);

/// <summary>
///     Reads Magnitudo quantity aliases from the compilation's <c>*.magnitudo</c> catalogs.
/// </summary>
/// <remarks>
///     Magnitudo emits its quantity aliases (<c>Kelvin</c>, <c>Mole</c>, ...) as global usings from a generator in
///     the same pass. One generator never sees the output of another. In a project that owns a catalog, an alias is
///     still an error type when another generator inspects it. Its storage type therefore has to come from the
///     catalog.
/// </remarks>
internal static class QuantityAliasCatalog
{
    private static readonly (SpecialType Special, string Name, string Keyword)[] Primitives =
    [
        (SpecialType.System_Boolean, "Boolean", "bool"),
        (SpecialType.System_Byte, "Byte", "byte"),
        (SpecialType.System_SByte, "SByte", "sbyte"),
        (SpecialType.System_Int16, "Int16", "short"),
        (SpecialType.System_UInt16, "UInt16", "ushort"),
        (SpecialType.System_Int32, "Int32", "int"),
        (SpecialType.System_UInt32, "UInt32", "uint"),
        (SpecialType.System_Int64, "Int64", "long"),
        (SpecialType.System_UInt64, "UInt64", "ulong"),
        (SpecialType.System_Single, "Single", "float"),
        (SpecialType.System_Double, "Double", "double"),
    ];

    /// <summary>
    ///     Maps the CLR name of each storage primitive an alias can use to its C# keyword.
    /// </summary>
    public static readonly Dictionary<string, string> PrimitiveKeywords = Primitives.ToDictionary(
        static primitive => primitive.Name,
        static primitive => primitive.Keyword,
        StringComparer.Ordinal);

    /// <summary>
    ///     Maps the special type of each storage primitive to its CLR name, the key of <see cref="PrimitiveKeywords" />.
    /// </summary>
    /// <remarks>
    ///     <see cref="SpecialType.System_String" /> is not in it, because no alias stores a string.
    /// </remarks>
    public static readonly Dictionary<SpecialType, string> PrimitiveNames = Primitives.ToDictionary(
        static primitive => primitive.Special,
        static primitive => primitive.Name);

    /// <summary>
    ///     Gets the alias-to-storage table from every <c>*.magnitudo</c> additional file.
    /// </summary>
    public static IncrementalValueProvider<EquatableArray<QuantityAlias>> Provider(
        IncrementalGeneratorInitializationContext context)
    {
        return context.AdditionalTextsProvider
            .Where(static file => file.Path.EndsWith(".magnitudo", StringComparison.OrdinalIgnoreCase))
            .SelectMany(static (file, cancellationToken) => Parse(file.GetText(cancellationToken)?.ToString() ?? ""))
            .Collect()
            .Select(static (aliases, _) => new EquatableArray<QuantityAlias>(aliases));
    }

    /// <summary>
    ///     Gets the storage primitive's CLR name for <paramref name="alias" />, or null when the catalog doesn't
    ///     define it.
    /// </summary>
    public static string? Find(EquatableArray<QuantityAlias> aliases, string alias)
    {
        foreach (QuantityAlias candidate in aliases)
        {
            if (candidate.Alias == alias)
                return candidate.Storage;
        }

        return null;
    }

    private static IEnumerable<QuantityAlias> Parse(string text)
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

            if (PrimitiveKeywords.ContainsKey(storage))
                yield return new QuantityAlias(parts[1].Trim(), storage);
        }
    }
}
