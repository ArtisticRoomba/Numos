using Microsoft.CodeAnalysis;
using Numos.API.Generators.Common;

namespace Numos.API.Generators.State;

/// <summary>
///     Symbol helpers for the attributes and members that <see cref="StateGenerator" /> and
///     <see cref="SnapshotGenerator" /> both read.
/// </summary>
internal static class StateSymbols
{
    private const string SerializeMetadataName = "Numos.CoreSim.State.NumosSerializeAttribute";
    private const string IgnoreMetadataName = "Numos.CoreSim.State.NumosIgnoreAttribute";

    /// <summary>
    ///     Gets the <c>[NumosSerialize]</c> on <paramref name="symbol" />, or null.
    /// </summary>
    public static AttributeData? FindSerialize(ISymbol symbol) => FindAttribute(symbol, SerializeMetadataName);

    /// <summary>
    ///     Whether <paramref name="symbol" /> has <c>[NumosIgnore]</c>.
    /// </summary>
    public static bool IsIgnored(ISymbol symbol) => FindAttribute(symbol, IgnoreMetadataName) is not null;

    private static AttributeData? FindAttribute(ISymbol symbol, string metadataName)
    {
        foreach (AttributeData attribute in symbol.GetAttributes())
        {
            if (attribute.AttributeClass is { } attributeClass &&
                PartialTypeModel.GetMetadataName(attributeClass) == metadataName)
                return attribute;
        }

        return null;
    }

    /// <summary>
    ///     Whether <paramref name="property" /> has a compiler-generated backing field in <paramref name="type" />.
    /// </summary>
    /// <remarks>
    ///     That covers auto-properties, positional record properties, and <c>field</c>-keyword properties.
    /// </remarks>
    public static bool HasBackingField(INamedTypeSymbol type, IPropertySymbol property)
    {
        foreach (ISymbol candidate in type.GetMembers())
        {
            if (candidate is IFieldSymbol backing && SymbolEqualityComparer.Default.Equals(backing.AssociatedSymbol, property))
                return true;
        }

        return false;
    }

    /// <summary>
    ///     Whether the attribute passes the named argument <paramref name="name" />, whatever its value.
    /// </summary>
    public static bool HasNamedArgument(AttributeData attribute, string name) => TryGetNamed(attribute, name, out _);

    /// <summary>
    ///     Gets the string value of the named argument <paramref name="name" />, or null.
    /// </summary>
    public static string? NamedString(AttributeData attribute, string name) =>
        TryGetNamed(attribute, name, out TypedConstant value) ? value.Value as string : null;

    /// <summary>
    ///     Gets the <c>typeof</c> value of the named argument <paramref name="name" />, or null.
    /// </summary>
    public static ITypeSymbol? NamedType(AttributeData attribute, string name) =>
        TryGetNamed(attribute, name, out TypedConstant value) ? value.Value as ITypeSymbol : null;

    private static bool TryGetNamed(AttributeData attribute, string name, out TypedConstant value)
    {
        foreach (KeyValuePair<string, TypedConstant> argument in attribute.NamedArguments)
        {
            if (argument.Key == name)
            {
                value = argument.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    /// <summary>
    ///     Gets the <c>Codec</c> a <c>[NumosSerialize]</c> names, or null.
    /// </summary>
    public static ITypeSymbol? CodecOf(AttributeData serialize) => NamedType(serialize, "Codec");

    /// <summary>
    ///     Whether <paramref name="type" /> has a field or property marked <c>[NumosSerialize]</c>.
    /// </summary>
    public static bool HasSerializedMembers(INamedTypeSymbol type)
    {
        foreach (ISymbol member in type.GetMembers())
        {
            if (member is IFieldSymbol or IPropertySymbol && FindSerialize(member) is not null)
                return true;
        }

        return false;
    }

    /// <summary>
    ///     Gets whether <paramref name="member" /> has <c>[NumosSerialize]</c>, and whether that names a codec.
    /// </summary>
    public static bool IsSerialized(ISymbol member, out bool hasCodec)
    {
        AttributeData? attribute = FindSerialize(member);
        hasCodec = attribute is not null && CodecOf(attribute) is not null;
        return attribute is not null;
    }
}
