using Microsoft.CodeAnalysis;

namespace Numos.API.Generators.Common;

/// <summary>
///     One type in the chain enclosing a generated member, outermost first.
/// </summary>
/// <param name="Keyword">The declaration keyword(s) to reopen the type with, such as <c>class</c> or <c>record struct</c>.</param>
/// <param name="Declaration">The escaped name plus any type parameter list, such as <c>Cache&lt;TKey, TValue&gt;</c>.</param>
internal sealed record ContainingTypeModel(string Keyword, string Declaration);

/// <summary>
///     A type that generated code reopens as a partial declaration, as plain equatable data.
/// </summary>
/// <param name="Namespace">The containing namespace, or empty for the global namespace.</param>
/// <param name="MetadataName">The namespace-qualified metadata name, with <c>+</c> between nested types.</param>
/// <param name="ContainingTypes">The type and every type enclosing it, outermost first.</param>
internal sealed record PartialTypeModel(
    string Namespace,
    string MetadataName,
    EquatableArray<ContainingTypeModel> ContainingTypes)
{
    /// <summary>
    ///     Gets <see cref="MetadataName" /> with the generic arity backtick replaced, for use in a hint name.
    /// </summary>
    public string HintName => MetadataName.Replace('`', '_');

    public static PartialTypeModel From(INamedTypeSymbol type)
    {
        var chain = new List<INamedTypeSymbol>();
        for (INamedTypeSymbol? current = type; current is not null; current = current.ContainingType)
            chain.Add(current);
        chain.Reverse();

        string @namespace = type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString();
        string metadataName = string.Join("+", chain.Select(static containing => containing.MetadataName));
        if (@namespace.Length > 0)
            metadataName = @namespace + "." + metadataName;

        return new PartialTypeModel(
            @namespace,
            metadataName,
            chain.Select(static containing => new ContainingTypeModel(GetKeyword(containing), GetDeclaration(containing)))
                .ToEquatableArray());
    }

    private static string GetKeyword(INamedTypeSymbol type)
    {
        return type switch
        {
            { IsRecord: true, TypeKind: TypeKind.Struct } => "record struct",
            { IsRecord: true } => "record",
            { TypeKind: TypeKind.Struct } => "struct",
            { TypeKind: TypeKind.Interface } => "interface",
            _ => "class",
        };
    }

    private static string GetDeclaration(INamedTypeSymbol type)
    {
        // Constraints are left off on purpose: a partial declaration may omit them, and they then come from the
        // user's declaration. Repeating them would mean reproducing every constraint form exactly.
        string name = CodeWriter.Escape(type.Name);
        if (type.TypeParameters.Length == 0)
            return name;

        return name + "<" + string.Join(", ",
            type.TypeParameters.Select(static parameter => CodeWriter.Escape(parameter.Name))) + ">";
    }
}
