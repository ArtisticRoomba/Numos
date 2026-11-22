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
///         and a C# 14 extension property on <c>AtmosSolverChunk</c>. Output is one file per declaring type, plus one
///         module initializer per assembly that registers every field when the assembly loads.
///     </para>
///     <para>
///         The same generator lowers <c>[ChunkComponent]</c> structs to one chunk field per struct field, with a view
///         type and extension members per struct. Both live here because a component field and a <c>[ChunkData]</c>
///         property can share a key or an accessor name, and only one pass sees both.
///     </para>
///     <para>
///         Per-property checks run in the attribute transform. Key conflicts and accessor collisions need every property in the
///         compilation, so they run after <c>Collect</c>. That step reruns whenever any <c>[ChunkData]</c> property changes,
///         but not on unrelated edits. It hands on one location-free model per type, so a file is only rewritten when that
///         type's output changes.
///     </para>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed partial class ChunkDataGenerator : IIncrementalGenerator
{
    internal const string PropertiesStepName = "ChunkDataProperties";
    internal const string ComponentDeclarationsStepName = "ChunkComponentDeclarations";
    internal const string ComponentsStepName = "ChunkComponents";
    internal const string LanguageVersionStepName = "ChunkDataLanguageVersion";
    internal const string ViewInfoStepName = "ChunkDataViewInfo";
    internal const string TypesStepName = "ChunkDataTypes";
    internal const string ModuleInitializerStepName = "ChunkDataModuleInitializer";

    private const string AttributeMetadataName = "Numos.API.ChunkDataAttribute";
    private const string ChunkFieldMetadataName = "Numos.API.ChunkField`1";
    private const string SolverChunkMetadataName = "Numos.API.AtmosSolverChunk";
    private const string SolverGasMixturePageMetadataName = "Numos.API.AtmosSolverGasMixturePage";
    private const string SolverChunksMetadataName = "Numos.API.AtmosSolverChunks";

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

        IncrementalValuesProvider<ChunkComponentDeclaration> components = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                ComponentAttributeMetadataName,
                static (node, _) => node is TypeDeclarationSyntax,
                static (attributeContext, cancellationToken) => CreateComponentModel(attributeContext, cancellationToken))
            .Where(static model => model is not null)
            .Select(static (model, _) => model!)
            .WithTrackingName(ComponentDeclarationsStepName);

        IncrementalValueProvider<LanguageVersion> languageVersion = context.ParseOptionsProvider
            .Select(static (options, _) => ((CSharpParseOptions)options).LanguageVersion)
            .WithTrackingName(LanguageVersionStepName);

        // Runs on every compilation change, but only the small equatable result flows on, so nothing downstream
        // reruns unless a view type actually gains or loses a member.
        IncrementalValueProvider<ReservedNames> viewInfo = context.CompilationProvider
            .Select(static (compilation, _) => GetReservedNames(compilation))
            .WithTrackingName(ViewInfoStepName);

        IncrementalValueProvider<Analysis> analysis = properties
            .Collect()
            .Combine(components.Collect())
            .Combine(languageVersion)
            .Combine(viewInfo)
            .Select(static (input, _) =>
                Analyze(input.Left.Left.Left, input.Left.Left.Right, input.Left.Right, input.Right));

        GeneratorOutput.Register(
            context,
            analysis.Select(static (result, _) => result.Data),
            TypesStepName,
            static type =>
                (type.Members[0].Property.Type.HintName + ".ChunkData.g.cs", ChunkDataEmitter.Emit(type)));

        GeneratorOutput.Register(
            context,
            analysis.Select(static (result, _) => result.Components),
            ComponentsStepName,
            static component =>
                (component.Type.HintName + ".ChunkComponent.g.cs", ChunkComponentEmitter.Emit(component)));

        // Each entry is a holder class whose static initializer registers the fields.
        IncrementalValueProvider<EquatableArray<string>> holders = analysis
            .Select(static (result, _) => result.Data.Types
                .Select(static type => type.Members[0].Property.TypeDisplay + "." + ChunkDataEmitter.HolderName)
                .Concat(result.Components.Types.Select(static component =>
                    component.TypeDisplay + "." + ChunkComponentEmitter.HolderName))
                .OrderBy(static holder => holder, StringComparer.Ordinal)
                .ToEquatableArray())
            .WithTrackingName(ModuleInitializerStepName);

        GeneratorOutput.RegisterModuleInitializer(
            context,
            holders,
            ChunkDataEmitter.ModuleInitializerHintName,
            ChunkDataEmitter.EmitModuleInitializer);
    }

    private static ReservedNames GetReservedNames(Compilation compilation)
    {
        // Dangerous() is the extension bridge to the Dangerous view, so it's taken even when that isn't referenced.
        return new ReservedNames(
            PublicMembers(compilation, SolverChunkMetadataName, "Dangerous"),
            PublicMembers(compilation, SolverGasMixturePageMetadataName),
            PublicMembers(compilation, SolverChunksMetadataName));
    }

    private static EquatableArray<string> PublicMembers(Compilation compilation, string metadataName, params string[] extra)
    {
        var names = new SortedSet<string>(ObjectMemberNames, StringComparer.Ordinal);
        names.UnionWith(extra);
        INamedTypeSymbol? type = compilation.GetTypeByMetadataName(metadataName);
        if (type is not null)
        {
            foreach (ISymbol member in type.GetMembers())
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

        bool accessible = PartialSyntax.IsAccessibleFromNamespace(property.DeclaredAccessibility);
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

            if (type.IsFileLocal || !PartialSyntax.IsAccessibleFromNamespace(type.DeclaredAccessibility))
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

        bool transient = false;
        int? length = null;
        bool clearOnSleep = false;
        bool clearOnVacuum = false;
        string? accessorName = null;
        foreach (KeyValuePair<string, TypedConstant> argument in attribute.NamedArguments)
        {
            switch (argument.Key)
            {
                case "Transient" when argument.Value.Value is bool value:
                    transient = value;
                    break;
                case "Length" when argument.Value.Value is int value:
                    length = value;
                    break;
                case "ClearOnSleep" when argument.Value.Value is bool value:
                    clearOnSleep = value;
                    break;
                case "ClearOnVacuum" when argument.Value.Value is bool value:
                    clearOnVacuum = value;
                    break;
                case "AccessorName":
                    accessorName = argument.Value.Value as string;
                    break;
            }
        }

        // Everything but scratch data is saved, and a saved key is a compatibility contract.
        if (!transient && !explicitKey)
            diagnostics.Add(DiagnosticInfo.Create(ChunkDataDiagnostics.SavedWithoutKey, attributeLocation, displayName));

        if (!transient && elementType is not null && !elementType.IsUnmanagedType)
        {
            diagnostics.Add(
                DiagnosticInfo.Create(
                    ChunkDataDiagnostics.SavedWithManagedType,
                    attributeLocation,
                    displayName,
                    elementType.ToDisplayString()));
        }

        // An error type is left alone. It may be a quantity alias over a primitive that this generator can't resolve.
        if (!transient &&
            elementType is { IsUnmanagedType: true } &&
            elementType.TypeKind != TypeKind.Error &&
            !IsPortableElementType(elementType))
        {
            diagnostics.Add(
                DiagnosticInfo.Create(
                    ChunkDataDiagnostics.SavedWithUnportableType,
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
            transient,
            length,
            clearOnSleep,
            clearOnVacuum,
            accessorName ?? property.Name);

        return new ChunkDataDeclaration(model, location, diagnostics.ToEquatableArray());
    }

    // Mirrors ChunkFieldDescriptor.IsPortableElementType in Numos.CoreSim, which decides what a .numos file can hold.
    private static bool IsPortableElementType(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { TypeKind: TypeKind.Enum, EnumUnderlyingType: { } underlying })
            type = underlying;

        switch (type.SpecialType)
        {
            case SpecialType.System_Boolean:
            case SpecialType.System_Byte:
            case SpecialType.System_SByte:
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
            case SpecialType.System_Int32:
            case SpecialType.System_UInt32:
            case SpecialType.System_Int64:
            case SpecialType.System_UInt64:
            case SpecialType.System_Single:
            case SpecialType.System_Double:
                return true;
        }

        // Half has no SpecialType.
        return type is
        {
            Name: "Half",
            TypeKind: TypeKind.Struct,
            ContainingType: null,
            ContainingNamespace: { Name: "System", ContainingNamespace.IsGlobalNamespace: true },
        };
    }

    private static Analysis Analyze(
        ImmutableArray<ChunkDataDeclaration> declarations,
        ImmutableArray<ChunkComponentDeclaration> componentDeclarations,
        LanguageVersion languageVersion, ReservedNames reservedNames)
    {
        if (declarations.IsDefaultOrEmpty && componentDeclarations.IsDefaultOrEmpty)
            return Analysis.Empty;

        ChunkDataDeclaration[] ordered = declarations
            .OrderBySource(static declaration => declaration.Location)
            .ThenBy(static declaration => declaration.Property.Type.MetadataName, StringComparer.Ordinal)
            .ThenBy(static declaration => declaration.Property.PropertyName, StringComparer.Ordinal)
            .ToArray();

        ChunkComponentDeclaration[] components = componentDeclarations
            .OrderBySource(static declaration => declaration.Location)
            .ThenBy(static declaration => declaration.Component.Type.MetadataName, StringComparer.Ordinal)
            .ToArray();

        var diagnostics = new List<DiagnosticInfo>();
        var invalid = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (ChunkDataDeclaration declaration in ordered)
        {
            diagnostics.AddRange(declaration.Diagnostics);
            if (declaration.HasErrors)
                invalid.Add(declaration);
        }

        foreach (ChunkComponentDeclaration component in components)
        {
            diagnostics.AddRange(component.Diagnostics);
            if (component.HasErrors)
                invalid.Add(component);
        }

        CheckKeys(diagnostics, KeyEntries(ordered, components, invalid), invalid);

        var accessors = new HashSet<object>(ReferenceEqualityComparer.Instance);
        if (languageVersion >= LanguageVersion.CSharp14)
        {
            CheckAccessorNames(diagnostics, ordered, components, invalid, reservedNames, accessors);
        }
        else if (ordered.FirstOrDefault(declaration => !invalid.Contains(declaration)) is { } first)
        {
            diagnostics.Add(
                DiagnosticInfo.Create(
                    ChunkDataDiagnostics.RequiresCSharp14,
                    first.Location,
                    languageVersion.ToDisplayString()));
        }
        else if (components.FirstOrDefault(component => !invalid.Contains(component)) is { } firstComponent)
        {
            // One error is enough for one project setting, so this only shows when no [ChunkData] reported it.
            diagnostics.Add(
                DiagnosticInfo.Create(
                    ChunkComponentDiagnostics.RequiresCSharp14,
                    firstComponent.Location,
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

        ChunkComponentModel[] valid = components
            .Where(component => !invalid.Contains(component))
            .Select(component => component.Component with { HasAccessors = accessors.Contains(component) })
            .OrderBy(static component => component.Type.MetadataName, StringComparer.Ordinal)
            .ToArray();

        return new Analysis(
            new GeneratorOutput<ChunkDataTypeModel>(diagnostics.ToEquatableArray(), types.ToEquatableArray()),
            new GeneratorOutput<ChunkComponentModel>(EquatableArray<DiagnosticInfo>.Empty, valid.ToEquatableArray()));
    }

    private static void CheckAccessorNames(
        List<DiagnosticInfo> diagnostics, ChunkDataDeclaration[] ordered,
        ChunkComponentDeclaration[] components, HashSet<object> invalid,
        ReservedNames reservedNames, HashSet<object> accessors)
    {
        var reserved = new HashSet<string>(reservedNames.Chunk, StringComparer.Ordinal);
        var namesByClass = new HashSet<(string Namespace, string Class, string Member)>();

        // Extension members on one receiver type in one namespace are ambiguous when their names match.
        var ownerByName = new Dictionary<(string Namespace, string Receiver, string Member), string>();

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

            if (ownerByName.TryGetValue((@namespace, SolverChunkMetadataName, memberName), out string? owner))
            {
                diagnostics.Add(
                    Collision(
                        declaration,
                        memberName,
                        $"the accessor generated for '{owner}' in the same namespace"));
            }
            else
            {
                ownerByName.Add((@namespace, SolverChunkMetadataName, memberName), model.DisplayName);
            }

            accessors.Add(declaration);
        }

        var receivers = new (string MetadataName, HashSet<string> Reserved)[]
        {
            (SolverChunkMetadataName, reserved),
            (SolverGasMixturePageMetadataName, new HashSet<string>(reservedNames.Page, StringComparer.Ordinal)),
        };

        var chunksReserved = new HashSet<string>(reservedNames.Chunks, StringComparer.Ordinal);
        var entryReserved = new HashSet<string>(ObjectMemberNames, StringComparer.Ordinal) { "Chunk", "Deconstruct" };

        foreach (ChunkComponentDeclaration declaration in components)
        {
            if (invalid.Contains(declaration))
                continue;

            ChunkComponentModel model = declaration.Component;
            string @namespace = model.Type.Namespace;
            string name = model.AccessorName;
            var names = new List<(string Receiver, string Member)>();
            string? collision = null;
            string? collidingName = null;

            if (entryReserved.Contains(name))
            {
                collidingName = name;
                collision = $"the member '{name}' of the item that With{name}() yields";
            }

            foreach ((string receiver, HashSet<string> receiverReserved) in receivers)
            {
                foreach (string member in new[] { "Get" + name, "TryGet" + name, "Has" + name })
                {
                    if (collision is null && receiverReserved.Contains(member))
                    {
                        collidingName = member;
                        collision = $"the {receiver} member '{member}'";
                    }

                    names.Add((receiver, member));
                }
            }

            if (collision is null && chunksReserved.Contains("With" + name))
            {
                collidingName = "With" + name;
                collision = $"the {SolverChunksMetadataName} member 'With{name}'";
            }

            names.Add((SolverChunksMetadataName, "With" + name));

            if (collision is null)
            {
                foreach ((string receiver, string member) in names)
                {
                    if (ownerByName.TryGetValue((@namespace, receiver, member), out string? owner))
                    {
                        collidingName = member;
                        collision = $"the accessor generated for '{owner}' in the same namespace";
                        break;
                    }
                }
            }

            if (collision is not null)
            {
                diagnostics.Add(
                    DiagnosticInfo.Create(
                        ChunkComponentDiagnostics.AccessorNameCollision,
                        declaration.Location,
                        collidingName!,
                        model.DisplayName,
                        collision));

                continue;
            }

            foreach ((string receiver, string member) in names)
                ownerByName.Add((@namespace, receiver, member), model.DisplayName);

            accessors.Add(declaration);
        }
    }

    /// <summary>
    ///     Lists every key a declaration registers, in source order, for <see cref="CheckKeys" />.
    /// </summary>
    /// <remarks>
    ///     A <c>[ChunkData]</c> property takes part even when another check rejected it, so a conflict is never hidden
    ///     behind an unrelated error. A component takes part only when it passed its own checks, because its keys
    ///     depend on them.
    /// </remarks>
    private static IEnumerable<KeyEntry> KeyEntries(
        ChunkDataDeclaration[] ordered, ChunkComponentDeclaration[] components,
        HashSet<object> invalid)
    {
        IEnumerable<KeyEntry> properties = ordered
            .Where(static declaration =>
                declaration.Property.Key is not null && declaration.Property.ElementTypeIdentity is not null)
            .Select(static declaration => new KeyEntry(
                declaration.Property.Key!,
                declaration.Property.ElementTypeIdentity!,
                declaration.Property.Transient,
                declaration.Property.Length,
                declaration.Property.ClearOnSleep,
                declaration.Property.ClearOnVacuum,
                declaration.Property.DisplayName,
                declaration.Location,
                declaration));

        IEnumerable<KeyEntry> fields = components
            .Where(component => !invalid.Contains(component))
            .SelectMany(static component => component.Fields.Select(field => new KeyEntry(
                field.Field.Key,
                field.ElementTypeIdentity,
                field.Field.Scratch,
                field.Field.Length,
                component.Component.ClearOnSleep,
                component.Component.ClearOnVacuum,
                component.Component.DisplayName + "." + field.Field.Name,
                field.Location,
                component)));

        // The sort is stable, so properties keep their order among themselves.
        return properties.Concat(fields).OrderBySource(static entry => entry.Location);
    }

    private static void CheckKeys(
        List<DiagnosticInfo> diagnostics, IEnumerable<KeyEntry> entries,
        HashSet<object> invalid)
    {
        foreach (IGrouping<string, KeyEntry> group in entries.GroupBy(static entry => entry.Key, StringComparer.Ordinal))
        {
            KeyEntry[] declarations = group.ToArray();
            if (declarations.Length < 2)
                continue;

            KeyEntry first = declarations[0];
            bool identical = declarations.All(declaration =>
                declaration.ElementTypeIdentity == first.ElementTypeIdentity &&
                declaration.Transient == first.Transient &&
                declaration.Length == first.Length &&
                declaration.ClearOnSleep == first.ClearOnSleep &&
                declaration.ClearOnVacuum == first.ClearOnVacuum);

            if (identical)
            {
                for (int i = 1; i < declarations.Length; i++)
                {
                    diagnostics.Add(
                        DiagnosticInfo.Create(
                            ChunkDataDiagnostics.DuplicateKey,
                            declarations[i].Location,
                            group.Key,
                            declarations[i].DisplayName,
                            first.DisplayName));
                }

                continue;
            }

            foreach (KeyEntry declaration in declarations)
            {
                string others = string.Join(
                    ", ",
                    declarations
                        .Where(other => !ReferenceEquals(other, declaration))
                        .Select(static other => "'" + other.DisplayName + "'"));

                diagnostics.Add(
                    DiagnosticInfo.Create(
                        ChunkDataDiagnostics.ConflictingKey,
                        declaration.Location,
                        group.Key,
                        declaration.DisplayName,
                        others));

                invalid.Add(declaration.Owner);
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

    /// <summary>
    ///     The public member names of the three view types that generated extension members must not reuse.
    /// </summary>
    private sealed record ReservedNames(
        EquatableArray<string> Chunk,
        EquatableArray<string> Page,
        EquatableArray<string> Chunks);

    /// <summary>
    ///     One key that a <c>[ChunkData]</c> property or a component field registers. <c>Owner</c> is the declaration
    ///     to drop when the key conflicts.
    /// </summary>
    private sealed record KeyEntry(
        string Key,
        string ElementTypeIdentity,
        bool Transient,
        int? Length,
        bool ClearOnSleep,
        bool ClearOnVacuum,
        string DisplayName,
        LocationInfo? Location,
        object Owner);

    /// <summary>
    ///     The result of the cross-declaration pass. Every diagnostic travels with <see cref="Data" />.
    /// </summary>
    private sealed record Analysis(
        GeneratorOutput<ChunkDataTypeModel> Data,
        GeneratorOutput<ChunkComponentModel> Components)
    {
        public static Analysis Empty { get; } =
            new(GeneratorOutput<ChunkDataTypeModel>.Empty, GeneratorOutput<ChunkComponentModel>.Empty);
    }
}
