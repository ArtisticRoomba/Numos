using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Numos.API.Generators.Common;

namespace Numos.API.Generators.Solver;

/// <summary>
///     Implements <c>IAtmosSolverDefinition&lt;TSelf&gt;</c> on <c>[AtmosSolver]</c> classes and registers their
///     descriptors with <c>AtmosSolverCatalog</c>.
/// </summary>
/// <remarks>
///     <para>
///         Output is one file per class, with a lazily built static <c>Descriptor</c>, plus one module initializer per
///         assembly that registers every descriptor when the assembly loads.
///     </para>
///     <para>
///         Per-class checks, including the class's <c>[SolverNeighborSelector]</c> methods, run in the attribute
///         transform. The duplicate-name check needs every class in the compilation, so it runs after <c>Collect</c>.
///         That step hands on one location-free model per class, so a file is only rewritten when that class's output
///         changes. A selector in a class without <c>[AtmosSolver]</c> is found by a second attribute pipeline.
///     </para>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class AtmosSolverGenerator : IIncrementalGenerator
{
    internal const string ClassesStepName = "AtmosSolverClasses";
    internal const string TypesStepName = "AtmosSolverTypes";
    internal const string ModuleInitializerStepName = "AtmosSolverModuleInitializer";
    internal const string SelectorPlacementStepName = "AtmosSolverSelectorPlacement";

    internal const string AttributeMetadataName = "Numos.API.AtmosSolverAttribute";
    private const string SelectorMetadataName = "Numos.API.SolverNeighborSelectorAttribute";
    private const string WorldSolverMetadataName = "Numos.API.IAtmosWorldSolver";
    private const string DefinitionMetadataName = "Numos.API.IAtmosSolverDefinition`1";
    private const string LinkInfoMetadataName = "Numos.API.AtmosExplicitLinkInfo";
    private const string DescriptorName = "Descriptor";

    // AtmosSolverNeighbors values, in declaration order.
    private const int NeighborsNone = 0;
    private const int NeighborsCartesian = 1;
    private const int NeighborsExplicitLinks = 2;
    private const int NeighborsAll = 3;

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<AtmosSolverDeclaration> classes = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                AttributeMetadataName,
                static (node, _) => node is ClassDeclarationSyntax or RecordDeclarationSyntax,
                static (attributeContext, cancellationToken) => CreateModel(attributeContext, cancellationToken))
            .Where(static model => model is not null)
            .Select(static (model, _) => model!)
            .WithTrackingName(ClassesStepName);

        IncrementalValueProvider<AtmosSolverOutput> output = classes
            .Collect()
            .Select(static (declarations, _) => Analyze(declarations));

        GeneratorOutput.Register(
            context,
            output.Select(static (result, _) => result.Output),
            TypesStepName,
            static type => (type.Type.HintName + ".AtmosSolver.g.cs", AtmosSolverEmitter.Emit(type)));

        IncrementalValueProvider<EquatableArray<string>> registrations = output
            .Select(static (result, _) => result.Registrations)
            .WithTrackingName(ModuleInitializerStepName);

        GeneratorOutput.RegisterModuleInitializer(
            context,
            registrations,
            AtmosSolverEmitter.ModuleInitializerHintName,
            AtmosSolverEmitter.EmitModuleInitializer);

        IncrementalValuesProvider<EquatableArray<DiagnosticInfo>> strays = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                SelectorMetadataName,
                static (node, _) => node is MethodDeclarationSyntax or LocalFunctionStatementSyntax,
                static (attributeContext, _) => CheckSelectorPlacement(attributeContext))
            .Where(static diagnostics => diagnostics.Count > 0)
            .WithTrackingName(SelectorPlacementStepName);

        DiagnosticReporter.Register(context, strays);
    }

    private static AtmosSolverDeclaration? CreateModel(
        GeneratorAttributeSyntaxContext context,
        CancellationToken cancellationToken)
    {
        if (context.TargetSymbol is not INamedTypeSymbol { TypeKind: TypeKind.Class } type ||
            context.Attributes.IsDefaultOrEmpty)
        {
            return null;
        }

        // A missing or non-constant name is already a compiler error.
        AttributeData attribute = context.Attributes[0];
        if (attribute.ConstructorArguments.Length != 1 ||
            attribute.ConstructorArguments[0].Kind == TypedConstantKind.Error)
        {
            return null;
        }

        var syntax = (TypeDeclarationSyntax)context.TargetNode;
        Compilation compilation = context.SemanticModel.Compilation;
        var diagnostics = new List<DiagnosticInfo>();
        string displayName = type.ToDisplayString();

        LocationInfo? location = LocationInfo.From(syntax.Identifier.GetLocation());
        LocationInfo? attributeLocation =
            LocationInfo.From(attribute.ApplicationSyntaxReference?.GetSyntax(cancellationToken)) ?? location;

        CheckShape(type, syntax, displayName, location, diagnostics);

        INamedTypeSymbol? worldSolver = compilation.GetTypeByMetadataName(WorldSolverMetadataName);
        if (worldSolver is not null && !type.AllInterfaces.Contains(worldSolver, SymbolEqualityComparer.Default))
            diagnostics.Add(DiagnosticInfo.Create(AtmosSolverDiagnostics.NotWorldSolver, location, displayName));

        if (!type.IsAbstract && !type.IsStatic && !HasCallableConstructor(type))
            diagnostics.Add(DiagnosticInfo.Create(AtmosSolverDiagnostics.NoConstructor, location, displayName));

        INamedTypeSymbol? definition = compilation.GetTypeByMetadataName(DefinitionMetadataName);
        if (!type.GetMembers(DescriptorName).IsEmpty ||
            type.Interfaces.Any(candidate =>
                SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, definition)))
        {
            diagnostics.Add(DiagnosticInfo.Create(AtmosSolverDiagnostics.AlreadyImplemented, location, displayName));
        }

        string name = attribute.ConstructorArguments[0].Value as string ?? "";
        if (string.IsNullOrWhiteSpace(name))
            diagnostics.Add(DiagnosticInfo.Create(AtmosSolverDiagnostics.BlankName, attributeLocation, displayName));

        string? after = null;
        string? before = null;
        int runsPerSecond = 0;
        int interval = 0;
        int neighbors = NeighborsNone;
        foreach (KeyValuePair<string, TypedConstant> argument in attribute.NamedArguments)
        {
            object? value = argument.Value.Value;
            switch (argument.Key)
            {
                case "After":
                    after = value as string;
                    break;
                case "Before":
                    before = value as string;
                    break;
                case "RunsPerSecond" when value is int rate:
                    runsPerSecond = rate;
                    break;
                case "Interval" when value is int ticks:
                    interval = ticks;
                    break;
                case "Neighbors" when value is byte mode:
                    neighbors = mode;
                    break;
            }
        }

        if (after is not null && before is not null)
            diagnostics.Add(DiagnosticInfo.Create(AtmosSolverDiagnostics.AfterAndBefore, attributeLocation, displayName));

        CheckRate(runsPerSecond, interval, displayName, attributeLocation, diagnostics);

        if (neighbors is < NeighborsNone or > NeighborsAll)
        {
            diagnostics.Add(
                DiagnosticInfo.Create(
                    AtmosSolverDiagnostics.InvalidSelection,
                    attributeLocation,
                    displayName,
                    "sets Neighbors to " + neighbors + ", which is not an AtmosSolverNeighbors value"));
        }

        AtmosSolverNeighborsModel? selection = FindSelector(
            type,
            compilation,
            neighbors != NeighborsNone,
            diagnostics,
            cancellationToken);

        string typeDisplay = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        selection ??= ModeSelection(name, neighbors);

        var model = new AtmosSolverModel(
            PartialTypeModel.From(type),
            typeDisplay,
            name,
            after,
            before,
            runsPerSecond,
            interval,
            selection,
            HidesInheritedDescriptor(type));

        return new AtmosSolverDeclaration(model, displayName, location, diagnostics.ToEquatableArray());
    }

    private static void CheckShape(
        INamedTypeSymbol type, TypeDeclarationSyntax syntax, string displayName,
        LocationInfo? location, List<DiagnosticInfo> diagnostics)
    {
        if (type.IsStatic)
            diagnostics.Add(DiagnosticInfo.Create(AtmosSolverDiagnostics.InvalidShape, location, displayName, "is static"));
        else if (type.IsAbstract)
            diagnostics.Add(DiagnosticInfo.Create(AtmosSolverDiagnostics.InvalidShape, location, displayName, "is abstract"));

        if (type.TypeParameters.Length > 0)
            diagnostics.Add(DiagnosticInfo.Create(AtmosSolverDiagnostics.InvalidShape, location, displayName, "is generic"));

        bool fileLocal = false;
        bool hidden = false;
        foreach ((INamedTypeSymbol containing, bool isPartial) in PartialSyntax.GetContainingChain(type, syntax))
        {
            if (!isPartial)
            {
                diagnostics.Add(
                    DiagnosticInfo.Create(
                        AtmosSolverDiagnostics.TypeNotPartial,
                        location,
                        containing.ToDisplayString(),
                        displayName));
            }

            fileLocal |= containing.IsFileLocal;

            // The module initializer sits outside the class, so it must be able to name every type in the chain.
            hidden |= containing.ContainingType is not null &&
                      !PartialSyntax.IsAccessibleFromNamespace(containing.DeclaredAccessibility);

            if (!SymbolEqualityComparer.Default.Equals(containing, type) && containing.TypeParameters.Length > 0)
            {
                diagnostics.Add(
                    DiagnosticInfo.Create(
                        AtmosSolverDiagnostics.InvalidShape,
                        location,
                        displayName,
                        "is nested in generic type '" + containing.ToDisplayString() + "'"));
            }
        }

        if (fileLocal)
        {
            diagnostics.Add(
                DiagnosticInfo.Create(
                    AtmosSolverDiagnostics.InvalidShape,
                    location,
                    displayName,
                    "is file-local or nested in a file-local type"));
        }

        if (hidden)
        {
            diagnostics.Add(
                DiagnosticInfo.Create(
                    AtmosSolverDiagnostics.InvalidShape,
                    location,
                    displayName,
                    "is private or protected, or nested in such a type"));
        }
    }

    private static void CheckRate(
        int runsPerSecond, int interval, string displayName,
        LocationInfo? location, List<DiagnosticInfo> diagnostics)
    {
        if (runsPerSecond < 0)
        {
            diagnostics.Add(
                DiagnosticInfo.Create(AtmosSolverDiagnostics.InvalidRate, location, displayName, "sets a negative RunsPerSecond"));
        }

        if (interval < 0)
        {
            diagnostics.Add(
                DiagnosticInfo.Create(AtmosSolverDiagnostics.InvalidRate, location, displayName, "sets a negative Interval"));
        }

        if (runsPerSecond != 0 && interval != 0)
        {
            diagnostics.Add(
                DiagnosticInfo.Create(
                    AtmosSolverDiagnostics.InvalidRate,
                    location,
                    displayName,
                    "sets both RunsPerSecond and Interval; set one of them"));
        }
        else if (runsPerSecond == 0 && interval == 0)
        {
            diagnostics.Add(DiagnosticInfo.Create(AtmosSolverDiagnostics.NoRate, location, displayName));
        }
    }

    private static bool HasCallableConstructor(INamedTypeSymbol type)
    {
        foreach (IMethodSymbol constructor in type.InstanceConstructors)
        {
            if (constructor.Parameters.Length == 0 &&
                constructor.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal
                    or Accessibility.ProtectedOrInternal)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Checks the class's <c>[SolverNeighborSelector]</c> methods and returns the selection the valid one gives.
    /// </summary>
    /// <returns>The selection, or null when the class has no selector or a selector check failed.</returns>
    private static AtmosSolverNeighborsModel? FindSelector(
        INamedTypeSymbol type, Compilation compilation, bool neighborsSet,
        List<DiagnosticInfo> diagnostics, CancellationToken cancellationToken)
    {
        INamedTypeSymbol? selectorAttribute = compilation.GetTypeByMetadataName(SelectorMetadataName);
        if (selectorAttribute is null)
            return null;

        INamedTypeSymbol? linkInfo = compilation.GetTypeByMetadataName(LinkInfoMetadataName);
        AtmosSolverNeighborsModel? selection = null;
        bool failed = false;
        int count = 0;
        foreach (ISymbol member in type.GetMembers())
        {
            if (member is not IMethodSymbol method || FindAttribute(method, selectorAttribute) is not { } attribute)
                continue;

            count++;
            string display = type.ToDisplayString() + "." + method.Name;
            LocationInfo? location = method.Locations.IsDefaultOrEmpty
                ? null
                : LocationInfo.From(method.Locations[0]);

            int before = diagnostics.Count;
            if (!IsSelectorSignature(method, linkInfo))
            {
                diagnostics.Add(
                    DiagnosticInfo.Create(
                        AtmosSolverDiagnostics.InvalidSelection,
                        location,
                        display,
                        "must have the signature 'static bool M(AtmosExplicitLinkInfo link)'"));
            }

            string key = attribute.ConstructorArguments.Length == 1
                ? attribute.ConstructorArguments[0].Value as string ?? ""
                : "";

            if (string.IsNullOrWhiteSpace(key))
            {
                LocationInfo? attributeLocation =
                    LocationInfo.From(attribute.ApplicationSyntaxReference?.GetSyntax(cancellationToken)) ?? location;

                diagnostics.Add(
                    DiagnosticInfo.Create(
                        AtmosSolverDiagnostics.InvalidSelection,
                        attributeLocation,
                        display,
                        "has a blank selection key"));
            }

            if (count > 1)
            {
                diagnostics.Add(
                    DiagnosticInfo.Create(
                        AtmosSolverDiagnostics.InvalidSelection,
                        location,
                        display,
                        "is a second neighbor selector on '" + type.ToDisplayString() + "'; a solver class can have one"));
            }

            if (neighborsSet)
            {
                diagnostics.Add(
                    DiagnosticInfo.Create(
                        AtmosSolverDiagnostics.InvalidSelection,
                        location,
                        display,
                        "is a neighbor selector, but the [AtmosSolver] attribute also sets Neighbors; use one of them"));
            }

            if (diagnostics.Count != before)
            {
                failed = true;
                continue;
            }

            bool includeCartesian = false;
            foreach (KeyValuePair<string, TypedConstant> argument in attribute.NamedArguments)
            {
                if (argument.Key == "IncludeCartesian" && argument.Value.Value is bool value)
                    includeCartesian = value;
            }

            selection = new AtmosSolverNeighborsModel(
                key,
                includeCartesian,
                type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + "." + CodeWriter.Escape(method.Name));
        }

        return failed ? null : selection;
    }

    private static bool IsSelectorSignature(IMethodSymbol method, INamedTypeSymbol? linkInfo)
    {
        return method is
               {
                   MethodKind: MethodKind.Ordinary,
                   IsStatic: true,
                   ReturnsByRef: false,
                   ReturnsByRefReadonly: false,
                   TypeParameters.Length: 0,
                   Parameters.Length: 1,
                   ReturnType.SpecialType: SpecialType.System_Boolean,
               } &&
               method.Parameters[0].RefKind == RefKind.None &&
               SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type, linkInfo);
    }

    private static AttributeData? FindAttribute(ISymbol symbol, INamedTypeSymbol attributeType)
    {
        foreach (AttributeData attribute in symbol.GetAttributes())
        {
            if (SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, attributeType))
                return attribute;
        }

        return null;
    }

    private static AtmosSolverNeighborsModel? ModeSelection(string name, int neighbors)
    {
        return neighbors switch
        {
            NeighborsCartesian => new AtmosSolverNeighborsModel(name + "#cartesian", true, "static _ => false"),
            NeighborsExplicitLinks => new AtmosSolverNeighborsModel(name + "#explicitlinks", false, "static _ => true"),
            NeighborsAll => new AtmosSolverNeighborsModel(name + "#all", true, "static _ => true"),
            _ => null,
        };
    }

    /// <summary>
    ///     Gets whether a base class has a <c>Descriptor</c> member that this class can see, so the generated one
    ///     needs <see langword="new" />.
    /// </summary>
    /// <remarks>
    ///     This happens when a solver class derives from another <c>[AtmosSolver]</c> class. A base in this
    ///     compilation does not have its generated <c>Descriptor</c> yet, so its attribute counts instead.
    /// </remarks>
    private static bool HidesInheritedDescriptor(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (!current.DeclaringSyntaxReferences.IsDefaultOrEmpty &&
                current.GetAttributes().Any(static attribute =>
                    attribute.AttributeClass is { } attributeClass &&
                    PartialTypeModel.GetMetadataName(attributeClass) == AttributeMetadataName))
            {
                return true;
            }

            bool sameAssembly = SymbolEqualityComparer.Default.Equals(current.ContainingAssembly, type.ContainingAssembly);
            foreach (ISymbol member in current.GetMembers(DescriptorName))
            {
                bool visible = member.DeclaredAccessibility switch
                {
                    Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal => true,
                    Accessibility.Internal or Accessibility.ProtectedAndInternal => sameAssembly,
                    _ => false,
                };

                if (visible)
                    return true;
            }
        }

        return false;
    }

    private static EquatableArray<DiagnosticInfo> CheckSelectorPlacement(GeneratorAttributeSyntaxContext context)
    {
        if (context.TargetSymbol is not IMethodSymbol method)
            return EquatableArray<DiagnosticInfo>.Empty;

        LocationInfo? location = method.Locations.IsDefaultOrEmpty ? null : LocationInfo.From(method.Locations[0]);
        if (method.MethodKind == MethodKind.LocalFunction)
        {
            return new[]
            {
                DiagnosticInfo.Create(
                    AtmosSolverDiagnostics.InvalidSelection,
                    location,
                    method.Name,
                    "is a local function; declare it as a static method of the [AtmosSolver] class"),
            }.ToEquatableArray();
        }

        INamedTypeSymbol? type = method.ContainingType;
        if (type is null)
            return EquatableArray<DiagnosticInfo>.Empty;

        foreach (AttributeData attribute in type.GetAttributes())
        {
            if (attribute.AttributeClass is { } attributeClass &&
                PartialTypeModel.GetMetadataName(attributeClass) == AttributeMetadataName)
            {
                return EquatableArray<DiagnosticInfo>.Empty;
            }
        }

        return new[]
        {
            DiagnosticInfo.Create(
                AtmosSolverDiagnostics.SelectorWithoutSolver,
                location,
                type.ToDisplayString() + "." + method.Name,
                type.ToDisplayString()),
        }.ToEquatableArray();
    }

    private static AtmosSolverOutput Analyze(ImmutableArray<AtmosSolverDeclaration> declarations)
    {
        if (declarations.IsDefaultOrEmpty)
        {
            return new AtmosSolverOutput(GeneratorOutput<AtmosSolverModel>.Empty, EquatableArray<string>.Empty);
        }

        AtmosSolverDeclaration[] ordered = declarations
            .OrderBySource(static declaration => declaration.Location)
            .ThenBy(static declaration => declaration.Model.TypeDisplay, StringComparer.Ordinal)
            .ToArray();

        var duplicates = new HashSet<AtmosSolverDeclaration>();
        var diagnostics = new List<DiagnosticInfo>();
        foreach (IGrouping<string, AtmosSolverDeclaration> group in ordered
                     .Where(static declaration => !string.IsNullOrWhiteSpace(declaration.Model.Name))
                     .GroupBy(static declaration => declaration.Model.Name, StringComparer.Ordinal))
        {
            AtmosSolverDeclaration[] members = group.ToArray();
            if (members.Length < 2)
                continue;

            foreach (AtmosSolverDeclaration declaration in members)
            {
                duplicates.Add(declaration);
                string others = string.Join(
                    "', '",
                    members.Where(other => !ReferenceEquals(other, declaration))
                        .Select(static other => other.DisplayName));

                diagnostics.Add(
                    DiagnosticInfo.Create(
                        AtmosSolverDiagnostics.DuplicateName,
                        declaration.Location,
                        group.Key,
                        declaration.DisplayName,
                        others));
            }
        }

        var types = new List<AtmosSolverModel>();
        var registrations = new List<string>();
        foreach (AtmosSolverDeclaration declaration in ordered)
        {
            bool hasErrors = false;
            foreach (DiagnosticInfo diagnostic in declaration.Diagnostics)
            {
                diagnostics.Add(diagnostic);
                hasErrors |= diagnostic.Descriptor.DefaultSeverity == DiagnosticSeverity.Error;
            }

            if (hasErrors)
                continue;

            types.Add(declaration.Model);
            if (!duplicates.Contains(declaration))
                registrations.Add(declaration.Model.TypeDisplay);
        }

        registrations.Sort(StringComparer.Ordinal);
        return new AtmosSolverOutput(
            new GeneratorOutput<AtmosSolverModel>(diagnostics.ToEquatableArray(), types.ToEquatableArray()),
            registrations.ToEquatableArray());
    }
}
