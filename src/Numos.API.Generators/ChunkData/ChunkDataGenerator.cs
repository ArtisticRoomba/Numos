using System.Collections.Immutable;
using System.Globalization;
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
///         and a C# 14 extension property on <c>AtmosSolverChunk</c>. Output is one file per declaring type.
///     </para>
///     <para>
///         Per-property checks run in the attribute transform. Key conflicts and accessor collisions need every property in the
///         compilation, so they run after <c>Collect</c>. That step reruns whenever any <c>[ChunkData]</c> property changes,
///         but not on unrelated edits. It hands on one location-free model per type, so a file is only rewritten when that
///         type's output changes.
///     </para>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class ChunkDataGenerator : IIncrementalGenerator
{
    internal const string PropertiesStepName = "ChunkDataProperties";
    internal const string LanguageVersionStepName = "ChunkDataLanguageVersion";
    internal const string ViewInfoStepName = "ChunkDataViewInfo";
    internal const string TypesStepName = "ChunkDataTypes";

    private const string AttributeMetadataName = "Numos.API.ChunkDataAttribute";
    private const string ChunkFieldMetadataName = "Numos.API.ChunkField`1";
    private const string SolverChunkMetadataName = "Numos.API.AtmosSolverChunk";

    // Members a generated accessor would be hidden behind or ambiguous with. Instance members always win over
    // extension members, so an accessor with one of these names compiles but can never be called. The view type's
    // own public members are added from the compilation; these cover the object members it inherits.
    private static readonly string[] ObjectMemberNames = ["ToString", "Equals", "GetHashCode", "GetType"];

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<ChunkDataDeclaration> properties = context.SyntaxProvider
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
        IncrementalValueProvider<EquatableArray<string>> viewInfo = context.CompilationProvider
            .Select(static (compilation, _) => GetReservedNames(compilation))
            .WithTrackingName(ViewInfoStepName);

        IncrementalValueProvider<GeneratorOutput<ChunkDataTypeModel>> output = properties
            .Collect()
            .Combine(languageVersion)
            .Combine(viewInfo)
            .Select(static (input, _) => Analyze(input.Left.Left, input.Left.Right, input.Right));

        GeneratorOutput.Register(
            context,
            output,
            TypesStepName,
            static type =>
                (type.Members[0].Property.Type.HintName + ".ChunkData.g.cs", ChunkDataEmitter.Emit(type)));
    }

    private static EquatableArray<string> GetReservedNames(Compilation compilation)
    {
        // Dangerous() is the extension bridge to the Dangerous view, so it's taken even when that isn't referenced.
        var names = new SortedSet<string>(ObjectMemberNames, StringComparer.Ordinal) { "Dangerous" };
        INamedTypeSymbol? solverChunk = compilation.GetTypeByMetadataName(SolverChunkMetadataName);
        if (solverChunk is not null)
        {
            foreach (ISymbol member in solverChunk.GetMembers())
            {
                if (member.DeclaredAccessibility == Accessibility.Public && member.CanBeReferencedByName)
                    names.Add(member.Name);
            }
        }

        return names.ToEquatableArray();
    }

    private static ChunkDataDeclaration? CreateModel(
        GeneratorAttributeSyntaxContext context,
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

        List<(INamedTypeSymbol Type, bool IsPartial)> chain = PartialSyntax.GetContainingChain(containingType, syntax);
        string displayName = string.Join(".", chain.Select(static link => link.Type.Name)) + "." + property.Name;

        if (!property.IsPartialDefinition ||
            property.PartialImplementationPart is not null ||
            property.SetMethod is not null)
        {
            diagnostics.Add(DiagnosticInfo.Create(ChunkDataDiagnostics.NotPartialDefinition, location, displayName));
        }

        if (!property.IsStatic)
            diagnostics.Add(DiagnosticInfo.Create(ChunkDataDiagnostics.NotStatic, location, displayName));

        INamedTypeSymbol? chunkFieldType = context.SemanticModel.Compilation.GetTypeByMetadataName(ChunkFieldMetadataName);
        ITypeSymbol? elementType = null;
        if (property.Type is INamedTypeSymbol namedType &&
            chunkFieldType is not null &&
            SymbolEqualityComparer.Default.Equals(namedType.OriginalDefinition, chunkFieldType))
        {
            elementType = namedType.TypeArguments[0];
        }
        else
        {
            diagnostics.Add(
                DiagnosticInfo.Create(
                    ChunkDataDiagnostics.NotChunkField,
                    location,
                    displayName,
                    property.Type.ToDisplayString()));
        }

        bool accessible = IsAccessibleFromNamespace(property.DeclaredAccessibility);
        foreach ((INamedTypeSymbol type, bool isPartial) in chain)
        {
            if (!isPartial)
            {
                diagnostics.Add(
                    DiagnosticInfo.Create(
                        ChunkDataDiagnostics.ContainingTypeNotPartial,
                        location,
                        type.ToDisplayString(),
                        displayName));
            }

            if (type.TypeParameters.Length > 0)
            {
                diagnostics.Add(
                    DiagnosticInfo.Create(
                        ChunkDataDiagnostics.ContainingTypeGeneric,
                        location,
                        type.ToDisplayString(),
                        displayName));
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
            key = containingType.ContainingNamespace.IsGlobalNamespace
                ? displayName
                : containingType.ContainingNamespace.ToDisplayString() + "." + displayName;
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
            diagnostics.Add(
                DiagnosticInfo.Create(
                    ChunkDataDiagnostics.RollbackWithManagedType,
                    attributeLocation,
                    displayName,
                    elementType.ToDisplayString()));
        }

        if (length is { } negativeLength && negativeLength < 0)
        {
            diagnostics.Add(
                DiagnosticInfo.Create(
                    ChunkDataDiagnostics.NegativeLength,
                    attributeLocation,
                    displayName,
                    negativeLength.ToString(CultureInfo.InvariantCulture)));
        }

        if (accessorName is not null && !SyntaxFacts.IsValidIdentifier(accessorName))
        {
            diagnostics.Add(
                DiagnosticInfo.Create(
                    ChunkDataDiagnostics.InvalidAccessorName,
                    attributeLocation,
                    accessorName,
                    displayName));
        }

        var model = new ChunkDataPropertyModel(
            PartialTypeModel.From(containingType),
            containingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            string.Concat(chain.Select(static link => link.Type.Name)) + "ChunkAccessors",
            displayName,
            property.Name,
            PartialSyntax.ModifiersWithoutPartial(syntax.Modifiers),
            property.Type.ToDisplayString(PartialSyntax.FullyQualifiedWithNullability),
            property.Type.NullableAnnotation == NullableAnnotation.Annotated,
            elementType?.ToDisplayString(PartialSyntax.FullyQualifiedWithNullability),
            elementType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            property.DeclaredAccessibility == Accessibility.Public &&
            chain.All(static link => link.Type.DeclaredAccessibility == Accessibility.Public),
            key,
            rollback,
            length,
            accessorName ?? property.Name);

        return new ChunkDataDeclaration(model, location, diagnostics.ToEquatableArray());
    }

    private static bool IsAccessibleFromNamespace(Accessibility accessibility)
    {
        return accessibility is Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal;
    }

    private static GeneratorOutput<ChunkDataTypeModel> Analyze(
        ImmutableArray<ChunkDataDeclaration> declarations,
        LanguageVersion languageVersion, EquatableArray<string> reservedNames)
    {
        if (declarations.IsDefaultOrEmpty)
            return GeneratorOutput<ChunkDataTypeModel>.Empty;

        ChunkDataDeclaration[] ordered = declarations
            .OrderBySource(static declaration => declaration.Location)
            .ThenBy(static declaration => declaration.Property.Type.MetadataName, StringComparer.Ordinal)
            .ThenBy(static declaration => declaration.Property.PropertyName, StringComparer.Ordinal)
            .ToArray();

        var diagnostics = new List<DiagnosticInfo>();
        var invalid = new HashSet<ChunkDataDeclaration>(ReferenceEqualityComparer.Instance);
        foreach (ChunkDataDeclaration declaration in ordered)
        {
            diagnostics.AddRange(declaration.Diagnostics);
            if (declaration.HasErrors)
                invalid.Add(declaration);
        }

        CheckKeys(diagnostics, ordered, invalid);

        var accessors = new HashSet<ChunkDataDeclaration>(ReferenceEqualityComparer.Instance);
        if (languageVersion >= LanguageVersion.CSharp14)
        {
            CheckAccessorNames(diagnostics, ordered, invalid, reservedNames, accessors);
        }
        else if (ordered.FirstOrDefault(declaration => !invalid.Contains(declaration)) is { } first)
        {
            diagnostics.Add(
                DiagnosticInfo.Create(
                    ChunkDataDiagnostics.RequiresCSharp14,
                    first.Location,
                    languageVersion.ToDisplayString()));
        }

        var types = new List<ChunkDataTypeModel>();
        foreach (IGrouping<string, ChunkDataDeclaration> type in ordered
                     .Where(declaration => !invalid.Contains(declaration))
                     .GroupBy(static declaration => declaration.Property.Type.MetadataName, StringComparer.Ordinal)
                     .OrderBy(static group => group.Key, StringComparer.Ordinal))
        {
            ChunkDataMemberModel[] members = type
                .Select(declaration => new ChunkDataMemberModel(declaration.Property, accessors.Contains(declaration)))
                .ToArray();

            types.Add(new ChunkDataTypeModel(members.ToEquatableArray()));
        }

        return new GeneratorOutput<ChunkDataTypeModel>(diagnostics.ToEquatableArray(), types.ToEquatableArray());
    }

    private static void CheckAccessorNames(
        List<DiagnosticInfo> diagnostics, ChunkDataDeclaration[] ordered,
        HashSet<ChunkDataDeclaration> invalid, EquatableArray<string> reservedNames,
        HashSet<ChunkDataDeclaration> accessors)
    {
        var reserved = new HashSet<string>(reservedNames, StringComparer.Ordinal);
        var namesByClass = new HashSet<(string Namespace, string Class, string Member)>();
        var ownerByNamespaceName = new Dictionary<(string Namespace, string Member), ChunkDataPropertyModel>();

        foreach (ChunkDataDeclaration declaration in ordered)
        {
            if (invalid.Contains(declaration))
                continue;

            ChunkDataPropertyModel model = declaration.Property;
            string @namespace = model.Type.Namespace;
            string memberName = model.AccessorName;

            if (reserved.Contains(memberName))
            {
                diagnostics.Add(
                    Collision(
                        declaration,
                        memberName,
                        $"the Numos.API.AtmosSolverChunk member '{memberName}'"));

                continue;
            }

            if (!namesByClass.Add((@namespace, model.AccessorClassName, memberName)))
            {
                diagnostics.Add(
                    Collision(
                        declaration,
                        memberName,
                        $"another accessor in '{model.AccessorClassName}' with the same name"));

                continue;
            }

            if (ownerByNamespaceName.TryGetValue((@namespace, memberName), out ChunkDataPropertyModel? owner))
            {
                diagnostics.Add(
                    Collision(
                        declaration,
                        memberName,
                        $"the accessor generated for '{owner.DisplayName}' in the same namespace"));
            }
            else
            {
                ownerByNamespaceName.Add((@namespace, memberName), model);
            }

            accessors.Add(declaration);
        }
    }

    private static void CheckKeys(
        List<DiagnosticInfo> diagnostics, ChunkDataDeclaration[] ordered,
        HashSet<ChunkDataDeclaration> invalid)
    {
        foreach (IGrouping<string, ChunkDataDeclaration> group in ordered
                     .Where(static declaration =>
                         declaration.Property.Key is not null && declaration.Property.ElementTypeIdentity is not null)
                     .GroupBy(static declaration => declaration.Property.Key!, StringComparer.Ordinal))
        {
            ChunkDataDeclaration[] declarations = group.ToArray();
            if (declarations.Length < 2)
                continue;

            ChunkDataPropertyModel first = declarations[0].Property;
            bool identical = declarations.All(declaration =>
                declaration.Property.ElementTypeIdentity == first.ElementTypeIdentity &&
                declaration.Property.Rollback == first.Rollback &&
                declaration.Property.Length == first.Length);

            if (identical)
            {
                for (int i = 1; i < declarations.Length; i++)
                {
                    diagnostics.Add(
                        DiagnosticInfo.Create(
                            ChunkDataDiagnostics.DuplicateKey,
                            declarations[i].Location,
                            group.Key,
                            declarations[i].Property.DisplayName,
                            first.DisplayName));
                }

                continue;
            }

            foreach (ChunkDataDeclaration declaration in declarations)
            {
                string others = string.Join(
                    ", ",
                    declarations
                        .Where(other => !ReferenceEquals(other, declaration))
                        .Select(static other => "'" + other.Property.DisplayName + "'"));

                diagnostics.Add(
                    DiagnosticInfo.Create(
                        ChunkDataDiagnostics.ConflictingKey,
                        declaration.Location,
                        group.Key,
                        declaration.Property.DisplayName,
                        others));

                invalid.Add(declaration);
            }
        }
    }

    private static DiagnosticInfo Collision(ChunkDataDeclaration declaration, string memberName, string collidesWith)
    {
        return DiagnosticInfo.Create(
            ChunkDataDiagnostics.AccessorNameCollision,
            declaration.Location,
            memberName,
            declaration.Property.DisplayName,
            collidesWith);
    }
}