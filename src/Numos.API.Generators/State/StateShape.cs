using Microsoft.CodeAnalysis;
using Numos.API.Generators.Common;

namespace Numos.API.Generators.State;

internal enum StateShapeKind
{
    /// <summary>
    ///     A primitive. The name is its CLR name, such as <c>Single</c>.
    /// </summary>
    Primitive,
    String,
    Int3,
    Gas,

    /// <summary>
    ///     A whole <c>GasRegistry</c>, written by value.
    /// </summary>
    GasRegistry,

    /// <summary>
    ///     An opaque <c>ReadOnlyMemory&lt;byte&gt;</c> payload, written as its length and bytes.
    /// </summary>
    Bytes,

    /// <summary>
    ///     An enum. The name is its fully qualified type, and the one argument is its underlying primitive.
    /// </summary>
    Enum,

    /// <summary>
    ///     A nested type with <c>[NumosSerialize]</c> members. The name is its fully qualified type.
    /// </summary>
    State,

    /// <summary>
    ///     An ordered collection type. The name is the unbound generic type, and the argument is the element.
    /// </summary>
    List,
    Array,
    Set,

    /// <summary>
    ///     A dictionary type. The name is the unbound generic type, and the arguments are the key then the value.
    /// </summary>
    Dictionary,
    NullableReference,
    NullableValue,

    /// <summary>
    ///     A collection type that is accepted only as a constructor parameter, such as <c>IEnumerable&lt;T&gt;</c>. The
    ///     name is the unbound generic type, and the arguments are its element (or key and value) shapes.
    /// </summary>
    Container,

    /// <summary>
    ///     A member whose <c>[NumosSerialize]</c> names a <c>Codec</c>. The name is its declared type, and
    ///     <see cref="StateShape.Codec" /> is the codec that handles it.
    /// </summary>
    Custom,

    /// <summary>
    ///     An unresolved identifier, presumably a quantity alias, still to be looked up in the catalog.
    /// </summary>
    Alias,
    Unsupported,
}

/// <summary>
///     A state field's type, reduced to what picks its codec.
/// </summary>
internal sealed record StateShape(StateShapeKind Kind, string Name, EquatableArray<StateShape> Arguments, string Codec = "")
{
    private const string Codecs = "global::Numos.CoreSim.State.Advanced.StateCodecs.";

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

    private static readonly HashSet<string> ParameterContainerTypes = new(StringComparer.Ordinal)
    {
        "System.Collections.Generic.IEnumerable`1",
        "System.Collections.Generic.IReadOnlyCollection`1",
        "System.Collections.Generic.ICollection`1",
        "System.Collections.Generic.IList`1",
        "System.Collections.Generic.List`1",
        "System.Collections.Generic.ISet`1",
        "System.Collections.Generic.HashSet`1",
        "System.Collections.Generic.IDictionary`2",
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
        // An alias is unbound here (see QuantityAliasCatalog). It is never annotated, because it is not known to be a
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

        string metadataName = PartialTypeModel.GetMetadataName(named.OriginalDefinition);
        if (metadataName == "Numos.Maths.Int3")
            return Leaf(StateShapeKind.Int3, "");

        if (metadataName == "Numos.CoreSim.GasProperties")
            return Leaf(StateShapeKind.Gas, "");

        if (metadataName == "Numos.CoreSim.GasRegistry")
            return Leaf(StateShapeKind.GasRegistry, "");

        if (metadataName == "System.ReadOnlyMemory`1" && named.TypeArguments[0].SpecialType == SpecialType.System_Byte)
            return Leaf(StateShapeKind.Bytes, "");

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

        if (named.TypeParameters.Length == 0 && (StateSymbols.HasSerializedMembers(named) || ImplementsOwnState(named)))
            return Leaf(StateShapeKind.State, FullName(named));

        return Unsupported(type);
    }

    /// <summary>
    ///     Classifies a constructor parameter type. Like <see cref="From" />, plus the collection interfaces and classes
    ///     a read value can be copied into but that can't be state fields themselves.
    /// </summary>
    public static StateShape FromParameter(ITypeSymbol type)
    {
        if (type.TypeKind == TypeKind.Error)
            return From(type);

        if (type.IsReferenceType && type.NullableAnnotation == NullableAnnotation.Annotated)
            return Of(StateShapeKind.NullableReference, "", FromParameter(type.WithNullableAnnotation(NullableAnnotation.NotAnnotated)));

        if (type is INamedTypeSymbol { IsGenericType: true } named &&
            ParameterContainerTypes.Contains(PartialTypeModel.GetMetadataName(named.OriginalDefinition)))
        {
            return new StateShape(
                StateShapeKind.Container,
                named.ToDisplayString(UnboundFormat),
                named.TypeArguments.Select(From).ToEquatableArray());
        }

        return From(type);
    }

    /// <summary>
    ///     Gets the shape of an <c>IReadOnlyList&lt;T&gt;</c> over <paramref name="element" />, which is how a snapshot
    ///     holds an array.
    /// </summary>
    public static StateShape ReadOnlyList(StateShape element) =>
        Of(StateShapeKind.List, "global::System.Collections.Generic.IReadOnlyList", element);

    /// <summary>
    ///     Gets the shape of a generated snapshot type, which implements <c>INumosState&lt;T&gt;</c> like any
    ///     serializable type.
    /// </summary>
    /// <param name="fullyQualifiedName">The snapshot type with its <c>global::</c> prefix.</param>
    public static StateShape Snapshot(string fullyQualifiedName) => Leaf(StateShapeKind.State, fullyQualifiedName);

    /// <summary>
    ///     Classifies a marked member: by its <c>Codec</c> when the attribute names one, whatever the member's type, and
    ///     by the type otherwise.
    /// </summary>
    public static StateShape ForMember(ITypeSymbol memberType, ITypeSymbol? codec)
    {
        return codec is not null
            ? new StateShape(StateShapeKind.Custom, FullName(memberType), EquatableArray<StateShape>.Empty, FullName(codec))
            : From(memberType);
    }

    /// <summary>
    ///     Whether <paramref name="type" /> implements <c>INumosState&lt;T&gt;</c> over itself. A generated snapshot from
    ///     a referenced assembly shows up this way, because its members carry no attributes but the interface does.
    /// </summary>
    public static bool ImplementsOwnState(INamedTypeSymbol type)
    {
        foreach (INamedTypeSymbol candidate in type.AllInterfaces)
        {
            if (candidate.TypeArguments.Length == 1 &&
                PartialTypeModel.GetMetadataName(candidate.OriginalDefinition) == "Numos.CoreSim.State.Advanced.INumosState`1" &&
                SymbolEqualityComparer.Default.Equals(candidate.TypeArguments[0], type))
            {
                return true;
            }
        }

        return false;
    }

    private static string FullName(ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    private static StateShape Unsupported(ITypeSymbol type) => Leaf(StateShapeKind.Unsupported, type.ToDisplayString());

    /// <summary>
    ///     Replaces every alias with its storage primitive. An alias the catalog does not know becomes unsupported.
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
    ///     Renders this shape as the type the member declares, for messages. An unsupported part appears under its own
    ///     name, because it has no codec to render.
    /// </summary>
    public string DescribeDeclared()
    {
        return Kind switch
        {
            StateShapeKind.Array => Arguments[0].DescribeDeclared() + "[]",
            StateShapeKind.NullableReference or StateShapeKind.NullableValue => Arguments[0].DescribeDeclared() + "?",
            StateShapeKind.List or StateShapeKind.Set or StateShapeKind.Dictionary =>
                Name.Replace("global::", "") + "<" +
                string.Join(", ", Arguments.Select(static argument => argument.DescribeDeclared())) + ">",
            StateShapeKind.Unsupported => Name,
            _ => TypeExpression.Replace("global::", ""),
        };
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
        StateShapeKind.GasRegistry => "global::Numos.CoreSim.GasRegistry",
        StateShapeKind.Bytes => "global::System.ReadOnlyMemory<byte>",
        StateShapeKind.Enum or StateShapeKind.State or StateShapeKind.Custom => Name,
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
        StateShapeKind.Custom => Codec,
        StateShapeKind.GasRegistry => Codecs + "GasRegistry",
        StateShapeKind.Bytes => Codecs + "Bytes",
        StateShapeKind.Enum => Codecs + "Enum<" + Name + ", " + Arguments[0].TypeExpression + ", " +
                               Arguments[0].CodecExpression + ">",
        StateShapeKind.State => Codecs + "State<" + Name + ">",
        // Primitive elements take the block path. It writes the same bytes as the per-element one.
        StateShapeKind.List or StateShapeKind.Array when Arguments[0] is { Kind: StateShapeKind.Primitive, Name: not "Boolean" } =>
            Generic("UnmanagedList", Arguments[0]),
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
