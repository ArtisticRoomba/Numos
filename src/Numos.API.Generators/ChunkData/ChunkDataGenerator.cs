using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Numos.API.Generators.Common;

namespace Numos.API.Generators.ChunkData;

/// <summary>
///     Implements <c>[ChunkData]</c> partial properties and generates typed chunk view accessors for them.
/// </summary>
/// <remarks>
///     <para>
///         Each property gets a body that returns a <c>ChunkField&lt;T&gt;</c> registered from a nested holder class,
///         and an extension property on <c>AtmosSolverChunk</c> (and <c>AtmosDangerousChunk</c> when
///         <c>Numos.API.Dangerous</c> is referenced). Output is one file per declaring type.
///     </para>
///     <para>
///         Per-property checks run in the attribute transform. Key conflicts and accessor collisions need every
///         property in the compilation, so they run after <c>Collect</c>; that step reruns whenever any
///         <c>[ChunkData]</c> property changes, but not on unrelated edits.
///     </para>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class ChunkDataGenerator : IIncrementalGenerator
{
    internal const string PropertiesStepName = "ChunkDataProperties";
    internal const string LanguageVersionStepName = "ChunkDataLanguageVersion";
    internal const string ViewInfoStepName = "ChunkDataViewInfo";

    private const string AttributeMetadataName = "Numos.API.ChunkDataAttribute";
    private const string ChunkFieldMetadataName = "Numos.API.ChunkField`1";
    private const string SolverChunkMetadataName = "Numos.API.AtmosSolverChunk";
    private const string DangerousChunkMetadataName = "Numos.API.Dangerous.AtmosDangerousChunk";

    private static readonly SymbolDisplayFormat FullyQualifiedWithNullability =
        SymbolDisplayFormat.FullyQualifiedFormat.AddMiscellaneousOptions(
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    // Members a generated accessor would be hidden behind or ambiguous with. Instance members always win over
    // extension members, so an accessor with one of these names compiles but can never be called. The view types'
    // own public members are added from the compilation; these cover object members and the Dangerous() bridge.
    private static readonly string[] SolverChunkReservedNames =
    [
        "Simulation", "Handle", "Position", "Dimensions", "VoxelCount", "IsAwake", "GetCellRef", "Get", "GetSpan",
        "ToString", "Equals", "GetHashCode", "GetType", "Dangerous",
    ];

    private static readonly string[] ObjectMemberNames = ["ToString", "Equals", "GetHashCode", "GetType"];

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<ChunkDataPropertyModel> properties = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                AttributeMetadataName,
                static (node, _) => node is PropertyDeclarationSyntax,
                static (attributeContext, cancellationToken) => CreateModel(attributeContext, cancellationToken))
            .Where(static model => model is not null)
            .Select(static (model, _) => model!)
            .WithTrackingName(PropertiesStepName);

        IncrementalValueProvider<LanguageVersion> languageVersion = context.ParseOptionsProvider
            .Select(static (options, _) => ((CSharpParseOptions)options).LanguageVersion)
            .WithTrackingName(LanguageVersionStepName);

        // Runs on every compilation change, but only the small equatable result flows on, so nothing downstream
        // reruns unless a view type actually gains or loses a member.
        IncrementalValueProvider<ChunkViewInfo> viewInfo = context.CompilationProvider
            .Select(static (compilation, _) => CreateViewInfo(compilation))
            .WithTrackingName(ViewInfoStepName);

        IncrementalValueProvider<((ImmutableArray<ChunkDataPropertyModel> Properties, LanguageVersion Language) Left,
            ChunkViewInfo View)> combined = properties
            .Collect()
            .Combine(languageVersion)
            .Combine(viewInfo);

        context.RegisterSourceOutput(combined, static (production, input) =>
            Execute(production, input.Left.Properties, input.Left.Language, input.View));
    }

    private static ChunkViewInfo CreateViewInfo(Compilation compilation)
    {
        var solverMembers = new SortedSet<string>(SolverChunkReservedNames, StringComparer.Ordinal);
        AddPublicMemberNames(compilation.GetTypeByMetadataName(SolverChunkMetadataName), solverMembers);

        INamedTypeSymbol? dangerousChunk = compilation.GetTypeByMetadataName(DangerousChunkMetadataName);
        var dangerousMembers = new SortedSet<string>(StringComparer.Ordinal);
        if (dangerousChunk is not null)
        {
            dangerousMembers.UnionWith(ObjectMemberNames);
            AddPublicMemberNames(dangerousChunk, dangerousMembers);
        }

        return new ChunkViewInfo(
            dangerousChunk is not null,
            solverMembers.ToEquatableArray(),
            dangerousMembers.ToEquatableArray());
    }

    private static void AddPublicMemberNames(INamedTypeSymbol? type, ISet<string> names)
    {
        if (type is null)
            return;

        foreach (ISymbol member in type.GetMembers())
        {
            if (member.DeclaredAccessibility == Accessibility.Public && member.CanBeReferencedByName)
                names.Add(member.Name);
        }
    }

    private static ChunkDataPropertyModel? CreateModel(GeneratorAttributeSyntaxContext context,
        CancellationToken cancellationToken)
    {
        if (context.TargetSymbol is not IPropertySymbol property || context.Attributes.IsDefaultOrEmpty)
            return null;

        var syntax = (PropertyDeclarationSyntax)context.TargetNode;
        AttributeData attribute = context.Attributes[0];
        INamedTypeSymbol containingType = property.ContainingType;
        var diagnostics = new List<DiagnosticInfo>();

        LocationInfo? location = LocationInfo.From(syntax.Identifier.GetLocation());
        LocationInfo? attributeLocation =
            LocationInfo.From(attribute.ApplicationSyntaxReference?.GetSyntax(cancellationToken)) ?? location;

        List<INamedTypeSymbol> chain = GetContainingChain(containingType);
        string displayName = string.Join(".", chain.Select(static type => type.Name)) + "." + property.Name;

        if (!property.IsPartialDefinition || property.PartialImplementationPart is not null ||
            property.SetMethod is not null)
        {
            diagnostics.Add(DiagnosticInfo.Create(ChunkDataDiagnostics.NotPartialDefinition, location, displayName));
        }

        if (!property.IsStatic)
            diagnostics.Add(DiagnosticInfo.Create(ChunkDataDiagnostics.NotStatic, location, displayName));

        INamedTypeSymbol? chunkFieldType = context.SemanticModel.Compilation.GetTypeByMetadataName(ChunkFieldMetadataName);
        ITypeSymbol? elementType = null;
        if (property.Type is INamedTypeSymbol namedType && chunkFieldType is not null &&
            SymbolEqualityComparer.Default.Equals(namedType.OriginalDefinition, chunkFieldType))
        {
            elementType = namedType.TypeArguments[0];
        }
        else
        {
            diagnostics.Add(DiagnosticInfo.Create(ChunkDataDiagnostics.NotChunkField, location, displayName,
                property.Type.ToDisplayString()));
        }

        bool accessible = IsAccessibleFromNamespace(property.DeclaredAccessibility);
        foreach (INamedTypeSymbol type in chain)
        {
            if (!IsPartial(type, cancellationToken))
            {
                diagnostics.Add(DiagnosticInfo.Create(ChunkDataDiagnostics.ContainingTypeNotPartial, location,
                    type.ToDisplayString(), displayName));
            }

            if (type.TypeParameters.Length > 0)
            {
                diagnostics.Add(DiagnosticInfo.Create(ChunkDataDiagnostics.ContainingTypeGeneric, location,
                    type.ToDisplayString(), displayName));
            }

            if (type.IsFileLocal || !IsAccessibleFromNamespace(type.DeclaredAccessibility))
                accessible = false;
        }

        if (!accessible)
            diagnostics.Add(DiagnosticInfo.Create(ChunkDataDiagnostics.Inaccessible, location, displayName));

        string? key;
        bool explicitKey = attribute.ConstructorArguments.Length == 1;
        if (explicitKey)
        {
            key = attribute.ConstructorArguments[0].Value as string;
            if (string.IsNullOrWhiteSpace(key))
            {
                diagnostics.Add(DiagnosticInfo.Create(ChunkDataDiagnostics.BlankKey, attributeLocation, displayName));
                key = null;
            }
        }
        else
        {
            key = CreateDefaultKey(containingType.ContainingNamespace, chain, property.Name);
        }

        bool rollback = false;
        int? length = null;
        string? accessorName = null;
        foreach (KeyValuePair<string, TypedConstant> argument in attribute.NamedArguments)
        {
            switch (argument.Key)
            {
                case "Rollback" when argument.Value.Value is bool value:
                    rollback = value;
                    break;
                case "Length" when argument.Value.Value is int value:
                    length = value;
                    break;
                case "AccessorName":
                    accessorName = argument.Value.Value as string;
                    break;
            }
        }

        if (rollback && !explicitKey)
            diagnostics.Add(DiagnosticInfo.Create(ChunkDataDiagnostics.RollbackWithoutKey, attributeLocation, displayName));

        if (rollback && elementType is not null && !elementType.IsUnmanagedType)
        {
            diagnostics.Add(DiagnosticInfo.Create(ChunkDataDiagnostics.RollbackWithManagedType, attributeLocation,
                displayName, elementType.ToDisplayString()));
        }

        if (length is { } negativeLength && negativeLength < 0)
        {
            diagnostics.Add(DiagnosticInfo.Create(ChunkDataDiagnostics.NegativeLength, attributeLocation, displayName,
                negativeLength.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        if (accessorName is not null && !SyntaxFacts.IsValidIdentifier(accessorName))
        {
            diagnostics.Add(DiagnosticInfo.Create(ChunkDataDiagnostics.InvalidAccessorName, attributeLocation,
                accessorName, displayName));
        }

        string modifiers = string.Join(" ", syntax.Modifiers
            .Where(static modifier => !modifier.IsKind(SyntaxKind.PartialKeyword))
            .Select(static modifier => modifier.Text));

        string @namespace = containingType.ContainingNamespace.IsGlobalNamespace
            ? ""
            : containingType.ContainingNamespace.ToDisplayString();

        string typeMetadataName = string.Join("+", chain.Select(static type => type.MetadataName));
        if (@namespace.Length > 0)
            typeMetadataName = @namespace + "." + typeMetadataName;

        return new ChunkDataPropertyModel(
            @namespace,
            typeMetadataName,
            containingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            string.Concat(chain.Select(static type => type.Name)) + "ChunkAccessors",
            chain.Select(static type => new ContainingTypeModel(GetTypeKeyword(type), type.Name)).ToEquatableArray(),
            property.Name,
            modifiers,
            property.Type.ToDisplayString(FullyQualifiedWithNullability),
            property.Type.NullableAnnotation == NullableAnnotation.Annotated,
            elementType?.ToDisplayString(FullyQualifiedWithNullability),
            elementType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            property.DeclaredAccessibility == Accessibility.Public &&
            chain.All(static type => type.DeclaredAccessibility == Accessibility.Public),
            key,
            rollback,
            length,
            accessorName ?? property.Name,
            location,
            diagnostics.ToEquatableArray());
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

    private static bool IsAccessibleFromNamespace(Accessibility accessibility)
    {
        return accessibility is Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal;
    }

    private static string CreateDefaultKey(INamespaceSymbol @namespace, List<INamedTypeSymbol> chain, string property)
    {
        string types = string.Join(".", chain.Select(static type => type.Name));
        return @namespace.IsGlobalNamespace
            ? types + "." + property
            : @namespace.ToDisplayString() + "." + types + "." + property;
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

    private static void Execute(SourceProductionContext production,
        ImmutableArray<ChunkDataPropertyModel> properties, LanguageVersion languageVersion, ChunkViewInfo view)
    {
        if (properties.IsDefaultOrEmpty)
            return;

        ChunkDataPropertyModel[] ordered = properties
            .OrderBy(static model => model.Location?.FilePath ?? "", StringComparer.Ordinal)
            .ThenBy(static model => model.Location?.TextSpan.Start ?? 0)
            .ThenBy(static model => model.TypeMetadataName, StringComparer.Ordinal)
            .ThenBy(static model => model.PropertyName, StringComparer.Ordinal)
            .ToArray();

        var invalid = new HashSet<ChunkDataPropertyModel>(ReferenceEqualityComparer.Instance);
        foreach (ChunkDataPropertyModel model in ordered)
        {
            foreach (DiagnosticInfo diagnostic in model.Diagnostics)
                production.ReportDiagnostic(diagnostic.ToDiagnostic(ChunkDataDiagnostics.Get(diagnostic.Id)));

            if (model.HasErrors)
                invalid.Add(model);
        }

        CheckKeys(production, ordered, invalid);

        bool useExtensionBlocks = languageVersion >= LanguageVersion.CSharp14;
        var reservedOnSolver = new HashSet<string>(view.SolverChunkMembers, StringComparer.Ordinal);
        var reservedOnDangerous = new HashSet<string>(view.DangerousChunkMembers, StringComparer.Ordinal);
        var namesByClass = new HashSet<(string Namespace, string Class, string Member)>();
        var ownerByNamespaceName = new Dictionary<(string Namespace, string Member), ChunkDataPropertyModel>();
        var accessors = new Dictionary<ChunkDataPropertyModel, AccessorTargets>(ReferenceEqualityComparer.Instance);

        foreach (ChunkDataPropertyModel model in ordered)
        {
            if (invalid.Contains(model))
                continue;

            string memberName = useExtensionBlocks ? model.AccessorName : "Get" + model.AccessorName;
            bool onSolver = !reservedOnSolver.Contains(memberName);
            bool onDangerous = view.HasDangerous && !reservedOnDangerous.Contains(memberName);

            if (!onSolver || (view.HasDangerous && !onDangerous))
            {
                string viewType = !onSolver ? "Numos.API.AtmosSolverChunk" : "Numos.API.Dangerous.AtmosDangerousChunk";
                ReportCollision(production, model, memberName, $"the {viewType} member '{memberName}'");
            }

            if (!namesByClass.Add((model.Namespace, model.AccessorClassName, memberName)))
            {
                ReportCollision(production, model, memberName,
                    $"another accessor in '{model.AccessorClassName}' with the same name");
                continue;
            }

            if (ownerByNamespaceName.TryGetValue((model.Namespace, memberName), out ChunkDataPropertyModel? owner))
            {
                ReportCollision(production, model, memberName,
                    $"the accessor generated for '{owner.DisplayName}' in the same namespace");
            }
            else
            {
                ownerByNamespaceName.Add((model.Namespace, memberName), model);
            }

            accessors.Add(model, new AccessorTargets(memberName, onSolver, onDangerous));
        }

        foreach (IGrouping<string, ChunkDataPropertyModel> type in ordered
                     .Where(model => !invalid.Contains(model))
                     .GroupBy(static model => model.TypeMetadataName, StringComparer.Ordinal)
                     .OrderBy(static group => group.Key, StringComparer.Ordinal))
        {
            ChunkDataPropertyModel[] members = type.ToArray();
            if (!useExtensionBlocks && members.Any(model => accessors.ContainsKey(model)))
            {
                ChunkDataPropertyModel first = members[0];
                production.ReportDiagnostic(Diagnostic.Create(ChunkDataDiagnostics.ExtensionMethodFallback,
                    first.Location?.ToLocation() ?? Location.None, first.TypeDisplay.Replace("global::", "")));
            }

            string source = ChunkDataEmitter.Emit(members, accessors, useExtensionBlocks, view.HasDangerous);
            production.AddSource(type.Key + ".ChunkData.g.cs", source);
        }
    }

    private static void CheckKeys(SourceProductionContext production, ChunkDataPropertyModel[] ordered,
        HashSet<ChunkDataPropertyModel> invalid)
    {
        foreach (IGrouping<string, ChunkDataPropertyModel> group in ordered
                     .Where(static model => model.Key is not null && model.ElementTypeIdentity is not null)
                     .GroupBy(static model => model.Key!, StringComparer.Ordinal))
        {
            ChunkDataPropertyModel[] declarations = group.ToArray();
            if (declarations.Length < 2)
                continue;

            ChunkDataPropertyModel first = declarations[0];
            bool identical = declarations.All(model =>
                model.ElementTypeIdentity == first.ElementTypeIdentity && model.Rollback == first.Rollback &&
                model.Length == first.Length);

            if (identical)
            {
                for (int i = 1; i < declarations.Length; i++)
                {
                    production.ReportDiagnostic(Diagnostic.Create(ChunkDataDiagnostics.DuplicateKey,
                        declarations[i].Location?.ToLocation() ?? Location.None,
                        group.Key, declarations[i].DisplayName, first.DisplayName));
                }

                continue;
            }

            foreach (ChunkDataPropertyModel model in declarations)
            {
                string others = string.Join(", ", declarations
                    .Where(other => !ReferenceEquals(other, model))
                    .Select(static other => "'" + other.DisplayName + "'"));
                production.ReportDiagnostic(Diagnostic.Create(ChunkDataDiagnostics.ConflictingKey,
                    model.Location?.ToLocation() ?? Location.None, group.Key, model.DisplayName, others));
                invalid.Add(model);
            }
        }
    }

    private static void ReportCollision(SourceProductionContext production, ChunkDataPropertyModel model,
        string memberName, string collidesWith)
    {
        production.ReportDiagnostic(Diagnostic.Create(ChunkDataDiagnostics.AccessorNameCollision,
            model.Location?.ToLocation() ?? Location.None, memberName, model.DisplayName, collidesWith));
    }
}

/// <summary>
///     Which chunk views get an accessor for one property, and under what member name.
/// </summary>
internal readonly record struct AccessorTargets(string MemberName, bool OnSolverChunk, bool OnDangerousChunk);
