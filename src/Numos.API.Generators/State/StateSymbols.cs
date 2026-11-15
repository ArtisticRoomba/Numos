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
    public static bool HasBackingField(INamedTypeSymbol type, IPropertySymbol property) =>
        FindBackingField(type, property) is not null;

    /// <summary>
    ///     Gets the compiler-generated backing field of <paramref name="property" /> in <paramref name="type" />, or null.
    /// </summary>
    public static IFieldSymbol? FindBackingField(INamedTypeSymbol type, IPropertySymbol property)
    {
        foreach (ISymbol candidate in type.GetMembers())
        {
            if (candidate is IFieldSymbol backing && SymbolEqualityComparer.Default.Equals(backing.AssociatedSymbol, property))
                return backing;
        }

        return null;
    }

    /// <summary>
    ///     Whether <paramref name="property" /> is an auto-property whose backing field carries <c>[NumosSerialize]</c>,
    ///     which is what <c>[field: NumosSerialize]</c> produces.
    /// </summary>
    public static bool HasMarkedBackingField(IPropertySymbol property) =>
        property.ContainingType is { } type && FindBackingField(type, property) is { } backing &&
        FindSerialize(backing) is not null;

    /// <summary>
    ///     Reports a <c>[NumosSerialize]</c> that the generators can't honor, found on <paramref name="member" />.
    /// </summary>
    /// <param name="member">Any member of the type being read.</param>
    /// <param name="ownerDisplayName">The type that declares the member, for diagnostics.</param>
    /// <param name="fallback">Where the diagnostic points when the member has no source location.</param>
    /// <param name="diagnostics">Receives the diagnostic.</param>
    /// <returns>True when a diagnostic was added, so the caller can stop generating for the type.</returns>
    /// <remarks>
    ///     <para>
    ///         Two marks are never read. One is on an indexer, which holds no single value. The other is
    ///         <c>[field: NumosSerialize]</c> on a property, which lands on the backing field. Both used to disappear
    ///         without a word. The backing field is the member that reports, at its property's location, so each
    ///         mark is reported once.
    ///     </para>
    ///     <para>
    ///         Call this for every member before the caller's own filters, because those filters drop backing fields
    ///         and implicitly declared members.
    ///     </para>
    /// </remarks>
    public static bool ReportUnsupportedMark(
        ISymbol member, string ownerDisplayName, LocationInfo? fallback, List<DiagnosticInfo> diagnostics)
    {
        switch (member)
        {
            case IPropertySymbol { IsIndexer: true } indexer when FindSerialize(indexer) is not null:
                string parameters = string.Join(", ", indexer.Parameters.Select(static parameter => parameter.Type.ToDisplayString()));
                diagnostics.Add(
                    DiagnosticInfo.Create(
                        StateDiagnostics.Indexer,
                        LocationInfo.From(indexer.Locations.FirstOrDefault()) ?? fallback,
                        ownerDisplayName + ".this[" + parameters + "]"));

                return true;

            case IFieldSymbol { AssociatedSymbol: IPropertySymbol property } backing when FindSerialize(backing) is not null:
                diagnostics.Add(
                    DiagnosticInfo.Create(
                        StateDiagnostics.BackingFieldTarget,
                        LocationInfo.From(property.Locations.FirstOrDefault()) ?? fallback,
                        ownerDisplayName + "." + property.Name));

                return true;

            default:
                return false;
        }
    }

    /// <summary>
    ///     Whether a member could hold state: a writable field, or a property with a setter.
    /// </summary>
    /// <remarks>
    ///     Readonly fields and get-only auto-properties do not count. They're set once in a constructor, which on a
    ///     participating type usually means a cache derived from the marked members.
    /// </remarks>
    public static bool IsMutable(ISymbol member)
    {
        return member switch
        {
            IFieldSymbol field => !field.IsReadOnly,
            IPropertySymbol property => property.SetMethod is not null,
            _ => false,
        };
    }

    /// <summary>
    ///     Decides whether <paramref name="member" /> is a field or property the state generators look at.
    /// </summary>
    /// <param name="type">The member's type.</param>
    /// <param name="member">A member of <paramref name="type" />.</param>
    /// <param name="memberType">The value type of the field or property.</param>
    /// <param name="hasStorage">
    ///     Whether the member stores a value: any declared field, or a property with a backing field (auto-properties,
    ///     positional record properties, and <c>field</c>-keyword properties).
    /// </param>
    /// <returns>False for members that are never state, such as methods, indexers, and backing fields.</returns>
    public static bool TryGetMember(
        INamedTypeSymbol type, ISymbol member,
        out ITypeSymbol? memberType, out bool hasStorage)
    {
        memberType = null;
        hasStorage = false;
        switch (member)
        {
            case IFieldSymbol field:
                // Backing fields are covered through their property. Other synthesized fields are not the user's.
                if (field.AssociatedSymbol is not null || field.IsImplicitlyDeclared)
                    return false;

                memberType = field.Type;
                hasStorage = true;
                return true;

            case IPropertySymbol { IsIndexer: false } property:
                memberType = property.Type;
                hasStorage = HasBackingField(type, property);

                // Compiler-synthesized record members such as EqualityContract have no storage and can't be marked.
                return hasStorage || !property.IsImplicitlyDeclared;

            default:
                return false;
        }
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
