using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Numos.API.Generators.Common;

namespace Numos.API.Generators.SolverDependency;

/// <summary>
///     Implements <c>[SolverDependency]</c> partial properties and the <c>IAtmosSolverDependencies</c> injection that
///     assigns them.
/// </summary>
/// <remarks>
///     <para>
///         Output is one file per declaring class: a backing field and getter per property, plus an
///         <c>InjectSolverDependencies</c> hook. A class whose base classes have no hook implements the interface and
///         introduces the hook (<c>protected virtual</c>, or <c>private</c> when sealed); a class deriving from one
///         that does overrides it and calls the base first. The base can be in source, where its own generated hook
///         isn't visible yet and is inferred from its <c>[SolverDependency]</c> properties, or in metadata.
///     </para>
///     <para>
///         Every check runs in the attribute transform, which sees one property at a time. The output step only
///         groups properties by type and drops repeated type-level diagnostics.
///     </para>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class SolverDependencyGenerator : IIncrementalGenerator
{
    internal const string PropertiesStepName = "SolverDependencyProperties";

    private const string AttributeMetadataName = "Numos.API.SolverDependencyAttribute";
    private const string DependenciesInterfaceMetadataName = "Numos.API.IAtmosSolverDependencies";
    private const string WorldSolverMetadataName = "Numos.API.IAtmosWorldSolver";
    private const string ResolverMetadataName = "Numos.API.AtmosSolverDependencyResolver";
    private const string HookName = "InjectSolverDependencies";

    private static readonly SymbolDisplayFormat FullyQualifiedWithNullability =
        SymbolDisplayFormat.FullyQualifiedFormat.AddMiscellaneousOptions(
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<SolverDependencyPropertyModel> properties = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                AttributeMetadataName,
                static (node, _) => node is PropertyDeclarationSyntax,
                static (attributeContext, cancellationToken) => CreateModel(attributeContext, cancellationToken))
            .Where(static model => model is not null)
            .Select(static (model, _) => model!)
            .WithTrackingName(PropertiesStepName);

        context.RegisterSourceOutput(properties.Collect(), static (production, models) => Execute(production, models));
    }

    private static SolverDependencyPropertyModel? CreateModel(GeneratorAttributeSyntaxContext context,
        CancellationToken cancellationToken)
    {
        if (context.TargetSymbol is not IPropertySymbol property || context.Attributes.IsDefaultOrEmpty)
            return null;

        var syntax = (PropertyDeclarationSyntax)context.TargetNode;
        Compilation compilation = context.SemanticModel.Compilation;
        INamedTypeSymbol type = property.ContainingType;
        var diagnostics = new List<DiagnosticInfo>();
        var typeDiagnostics = new List<DiagnosticInfo>();

        LocationInfo? location = LocationInfo.From(syntax.Identifier.GetLocation());
        LocationInfo? attributeLocation =
            LocationInfo.From(context.Attributes[0].ApplicationSyntaxReference?.GetSyntax(cancellationToken)) ??
            location;
        LocationInfo? typeLocation = syntax.Parent is BaseTypeDeclarationSyntax declaration
            ? LocationInfo.From(declaration.Identifier.GetLocation())
            : location;

        List<INamedTypeSymbol> chain = GetContainingChain(type);
        string typeDisplayName = type.ToDisplayString();
        string displayName = typeDisplayName + "." + property.Name;

        foreach (INamedTypeSymbol containing in chain)
        {
            if (!IsPartial(containing, cancellationToken))
            {
                typeDiagnostics.Add(DiagnosticInfo.Create(SolverDependencyDiagnostics.TypeNotPartial, typeLocation,
                    containing.ToDisplayString(), displayName));
            }
        }

        bool isClass = type.TypeKind == TypeKind.Class;
        if (!isClass)
        {
            typeDiagnostics.Add(DiagnosticInfo.Create(SolverDependencyDiagnostics.NotClass, typeLocation,
                displayName, typeDisplayName));
        }

        if (!property.IsPartialDefinition || property.PartialImplementationPart is not null ||
            property.SetMethod is not null || property.IsIndexer || property.ReturnsByRef ||
            property.ReturnsByRefReadonly)
        {
            diagnostics.Add(DiagnosticInfo.Create(SolverDependencyDiagnostics.NotPartialDefinition, location,
                displayName));
        }

        if (property.IsStatic)
            diagnostics.Add(DiagnosticInfo.Create(SolverDependencyDiagnostics.Static, location, displayName));

        if (!property.Type.IsReferenceType)
        {
            diagnostics.Add(DiagnosticInfo.Create(SolverDependencyDiagnostics.NotReferenceType, location,
                displayName, property.Type.ToDisplayString()));
        }

        bool optional = false;
        foreach (KeyValuePair<string, TypedConstant> argument in context.Attributes[0].NamedArguments)
        {
            if (argument.Key == "Optional" && argument.Value.Value is bool value)
                optional = value;
        }

        // Oblivious (nullable disabled) types say nothing either way, so only an explicit annotation can disagree.
        NullableAnnotation annotation = property.NullableAnnotation;
        if (optional && annotation == NullableAnnotation.NotAnnotated)
        {
            diagnostics.Add(DiagnosticInfo.Create(SolverDependencyDiagnostics.OptionalMismatch, attributeLocation,
                displayName, "is Optional but its type is not nullable; declare the type with '?'"));
        }
        else if (!optional && annotation == NullableAnnotation.Annotated)
        {
            diagnostics.Add(DiagnosticInfo.Create(SolverDependencyDiagnostics.OptionalMismatch, location,
                displayName, "has a nullable type but is required; set Optional = true or remove the '?'"));
        }

        INamedTypeSymbol? dependenciesInterface = compilation.GetTypeByMetadataName(DependenciesInterfaceMetadataName);
        INamedTypeSymbol? worldSolver = compilation.GetTypeByMetadataName(WorldSolverMetadataName);
        INamedTypeSymbol? resolver = compilation.GetTypeByMetadataName(ResolverMetadataName);
        INamedTypeSymbol? attributeType = context.Attributes[0].AttributeClass;

        if (isClass && !type.IsAbstract && worldSolver is not null &&
            !type.AllInterfaces.Contains(worldSolver, SymbolEqualityComparer.Default))
        {
            typeDiagnostics.Add(DiagnosticInfo.Create(SolverDependencyDiagnostics.NotWorldSolver, typeLocation,
                typeDisplayName));
        }

        if (isClass && dependenciesInterface is not null && DeclaresOwnInjection(type, dependenciesInterface))
        {
            typeDiagnostics.Add(DiagnosticInfo.Create(SolverDependencyDiagnostics.AlreadyImplemented, typeLocation,
                typeDisplayName));
        }

        DependencyHookModel hook = isClass
            ? FindHook(type, dependenciesInterface, resolver, attributeType, typeLocation, typeDisplayName,
                typeDiagnostics)
            : new DependencyHookModel(false, "private", false);

        string modifiers = string.Join(" ", syntax.Modifiers
            .Where(static modifier => !modifier.IsKind(SyntaxKind.PartialKeyword))
            .Select(static modifier => modifier.Text));

        string @namespace = type.ContainingNamespace.IsGlobalNamespace
            ? ""
            : type.ContainingNamespace.ToDisplayString();

        string typeMetadataName = string.Join("+", chain.Select(static containing => containing.MetadataName));
        if (@namespace.Length > 0)
            typeMetadataName = @namespace + "." + typeMetadataName;

        return new SolverDependencyPropertyModel(
            @namespace,
            typeMetadataName,
            typeDisplayName,
            chain.Select(static containing => new DependencyContainingTypeModel(
                GetTypeKeyword(containing), GetDeclarationName(containing))).ToEquatableArray(),
            hook,
            property.Name,
            modifiers,
            property.Type.ToDisplayString(FullyQualifiedWithNullability),
            annotation == NullableAnnotation.Annotated,
            property.Type.WithNullableAnnotation(NullableAnnotation.NotAnnotated)
                .ToDisplayString(FullyQualifiedWithNullability),
            optional,
            location,
            diagnostics.ToEquatableArray(),
            typeDiagnostics.ToEquatableArray());
    }

    /// <summary>
    ///     Works out whether the type introduces the injection hook or overrides one from a base class.
    /// </summary>
    /// <remarks>
    ///     Walks the base chain and stops at the first class that has a say: one with an overridable hook (written by
    ///     hand, or generated into a referenced assembly), one in this compilation whose own
    ///     <c>[SolverDependency]</c> properties will get a generated hook, or one that implements the interface with
    ///     no hook to extend.
    /// </remarks>
    private static DependencyHookModel FindHook(INamedTypeSymbol type, INamedTypeSymbol? dependenciesInterface,
        INamedTypeSymbol? resolver, INamedTypeSymbol? attributeType, LocationInfo? typeLocation,
        string typeDisplayName, List<DiagnosticInfo> typeDiagnostics)
    {
        for (INamedTypeSymbol? current = type.BaseType;
             current is not null && current.SpecialType != SpecialType.System_Object;
             current = current.BaseType)
        {
            IMethodSymbol? method = FindOverridableHook(current, type, resolver);
            if (method is not null)
                return new DependencyHookModel(true, OverrideAccessibility(method, type), !method.IsAbstract);

            // A hook that exists but can't be overridden (sealed, private, non-virtual) on a class that injects
            // means the chain is closed here, even if a class further up still has a virtual one.
            bool implementsInterface = dependenciesInterface is not null &&
                                       current.AllInterfaces.Contains(dependenciesInterface,
                                           SymbolEqualityComparer.Default);
            if (implementsInterface && !current.GetMembers(HookName).IsEmpty)
            {
                typeDiagnostics.Add(DiagnosticInfo.Create(SolverDependencyDiagnostics.BaseNotOverridable,
                    typeLocation, current.ToDisplayString(), typeDisplayName));
                return new DependencyHookModel(true, "protected", false);
            }

            if (attributeType is not null && HasSourceDependencies(current, attributeType))
                return new DependencyHookModel(true, "protected", true);

            if (dependenciesInterface is not null &&
                current.Interfaces.Contains(dependenciesInterface, SymbolEqualityComparer.Default))
            {
                typeDiagnostics.Add(DiagnosticInfo.Create(SolverDependencyDiagnostics.BaseNotOverridable,
                    typeLocation, current.ToDisplayString(), typeDisplayName));
                return new DependencyHookModel(true, "protected", false);
            }
        }

        return new DependencyHookModel(false, type.IsSealed ? "private" : "protected", false);
    }

    private static IMethodSymbol? FindOverridableHook(INamedTypeSymbol baseType, INamedTypeSymbol derived,
        INamedTypeSymbol? resolver)
    {
        foreach (ISymbol member in baseType.GetMembers(HookName))
        {
            if (member is not IMethodSymbol { IsStatic: false, ReturnsVoid: true, Parameters.Length: 1 } method ||
                method.IsSealed || !(method.IsVirtual || method.IsOverride || method.IsAbstract) ||
                method.TypeParameters.Length > 0)
            {
                continue;
            }

            if (resolver is not null &&
                !SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type, resolver))
            {
                continue;
            }

            bool sameAssembly = SymbolEqualityComparer.Default.Equals(baseType.ContainingAssembly,
                derived.ContainingAssembly);
            bool visible = method.DeclaredAccessibility switch
            {
                Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal => true,
                Accessibility.Internal or Accessibility.ProtectedAndInternal => sameAssembly,
                _ => false,
            };

            if (visible)
                return method;
        }

        return null;
    }

    private static string OverrideAccessibility(IMethodSymbol method, INamedTypeSymbol derived)
    {
        bool sameAssembly = SymbolEqualityComparer.Default.Equals(method.ContainingAssembly, derived.ContainingAssembly);
        return method.DeclaredAccessibility switch
        {
            Accessibility.Public => "public",
            Accessibility.Internal => "internal",
            Accessibility.ProtectedAndInternal => "private protected",
            // Overriding a protected internal member from another assembly has to drop the internal part.
            Accessibility.ProtectedOrInternal => sameAssembly ? "protected internal" : "protected",
            _ => "protected",
        };
    }

    private static bool HasSourceDependencies(INamedTypeSymbol type, INamedTypeSymbol attributeType)
    {
        if (type.DeclaringSyntaxReferences.IsDefaultOrEmpty)
            return false;

        foreach (ISymbol member in type.GetMembers())
        {
            if (member is not IPropertySymbol { IsStatic: false } property)
                continue;

            foreach (AttributeData attribute in property.GetAttributes())
            {
                if (SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, attributeType))
                    return true;
            }
        }

        return false;
    }

    private static bool DeclaresOwnInjection(INamedTypeSymbol type, INamedTypeSymbol dependenciesInterface)
    {
        if (!type.GetMembers(HookName).IsEmpty)
            return true;

        if (!type.AllInterfaces.Contains(dependenciesInterface, SymbolEqualityComparer.Default))
            return false;

        foreach (ISymbol member in dependenciesInterface.GetMembers())
        {
            ISymbol? implementation = type.FindImplementationForInterfaceMember(member);
            if (implementation is not null &&
                SymbolEqualityComparer.Default.Equals(implementation.ContainingType, type))
            {
                return true;
            }
        }

        return false;
    }

    private static List<INamedTypeSymbol> GetContainingChain(INamedTypeSymbol type)
    {
        var chain = new List<INamedTypeSymbol>();
        for (INamedTypeSymbol? current = type; current is not null; current = current.ContainingType)
            chain.Add(current);
        chain.Reverse();
        return chain;
    }

    private static bool IsPartial(INamedTypeSymbol type, CancellationToken cancellationToken)
    {
        foreach (SyntaxReference reference in type.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax(cancellationToken) is TypeDeclarationSyntax declaration &&
                declaration.Modifiers.Any(SyntaxKind.PartialKeyword))
            {
                return true;
            }
        }

        return false;
    }

    private static string GetTypeKeyword(INamedTypeSymbol type)
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

    private static string GetDeclarationName(INamedTypeSymbol type)
    {
        // Constraints are left off on purpose: a partial declaration may omit them, and they then come from the
        // user's declaration. Repeating them would mean reproducing every constraint form exactly.
        string name = CodeWriter.Escape(type.Name);
        if (type.TypeParameters.Length == 0)
            return name;

        return name + "<" + string.Join(", ",
            type.TypeParameters.Select(static parameter => CodeWriter.Escape(parameter.Name))) + ">";
    }

    private static void Execute(SourceProductionContext production,
        ImmutableArray<SolverDependencyPropertyModel> properties)
    {
        if (properties.IsDefaultOrEmpty)
            return;

        foreach (IGrouping<string, SolverDependencyPropertyModel> type in properties
                     .GroupBy(static model => model.TypeMetadataName, StringComparer.Ordinal)
                     .OrderBy(static group => group.Key, StringComparer.Ordinal))
        {
            SolverDependencyPropertyModel[] members = type
                .OrderBy(static model => model.Location?.FilePath ?? "", StringComparer.Ordinal)
                .ThenBy(static model => model.Location?.TextSpan.Start ?? 0)
                .ThenBy(static model => model.PropertyName, StringComparer.Ordinal)
                .ToArray();

            var reportedTypeDiagnostics = new HashSet<(string Id, EquatableArray<string> Arguments)>();
            bool typeHasErrors = false;
            var valid = new List<SolverDependencyPropertyModel>();
            foreach (SolverDependencyPropertyModel model in members)
            {
                foreach (DiagnosticInfo diagnostic in model.TypeDiagnostics)
                {
                    if (!reportedTypeDiagnostics.Add((diagnostic.Id, diagnostic.Arguments)))
                        continue;

                    DiagnosticDescriptor descriptor = SolverDependencyDiagnostics.Get(diagnostic.Id);
                    production.ReportDiagnostic(diagnostic.ToDiagnostic(descriptor));
                    typeHasErrors |= descriptor.DefaultSeverity == DiagnosticSeverity.Error;
                }

                bool propertyHasErrors = false;
                foreach (DiagnosticInfo diagnostic in model.Diagnostics)
                {
                    DiagnosticDescriptor descriptor = SolverDependencyDiagnostics.Get(diagnostic.Id);
                    production.ReportDiagnostic(diagnostic.ToDiagnostic(descriptor));
                    propertyHasErrors |= descriptor.DefaultSeverity == DiagnosticSeverity.Error;
                }

                if (!propertyHasErrors)
                    valid.Add(model);
            }

            if (typeHasErrors || valid.Count == 0)
                continue;

            production.AddSource(type.Key.Replace('`', '_') + ".SolverDependencies.g.cs",
                SolverDependencyEmitter.Emit(valid));
        }
    }
}
