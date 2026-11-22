using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Numos.API.Generators.Common;

namespace Numos.API.Generators.State;

public sealed partial class StateGenerator
{
    /// <summary>
    ///     The generated hook that writes the state fields of a class and its base classes in an id range.
    /// </summary>
    internal const string WriteHookName = "WriteNumosState";

    /// <summary>
    ///     The generated hook that decodes one state field of a class or its base classes.
    /// </summary>
    internal const string ReadHookName = "ReadNumosStateField";

    /// <summary>
    ///     The generated hook that writes what the read hook decoded into the instance.
    /// </summary>
    internal const string ApplyHookName = "ApplyNumosState";

    /// <summary>
    ///     The generated hook that hashes each field that is not host-editable.
    /// </summary>
    internal const string HashHookName = "HashNumosBulkState";

    /// <summary>
    ///     The generated hook that lists the member descriptors and the fields that are not host-editable.
    /// </summary>
    internal const string DescribeHookName = "DescribeNumosState";

    /// <summary>
    ///     The generated hook that resets the <c>[NumosScratch]</c> members of a class and its base classes.
    /// </summary>
    internal const string ScratchHookName = "ResetNumosScratch";

    /// <summary>
    ///     The <c>protected</c> constant that publishes a class's schema entries to a derived class in another assembly.
    /// </summary>
    internal const string SchemaFieldsName = "__numosSchemaFields";

    private const string TargetMetadataName = "Numos.CoreSim.State.Advanced.INumosStateTarget";
    private const string ScratchTargetMetadataName = "Numos.CoreSim.State.Advanced.INumosScratchTarget";
    private const string BulkTargetMetadataName = "Numos.CoreSim.State.Advanced.INumosBulkStateTarget";
    private const string DescribedMetadataName = "Numos.CoreSim.State.INumosStateDescribed";

    private static readonly string[] StateHookNames = [WriteHookName, ReadHookName, ApplyHookName, HashHookName, DescribeHookName];

    /// <summary>
    ///     Whether a class is restored in place by its own declaration: a class solver or a simulation component.
    /// </summary>
    private static bool IsRestoredInPlace(INamedTypeSymbol type)
    {
        return (type.TypeKind == TypeKind.Class && Implements(type, WorldSolverMetadataName)) ||
               FindAttribute(type, SimulationComponentMetadataName) is not null;
    }

    /// <summary>
    ///     Walks the base classes of a class restored in place, outermost first, and works out what each one adds.
    /// </summary>
    /// <param name="type">The class. Its base type is not object.</param>
    /// <param name="semanticModel">A model of the class's tree, for scratch initializers.</param>
    /// <param name="ids">
    ///     Receives the wire id of every field of every base class, so the class's own fields can't reuse one.
    /// </param>
    /// <param name="cancellationToken">The token to observe.</param>
    /// <remarks>
    ///     <para>
    ///         The nearest base class that is itself restored in place emits its own code, and the code of the plain
    ///         base classes above it. The class only reads those. It analyzes and emits the plain base classes below
    ///         that one, which no other pipeline sees as restored in place.
    ///     </para>
    ///     <para>
    ///         A base class in another assembly ends the walk. Its private members aren't visible, so it must carry the
    ///         generated hooks and publish its schema entries in <see cref="SchemaFieldsName" />.
    ///     </para>
    /// </remarks>
    private static ChainAnalysis AnalyzeChain(
        INamedTypeSymbol type, SemanticModel semanticModel, Dictionary<int, string> ids,
        CancellationToken cancellationToken)
    {
        var analysis = new ChainAnalysis();
        var bases = new List<INamedTypeSymbol>();
        INamedTypeSymbol? foreign = null;
        for (INamedTypeSymbol? current = type.BaseType;
             current is { SpecialType: not SpecialType.System_Object };
             current = current.BaseType)
        {
            if (current.DeclaringSyntaxReferences.IsDefaultOrEmpty)
            {
                foreign = current;
                break;
            }

            bases.Add(current);
        }

        int silentFrom = bases.FindIndex(IsRestoredInPlace);
        if (silentFrom < 0)
            silentFrom = bases.Count;

        ChainAbove above = analysis.Above;
        if (foreign is not null && ReadForeignBase(foreign, above, analysis, ids) is { } reason)
        {
            analysis.Failed = true;

            // A base restored in place between the two reports the problem itself.
            if (silentFrom == bases.Count)
                analysis.ForeignProblem = (foreign.ToDisplayString(), reason);
        }

        for (int index = bases.Count - 1; index >= 0; index--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            INamedTypeSymbol level = bases[index];
            LevelAnalysis result = AnalyzeLevel(level, above, semanticModel, ids, cancellationToken);
            analysis.ChainFields.AddRange(result.Fields);
            if (!result.CanGenerate)
                analysis.Failed = true;

            if (index < silentFrom)
            {
                analysis.LevelDiagnostics.AddRange(result.Diagnostics);
                analysis.PlainBaseNames.Add(level.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                if (result.HasState || result.HasScratch)
                    analysis.ContentLevel = level.ToDisplayString();

                if (result.ToDeclaration() is { } declaration)
                    analysis.Levels.Add(declaration);
            }

            above.Add(result);
        }

        return analysis;
    }

    /// <summary>
    ///     Reads what a base class in another assembly provides, and claims its field ids.
    /// </summary>
    /// <returns>Null when the class can derive from it, otherwise the <c>NUMOSSTATE013</c> reason.</returns>
    private static string? ReadForeignBase(
        INamedTypeSymbol foreign, ChainAbove above, ChainAnalysis analysis, Dictionary<int, string> ids)
    {
        bool target = Implements(foreign, TargetMetadataName);
        bool scratch = Implements(foreign, ScratchTargetMetadataName);
        if (!target && !scratch)
            return StateDiagnostics.ForeignBaseReason;

        if (target)
        {
            if (!HasOverridableHook(foreign, WriteHookName) || ReadSchemaFields(foreign) is not { } entries)
                return StateDiagnostics.OldBaseReason;

            above.StateHooks = true;
            above.Target = true;
            above.SchemaFields = true;
            above.Fields = Implements(foreign, DescribedMetadataName);
            above.Bulk = Implements(foreign, BulkTargetMetadataName);
            string owner = foreign.ToDisplayString();
            foreach (SchemaEntry entry in entries)
            {
                analysis.MetadataSchema.Add(entry);
                if (!ids.ContainsKey(entry.Id))
                    ids.Add(entry.Id, owner + "." + entry.WireName);
            }
        }

        if (scratch)
        {
            if (!HasOverridableHook(foreign, ScratchHookName))
                return StateDiagnostics.OldBaseReason;

            above.ScratchHooks = true;
            above.ScratchTarget = true;
        }

        return null;
    }

    /// <summary>
    ///     Whether <paramref name="type" /> or a base class of it declares a hook a derived class in another assembly
    ///     can override.
    /// </summary>
    private static bool HasOverridableHook(INamedTypeSymbol type, string name)
    {
        for (INamedTypeSymbol? current = type; current is not null; current = current.BaseType)
        {
            foreach (ISymbol member in current.GetMembers(name))
            {
                if (member is IMethodSymbol { IsStatic: false, IsSealed: false } method &&
                    (method.IsVirtual || method.IsOverride || method.IsAbstract) &&
                    method.DeclaredAccessibility is Accessibility.Protected or Accessibility.ProtectedOrInternal
                        or Accessibility.Public)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    ///     Reads the nearest <see cref="SchemaFieldsName" /> constant of <paramref name="type" /> or its base classes.
    /// </summary>
    private static List<SchemaEntry>? ReadSchemaFields(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? current = type; current is not null; current = current.BaseType)
        {
            foreach (ISymbol member in current.GetMembers(SchemaFieldsName))
            {
                if (member is IFieldSymbol { IsConst: true, ConstantValue: string text })
                    return SchemaFingerprints.Parse(text);
            }
        }

        return null;
    }

    /// <summary>
    ///     Analyzes one base class in this compilation with the rules of a type restored in place.
    /// </summary>
    /// <param name="level">The base class.</param>
    /// <param name="above">What the base classes of <paramref name="level" /> provide.</param>
    /// <param name="semanticModel">A model of any tree in the compilation.</param>
    /// <param name="ids">The wire ids claimed so far. The level's fields add theirs.</param>
    /// <param name="cancellationToken">The token to observe.</param>
    /// <remarks>
    ///     The partial, generic, and file-local rules apply only when the level marks a member, because otherwise
    ///     nothing is generated into it. Its unmarked members draw <c>NUMOSSTATE016</c> either way.
    /// </remarks>
    private static LevelAnalysis AnalyzeLevel(
        INamedTypeSymbol level, ChainAbove above, SemanticModel semanticModel, Dictionary<int, string> ids,
        CancellationToken cancellationToken)
    {
        string displayName = level.ToDisplayString();
        TypeDeclarationSyntax? declaration = null;
        foreach (SyntaxReference reference in level.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax(cancellationToken) is TypeDeclarationSyntax candidate)
            {
                declaration = candidate;
                break;
            }
        }

        LocationInfo? location = declaration is null ? null : LocationInfo.From(declaration.Identifier.GetLocation());
        bool owned = IsRestoredInPlace(level);
        bool isComponent = owned && FindAttribute(level, SimulationComponentMetadataName) is not null;
        var result = new LevelAnalysis(level, owned)
        {
            HasState = StateSymbols.HasSerializedMembers(level),
            HasScratch = StateSymbols.HasScratchMembers(level),
        };

        if ((result.HasState || result.HasScratch || isComponent) && declaration is not null)
        {
            List<(INamedTypeSymbol Type, bool IsPartial)> chain = PartialSyntax.GetContainingChain(level, declaration);
            for (int i = chain.Count - 1; i >= 0; i--)
            {
                (INamedTypeSymbol current, bool isPartial) = chain[i];
                if (current.IsFileLocal)
                {
                    result.Diagnostics.Add(DiagnosticInfo.Create(StateDiagnostics.FileLocal, location, displayName));
                    result.CanGenerate = false;
                    break;
                }

                if (!isPartial)
                {
                    result.Diagnostics.Add(
                        DiagnosticInfo.Create(StateDiagnostics.TypeNotPartial, location, current.ToDisplayString(), displayName));

                    result.CanGenerate = false;
                }

                if (current.TypeParameters.Length > 0)
                {
                    result.Diagnostics.Add(DiagnosticInfo.Create(StateDiagnostics.Generic, location, displayName));
                    result.CanGenerate = false;
                    break;
                }
            }
        }

        bool isSolverConfiguration = Implements(level, SolverConfigurationMetadataName);
        MemberScan scan = ScanMembers(
            level, displayName, location, true, !isSolverConfiguration, true, semanticModel, ids, result.Diagnostics,
            cancellationToken);

        result.Fields.AddRange(scan.Fields);
        result.Resets.AddRange(scan.Resets);
        result.Usings = scan.Usings;
        if (scan.Failed)
            result.CanGenerate = false;

        // Every base class is open to derivation, so a level with state of its own introduces the hooks.
        result.StateHook = above.StateHooks
            ? result.HasState ? StateHookKind.Override : StateHookKind.Inherit
            : result.HasState || isComponent ? StateHookKind.Introduce : StateHookKind.None;

        result.ScratchHook = above.ScratchHooks
            ? result.HasScratch ? StateHookKind.Override : StateHookKind.Inherit
            : result.HasScratch ? StateHookKind.Introduce : StateHookKind.None;

        foreach (string generated in HookNames(result.StateHook, result.ScratchHook))
        {
            if (FindMethod(level, generated) is { } member)
            {
                result.Diagnostics.Add(
                    DiagnosticInfo.Create(
                        StateDiagnostics.HandWritten,
                        LocationInfo.From(member.Locations.FirstOrDefault()) ?? location,
                        displayName,
                        generated));

                result.CanGenerate = false;
            }
        }

        return result;
    }

    /// <summary>
    ///     Gets the hooks a class declares or overrides, which it must not write by hand.
    /// </summary>
    private static IEnumerable<string> HookNames(StateHookKind state, StateHookKind scratch)
    {
        if (state is StateHookKind.Introduce or StateHookKind.Override)
        {
            foreach (string name in StateHookNames)
                yield return name;
        }

        if (scratch is StateHookKind.Introduce or StateHookKind.Override)
            yield return ScratchHookName;
    }

    /// <summary>
    ///     What the base classes above one class in a chain provide.
    /// </summary>
    private sealed class ChainAbove
    {
        /// <summary>
        ///     Gets or sets whether a base class has the state hooks.
        /// </summary>
        public bool StateHooks { get; set; }

        /// <summary>
        ///     Gets or sets whether a base class has the scratch hook.
        /// </summary>
        public bool ScratchHooks { get; set; }

        /// <summary>
        ///     Gets or sets whether a base class implements <c>INumosStateTarget</c>.
        /// </summary>
        public bool Target { get; set; }

        /// <summary>
        ///     Gets or sets whether a base class implements <c>INumosScratchTarget</c>.
        /// </summary>
        public bool ScratchTarget { get; set; }

        /// <summary>
        ///     Gets or sets whether a base class has a state field.
        /// </summary>
        public bool Fields { get; set; }

        /// <summary>
        ///     Gets or sets whether a base class has a field that is not host-editable.
        /// </summary>
        public bool Bulk { get; set; }

        /// <summary>
        ///     Gets or sets whether a base class has the <see cref="SchemaFieldsName" /> constant, which a new one hides.
        /// </summary>
        public bool SchemaFields { get; set; }

        /// <summary>
        ///     Takes in one more level, which the next class down derives from.
        /// </summary>
        /// <remarks>
        ///     A base class restored in place that has the state hooks implements <c>INumosStateTarget</c> unless a
        ///     class above it does, and since a derived class exists it isn't sealed, so it has the constant too.
        /// </remarks>
        public void Add(LevelAnalysis level)
        {
            StateHooks |= level.StateHook != StateHookKind.None;
            ScratchHooks |= level.ScratchHook != StateHookKind.None;
            Target |= level.Owned && level.StateHook != StateHookKind.None;
            ScratchTarget |= level.Owned && level.ScratchHook != StateHookKind.None;
            SchemaFields |= level.Owned && level.StateHook != StateHookKind.None;
            Fields |= level.Fields.Count > 0;
            Bulk |= level.Fields.Any(static field => !field.Field.HostEditable);
        }
    }

    /// <summary>
    ///     One base class as <see cref="AnalyzeLevel" /> found it.
    /// </summary>
    private sealed class LevelAnalysis(INamedTypeSymbol type, bool owned)
    {
        /// <summary>
        ///     Gets whether the level is restored in place by its own declaration, so it emits its own code.
        /// </summary>
        public bool Owned { get; } = owned;

        /// <summary>
        ///     Gets or sets whether the level marks a member <c>[NumosSerialize]</c>.
        /// </summary>
        public bool HasState { get; init; }

        /// <summary>
        ///     Gets or sets whether the level marks a member <c>[NumosScratch]</c>.
        /// </summary>
        public bool HasScratch { get; init; }

        /// <summary>
        ///     Gets the level's state fields in member order.
        /// </summary>
        public List<StateFieldDeclaration> Fields { get; } = [];

        /// <summary>
        ///     Gets the level's scratch resets.
        /// </summary>
        public List<ScratchResetModel> Resets { get; } = [];

        /// <summary>
        ///     Gets or sets the using directives the copied scratch initializers need.
        /// </summary>
        public ScratchUsings Usings { get; set; } = new();

        /// <summary>
        ///     Gets the diagnostics about the level and its members.
        /// </summary>
        public List<DiagnosticInfo> Diagnostics { get; } = [];

        /// <summary>
        ///     Gets or sets whether nothing has ruled out generating the level's code.
        /// </summary>
        public bool CanGenerate { get; set; } = true;

        /// <summary>
        ///     Gets or sets how the level takes part in the state hooks.
        /// </summary>
        public StateHookKind StateHook { get; set; }

        /// <summary>
        ///     Gets or sets how the level takes part in the scratch hook.
        /// </summary>
        public StateHookKind ScratchHook { get; set; }

        /// <summary>
        ///     Gets the code a derived class emits into this plain level, or null when there's nothing to emit.
        /// </summary>
        public StateLevelDeclaration? ToDeclaration()
        {
            bool state = StateHook is StateHookKind.Introduce or StateHookKind.Override;
            bool scratch = ScratchHook is StateHookKind.Introduce or StateHookKind.Override;
            if (Owned || (!state && !scratch))
                return null;

            var prototype = new StateTypeModel(
                PartialTypeModel.From(type),
                type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                true,
                false,
                false,
                null,
                SnapshotKind.None,
                EquatableArray<StateFieldModel>.Empty,
                StateReadPlan.Empty,
                null,
                state ? new StateHooksModel(StateHook, true, false, false, false, false, false) : null);

            ScratchTypeModel? scratchModel = scratch
                ? new ScratchTypeModel(
                    PartialTypeModel.From(type), Usings.File, Usings.Namespace, Resets.ToEquatableArray(), ScratchHook, false)
                : null;

            return new StateLevelDeclaration(
                prototype,
                Fields.OrderBy(static field => field.Field.Id).ToEquatableArray(),
                scratchModel,
                CanGenerate);
        }
    }

    /// <summary>
    ///     What <see cref="AnalyzeChain" /> found.
    /// </summary>
    private sealed class ChainAnalysis
    {
        /// <summary>
        ///     Gets what the base classes provide to the class.
        /// </summary>
        public ChainAbove Above { get; } = new();

        /// <summary>
        ///     Gets or sets whether a base class rules out generating the class's code.
        /// </summary>
        public bool Failed { get; set; }

        /// <summary>
        ///     Gets or sets the base class in another assembly and the <c>NUMOSSTATE013</c> reason, when the class
        ///     itself reports it.
        /// </summary>
        public (string Base, string Reason)? ForeignProblem { get; set; }

        /// <summary>
        ///     Gets or sets the nearest plain level the class analyzes that marks a member, or null.
        /// </summary>
        public string? ContentLevel { get; set; }

        /// <summary>
        ///     Gets whether a plain level the class analyzes marks a member.
        /// </summary>
        public bool LevelsHaveContent => ContentLevel is not null;

        /// <summary>
        ///     Gets the plain levels the class emits, outermost first.
        /// </summary>
        public List<StateLevelDeclaration> Levels { get; } = [];

        /// <summary>
        ///     Gets the diagnostics about the plain levels the class analyzes.
        /// </summary>
        public List<DiagnosticInfo> LevelDiagnostics { get; } = [];

        /// <summary>
        ///     Gets the plain levels the class analyzes, by fully qualified name.
        /// </summary>
        public List<string> PlainBaseNames { get; } = [];

        /// <summary>
        ///     Gets every state field of every base class in this compilation.
        /// </summary>
        public List<StateFieldDeclaration> ChainFields { get; } = [];

        /// <summary>
        ///     Gets the schema entries of a base class in another assembly.
        /// </summary>
        public List<SchemaEntry> MetadataSchema { get; } = [];

        /// <summary>
        ///     Gets the declaration-time form of the chain.
        /// </summary>
        public StateChainDeclaration ToDeclaration()
        {
            return new StateChainDeclaration(
                Levels.ToEquatableArray(),
                LevelDiagnostics.ToEquatableArray(),
                PlainBaseNames.ToEquatableArray(),
                ChainFields.ToEquatableArray(),
                MetadataSchema.ToEquatableArray());
        }
    }

    /// <summary>
    ///     The members <see cref="ScanMembers" /> read.
    /// </summary>
    private sealed class MemberScan
    {
        /// <summary>
        ///     Gets the state fields in member order.
        /// </summary>
        public List<StateFieldDeclaration> Fields { get; } = [];

        /// <summary>
        ///     Gets the scratch resets in member order.
        /// </summary>
        public List<ScratchResetModel> Resets { get; } = [];

        /// <summary>
        ///     Gets the using directives the copied scratch initializers need.
        /// </summary>
        public ScratchUsings Usings { get; } = new();

        /// <summary>
        ///     Gets or sets whether every field is provably immutable.
        /// </summary>
        public bool Immutable { get; set; } = true;

        /// <summary>
        ///     Gets or sets whether a member error rules out generating the type's code.
        /// </summary>
        public bool Failed { get; set; }
    }
}
