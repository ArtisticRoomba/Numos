using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Numos.API.Generators.Common;

/// <summary>
///     Symbol and syntax helpers shared by the generators that implement partial properties.
/// </summary>
internal static class PartialSyntax
{
    public static readonly SymbolDisplayFormat FullyQualifiedWithNullability =
        SymbolDisplayFormat.FullyQualifiedFormat.AddMiscellaneousOptions(
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    /// <summary>
    ///     Gets <paramref name="type" /> and every type enclosing it, outermost first, with whether the declaration
    ///     <paramref name="member" /> sits in is marked partial.
    /// </summary>
    /// <remarks>
    ///     Only the declarations around <paramref name="member" /> are read. Mixing partial and non-partial
    ///     declarations of one type is already CS0260, so the type's other declarations can't disagree.
    /// </remarks>
    public static List<(INamedTypeSymbol Type, bool IsPartial)> GetContainingChain(
        INamedTypeSymbol type,
        SyntaxNode member)
    {
        var chain = new List<(INamedTypeSymbol Type, bool IsPartial)>();
        SyntaxNode? node = member.Parent;
        for (INamedTypeSymbol? current = type; current is not null; current = current.ContainingType)
        {
            while (node is not null and not TypeDeclarationSyntax)
                node = node.Parent;

            chain.Add(
                (current, node is TypeDeclarationSyntax declaration &&
                          declaration.Modifiers.Any(SyntaxKind.PartialKeyword)));

            node = node?.Parent;
        }

        chain.Reverse();
        return chain;
    }

    /// <summary>
    ///     Gets the modifiers of a partial member declaration without its <c>partial</c> keyword.
    /// </summary>
    public static string ModifiersWithoutPartial(SyntaxTokenList modifiers)
    {
        return string.Join(
            " ",
            modifiers
                .Where(static modifier => !modifier.IsKind(SyntaxKind.PartialKeyword))
                .Select(static modifier => modifier.Text));
    }

    /// <summary>
    ///     Orders models by where they were declared, so output doesn't depend on the order the driver produced them.
    /// </summary>
    public static IOrderedEnumerable<T> OrderBySource<T>(this IEnumerable<T> items, Func<T, LocationInfo?> location)
    {
        return items
            .OrderBy(item => location(item)?.FilePath ?? "", StringComparer.Ordinal)
            .ThenBy(item => location(item)?.TextSpan.Start ?? 0);
    }
}