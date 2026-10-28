using Microsoft.CodeAnalysis;
using Numos.API.Generators.Common;

namespace Numos.API.Generators.State;

/// <summary>
///     Decides whether a member type is provably immutable, so a solver configuration built only from such members
///     can be its own snapshot.
/// </summary>
/// <remarks>
///     Deliberately conservative. <c>IReadOnlyList&lt;T&gt;</c> and the other read-only interfaces do not count,
///     because the list behind them can still change. Anything not recognized here makes the snapshot a copy. A copy
///     is always correct, just slower.
/// </remarks>
internal static class Immutability
{
    public static bool IsImmutable(ITypeSymbol type)
    {
        return IsImmutable(type, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default));
    }

    private static bool IsImmutable(ITypeSymbol type, HashSet<ITypeSymbol> visiting)
    {
        // Quantity aliases are error types inside a project with a units catalog. They are all primitives.
        if (type.TypeKind == TypeKind.Error)
            return true;

        if (type.IsReferenceType && type.NullableAnnotation == NullableAnnotation.Annotated)
            type = type.WithNullableAnnotation(NullableAnnotation.NotAnnotated);

        if (type.SpecialType == SpecialType.System_String ||
            QuantityAliasCatalog.PrimitiveNames.ContainsKey(type.SpecialType))
        {
            return true;
        }

        if (type is not INamedTypeSymbol named)
            return false;

        if (named.TypeKind == TypeKind.Enum)
            return true;

        if (named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            return IsImmutable(named.TypeArguments[0], visiting);

        string metadataName = PartialTypeModel.GetMetadataName(named.OriginalDefinition);
        if (metadataName is "Numos.Maths.Int3" or "Numos.CoreSim.GasProperties")
            return true;

        string ns = named.ContainingNamespace?.ToDisplayString() ?? "";
        if (named.IsGenericType && ns is "System.Collections.Frozen" or "System.Collections.Immutable")
            return named.TypeArguments.All(argument => IsImmutable(argument, visiting));

        if (named.TypeParameters.Length > 0 || !StateSymbols.HasSerializedMembers(named))
            return false;

        // A type that refers back to itself is immutable if everything else in it is.
        if (!visiting.Add(named))
            return true;

        foreach (ISymbol member in named.GetMembers())
        {
            if (member.IsStatic || !StateSymbols.IsSerialized(member, out bool hasCodec))
                continue;

            bool fixedValue = member switch
            {
                IFieldSymbol field => field.IsReadOnly,
                IPropertySymbol property => property.SetMethod is null or { IsInitOnly: true },
                _ => true,
            };

            ITypeSymbol? memberType = member switch
            {
                IFieldSymbol field => field.Type,
                IPropertySymbol property => property.Type,
                _ => null,
            };

            if (hasCodec || !fixedValue || memberType is null || !IsImmutable(memberType, visiting))
                return false;
        }

        return true;
    }
}
