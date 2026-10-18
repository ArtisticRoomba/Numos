using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
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
///         Every check runs in the attribute transform, which sees one property at a time and reruns for every
///         property on each compilation change. The checks that walk the base chain only run for the type's first
///         <c>[SolverDependency]</c> property. The grouping step after <c>Collect</c> splits diagnostics from one
///         location-free model per type, so a file is only rewritten when that type's output changes.
///     </para>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class SolverDependencyGenerator : IIncrementalGenerator
{
    internal const string PropertiesStepName = "SolverDependencyProperties";
    internal const string TypesStepName = "SolverDependencyTypes";

    private const string AttributeMetadataName = "Numos.API.SolverDependencyAttribute";
    private const string DependenciesInterfaceMetadataName = "Numos.API.IAtmosSolverDependencies";
    private const string WorldSolverMetadataName = "Numos.API.IAtmosWorldSolver";
    private const string ResolverMetadataName = "Numos.API.AtmosSolverDependencyResolver";
    private const string HookName = "InjectSolverDependencies";

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<SolverDependencyDeclaration> properties = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                AttributeMetadataName,
                static (node, _) => node is PropertyDeclarationSyntax,
                static (attributeContext, cancellationToken) => CreateModel(attributeContext, cancellationToken))
            .Where(static model => model is not null)
            .Select(static (model, _) => model!)
            .WithTrackingName(PropertiesStepName);

        IncrementalValueProvider<GeneratorOutput<SolverDependencyTypeModel>> output = properties
            .Collect()
            .Select(static (declarations, _) => Analyze(declarations));

        GeneratorOutput.Register(
            context,
            output,
            TypesStepName,
            static type =>
                (type.Type.HintName + ".SolverDependencies.g.cs", SolverDependencyEmitter.Emit(type)));
    }

    private static SolverDependencyDeclaration? CreateModel(
        GeneratorAttributeSyntaxContext context,
        CancellationToken cancellationToken)
    {
        if (context.TargetSymbol is not IPropertySymbol property || context.Attributes.IsDefaultOrEmpty)
            return null;

        var syntax = (PropertyDeclarationSyntax)context.TargetNode;
        AttributeData attribute = context.Attributes[0];
        INamedTypeSymbol type = property.ContainingType;
        var diagnostics = new List<DiagnosticInfo>();
        var typeDiagnostics = new List<DiagnosticInfo>();

        LocationInfo? location = LocationInfo.From(syntax.Identifier.GetLocation());
        LocationInfo? attributeLocation =
            LocationInfo.From(attribute.ApplicationSyntaxReference?.GetSyntax(cancellationToken)) ?? location;

        LocationInfo? typeLocation = syntax.Parent is BaseTypeDeclarationSyntax declaration
            ? LocationInfo.From(declaration.Identifier.GetLocation())
            : location;

        string typeDisplayName = type.ToDisplayString();
        string displayName = typeDisplayName + "." + property.Name;

        foreach ((INamedTypeSymbol containing, bool isPartial) in PartialSyntax.GetContainingChain(type, syntax))
        {
            if (!isPartial)
            {
                typeDiagnostics.Add(
                    DiagnosticInfo.Create(
                        SolverDependencyDiagnostics.TypeNotPartial,
                        typeLocation,
                        containing.ToDisplayString(),
                        displayName));
            }
        }

        bool isClass = type.TypeKind == TypeKind.Class;
        if (!isClass)
        {
            typeDiagnostics.Add(
                DiagnosticInfo.Create(
                    SolverDependencyDiagnostics.NotClass,
                    typeLocation,
                    displayName,
                    typeDisplayName));
        }

        if (!property.IsPartialDefinition ||
            property.PartialImplementationPart is not null ||
            property.SetMethod is not null ||
            property.IsIndexer ||
            property.ReturnsByRef ||
            property.ReturnsByRefReadonly)
        {
            diagnostics.Add(
                DiagnosticInfo.Create(
                    SolverDependencyDiagnostics.NotPartialDefinition,
                    location,
                    displayName));
        }

        if (property.IsStatic)
            diagnostics.Add(DiagnosticInfo.Create(SolverDependencyDiagnostics.Static, location, displayName));

        if (!property.Type.IsReferenceType)
        {
            diagnostics.Add(
                DiagnosticInfo.Create(
                    SolverDependencyDiagnostics.NotReferenceType,
                    location,
                    displayName,
                    property.Type.ToDisplayString()));
        }

        bool optional = false;
        foreach (KeyValuePair<string, TypedConstant> argument in attribute.NamedArguments)
        {
            if (argument.Key == "Optional" && argument.Value.Value is bool value)
                optional = value;
        }

        // Oblivious (nullable disabled) types say nothing either way, so only an explicit annotation can disagree.
        NullableAnnotation annotation = property.NullableAnnotation;
        if (optional && annotation == NullableAnnotation.NotAnnotated)
        {
            diagnostics.Add(
                DiagnosticInfo.Create(
                    SolverDependencyDiagnostics.OptionalMismatch,
                    attributeLocation,
                    displayName,
                    "is Optional but its type is not nullable; declare the type with '?'"));
        }
        else if (!optional && annotation == NullableAnnotation.Annotated)
        {
            diagnostics.Add(
                DiagnosticInfo.Create(
                    SolverDependencyDiagnostics.OptionalMismatch,
                    location,
                    displayName,
                    "has a nullable type but is required; set Optional = true or remove the '?'"));
        }

        INamedTypeSymbol? attributeType = attribute.AttributeClass;
        DependencyTypeAnalysis? typeAnalysis = IsFirstDependency(property, type, attributeType)
            ? AnalyzeType(type, context.SemanticModel.Compilation, attributeType, typeDisplayName)
            : null;

        var model = new SolverDependencyPropertyModel(
            property.Name,
            PartialSyntax.ModifiersWithoutPartial(syntax.Modifiers),
            property.Type.ToDisplayString(PartialSyntax.FullyQualifiedWithNullability),
            annotation == NullableAnnotation.Annotated,
            property.Type.WithNullableAnnotation(NullableAnnotation.NotAnnotated)
                .ToDisplayString(PartialSyntax.FullyQualifiedWithNullability),
            optional);

        return new SolverDependencyDeclaration(
            PartialTypeModel.From(type),
            model,
            location,
            typeLocation,
            diagnostics.ToEquatableArray(),
            typeDiagnostics.ToEquatableArray(),
            typeAnalysis);
    }

    /// <summary>
    ///     Gets whether <paramref name="property" /> is the first <c>[SolverDependency]</c> property on its type.
    /// </summary>
    /// <remarks>
    ///     The transform reruns for every property on each compilation change, and the type-level checks walk the
    ///     base chain and bind attributes on base members. Gating them on one property keeps that to once per type.
    ///     Indexers are skipped because the syntax predicate never sees them.
    /// </remarks>
    private static bool IsFirstDependency(
        IPropertySymbol property, INamedTypeSymbol type,
        INamedTypeSymbol? attributeType)
    {
        IPropertySymbol definition = property.PartialDefinitionPart ?? property;
        foreach (ISymbol member in type.GetMembers())
        {
            if (member is IPropertySymbol { IsIndexer: false } candidate && HasAttribute(candidate, attributeType))
                return SymbolEqualityComparer.Default.Equals(candidate, definition);
        }

        return true;
    }

    private static DependencyTypeAnalysis AnalyzeType(
        INamedTypeSymbol type, Compilation compilation,
        INamedTypeSymbol? attributeType, string typeDisplayName)
    {
        if (type.TypeKind != TypeKind.Class)
        {
            return new DependencyTypeAnalysis(
                new DependencyHookModel(false, "private", false),
                EquatableArray<DiagnosticInfo>.Empty);
        }

        INamedTypeSymbol? dependenciesInterface = compilation.GetTypeByMetadataName(DependenciesInterfaceMetadataName);
        INamedTypeSymbol? worldSolver = compilation.GetTypeByMetadataName(WorldSolverMetadataName);
        INamedTypeSymbol? resolver = compilation.GetTypeByMetadataName(ResolverMetadataName);
        var diagnostics = new List<DiagnosticInfo>();

        if (!type.IsAbstract &&
            worldSolver is not null &&
            !type.AllInterfaces.Contains(worldSolver, SymbolEqualityComparer.Default))
        {
            diagnostics.Add(DiagnosticInfo.Create(SolverDependencyDiagnostics.NotWorldSolver, null, typeDisplayName));
        }

        if (dependenciesInterface is not null && DeclaresOwnInjection(type, dependenciesInterface))
        {
            diagnostics.Add(
                DiagnosticInfo.Create(
                    SolverDependencyDiagnostics.AlreadyImplemented,
                    null,
                    typeDisplayName));
        }

        DependencyHookModel hook = FindHook(
            type,
            dependenciesInterface,
            resolver,
            attributeType,
            typeDisplayName,
            diagnostics);

        return new DependencyTypeAnalysis(hook, diagnostics.ToEquatableArray());
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
    private static DependencyHookModel FindHook(
        INamedTypeSymbol type, INamedTypeSymbol? dependenciesInterface,
        INamedTypeSymbol? resolver, INamedTypeSymbol? attributeType, string typeDisplayName,
        List<DiagnosticInfo> typeDiagnostics)
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
                                       current.AllInterfaces.Contains(
                                           dependenciesInterface,
                                           SymbolEqualityComparer.Default);

            if (implementsInterface && !current.GetMembers(HookName).IsEmpty)
            {
                typeDiagnostics.Add(
                    DiagnosticInfo.Create(
                        SolverDependencyDiagnostics.BaseNotOverridable,
                        null,
                        current.ToDisplayString(),
                        typeDisplayName));

                return new DependencyHookModel(true, "protected", false);
            }

            if (attributeType is not null && HasSourceDependencies(current, attributeType))
                return new DependencyHookModel(true, "protected", true);

            if (dependenciesInterface is not null &&
                current.Interfaces.Contains(dependenciesInterface, SymbolEqualityComparer.Default))
            {
                typeDiagnostics.Add(
                    DiagnosticInfo.Create(
                        SolverDependencyDiagnostics.BaseNotOverridable,
                        null,
                        current.ToDisplayString(),
                        typeDisplayName));

                return new DependencyHookModel(true, "protected", false);
            }
        }

        return new DependencyHookModel(false, type.IsSealed ? "private" : "protected", false);
    }

    private static IMethodSymbol? FindOverridableHook(
        INamedTypeSymbol baseType, INamedTypeSymbol derived,
        INamedTypeSymbol? resolver)
    {
        foreach (ISymbol member in baseType.GetMembers(HookName))
        {
            if (member is not IMethodSymbol { IsStatic: false, ReturnsVoid: true, Parameters.Length: 1 } method ||
                method.IsSealed ||
                !(method.IsVirtual || method.IsOverride || method.IsAbstract) ||
                method.TypeParameters.Length > 0)
            {
                continue;
            }

            if (resolver is not null &&
                !SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type, resolver))
            {
                continue;
            }

            bool sameAssembly = SymbolEqualityComparer.Default.Equals(
                baseType.ContainingAssembly,
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
            if (member is IPropertySymbol { IsStatic: false } property && HasAttribute(property, attributeType))
                return true;
        }

        return false;
    }

    private static bool HasAttribute(IPropertySymbol property, INamedTypeSymbol? attributeType)
    {
        foreach (AttributeData attribute in property.GetAttributes())
        {
            if (SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, attributeType))
                return true;
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

    private static GeneratorOutput<SolverDependencyTypeModel> Analyze(
        ImmutableArray<SolverDependencyDeclaration> declarations)
    {
        if (declarations.IsDefaultOrEmpty)
            return GeneratorOutput<SolverDependencyTypeModel>.Empty;

        var diagnostics = new List<DiagnosticInfo>();
        var types = new List<SolverDependencyTypeModel>();
        foreach (IGrouping<string, SolverDependencyDeclaration> type in declarations
                     .GroupBy(static declaration => declaration.Type.MetadataName, StringComparer.Ordinal)
                     .OrderBy(static group => group.Key, StringComparer.Ordinal))
        {
            SolverDependencyDeclaration[] members = type
                .OrderBySource(static declaration => declaration.Location)
                .ThenBy(static declaration => declaration.Property.PropertyName, StringComparer.Ordinal)
                .ToArray();

            DependencyTypeAnalysis? analysis = members
                .Select(static declaration => declaration.TypeAnalysis)
                .FirstOrDefault(static analysis => analysis is not null);

            var reportedTypeDiagnostics = new HashSet<(DiagnosticDescriptor, EquatableArray<string>)>();
            bool typeHasErrors = false;
            var valid = new List<SolverDependencyPropertyModel>();
            for (int i = 0; i < members.Length; i++)
            {
                SolverDependencyDeclaration declaration = members[i];
                IEnumerable<DiagnosticInfo> typeDiagnostics = declaration.TypeDiagnostics;
                if (i == 0 && analysis is not null)
                {
                    LocationInfo? typeLocation = declaration.TypeLocation;
                    typeDiagnostics = typeDiagnostics.Concat(
                        analysis.Diagnostics
                            .Select(diagnostic => diagnostic with { Location = typeLocation }));
                }

                foreach (DiagnosticInfo diagnostic in typeDiagnostics)
                {
                    if (!reportedTypeDiagnostics.Add((diagnostic.Descriptor, diagnostic.Arguments)))
                        continue;

                    diagnostics.Add(diagnostic);
                    typeHasErrors |= diagnostic.Descriptor.DefaultSeverity == DiagnosticSeverity.Error;
                }

                bool propertyHasErrors = false;
                foreach (DiagnosticInfo diagnostic in declaration.Diagnostics)
                {
                    diagnostics.Add(diagnostic);
                    propertyHasErrors |= diagnostic.Descriptor.DefaultSeverity == DiagnosticSeverity.Error;
                }

                if (!propertyHasErrors)
                    valid.Add(declaration.Property);
            }

            // The analysis rides on the type's first [SolverDependency] property, which is always among the members.
            if (typeHasErrors || valid.Count == 0 || analysis is null)
                continue;

            types.Add(new SolverDependencyTypeModel(members[0].Type, analysis.Hook, valid.ToEquatableArray()));
        }

        return new GeneratorOutput<SolverDependencyTypeModel>(diagnostics.ToEquatableArray(), types.ToEquatableArray());
    }
}