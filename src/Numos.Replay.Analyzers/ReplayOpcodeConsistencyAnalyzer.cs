using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Numos.Replay.SourceGen;

/// <summary>
///     Cross-checks that every <c>AtmosOperationCode</c>/<c>AtmosWorldOperationCode</c> member has both a
///     <c>[WireOperation]</c> wire codec registration and a case in its family's hand-written "Apply" switch
///     (<c>AtmosKernel.ApplyRecordedOperation</c>, <c>AtmosWorld.ApplyWorldOperation</c>), so forgetting either one
///     is a build error instead of a runtime replay failure.
/// </summary>
/// <remarks>
///     Neither check can run from a single compilation: the enums, the concrete operation records, and the Apply
///     switches all live in <c>Numos.CoreSim</c>/<c>Numos.API</c>, while <c>[WireOperation]</c> registrations live in
///     <c>Numos.Serialization</c>, which only sees the former as compiled metadata (no method-body syntax). So this
///     analyzer self-scopes per compilation: the registration check only fires where at least one registration for
///     a family is actually found (i.e. <c>Numos.Serialization</c>), and the Apply-switch check only fires where the
///     relevant Apply method has in-source syntax to inspect (i.e. <c>Numos.CoreSim</c> for the simulation family,
///     <c>Numos.API</c> for the world family). This means <c>Numos.Replay.Analyzers</c> must be referenced from all
///     three projects for full coverage -- see each project's analyzer <c>ProjectReference</c>.
///     <para>
///         This analyzer deliberately lives in its own assembly rather than alongside <c>ReplayCodecGenerator</c>
///         in <c>Numos.Replay.SourceGen</c>. That generator's <c>RegisterPostInitializationOutput</c> unconditionally
///         emits a <c>WireOperationAttribute</c> definition into every compilation that references it, including
///         ones (like <c>Numos.CoreSim</c>/<c>Numos.API</c>) that have no use for it. If those two projects also
///         referenced the generator (needed only so this analyzer could run there), <c>Numos.Serialization</c> --
///         which has <c>InternalsVisibleTo</c> access to both and also references the generator directly -- would
///         see three colliding declarations of the same internal type and fail with CS0436. Splitting the analyzer
///         out means <c>Numos.CoreSim</c>/<c>Numos.API</c> pull in only this diagnostic-only assembly, which never
///         generates anything.
///     </para>
///     <para>
///         The Apply methods are located by exact metadata name rather than by pattern-matching "some switch over
///         the operation base type" -- other switches/patterns over these types exist in the codebase
///         (e.g. <c>AtmosWorldReplayArchive</c>'s config-snapshot lookup) that are deliberately partial and would
///         false-positive under a structural heuristic. A rename of either Apply method requires updating the
///         metadata name here; that failure mode (analyzer silently stops checking) is preferable to a heuristic
///         that misidentifies a partial switch as the exhaustive one.
///     </para>
///     <para>
///         Two conditions make the Apply-switch check silently skip an operation rather than report on it: a
///         renamed Apply method (above), and an operation whose <c>Code</c> override isn't one of the recognized
///         constant-expression shapes (<see cref="TryGetCodeOverrideValue" />) -- e.g. a computed value instead of
///         a plain <c>=&gt; SomeCode.Member;</c>. Both trade a rare false negative for avoiding false positives on
///         unusual but valid code; neither is enforced elsewhere, so don't assume "no NUMOSREPLAYGEN009" means
///         every opcode was actually checked.
///     </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ReplayOpcodeConsistencyAnalyzer : DiagnosticAnalyzer
{
    private const string WireOperationAttributeMetadataName = "Numos.Replay.SourceGen.WireOperationAttribute";
    private const string SimOperationBaseMetadataName = "Numos.CoreSim.Replay.AtmosOperation";
    private const string SimOperationCodeMetadataName = "Numos.CoreSim.Replay.AtmosOperationCode";
    private const string SimApplyHostMetadataName = "Numos.CoreSim.AtmosKernel";
    private const string SimApplyMethodName = "ApplyRecordedOperation";
    private const string WorldOperationBaseMetadataName = "Numos.API.AtmosWorldOperation";
    private const string WorldOperationCodeMetadataName = "Numos.API.AtmosWorldOperationCode";
    private const string WorldApplyHostMetadataName = "Numos.API.AtmosWorld";
    private const string WorldApplyMethodName = "ApplyWorldOperation";

    private static readonly SymbolDisplayFormat FullyQualified = SymbolDisplayFormat.FullyQualifiedFormat;

    private static readonly DiagnosticDescriptor UnregisteredCode = new(
        "NUMOSREPLAYGEN008",
        "Opcode has no wire codec registration",
        "'{0}.{1}' has no [WireOperation] registration; replaying it will fail at runtime instead of build time",
        "Numos.Replay.SourceGen",
        DiagnosticSeverity.Error,
        true,
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    private static readonly DiagnosticDescriptor MissingApplyCase = new(
        "NUMOSREPLAYGEN009",
        "Opcode has no Apply-switch case",
        "'{0}' (code '{1}.{2}') has no case in '{3}.{4}'; replaying it will fail at runtime instead of build time",
        "Numos.Replay.SourceGen",
        DiagnosticSeverity.Error,
        true,
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(UnregisteredCode, MissingApplyCase);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        Compilation compilation = context.Compilation;
        var wireOperationAttribute = compilation.GetTypeByMetadataName(WireOperationAttributeMetadataName);
        var sim = FamilyState.Resolve(compilation, SimOperationBaseMetadataName, SimOperationCodeMetadataName);
        var world = FamilyState.Resolve(compilation, WorldOperationBaseMetadataName, WorldOperationCodeMetadataName);
        if (wireOperationAttribute is null && sim is null && world is null)
            return;

        // RS1030 forbids Compilation.GetSemanticModel() inside an analyzer (uncached, defeats the driver's reuse);
        // RegisterSemanticModelAction hands us one per tree instead, which we stash for the compilation-end checks.
        var semanticModels = new ConcurrentDictionary<SyntaxTree, SemanticModel>();
        context.RegisterSemanticModelAction(smc => semanticModels[smc.SemanticModel.SyntaxTree] = smc.SemanticModel);

        context.RegisterSymbolAction(
            symbolContext => AnalyzeNamedType(symbolContext, wireOperationAttribute, sim, world, semanticModels), SymbolKind.NamedType);

        context.RegisterCompilationEndAction(endContext =>
        {
            ReportUnregisteredCodes(endContext, sim);
            ReportUnregisteredCodes(endContext, world);
            ReportMissingApplyCases(
                endContext, sim, SimApplyHostMetadataName, SimApplyMethodName, semanticModels);
            ReportMissingApplyCases(
                endContext, world, WorldApplyHostMetadataName, WorldApplyMethodName, semanticModels);
        });
    }

    private static void AnalyzeNamedType(
        SymbolAnalysisContext context,
        INamedTypeSymbol? wireOperationAttribute,
        FamilyState? sim,
        FamilyState? world,
        ConcurrentDictionary<SyntaxTree, SemanticModel> semanticModels)
    {
        if (context.Symbol is not INamedTypeSymbol namedType)
            return;

        if (wireOperationAttribute != null)
        {
            foreach (var attribute in namedType.GetAttributes())
            {
                if (!SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, wireOperationAttribute) ||
                    attribute.ConstructorArguments.Length != 2 ||
                    attribute.ConstructorArguments[0].Value is not INamedTypeSymbol operationType)
                {
                    continue;
                }

                TypedConstant codeConstant = attribute.ConstructorArguments[1];
                Location? hostLocation = namedType.Locations.FirstOrDefault(static l => l.IsInSource);
                if (sim != null && InheritsFrom(operationType, sim.Base) &&
                    SymbolEqualityComparer.Default.Equals(codeConstant.Type, sim.CodeEnum))
                {
                    sim.RegisteredCodes.TryAdd(CodeKey(codeConstant.Value), true);
                    sim.RegistrationHostLocation ??= hostLocation;
                }
                else if (world != null && InheritsFrom(operationType, world.Base) &&
                         SymbolEqualityComparer.Default.Equals(codeConstant.Type, world.CodeEnum))
                {
                    world.RegisteredCodes.TryAdd(CodeKey(codeConstant.Value), true);
                    world.RegistrationHostLocation ??= hostLocation;
                }
            }
        }

        if (namedType.IsAbstract)
            return;

        // Resolving each candidate's Code value needs a semantic model, but RegisterSymbolAction and
        // RegisterSemanticModelAction can fire in either order -- so just record the candidate here and resolve its
        // Code value later, in the compilation-end action, once every tree's semantic model is guaranteed cached.
        if (sim != null && InheritsFrom(namedType, sim.Base))
            sim.ConcreteOperationCandidates.Add(namedType);
        else if (world != null && InheritsFrom(namedType, world.Base))
            world.ConcreteOperationCandidates.Add(namedType);
    }

    private static void ReportUnregisteredCodes(CompilationAnalysisContext context, FamilyState? family)
    {
        if (family is null || family.RegisteredCodes.Count == 0)
            return;

        foreach (var member in family.CodeEnum.GetMembers().OfType<IFieldSymbol>())
        {
            if (!member.HasConstantValue || family.RegisteredCodes.ContainsKey(CodeKey(member.ConstantValue)))
                continue;

            context.ReportDiagnostic(Diagnostic.Create(
                UnregisteredCode,
                family.RegistrationHostLocation ?? Location.None,
                family.CodeEnum.ToDisplayString(FullyQualified),
                member.Name));
        }
    }

    private static void ReportMissingApplyCases(
        CompilationAnalysisContext context,
        FamilyState? family,
        string applyHostMetadataName,
        string applyMethodName,
        ConcurrentDictionary<SyntaxTree, SemanticModel> semanticModels)
    {
        if (family is null || family.ConcreteOperationCandidates.IsEmpty)
            return;

        var applyHost = context.Compilation.GetTypeByMetadataName(applyHostMetadataName);
        var applyMethod = applyHost?.GetMembers(applyMethodName).OfType<IMethodSymbol>().FirstOrDefault();
        if (applyMethod is null || applyMethod.DeclaringSyntaxReferences.Length == 0)
            return; // Metadata-only in this compilation (or absent) -- checked from whichever compilation has its source instead.

        var declaration = applyMethod.DeclaringSyntaxReferences[0].GetSyntax() as MethodDeclarationSyntax;
        var switchStatement = declaration?.Body?.DescendantNodes().OfType<SwitchStatementSyntax>().FirstOrDefault();
        if (switchStatement is null || !semanticModels.TryGetValue(switchStatement.SyntaxTree, out var switchModel))
            return;

        var caseTypes = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var section in switchStatement.Sections)
        foreach (var label in section.Labels)
        {
            if (label is CasePatternSwitchLabelSyntax { Pattern: DeclarationPatternSyntax declarationPattern })
            {
                var caseType = switchModel.GetTypeInfo(declarationPattern.Type).Type;
                if (caseType != null)
                    caseTypes.Add(caseType);
            }
        }

        foreach (var operationType in family.ConcreteOperationCandidates)
        {
            if (caseTypes.Contains(operationType) ||
                !TryGetCodeOverrideValue(operationType, semanticModels, out object? code, out var location))
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                MissingApplyCase,
                location,
                operationType.ToDisplayString(FullyQualified),
                family.CodeEnum.ToDisplayString(FullyQualified),
                ResolveEnumMemberName(family.CodeEnum, code),
                applyHost!.ToDisplayString(FullyQualified),
                applyMethodName));
        }
    }

    private static bool TryGetCodeOverrideValue(
        INamedTypeSymbol operationType, ConcurrentDictionary<SyntaxTree, SemanticModel> semanticModels, out object? value, out Location location)
    {
        var codeProperty = operationType.GetMembers("Code").OfType<IPropertySymbol>().FirstOrDefault(static p => p.IsOverride);
        ExpressionSyntax? expression = null;
        if (codeProperty?.DeclaringSyntaxReferences.Length > 0 &&
            codeProperty.DeclaringSyntaxReferences[0].GetSyntax() is PropertyDeclarationSyntax propertyDeclaration)
        {
            expression = propertyDeclaration.ExpressionBody?.Expression;
            if (expression is null && propertyDeclaration.AccessorList != null)
            {
                var getter = propertyDeclaration.AccessorList.Accessors
                    .FirstOrDefault(static a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
                expression = getter?.ExpressionBody?.Expression ??
                             getter?.Body?.Statements.OfType<ReturnStatementSyntax>().FirstOrDefault()?.Expression;
            }
        }

        if (expression is null || !semanticModels.TryGetValue(expression.SyntaxTree, out var semanticModel))
        {
            value = null;
            location = Location.None;
            return false;
        }

        var constant = semanticModel.GetConstantValue(expression);
        if (!constant.HasValue)
        {
            value = null;
            location = Location.None;
            return false;
        }

        value = constant.Value;
        location = expression.GetLocation();
        return true;
    }

    private static string CodeKey(object? value)
    {
        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null";
    }

    private static string ResolveEnumMemberName(INamedTypeSymbol codeEnum, object? value)
    {
        foreach (var member in codeEnum.GetMembers().OfType<IFieldSymbol>())
        {
            if (member.HasConstantValue && Equals(member.ConstantValue, value))
                return member.Name;
        }

        return CodeKey(value);
    }

    private static bool InheritsFrom(ITypeSymbol? type, INamedTypeSymbol? baseType)
    {
        if (baseType is null)
            return false;

        for (var current = type?.BaseType; current != null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, baseType))
                return true;
        }

        return false;
    }

    /// <summary>Mutable, concurrency-safe scratch state for one operation family, accumulated across the
    /// compilation's <see cref="SymbolKind.NamedType" /> symbol actions (which <c>EnableConcurrentExecution</c> may
    /// run on multiple threads) and consumed once, single-threaded, from the compilation-end action.</summary>
    private sealed class FamilyState
    {
        private FamilyState(INamedTypeSymbol @base, INamedTypeSymbol codeEnum)
        {
            Base = @base;
            CodeEnum = codeEnum;
        }

        public INamedTypeSymbol Base { get; }

        public INamedTypeSymbol CodeEnum { get; }

        public ConcurrentDictionary<string, bool> RegisteredCodes { get; } = new(StringComparer.Ordinal);

        public ConcurrentBag<INamedTypeSymbol> ConcreteOperationCandidates { get; } = new();

        /// <summary>Any one registration host's location, used to anchor a missing-registration diagnostic
        /// somewhere fixable in source. Which host wins under a benign race is irrelevant -- they're all equally
        /// correct places to point a developer at.</summary>
        public Location? RegistrationHostLocation { get; set; }

        public static FamilyState? Resolve(Compilation compilation, string baseMetadataName, string codeEnumMetadataName)
        {
            var @base = compilation.GetTypeByMetadataName(baseMetadataName);
            var codeEnum = compilation.GetTypeByMetadataName(codeEnumMetadataName);
            return @base != null && codeEnum != null ? new FamilyState(@base, codeEnum) : null;
        }
    }
}
