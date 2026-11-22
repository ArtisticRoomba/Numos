using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Numos.API.Generators.Common;

namespace Numos.API.Generators.State;

/// <summary>
///     Implements <c>INumosState&lt;T&gt;</c> for every type with <c>[NumosSerialize]</c> members, plus
///     <c>IAtmosSolverConfiguration</c>'s equality, hash, and snapshot when the type is a solver configuration. A class
///     solver gets <c>INumosStateTarget</c> instead, which reads state back into the registered instance. A class solver
///     with <c>[NumosScratch]</c> members also gets <c>INumosScratchTarget</c>, in a file of its own.
/// </summary>
/// <remarks>
///     <para>
///         A type takes part as soon as one of its fields or properties is marked, and there is no type-level
///         attribute. Membership is opt-in, with a warning for each mutable member that is neither marked nor
///         <c>[NumosIgnore]</c>. A class solver draws the same warning when it marks nothing, because a restore
///         keeps its fields either way.
///     </para>
///     <para>
///         Quantity aliases are error types in a project that owns a units catalog (see
///         <see cref="QuantityAliasCatalog" />). Shapes therefore keep them unbound until Numos combines them with the
///         catalog.
///     </para>
///     <para>
///         Every solver configuration with a constant key also lands in one module initializer per assembly, which
///         registers it for loading.
///     </para>
///     <para>
///         A <c>[SimulationComponent]</c> class takes part even without marked members. It is restored in place like a
///         class solver and also gets <c>ISimulationComponentDefinition&lt;T&gt;</c>. A second module initializer adds
///         every component class to <c>SimulationComponentCatalog</c>.
///     </para>
///     <para>
///         A class solver or component may derive from a class that holds part of its state. The chain shares one wire
///         object through virtual hooks (see <see cref="AnalyzeChain" /> and <c>StateEmitter.EmitHooked</c>). A class
///         solver that marks nothing still takes part when a plain base class it emits marks something.
///     </para>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed partial class StateGenerator : IIncrementalGenerator
{
    internal const string TypesStepName = "NumosStateTypes";
    internal const string UnmarkedSolversStepName = "NumosStateUnmarkedSolvers";
    internal const string ModuleInitializerHintName = "NumosSolverConfigurationModuleInitializer.g.cs";
    internal const string ComponentModuleInitializerHintName = "NumosSimulationComponentModuleInitializer.g.cs";

    private const string SolverConfigurationMetadataName = "Numos.CoreSim.IAtmosSolverConfiguration";
    private const string WorldSolverMetadataName = "Numos.API.IAtmosWorldSolver";
    private const string ChunkComponentMetadataName = "Numos.API.ChunkComponentAttribute";
    private const string SimulationComponentMetadataName = "Numos.API.SimulationComponentAttribute";

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Snapshots and their live types belong to SnapshotGenerator, which reads the same attributes differently.
        IncrementalValueProvider<EquatableArray<string>> snapshotParticipants = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                SnapshotGenerator.SnapshotOfMetadataName,
                static (node, _) => node is TypeDeclarationSyntax,
                static (syntaxContext, _) => SnapshotGenerator.ParticipantNames(syntaxContext))
            .SelectMany(static (names, _) => names)
            .Collect()
            .Select(static (names, _) => names.Distinct(StringComparer.Ordinal).OrderBy(static name => name, StringComparer.Ordinal).ToEquatableArray());

        // A class with a base list may be a class solver that marks nothing but inherits state from a base class.
        IncrementalValuesProvider<StateTypeDeclaration> declarations = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is TypeDeclarationSyntax declaration &&
                                    (Participates(declaration) || declaration is ClassDeclarationSyntax { BaseList: not null }),
                static (syntaxContext, cancellationToken) => CreateDeclaration(syntaxContext, cancellationToken))
            .Where(static declaration => declaration is not null)
            .Combine(snapshotParticipants)
            .Where(static pair => !pair.Right.Contains(pair.Left!.Prototype.TypeName))
            .Select(static (pair, _) => pair.Left!);

        // A plain base class of a class restored in place gets its code from that class, so its own output would clash.
        IncrementalValueProvider<EquatableArray<string>> plainBases = declarations
            .Select(static (declaration, _) => declaration.Chain?.PlainBaseNames ?? EquatableArray<string>.Empty)
            .Collect()
            .Select(static (names, _) => names
                .SelectMany(static batch => batch)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static name => name, StringComparer.Ordinal)
                .ToEquatableArray());

        IncrementalValuesProvider<StateTypeResult> types = declarations
            .Combine(plainBases)
            .Where(static pair => !pair.Right.Contains(pair.Left.Prototype.TypeName))
            .Select(static (pair, _) => pair.Left)
            .Combine(QuantityAliasCatalog.Provider(context))
            .Select(static (pair, _) => Resolve(pair.Left, pair.Right))
            .WithTrackingName(TypesStepName);

        DiagnosticReporter.Register(context, types.Select(static (result, _) => result.Diagnostics));

        context.RegisterSourceOutput(
            types.Select(static (result, _) => result.Model).Where(static model => model is not null),
            static (production, model) =>
                production.AddSource(model!.Type.HintName + ".NumosState.g.cs", StateEmitter.Emit(model)));

        context.RegisterSourceOutput(
            types.Select(static (result, _) => result.Scratch).Where(static scratch => scratch is not null),
            static (production, scratch) =>
                production.AddSource(scratch!.Type.HintName + ".NumosScratch.g.cs", StateEmitter.EmitScratch(scratch)));

        // Two derived classes analyze a shared base class the same way, so its code and diagnostics go out once.
        IncrementalValuesProvider<StateLevelOutput> levels = types
            .Select(static (result, _) => result.Levels)
            .Collect()
            .SelectMany(static (batches, _) => batches
                .SelectMany(static batch => batch)
                .GroupBy(static level => (level.Model?.Type ?? level.Scratch!.Type).MetadataName, StringComparer.Ordinal)
                .OrderBy(static group => group.Key, StringComparer.Ordinal)
                .Select(static group => group.First()));

        context.RegisterSourceOutput(
            levels,
            static (production, level) =>
            {
                if (level.Model is { } model)
                    production.AddSource(model.Type.HintName + ".NumosState.g.cs", StateEmitter.Emit(model));

                if (level.Scratch is { } scratch)
                    production.AddSource(scratch.Type.HintName + ".NumosScratch.g.cs", StateEmitter.EmitScratch(scratch));
            });

        DiagnosticReporter.Register(
            context,
            types
                .Select(static (result, _) => result.LevelDiagnostics)
                .Collect()
                .Select(static (batches, _) => batches.SelectMany(static batch => batch).Distinct().ToEquatableArray()));

        IncrementalValueProvider<EquatableArray<string>> registrations = types
            .Select(static (result, _) => result.Registration)
            .Where(static registration => registration is not null)
            .Collect()
            .Select(static (names, _) => names
                .Select(static name => name!)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static name => name, StringComparer.Ordinal)
                .ToEquatableArray());

        GeneratorOutput.RegisterModuleInitializer(
            context,
            registrations,
            ModuleInitializerHintName,
            StateEmitter.EmitModuleInitializer);

        IncrementalValueProvider<EquatableArray<string>> componentRegistrations = types
            .Select(static (result, _) => result.ComponentRegistration)
            .Where(static registration => registration is not null)
            .Collect()
            .Select(static (names, _) => names
                .Select(static name => name!)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static name => name, StringComparer.Ordinal)
                .ToEquatableArray());

        GeneratorOutput.RegisterModuleInitializer(
            context,
            componentRegistrations,
            ComponentModuleInitializerHintName,
            StateEmitter.EmitComponentModuleInitializer);

        // A class solver with no marked member that derives from object never reaches the path above, but its fields
        // still survive a restore. A scratch member counts as marked. The path above reports a derived one, because
        // its base classes may hold state.
        IncrementalValuesProvider<EquatableArray<DiagnosticInfo>> unmarkedSolvers = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is ClassDeclarationSyntax { BaseList: not null } declaration &&
                                    !HasMarkedMemberSyntax(declaration),
                static (syntaxContext, cancellationToken) => ReportUnmarkedSolver(syntaxContext, cancellationToken))
            .Where(static diagnostics => diagnostics.Count > 0)
            .WithTrackingName(UnmarkedSolversStepName);

        DiagnosticReporter.Register(context, unmarkedSolvers);
    }

    /// <summary>
    ///     A cheap syntactic filter: does this declaration itself mark a field, property, indexer, or positional
    ///     parameter with <c>[NumosSerialize]</c> or <c>[NumosScratch]</c>? Indexers can't hold state, but the
    ///     transform reports a marked one.
    /// </summary>
    /// <remarks>
    ///     Matches on the attribute's simple name, so an alias of the attribute isn't seen. The transform confirms the
    ///     match against the real attribute type.
    /// </remarks>
    private static bool HasMarkedMemberSyntax(TypeDeclarationSyntax declaration)
    {
        if (declaration.ParameterList is { } parameters)
        {
            foreach (ParameterSyntax parameter in parameters.Parameters)
            {
                if (HasMarkName(parameter.AttributeLists))
                    return true;
            }
        }

        foreach (MemberDeclarationSyntax member in declaration.Members)
        {
            if (member is FieldDeclarationSyntax or PropertyDeclarationSyntax or IndexerDeclarationSyntax &&
                HasMarkName(member.AttributeLists))
                return true;
        }

        return false;
    }

    /// <summary>
    ///     The syntactic filter for the main pipeline: a declaration that marks a member, or one that carries
    ///     <c>[SimulationComponent]</c>, which takes part with no marked member at all.
    /// </summary>
    private static bool Participates(TypeDeclarationSyntax declaration)
    {
        return HasMarkedMemberSyntax(declaration) ||
               HasAttributeName(declaration.AttributeLists, static name => name is "SimulationComponent" or "SimulationComponentAttribute");
    }

    private static bool HasMarkName(SyntaxList<AttributeListSyntax> lists)
    {
        return HasAttributeName(
            lists,
            static name => name is "NumosSerialize" or "NumosSerializeAttribute" or "NumosScratch" or "NumosScratchAttribute");
    }

    private static bool HasAttributeName(SyntaxList<AttributeListSyntax> lists, Func<string, bool> match)
    {
        foreach (AttributeListSyntax list in lists)
        {
            foreach (AttributeSyntax attribute in list.Attributes)
            {
                string name = attribute.Name switch
                {
                    QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
                    AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText,
                    SimpleNameSyntax simple => simple.Identifier.ValueText,
                    _ => "",
                };

                if (match(name))
                    return true;
            }
        }

        return false;
    }

    private static StateTypeDeclaration? CreateDeclaration(GeneratorSyntaxContext context, CancellationToken cancellationToken)
    {
        var syntax = (TypeDeclarationSyntax)context.Node;
        if (context.SemanticModel.GetDeclaredSymbol(syntax, cancellationToken) is not INamedTypeSymbol type)
            return null;

        // A declaration that only passed the filter through its base list matters for a derived class solver alone.
        bool isWorldSolver = Implements(type, WorldSolverMetadataName);
        bool isClassSolver = isWorldSolver && type.TypeKind == TypeKind.Class;
        bool derives = type.TypeKind == TypeKind.Class && type.BaseType is { SpecialType: not SpecialType.System_Object };
        if (!Participates(syntax) && !(derives && isClassSolver))
            return null;

        // A [ChunkComponent] struct marks its fields as chunk fields, not as state. The chunk data generator owns it.
        if (type.GetAttributes().Any(static attribute =>
                attribute.AttributeClass is { } attributeClass &&
                PartialTypeModel.GetMetadataName(attributeClass) == ChunkComponentMetadataName))
            return null;

        // A partial type can mark members in several declarations. Only the first of those speaks for the type.
        if (FindSpeaker(type, cancellationToken) != syntax)
            return null;

        bool hasState = StateSymbols.HasSerializedMembers(type);
        bool hasScratch = StateSymbols.HasScratchMembers(type);
        AttributeData? componentAttribute = FindAttribute(type, SimulationComponentMetadataName);
        if (!hasState && !hasScratch && componentAttribute is null && !(derives && isClassSolver))
            return null;

        LocationInfo? typeLocation = LocationInfo.From(syntax.Identifier.GetLocation());
        string typeDisplayName = type.ToDisplayString();
        var diagnostics = new List<DiagnosticInfo>();
        var ids = new Dictionary<int, string>();
        bool canGenerate = true;
        bool ownState = hasState;

        // A component takes part with no marked member, and gets the definition interface either way.
        string? componentKey = null;
        if (componentAttribute is not null)
        {
            componentKey = ReadComponentKey(componentAttribute, typeDisplayName, typeLocation, diagnostics);
            if (componentKey is null)
                canGenerate = false;

            hasState = true;
        }

        bool isComponent = componentAttribute is not null;
        if (!hasState && !isClassSolver)
        {
            // Nothing else would be generated, so the type-level rules below have nothing to say.
            foreach (ISymbol member in type.GetMembers())
            {
                LocationInfo? location = LocationInfo.From(member.Locations.FirstOrDefault()) ?? typeLocation;
                if (!StateSymbols.ReportUnsupportedMark(member, typeDisplayName, location, diagnostics) &&
                    member is IFieldSymbol or IPropertySymbol && StateSymbols.IsScratch(member))
                {
                    ScratchMember.ReportMisplaced(
                        member, StateSymbols.FindSerialize(member), typeDisplayName + "." + member.Name, location, diagnostics);
                }
            }

            return Skipped(type, diagnostics);
        }

        // A class solver or a component may derive from a class, whose fields then share its wire object. Any other
        // type is built from data through its own constructor, which can't reach a base class's members.
        bool isSolverConfiguration = Implements(type, SolverConfigurationMetadataName);
        bool inPlace = isClassSolver || isComponent;
        bool derivedDataType = derives && (!inPlace || isSolverConfiguration);
        ChainAnalysis? chain = null;
        if (derives && !derivedDataType)
        {
            chain = AnalyzeChain(type, context.SemanticModel, ids, cancellationToken);

            // A solver that marks nothing and whose own plain base classes mark nothing inherits every generated member.
            if (!ownState && !hasScratch && !isComponent && !chain.LevelsHaveContent)
            {
                ScanMembers(type, typeDisplayName, typeLocation, true, true, true, context.SemanticModel, ids, diagnostics, cancellationToken);
                return Skipped(type, diagnostics, chain.ToDeclaration());
            }
        }

        StateChainDeclaration? chainDeclaration = chain?.ToDeclaration();

        // The innermost type reports first, so walk the outermost-first chain backwards. A solver that marks nothing
        // itself gets code because of a base class's marks, so the message names that class.
        string markedBy = ownState || hasScratch || isComponent ? typeDisplayName : chain?.ContentLevel ?? typeDisplayName;
        List<(INamedTypeSymbol Type, bool IsPartial)> containing = PartialSyntax.GetContainingChain(type, syntax);
        for (int i = containing.Count - 1; i >= 0; i--)
        {
            (INamedTypeSymbol current, bool isPartial) = containing[i];
            if (current.IsFileLocal)
            {
                // The hint name would carry the mangled metadata name, and a generated partial can't reopen the type anyway.
                diagnostics.Add(DiagnosticInfo.Create(StateDiagnostics.FileLocal, typeLocation, typeDisplayName));
                return Skipped(type, diagnostics, chainDeclaration);
            }

            if (!isPartial)
            {
                diagnostics.Add(
                    DiagnosticInfo.Create(StateDiagnostics.TypeNotPartial, typeLocation, current.ToDisplayString(), markedBy));

                canGenerate = false;
            }

            if (current.TypeParameters.Length > 0)
            {
                // Field types would mention type parameters no codec covers, so stop before reporting them too.
                diagnostics.Add(DiagnosticInfo.Create(StateDiagnostics.Generic, typeLocation, typeDisplayName));
                return Skipped(type, diagnostics, chainDeclaration);
            }
        }

        if (isWorldSolver && type.TypeKind != TypeKind.Class)
        {
            // Its fields would only produce noise about attributes that can't fix anything.
            diagnostics.Add(DiagnosticInfo.Create(StateDiagnostics.WorldSolver, typeLocation, typeDisplayName));
            return Skipped(type, diagnostics);
        }

        if (derivedDataType)
        {
            diagnostics.Add(
                DiagnosticInfo.Create(
                    StateDiagnostics.DerivedClass, typeLocation, typeDisplayName, type.BaseType!.ToDisplayString(),
                    StateDiagnostics.DerivedDataTypeReason));

            canGenerate = false;
        }
        else if (chain is not null)
        {
            if (chain.ForeignProblem is { } problem)
            {
                diagnostics.Add(
                    DiagnosticInfo.Create(StateDiagnostics.DerivedClass, typeLocation, typeDisplayName, problem.Base, problem.Reason));
            }

            // A base class that can't generate reports why itself, or through the diagnostic above.
            if (chain.Failed)
                canGenerate = false;
        }

        if (isComponent && !CheckComponentShape(type, isWorldSolver, isSolverConfiguration, typeDisplayName, typeLocation, diagnostics))
            canGenerate = false;

        // Numos never builds a type restored in place from data, so its collections can be mutable. A restore refills
        // them. A configuration keeps the read-only rule, because its snapshot must be immutable.
        bool restoresInPlace = inPlace && !isSolverConfiguration;
        ChainAbove above = chain?.Above ?? new ChainAbove();

        // A sealed type whose base classes have no hooks keeps everything inline. Any other type restored in place
        // shares the work with its base and derived classes through the hooks.
        StateHookKind stateHook = !restoresInPlace ? StateHookKind.None
            : above.StateHooks ? ownState ? StateHookKind.Override : StateHookKind.Inherit
            : !type.IsSealed && (ownState || isComponent) ? StateHookKind.Introduce
            : StateHookKind.None;

        StateHookKind scratchHook = !restoresInPlace ? StateHookKind.None
            : above.ScratchHooks ? hasScratch ? StateHookKind.Override : StateHookKind.Inherit
            : !type.IsSealed && hasScratch ? StateHookKind.Introduce
            : StateHookKind.None;

        bool chainState = hasState || above.StateHooks;
        bool chainScratch = hasScratch || above.ScratchHooks;
        IEnumerable<string> owned = OwnedMemberNames(
                isSolverConfiguration, chainState && (isWorldSolver || isComponent), chainScratch && inPlace)
            .Concat(HookNames(stateHook, scratchHook));

        foreach (string generated in owned)
        {
            if (FindMethod(type, generated) is { } member)
            {
                diagnostics.Add(
                    DiagnosticInfo.Create(
                        StateDiagnostics.HandWritten,
                        LocationInfo.From(member.Locations.FirstOrDefault()) ?? typeLocation,
                        typeDisplayName,
                        generated));

                canGenerate = false;
            }
        }

        string? stateKey = ReadConstantKey(type, context.SemanticModel, cancellationToken, out IPropertySymbol? keyProperty);
        if (stateKey is not null && string.IsNullOrWhiteSpace(stateKey))
        {
            diagnostics.Add(
                DiagnosticInfo.Create(
                    StateDiagnostics.BlankKey,
                    LocationInfo.From(keyProperty?.Locations.FirstOrDefault()) ?? typeLocation,
                    typeDisplayName));

            canGenerate = false;
            stateKey = null;
        }
        else if (stateKey is null && isSolverConfiguration)
        {
            diagnostics.Add(
                DiagnosticInfo.Create(
                    StateDiagnostics.NonConstantKey,
                    LocationInfo.From(keyProperty?.Locations.FirstOrDefault()) ?? typeLocation,
                    typeDisplayName));

            canGenerate = false;
        }

        MemberScan scan = ScanMembers(
            type, typeDisplayName, typeLocation, inPlace, restoresInPlace, isWorldSolver || isComponent,
            context.SemanticModel, ids, diagnostics, cancellationToken);

        if (scan.Failed)
            canGenerate = false;

        SnapshotKind snapshot = SnapshotKind.None;
        if (isSolverConfiguration && FindMethod(type, "CreateSnapshot") is null)
            snapshot = scan.Immutable ? SnapshotKind.Self : SnapshotKind.Copy;

        string? registration = null;
        if (isSolverConfiguration && stateKey is not null)
        {
            if (IsReachableFromAssembly(type))
                registration = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            else
                diagnostics.Add(DiagnosticInfo.Create(StateDiagnostics.NotRegistrable, typeLocation, typeDisplayName));
        }

        StateHooksModel? hooks = stateHook == StateHookKind.None
            ? null
            : new StateHooksModel(
                stateHook,
                false,
                !above.Target,
                scan.Fields.Count > 0 || above.Fields,
                scan.Fields.Any(static field => !field.Field.HostEditable) || above.Bulk,
                !type.IsSealed,
                above.SchemaFields);

        var prototype = new StateTypeModel(
            PartialTypeModel.From(type),
            type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            type.IsReferenceType,
            isSolverConfiguration,
            isWorldSolver,
            stateKey,
            snapshot,
            EquatableArray<StateFieldModel>.Empty,
            StateReadPlan.Empty,
            componentKey,
            hooks);

        // A type that inherits the scratch hook still implements the interface when no base class does.
        bool implementsScratch = chainScratch && !above.ScratchTarget;
        ScratchTypeModel? scratch = scan.Resets.Count > 0 || (scratchHook == StateHookKind.Inherit && implementsScratch)
            ? new ScratchTypeModel(
                PartialTypeModel.From(type), scan.Usings.File, scan.Usings.Namespace, scan.Resets.ToEquatableArray(),
                scratchHook, implementsScratch)
            : null;

        return new StateTypeDeclaration(
            prototype,
            typeDisplayName,
            typeLocation,
            registration,
            canGenerate,
            chainState,
            scan.Fields.OrderBy(static field => field.Field.Id).ToEquatableArray(),
            Constructors(type),
            scratch,
            diagnostics.ToEquatableArray(),
            isComponent ? prototype.TypeName : null,
            chainDeclaration);
    }

    /// <summary>
    ///     Picks the declaration that speaks for a type: the first that marks a member or carries
    ///     <c>[SimulationComponent]</c>, or else the first class declaration with a base list.
    /// </summary>
    private static TypeDeclarationSyntax? FindSpeaker(INamedTypeSymbol type, CancellationToken cancellationToken)
    {
        TypeDeclarationSyntax? derived = null;
        foreach (SyntaxReference reference in type.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax(cancellationToken) is not TypeDeclarationSyntax candidate)
                continue;

            if (Participates(candidate))
                return candidate;

            if (derived is null && candidate is ClassDeclarationSyntax { BaseList: not null })
                derived = candidate;
        }

        return derived;
    }

    /// <summary>
    ///     Reads the state fields and scratch resets one class declares, with the member rules every state type shares.
    /// </summary>
    /// <param name="type">The class or struct.</param>
    /// <param name="typeDisplayName">The type for diagnostics.</param>
    /// <param name="typeLocation">Where a diagnostic about a member without a location points.</param>
    /// <param name="inPlace">
    ///     Whether the type is restored in place: <c>[NumosScratch]</c> is allowed, and unmarked readonly collections
    ///     draw <c>NUMOSSTATE016</c>.
    /// </param>
    /// <param name="restoresInPlace">Whether mutable collections are allowed, because a restore refills them.</param>
    /// <param name="assignsInPlace">Whether every field must be assignable or refillable.</param>
    /// <param name="semanticModel">A model of any tree in the compilation.</param>
    /// <param name="ids">The wire ids claimed so far. Each field adds its own.</param>
    /// <param name="diagnostics">Receives the member diagnostics.</param>
    /// <param name="cancellationToken">The token to observe.</param>
    private static MemberScan ScanMembers(
        INamedTypeSymbol type, string typeDisplayName, LocationInfo? typeLocation, bool inPlace, bool restoresInPlace,
        bool assignsInPlace, SemanticModel semanticModel, Dictionary<int, string> ids, List<DiagnosticInfo> diagnostics,
        CancellationToken cancellationToken)
    {
        var scan = new MemberScan();
        foreach (ISymbol member in type.GetMembers())
        {
            cancellationToken.ThrowIfCancellationRequested();
            LocationInfo? location = LocationInfo.From(member.Locations.FirstOrDefault()) ?? typeLocation;
            if (StateSymbols.ReportUnsupportedMark(member, typeDisplayName, location, diagnostics))
                scan.Failed = true;

            if (!StateSymbols.TryGetMember(type, member, out ITypeSymbol? memberType, out bool hasStorage))
                continue;

            if (StateSymbols.IsScratch(member))
            {
                ScratchResetModel? reset = ScratchMember.TryParse(
                    type, member, inPlace, location, semanticModel, scan.Usings, diagnostics, cancellationToken);

                if (reset is null)
                    scan.Failed = true;
                else
                    scan.Resets.Add(reset);

                continue;
            }

            SerializedMember? parsed = SerializedMember.TryParse(
                member, StateSymbols.FindSerialize(member), hasStorage, typeDisplayName, location, WireNameChecks.None,
                inPlace, ids, diagnostics);

            if (parsed is null)
                continue;

            ITypeSymbol? codec = parsed.Codec;
            StateShape shape = StateShape.ForMember(memberType!, codec, restoresInPlace);
            bool refillable = restoresInPlace && RefillsWithoutAssigning(shape);
            if (assignsInPlace && (!parsed.Settable || parsed.InitOnly) && !refillable)
            {
                // A restore writes into the existing instance, so there's no constructor or initializer to use.
                diagnostics.Add(DiagnosticInfo.Create(StateDiagnostics.NotAssignable, location, parsed.DisplayName));
                scan.Failed = true;
                continue;
            }

            if (StateSymbols.HasNamedArgument(parsed.Attribute, "Count"))
            {
                diagnostics.Add(DiagnosticInfo.Create(StateDiagnostics.CountOutsideSnapshot, location, parsed.DisplayName));
                scan.Failed = true;
                continue;
            }

            if (codec is not null || (parsed.Settable && !parsed.InitOnly) || !Immutability.IsImmutable(memberType!))
                scan.Immutable = false;

            bool hostEditable = !StateSymbols.TryGetNamed(parsed.Attribute, "HostEditable", out TypedConstant editable) ||
                                editable.Value is not false;

            if (!hostEditable && !restoresInPlace)
            {
                // Only the recording boundary reads the flag, and only state restored in place has one.
                diagnostics.Add(DiagnosticInfo.Create(StateDiagnostics.HostEditableIgnored, location, parsed.DisplayName));
                hostEditable = true;
            }

            var field = new StateFieldModel(
                parsed.Id, CodeWriter.Escape(member.Name), shape, member.Name, parsed.Settable, parsed.InitOnly, parsed.WireName,
                hostEditable, shape.AliasName, scan.Fields.Count);

            scan.Fields.Add(new StateFieldDeclaration(field, parsed.DisplayName, location));
        }

        return scan;
    }

    /// <summary>
    ///     Reports <c>NUMOSSTATE016</c> for each unmarked mutable member of a class solver that marks nothing, with
    ///     neither <c>[NumosSerialize]</c> nor <c>[NumosScratch]</c>, and derives from object.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The member checks are the ones <see cref="CreateDeclaration" /> runs for an unmarked member, so
    ///         <c>[NumosIgnore]</c>, statics, and readonly fields and get-only properties that hold no array or mutable
    ///         collection stay quiet here too. The type needs
    ///         no <c>partial</c>, because nothing is generated for it.
    ///     </para>
    ///     <para>
    ///         Every declaration of a partial solver passes the syntax filter. Only the first one with a base list
    ///         reports, so each member is reported once.
    ///     </para>
    /// </remarks>
    private static EquatableArray<DiagnosticInfo> ReportUnmarkedSolver(
        GeneratorSyntaxContext context, CancellationToken cancellationToken)
    {
        var syntax = (ClassDeclarationSyntax)context.Node;
        if (context.SemanticModel.GetDeclaredSymbol(syntax, cancellationToken) is not INamedTypeSymbol type ||
            type.TypeKind != TypeKind.Class ||
            type.BaseType is { SpecialType: not SpecialType.System_Object } ||
            !Implements(type, WorldSolverMetadataName) ||
            StateSymbols.HasSerializedMembers(type) ||
            StateSymbols.HasScratchMembers(type))
        {
            return EquatableArray<DiagnosticInfo>.Empty;
        }

        foreach (SyntaxReference reference in type.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax(cancellationToken) is ClassDeclarationSyntax { BaseList: not null } candidate)
            {
                if (candidate != syntax)
                    return EquatableArray<DiagnosticInfo>.Empty;

                break;
            }
        }

        LocationInfo? typeLocation = LocationInfo.From(syntax.Identifier.GetLocation());
        string typeDisplayName = type.ToDisplayString();
        var diagnostics = new List<DiagnosticInfo>();
        var ids = new Dictionary<int, string>();
        foreach (ISymbol member in type.GetMembers())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!StateSymbols.TryGetMember(type, member, out _, out bool hasStorage))
                continue;

            LocationInfo? location = LocationInfo.From(member.Locations.FirstOrDefault()) ?? typeLocation;
            SerializedMember.TryParse(
                member, null, hasStorage, typeDisplayName, location, WireNameChecks.None, true, ids, diagnostics);
        }

        return diagnostics.ToEquatableArray();
    }

    /// <summary>
    ///     Whether a restore in place can fill a member it can't assign: a non-nullable <c>List&lt;T&gt;</c>,
    ///     <c>HashSet&lt;T&gt;</c>, or <c>Dictionary&lt;TKey, TValue&gt;</c>, which it clears and refills.
    /// </summary>
    /// <remarks>
    ///     A nullable one is left out, because the data can hold null and only an assignment can write that. An array
    ///     is left out, because data of another length needs a new array.
    /// </remarks>
    private static bool RefillsWithoutAssigning(StateShape shape)
    {
        return shape.Kind != StateShapeKind.NullableReference &&
               shape.Refill is StateRefill.List or StateRefill.Set or StateRefill.Dictionary;
    }

    private static StateTypeDeclaration Skipped(
        INamedTypeSymbol type, List<DiagnosticInfo> diagnostics, StateChainDeclaration? chain = null)
    {
        var prototype = new StateTypeModel(
            PartialTypeModel.From(type),
            type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            type.IsReferenceType,
            false,
            false,
            null,
            SnapshotKind.None,
            EquatableArray<StateFieldModel>.Empty,
            StateReadPlan.Empty);

        return new StateTypeDeclaration(
            prototype,
            type.ToDisplayString(),
            null,
            null,
            false,
            false,
            EquatableArray<StateFieldDeclaration>.Empty,
            EquatableArray<StateConstructorModel>.Empty,
            null,
            diagnostics.ToEquatableArray(),
            null,
            chain);
    }

    /// <summary>
    ///     Reads the constant a get-only <c>Key</c> property returns, such as <c>public string Key =&gt; "my-mod/fire";</c>.
    /// </summary>
    /// <param name="type">The type to read the key of.</param>
    /// <param name="semanticModel">
    ///     The model of the tree that declares <paramref name="type" />. A <c>Key</c> declared in another partial
    ///     file is read through a model of that file's tree.
    /// </param>
    /// <param name="cancellationToken">The token to observe.</param>
    /// <param name="keyProperty">The <c>Key</c> property, even when its value is not a constant.</param>
    /// <returns>The key, or null when there's no such property or it doesn't return a compile-time constant.</returns>
    private static string? ReadConstantKey(
        INamedTypeSymbol type, SemanticModel semanticModel, CancellationToken cancellationToken,
        out IPropertySymbol? keyProperty)
    {
        keyProperty = null;
        foreach (ISymbol member in type.GetMembers())
        {
            if (member is not IPropertySymbol { IsStatic: false, IsIndexer: false, Type.SpecialType: SpecialType.System_String } property ||
                property.IsImplicitlyDeclared ||
                (property.Name != "Key" && !property.Name.EndsWith(".Key", StringComparison.Ordinal)))
            {
                continue;
            }

            keyProperty = property;
            if (property.SetMethod is not null || StateSymbols.HasBackingField(type, property))
                return null;

            foreach (SyntaxReference reference in property.DeclaringSyntaxReferences)
            {
                if (reference.GetSyntax(cancellationToken) is not PropertyDeclarationSyntax declaration)
                    continue;

                ExpressionSyntax? expression = declaration.ExpressionBody?.Expression;
                if (expression is null && declaration.AccessorList is { } accessors)
                {
                    foreach (AccessorDeclarationSyntax accessor in accessors.Accessors)
                    {
                        if (!accessor.IsKind(SyntaxKind.GetAccessorDeclaration))
                            continue;

                        expression = accessor.ExpressionBody?.Expression;
                        if (expression is null &&
                            accessor.Body is { Statements.Count: 1 } body &&
                            body.Statements[0] is ReturnStatementSyntax { Expression: { } returned })
                        {
                            expression = returned;
                        }
                    }
                }

                if (expression is null)
                    return null;

                SemanticModel model = declaration.SyntaxTree == semanticModel.SyntaxTree
                    ? semanticModel
                    : semanticModel.Compilation.GetSemanticModel(declaration.SyntaxTree);

                Optional<object?> constant = model.GetConstantValue(expression, cancellationToken);
                return constant is { HasValue: true, Value: string key } ? key : null;
            }

            return null;
        }

        return null;
    }

    /// <summary>
    ///     Whether the generated module initializer, which sits outside the type, can name it.
    /// </summary>
    private static bool IsReachableFromAssembly(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? current = type; current is not null; current = current.ContainingType)
        {
            if (!PartialSyntax.IsAccessibleFromNamespace(current.DeclaredAccessibility))
                return false;
        }

        return true;
    }

    /// <summary>
    ///     Gets every constructor the generated reader could call. Accessibility does not matter, because the reader is
    ///     generated into the type. Only the copy constructor of a record is left out.
    /// </summary>
    private static EquatableArray<StateConstructorModel> Constructors(INamedTypeSymbol type)
    {
        var constructors = new List<StateConstructorModel>();
        foreach (IMethodSymbol constructor in type.InstanceConstructors)
        {
            if (type.IsRecord &&
                constructor.Parameters.Length == 1 &&
                SymbolEqualityComparer.Default.Equals(constructor.Parameters[0].Type, type))
            {
                continue;
            }

            constructors.Add(
                new StateConstructorModel(
                    constructor.Parameters
                        .Select(static parameter => new StateParameterModel(
                            parameter.Name,
                            StateShape.FromParameter(parameter.Type),
                            parameter.Type.ToDisplayString(),
                            parameter.HasExplicitDefaultValue ? DefaultLiteral(parameter.ExplicitDefaultValue) : null))
                        .ToEquatableArray()));
        }

        return constructors.ToEquatableArray();
    }

    private static string DefaultLiteral(object? value)
    {
        return value switch
        {
            null => "default",
            float.NaN => "float.NaN",
            float.PositiveInfinity => "float.PositiveInfinity",
            float.NegativeInfinity => "float.NegativeInfinity",
            double.NaN => "double.NaN",
            double.PositiveInfinity => "double.PositiveInfinity",
            double.NegativeInfinity => "double.NegativeInfinity",
            _ => SymbolDisplay.FormatPrimitive(value, true, false) ?? "default",
        };
    }

    private static IEnumerable<string> OwnedMemberNames(bool isSolverConfiguration, bool isStateTarget, bool isScratchTarget)
    {
        if (isStateTarget)
        {
            yield return "WriteState";
            yield return "ReadStateInto";
        }

        if (isScratchTarget)
            yield return "ResetScratch";

        if (!isSolverConfiguration)
            yield break;

        yield return "SemanticallyEquals";
        yield return "ComputeStateHash";
    }

    private static ISymbol? FindMethod(INamedTypeSymbol type, string name)
    {
        foreach (ISymbol member in type.GetMembers())
        {
            if (member.IsImplicitlyDeclared ||
                member is not IMethodSymbol { MethodKind: MethodKind.Ordinary or MethodKind.ExplicitInterfaceImplementation })
            {
                continue;
            }

            if (member.Name == name || member.Name.EndsWith("." + name, StringComparison.Ordinal))
                return member;
        }

        return null;
    }

    /// <summary>
    ///     Reads the key a <c>[SimulationComponent]</c> attribute passes, and reports a blank one.
    /// </summary>
    /// <returns>The key, or null when it is blank or the argument is already a compiler error.</returns>
    private static string? ReadComponentKey(
        AttributeData attribute, string typeDisplayName, LocationInfo? typeLocation, List<DiagnosticInfo> diagnostics)
    {
        if (attribute.ConstructorArguments.Length != 1 || attribute.ConstructorArguments[0].Kind == TypedConstantKind.Error)
            return null;

        if (attribute.ConstructorArguments[0].Value is string key && key.Trim().Length > 0)
            return key;

        LocationInfo? location = LocationInfo.From(attribute.ApplicationSyntaxReference?.GetSyntax()) ?? typeLocation;
        diagnostics.Add(DiagnosticInfo.Create(StateDiagnostics.BlankComponentKey, location, typeDisplayName));
        return null;
    }

    /// <summary>
    ///     Checks what <c>NUMOSSTATE001</c>, <c>002</c>, and <c>013</c> leave to a simulation component: Numos creates it
    ///     with a parameterless constructor from a module initializer, and treats it as nothing but a component.
    /// </summary>
    /// <returns>True when the shape is fine.</returns>
    private static bool CheckComponentShape(
        INamedTypeSymbol type, bool isWorldSolver, bool isSolverConfiguration, string typeDisplayName,
        LocationInfo? typeLocation, List<DiagnosticInfo> diagnostics)
    {
        int before = diagnostics.Count;
        void Report(string reason) =>
            diagnostics.Add(DiagnosticInfo.Create(StateDiagnostics.ComponentShape, typeLocation, typeDisplayName, reason));

        if (type.TypeKind != TypeKind.Class || type.IsRecord)
            Report("must be a class and not a record");
        else if (type.IsStatic)
            Report("is static");
        else if (type.IsAbstract)
            Report("is abstract");

        if (!type.IsStatic && !type.IsAbstract && !HasAccessibleParameterlessConstructor(type))
            Report("has no public or internal parameterless constructor");

        if (!IsReachableFromAssembly(type))
            Report("is private or protected, or nested in such a type, so the module initializer can't register it");

        if (isWorldSolver)
            Report("also implements IAtmosWorldSolver; keep the solver and its per-simulation state in separate classes");

        if (isSolverConfiguration)
            Report("also implements IAtmosSolverConfiguration");

        return diagnostics.Count == before;
    }

    private static bool HasAccessibleParameterlessConstructor(INamedTypeSymbol type)
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

    private static AttributeData? FindAttribute(INamedTypeSymbol type, string metadataName)
    {
        foreach (AttributeData attribute in type.GetAttributes())
        {
            if (attribute.AttributeClass is { } attributeClass && PartialTypeModel.GetMetadataName(attributeClass) == metadataName)
                return attribute;
        }

        return null;
    }

    private static bool Implements(INamedTypeSymbol type, string interfaceMetadataName)
    {
        foreach (INamedTypeSymbol candidate in type.AllInterfaces)
        {
            if (PartialTypeModel.GetMetadataName(candidate) == interfaceMetadataName)
                return true;
        }

        return false;
    }

    private static StateTypeResult Resolve(StateTypeDeclaration declaration, EquatableArray<QuantityAlias> aliases)
    {
        var diagnostics = new List<DiagnosticInfo>(declaration.Diagnostics);
        var fields = new List<StateFieldModel>();
        foreach (StateFieldDeclaration field in declaration.Fields)
        {
            if (field.Resolve(aliases, diagnostics, true) is { } resolved)
                fields.Add(resolved);
        }

        // The base classes' own diagnostics go out once through the levels, so a base field that fails here only
        // stops this type from generating.
        var chainEntries = new List<SchemaEntry>();
        EquatableArray<StateLevelOutput> levels = EquatableArray<StateLevelOutput>.Empty;
        EquatableArray<DiagnosticInfo> levelDiagnostics = EquatableArray<DiagnosticInfo>.Empty;
        bool chainResolved = true;
        if (declaration.Chain is { } chain)
        {
            chainResolved = ResolveLevels(chain, aliases, out levels, out levelDiagnostics);
            chainEntries.AddRange(chain.MetadataSchema);
            var ignored = new List<DiagnosticInfo>();
            foreach (StateFieldDeclaration field in chain.ChainFields)
            {
                if (field.Resolve(aliases, ignored, true) is { } resolved)
                    chainEntries.Add(SchemaFingerprints.Entry(resolved));
                else
                    chainResolved = false;
            }
        }

        bool hasErrors = diagnostics.Any(static diagnostic => diagnostic.Descriptor.DefaultSeverity == DiagnosticSeverity.Error);
        if (!declaration.CanGenerate || hasErrors || !chainResolved)
            return new StateTypeResult(null, null, null, diagnostics.ToEquatableArray(), null, levels, levelDiagnostics);

        // A solver with only scratch members has no state, so it gets no state interface and no checkpoint entry.
        if (!declaration.HasState)
        {
            return new StateTypeResult(
                null, declaration.Scratch, null, diagnostics.ToEquatableArray(), null, levels, levelDiagnostics);
        }

        // Only checked once the fields themselves are fine, so a bad field type isn't reported twice. A solver is
        // restored in place, so it needs no constructor the reader can call.
        string reason = "";
        StateReadPlan? plan = declaration.Prototype.RestoresInPlace
            ? StateReadPlan.Empty
            : StateBinding.Bind(declaration.Constructors, fields, aliases, out reason);

        if (plan is null)
        {
            diagnostics.Add(DiagnosticInfo.Create(StateDiagnostics.NoConstructor, declaration.Location, declaration.DisplayName, reason));
            return new StateTypeResult(null, null, null, diagnostics.ToEquatableArray(), null, levels, levelDiagnostics);
        }

        StateTypeModel model = declaration.Prototype with { Fields = fields.ToEquatableArray(), Read = plan };
        if (model.Hooks is { } hooks)
        {
            // The base classes' fields share the wire object, so the fingerprint covers them too.
            chainEntries.AddRange(fields.Select(SchemaFingerprints.Entry));
            model = model with
            {
                Hooks = hooks with
                {
                    Fingerprint = SchemaFingerprints.Compute(chainEntries),
                    SchemaFields = hooks.EmitsSchemaFields ? SchemaFingerprints.Format(chainEntries) : "",
                },
            };
        }

        return new StateTypeResult(
            model, declaration.Scratch, declaration.Registration, diagnostics.ToEquatableArray(),
            declaration.ComponentRegistration, levels, levelDiagnostics);
    }

    /// <summary>
    ///     Resolves the fields of the plain base classes a type emits.
    /// </summary>
    /// <param name="chain">The type's chain.</param>
    /// <param name="aliases">The units catalog.</param>
    /// <param name="levels">Receives the base classes that have no errors.</param>
    /// <param name="diagnostics">Receives every diagnostic about the base classes.</param>
    /// <returns>True when every base class can generate.</returns>
    private static bool ResolveLevels(
        StateChainDeclaration chain, EquatableArray<QuantityAlias> aliases, out EquatableArray<StateLevelOutput> levels,
        out EquatableArray<DiagnosticInfo> diagnostics)
    {
        var reported = new List<DiagnosticInfo>(chain.LevelDiagnostics);
        var outputs = new List<StateLevelOutput>();
        bool resolved = true;
        foreach (StateLevelDeclaration level in chain.Levels)
        {
            int before = reported.Count;
            var fields = new List<StateFieldModel>();
            foreach (StateFieldDeclaration field in level.Fields)
            {
                if (field.Resolve(aliases, reported, true) is { } model)
                    fields.Add(model);
            }

            bool failed = !level.CanGenerate ||
                          reported.Skip(before).Any(static diagnostic => diagnostic.Descriptor.DefaultSeverity == DiagnosticSeverity.Error);

            if (failed)
            {
                resolved = false;
                continue;
            }

            StateTypeModel? state = level.Prototype.Hooks is null ? null : level.Prototype with { Fields = fields.ToEquatableArray() };
            outputs.Add(new StateLevelOutput(state, level.Scratch));
        }

        levels = outputs.ToEquatableArray();
        diagnostics = reported.ToEquatableArray();
        return resolved;
    }
}
