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
///     the same pass, and one generator never sees another's output. In a project that owns a catalog, an alias is
///     still an error type when another generator inspects it, so its storage type has to come from the catalog.
/// </remarks>
internal static class QuantityAliasCatalog
{
    /// <summary>
    ///     Maps the CLR name of each storage primitive an alias can use to its C# keyword.
    /// </summary>
    public static readonly Dictionary<string, string> PrimitiveKeywords = new(StringComparer.Ordinal)
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
