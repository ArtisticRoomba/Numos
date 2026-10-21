using Microsoft.CodeAnalysis;
using Numos.API.Generators.Common;

namespace Numos.API.Generators.State;

internal enum StateShapeKind
{
    /// <summary>
    ///     A primitive; the name is its CLR name, such as <c>Single</c>.
    /// </summary>
    Primitive,
    String,
    Int3,
    Gas,

    /// <summary>
    ///     An enum; the name is its fully qualified type and the one argument is its underlying primitive.
    /// </summary>
    Enum,

    /// <summary>
    ///     A nested <c>[NumosState]</c> type; the name is its fully qualified type.
    /// </summary>
    State,

    /// <summary>
    ///     An ordered collection type; the name is the unbound generic type, the argument the element.
    /// </summary>
    List,
    Array,
    Set,

    /// <summary>
    ///     A dictionary type; the name is the unbound generic type, the arguments key then value.
    /// </summary>
    Dictionary,
    NullableReference,
    NullableValue,

    /// <summary>
    ///     An unresolved identifier, presumably a quantity alias, still to be looked up in the catalog.
    /// </summary>
    Alias,
    Unsupported,
}

/// <summary>
///     A state field's type, reduced to what picks its codec.
/// </summary>
internal sealed record StateShape(StateShapeKind Kind, string Name, EquatableArray<StateShape> Arguments)
{
    private const string Codecs = "global::Numos.CoreSim.State.StateCodecs.";

    private const string StateAttributeMetadataName = "Numos.CoreSim.State.NumosStateAttribute";

    private static readonly SymbolDisplayFormat UnboundFormat =
        SymbolDisplayFormat.FullyQualifiedFormat.WithGenericsOptions(SymbolDisplayGenericsOptions.None);

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

    private static readonly HashSet<string> ListTypes = new(StringComparer.Ordinal)
    {
        "System.Collections.Generic.IReadOnlyList`1",
        "System.Collections.Immutable.ImmutableArray`1",
    };

    private static readonly HashSet<string> SetTypes = new(StringComparer.Ordinal)
    {
        "System.Collections.Generic.IReadOnlySet`1",
        "System.Collections.Frozen.FrozenSet`1",
    };

    private static readonly HashSet<string> DictionaryTypes = new(StringComparer.Ordinal)
    {
        "System.Collections.Generic.IReadOnlyDictionary`2",
        "System.Collections.Generic.Dictionary`2",
        "System.Collections.Frozen.FrozenDictionary`2",
    };

    private static StateShape Leaf(StateShapeKind kind, string name) => new(kind, name, EquatableArray<StateShape>.Empty);

    private static StateShape Of(StateShapeKind kind, string name, params StateShape[] arguments) =>
        new(kind, name, new EquatableArray<StateShape>(arguments));

    /// <summary>
    ///     Classifies a member type. Unknown types become <see cref="StateShapeKind.Unsupported" /> leaves rather than
    ///     failing, so one bad member doesn't hide the rest of the type's diagnostics.
    /// </summary>
    public static StateShape From(ITypeSymbol type)
    {
        // An alias is unbound here; see QuantityAliasCatalog. It's never annotated, since it isn't known to be a
        // reference type.
        if (type.TypeKind == TypeKind.Error)
            return Leaf(StateShapeKind.Alias, type.Name);

        if (type.IsReferenceType && type.NullableAnnotation == NullableAnnotation.Annotated)
            return Of(StateShapeKind.NullableReference, "", From(type.WithNullableAnnotation(NullableAnnotation.NotAnnotated)));

        if (Primitives.TryGetValue(type.SpecialType, out string? primitive))
            return Leaf(StateShapeKind.Primitive, primitive);

        if (type.SpecialType == SpecialType.System_String)
            return Leaf(StateShapeKind.String, "");

        if (type is IArrayTypeSymbol array)
            return array.IsSZArray ? Of(StateShapeKind.Array, "", From(array.ElementType)) : Unsupported(type);

        if (type is not INamedTypeSymbol named)
            return Unsupported(type);

        if (named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            return Of(StateShapeKind.NullableValue, "", From(named.TypeArguments[0]));

        if (named.TypeKind == TypeKind.Enum)
        {
            return named.EnumUnderlyingType is { } underlying && Primitives.TryGetValue(underlying.SpecialType, out string? name)
                ? Of(StateShapeKind.Enum, FullName(named), Leaf(StateShapeKind.Primitive, name))
                : Unsupported(type);
        }

        string metadataName = MetadataName(named.OriginalDefinition);
        if (metadataName == "Numos.Maths.Int3")
            return Leaf(StateShapeKind.Int3, "");

        if (metadataName == "Numos.CoreSim.GasProperties")
            return Leaf(StateShapeKind.Gas, "");

        if (ListTypes.Contains(metadataName))
            return Of(StateShapeKind.List, named.ToDisplayString(UnboundFormat), From(named.TypeArguments[0]));

        if (SetTypes.Contains(metadataName))
            return Of(StateShapeKind.Set, named.ToDisplayString(UnboundFormat), From(named.TypeArguments[0]));

        if (DictionaryTypes.Contains(metadataName))
        {
            StateShape key = From(named.TypeArguments[0]);
            return key.Kind is StateShapeKind.Primitive or StateShapeKind.String or StateShapeKind.Gas
                or StateShapeKind.Enum or StateShapeKind.Int3 or StateShapeKind.Alias
                ? Of(StateShapeKind.Dictionary, named.ToDisplayString(UnboundFormat), key, From(named.TypeArguments[1]))
                : Unsupported(type);
        }

        if (named.TypeParameters.Length == 0 && HasStateAttribute(named))
            return Leaf(StateShapeKind.State, FullName(named));

        return Unsupported(type);
    }

    public static bool HasStateAttribute(INamedTypeSymbol type)
    {
        foreach (AttributeData attribute in type.GetAttributes())
        {
            if (attribute.AttributeClass is { } attributeClass &&
                MetadataName(attributeClass) == StateAttributeMetadataName)
            {
                return true;
            }
        }

        return false;
    }

    public static string MetadataName(INamedTypeSymbol type)
    {
        string name = type.MetadataName;
        for (INamedTypeSymbol? containing = type.ContainingType; containing is not null; containing = containing.ContainingType)
            name = containing.MetadataName + "+" + name;

        return type.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() + "." + name : name;
    }

    private static string FullName(ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    private static StateShape Unsupported(ITypeSymbol type) => Leaf(StateShapeKind.Unsupported, type.ToDisplayString());

    /// <summary>
    ///     Replaces every alias with its storage primitive; an alias the catalog doesn't know becomes unsupported.
    /// </summary>
    public StateShape Resolve(EquatableArray<QuantityAlias> aliases)
    {
        if (Kind == StateShapeKind.Alias)
        {
            string? storage = QuantityAliasCatalog.Find(aliases, Name);
            return storage is null ? Leaf(StateShapeKind.Unsupported, Name) : Leaf(StateShapeKind.Primitive, storage);
        }

        if (Arguments.Count == 0)
            return this;

        return this with { Arguments = Arguments.Select(argument => argument.Resolve(aliases)).ToEquatableArray() };
    }

    /// <summary>
    ///     Gets the first unsupported type inside this shape, or null if there is none.
    /// </summary>
    public string? FindUnsupported()
    {
        if (Kind == StateShapeKind.Unsupported)
            return Name;

        foreach (StateShape argument in Arguments)
        {
            if (argument.FindUnsupported() is { } unsupported)
                return unsupported;
        }

        return null;
    }

    /// <summary>
    ///     Gets the C# type this shape stands for, with aliases spelled as their storage primitive.
    /// </summary>
    public string TypeExpression => Kind switch
    {
        StateShapeKind.Primitive => QuantityAliasCatalog.PrimitiveKeywords[Name],
        StateShapeKind.String => "string",
        StateShapeKind.Int3 => "global::Numos.Maths.Int3",
        StateShapeKind.Gas => "global::Numos.CoreSim.GasProperties",
        StateShapeKind.Enum or StateShapeKind.State => Name,
        StateShapeKind.Array => Arguments[0].TypeExpression + "[]",
        StateShapeKind.NullableReference or StateShapeKind.NullableValue => Arguments[0].TypeExpression + "?",
        _ => Name + "<" + string.Join(", ", Arguments.Select(static argument => argument.TypeExpression)) + ">",
    };

    /// <summary>
    ///     Gets the <c>StateCodecs</c> type that writes, compares, and orders this shape.
    /// </summary>
    public string CodecExpression => Kind switch
    {
        StateShapeKind.Primitive => Codecs + Name,
        StateShapeKind.String => Codecs + "String",
        StateShapeKind.Int3 => Codecs + "Int3",
        StateShapeKind.Gas => Codecs + "Gas",
        StateShapeKind.Enum => Codecs + "Enum<" + Name + ", " + Arguments[0].TypeExpression + ", " +
                               Arguments[0].CodecExpression + ">",
        StateShapeKind.State => Codecs + "State<" + Name + ">",
        StateShapeKind.List or StateShapeKind.Array => Generic("List", Arguments[0]),
        StateShapeKind.Set => Generic("Set", Arguments[0]),
        StateShapeKind.Dictionary => Generic("Dictionary", Arguments[0], Arguments[1]),
        StateShapeKind.NullableReference => Codecs + "Nullable<" + Arguments[0].TypeExpression + ", " +
                                            Arguments[0].CodecExpression + ">",
        StateShapeKind.NullableValue => Codecs + "NullableValue<" + Arguments[0].TypeExpression + ", " +
                                        Arguments[0].CodecExpression + ">",
        _ => throw new InvalidOperationException("No codec for " + Kind + " '" + Name + "'."),
    };

    private string Generic(string codec, params StateShape[] elements)
    {
        return Codecs + codec + "<" + TypeExpression + ", " +
               string.Join(", ", elements.Select(static element => element.TypeExpression + ", " + element.CodecExpression)) +
               ">";
    }
}
