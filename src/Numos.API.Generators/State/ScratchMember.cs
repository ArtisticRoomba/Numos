using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Numos.API.Generators.Common;

namespace Numos.API.Generators.State;

/// <summary>
///     How the generated <c>ResetScratch</c> resets one <c>[NumosScratch]</c> member.
/// </summary>
internal enum ScratchResetKind
{
    /// <summary>
    ///     Assigns a copy of the member's initializer.
    /// </summary>
    Initializer,

    /// <summary>
    ///     Calls the parameterless <c>Clear()</c> of the instance the member holds.
    /// </summary>
    Clear,

    /// <summary>
    ///     Assigns <c>default</c>.
    /// </summary>
    Default,
}

/// <param name="MemberName">The escaped member to reset.</param>
/// <param name="Kind">How to reset it.</param>
/// <param name="Initializer">
///     The initializer's source text for <see cref="ScratchResetKind.Initializer" />, otherwise null.
/// </param>
/// <param name="NeedsBlock">
///     Whether the initializer declares a variable, such as an <c>out var</c>. The assignment then gets its own block,
///     so two copied initializers can't declare the same local in one method.
/// </param>
internal sealed record ScratchResetModel(string MemberName, ScratchResetKind Kind, string? Initializer, bool NeedsBlock);

/// <summary>
///     A class solver's generated <c>INumosScratchTarget</c>, or the scratch hook of a class in a chain.
/// </summary>
/// <param name="Type">The solver to reopen.</param>
/// <param name="FileUsings">
///     The non-global using directives of each file whose initializer is copied, so the copies bind as they do there.
/// </param>
/// <param name="NamespaceUsings">The using directives declared inside the namespace of those files.</param>
/// <param name="Resets">The resets in member declaration order.</param>
/// <param name="Hook">
///     How the class takes part in the <c>ResetNumosScratch</c> hook. With <see cref="StateHookKind.None" />, the
///     interface member resets inline.
/// </param>
/// <param name="ImplementsInterface">Whether the class implements <c>INumosScratchTarget</c>, because no base class does.</param>
internal sealed record ScratchTypeModel(
    PartialTypeModel Type,
    EquatableArray<string> FileUsings,
    EquatableArray<string> NamespaceUsings,
    EquatableArray<ScratchResetModel> Resets,
    StateHookKind Hook = StateHookKind.None,
    bool ImplementsInterface = true);

/// <summary>
///     Reads <c>[NumosScratch]</c> members into resets.
/// </summary>
internal static class ScratchMember
{
    /// <summary>
    ///     Reports a <c>[NumosScratch]</c> that can't take effect: one next to another mark, or one outside a class
    ///     solver.
    /// </summary>
    /// <param name="member">The scratch member.</param>
    /// <param name="serialize">The member's <c>[NumosSerialize]</c>, or null.</param>
    /// <param name="displayName">The member qualified by its owner.</param>
    /// <param name="location">Where the diagnostic points.</param>
    /// <param name="diagnostics">Receives the diagnostic.</param>
    public static void ReportMisplaced(
        ISymbol member, AttributeData? serialize, string displayName, LocationInfo? location,
        List<DiagnosticInfo> diagnostics)
    {
        if (serialize is not null)
            diagnostics.Add(DiagnosticInfo.Create(StateDiagnostics.ScratchWithOtherMark, location, displayName, "NumosSerialize"));
        else if (StateSymbols.IsIgnored(member))
            diagnostics.Add(DiagnosticInfo.Create(StateDiagnostics.ScratchWithOtherMark, location, displayName, "NumosIgnore"));
        else
            diagnostics.Add(DiagnosticInfo.Create(StateDiagnostics.ScratchOutsideSolver, location, displayName));
    }

    /// <summary>
    ///     Picks the reset for one <c>[NumosScratch]</c> field or property, or reports why there is none.
    /// </summary>
    /// <param name="owner">The type that declares <paramref name="member" />.</param>
    /// <param name="member">
    ///     A field or property that <see cref="StateSymbols.TryGetMember" /> accepts. Indexers and backing fields are
    ///     reported by <see cref="StateSymbols.ReportUnsupportedMark" />.
    /// </param>
    /// <param name="classSolver">Whether <paramref name="owner" /> is a class solver.</param>
    /// <param name="location">Where diagnostics about the member point.</param>
    /// <param name="semanticModel">A model of any tree in the compilation. Each initializer is bound in its own tree.</param>
    /// <param name="usings">Receives the using directives each copied initializer needs.</param>
    /// <param name="diagnostics">Receives the diagnostic when there is no reset.</param>
    /// <param name="cancellationToken">The token to observe.</param>
    /// <returns>The reset, or null after a diagnostic.</returns>
    /// <remarks>
    ///     The rules, in order: an assignable member with an initializer gets the initializer again. A member that
    ///     can't be assigned is cleared, when its type is a class with a parameterless <c>Clear()</c>. Any other
    ///     assignable member gets <c>default</c>.
    /// </remarks>
    public static ScratchResetModel? TryParse(
        INamedTypeSymbol owner, ISymbol member, bool classSolver, LocationInfo? location, SemanticModel semanticModel,
        ScratchUsings usings, List<DiagnosticInfo> diagnostics, CancellationToken cancellationToken)
    {
        string displayName = owner.ToDisplayString() + "." + member.Name;
        AttributeData? serialize = StateSymbols.FindSerialize(member);
        if (!classSolver || member.IsStatic || member is IFieldSymbol { IsConst: true } || serialize is not null ||
            StateSymbols.IsIgnored(member))
        {
            ReportMisplaced(member, serialize, displayName, location, diagnostics);
            return null;
        }

        string memberName = CodeWriter.Escape(member.Name);
        (bool assignable, ITypeSymbol type) = member switch
        {
            IFieldSymbol field => (!field.IsReadOnly, field.Type),
            IPropertySymbol property => (property.SetMethod is { IsInitOnly: false }, property.Type),
            _ => (false, null!),
        };

        if (FindInitializer(member, cancellationToken) is { } initializer && assignable)
        {
            SemanticModel model = initializer.SyntaxTree == semanticModel.SyntaxTree
                ? semanticModel
                : semanticModel.Compilation.GetSemanticModel(initializer.SyntaxTree);

            if (RejectInitializer(owner, initializer, model, cancellationToken) is { } reason)
            {
                diagnostics.Add(DiagnosticInfo.Create(StateDiagnostics.ScratchInitializer, location, displayName, reason));
                return null;
            }

            usings.AddFrom(initializer);
            string text = initializer.ToString().Replace("\r\n", "\n");
            bool needsBlock = initializer.DescendantNodesAndSelf().OfType<VariableDesignationSyntax>().Any();
            return new ScratchResetModel(memberName, ScratchResetKind.Initializer, text, needsBlock);
        }

        if (assignable)
            return new ScratchResetModel(memberName, ScratchResetKind.Default, null, false);

        if (HasClear(type, owner, semanticModel.Compilation))
            return new ScratchResetModel(memberName, ScratchResetKind.Clear, null, false);

        diagnostics.Add(DiagnosticInfo.Create(StateDiagnostics.ScratchNotResettable, location, displayName, type.ToDisplayString()));
        return null;
    }

    private static ExpressionSyntax? FindInitializer(ISymbol member, CancellationToken cancellationToken)
    {
        foreach (SyntaxReference reference in member.DeclaringSyntaxReferences)
        {
            switch (reference.GetSyntax(cancellationToken))
            {
                case VariableDeclaratorSyntax { Initializer: { } initializer }:
                    return initializer.Value;
                case PropertyDeclarationSyntax { Initializer: { } initializer }:
                    return initializer.Value;
            }
        }

        return null;
    }

    /// <summary>
    ///     Gets why <paramref name="initializer" /> can't be copied into a method body, or null when it can.
    /// </summary>
    /// <remarks>
    ///     A primary constructor parameter is the case that matters. An initializer reads it once, but a method that
    ///     names it makes the compiler capture it in a hidden field. The reset would then read that captured copy,
    ///     which the solver may have changed since.
    /// </remarks>
    private static string? RejectInitializer(
        INamedTypeSymbol owner, ExpressionSyntax initializer, SemanticModel model, CancellationToken cancellationToken)
    {
        if (initializer.ContainsDirectives)
            return "contains a preprocessor directive";

        if (initializer is InitializerExpressionSyntax)
            return "is an array initializer, which only a declaration accepts";

        foreach (IdentifierNameSyntax identifier in initializer.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
        {
            if (model.GetSymbolInfo(identifier, cancellationToken).Symbol is IParameterSymbol
                {
                    ContainingSymbol: IMethodSymbol { MethodKind: MethodKind.Constructor } constructor,
                } parameter &&
                SymbolEqualityComparer.Default.Equals(constructor.ContainingType, owner))
            {
                return "reads the primary constructor parameter '" + parameter.Name + "'";
            }
        }

        return null;
    }

    /// <summary>
    ///     Whether <paramref name="type" /> is a reference type with an instance <c>Clear()</c> that
    ///     <paramref name="owner" /> can call.
    /// </summary>
    /// <remarks>
    ///     A struct is left out, because a readonly field or a getter hands out a copy, and clearing the copy changes
    ///     nothing.
    /// </remarks>
    private static bool HasClear(ITypeSymbol type, INamedTypeSymbol owner, Compilation compilation)
    {
        if (!type.IsReferenceType || type is ITypeParameterSymbol)
            return false;

        for (ITypeSymbol? current = type; current is not null; current = current.BaseType)
        {
            if (HasOwnClear(current, owner, compilation))
                return true;
        }

        // A class can implement an interface's Clear explicitly, as an array does IList.Clear, and then the call
        // doesn't compile. Only an interface-typed member reaches Clear through its interfaces.
        if (type.TypeKind != TypeKind.Interface)
            return false;

        foreach (INamedTypeSymbol candidate in type.AllInterfaces)
        {
            if (HasOwnClear(candidate, owner, compilation))
                return true;
        }

        return false;
    }

    private static bool HasOwnClear(ITypeSymbol type, INamedTypeSymbol owner, Compilation compilation)
    {
        foreach (ISymbol candidate in type.GetMembers("Clear"))
        {
            if (candidate is IMethodSymbol
                {
                    IsStatic: false,
                    MethodKind: MethodKind.Ordinary,
                    Parameters.Length: 0,
                    TypeParameters.Length: 0,
                } method &&
                compilation.IsSymbolAccessibleWithin(method, owner))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
///     Collects the using directives that the copied initializers of one solver need, without duplicates.
/// </summary>
internal sealed class ScratchUsings
{
    private readonly List<string> _file = [];
    private readonly List<string> _namespace = [];

    /// <summary>
    ///     Gets the directives that go at the top of the generated file.
    /// </summary>
    public EquatableArray<string> File => _file.ToEquatableArray();

    /// <summary>
    ///     Gets the directives that go inside the generated namespace.
    /// </summary>
    public EquatableArray<string> Namespace => _namespace.ToEquatableArray();

    /// <summary>
    ///     Adds the directives in scope at <paramref name="node" />.
    /// </summary>
    /// <remarks>
    ///     Global usings are skipped, because they already apply to the generated file.
    /// </remarks>
    public void AddFrom(SyntaxNode node)
    {
        if (node.SyntaxTree.GetRoot() is CompilationUnitSyntax unit)
            Add(_file, unit.Usings);

        foreach (BaseNamespaceDeclarationSyntax declaration in node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse())
            Add(_namespace, declaration.Usings);
    }

    private static void Add(List<string> target, SyntaxList<UsingDirectiveSyntax> usings)
    {
        foreach (UsingDirectiveSyntax directive in usings)
        {
            if (directive.GlobalKeyword.IsKind(SyntaxKind.None) && !target.Contains(directive.ToString()))
                target.Add(directive.ToString());
        }
    }
}
