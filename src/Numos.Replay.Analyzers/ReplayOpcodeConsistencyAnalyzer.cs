using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Numos.Replay.SourceGen;

/// <summary>
///     Cross-checks that every <c>AtmosOperationCode</c>/<c>AtmosWorldOperationCode</c> member has a
///     <c>[WireOperation]</c> wire codec registration, so forgetting one is a build error instead of a runtime replay
///     failure.
/// </summary>
/// <remarks>
///     The enums and operation records live in <c>Numos.CoreSim</c>/<c>Numos.API</c>, while <c>[WireOperation]</c>
///     registrations live in <c>Numos.Serialization</c>. The check only fires in a compilation where at least one
///     registration for a family is actually found (i.e. <c>Numos.Serialization</c>), so the other projects that
///     reference this assembly for <see cref="ApplySwitchGenerator" /> stay quiet.
///     <para>
///         This analyzer deliberately lives in its own assembly rather than alongside <c>ReplayCodecGenerator</c>
///         in <c>Numos.Replay.SourceGen</c>. That generator's <c>RegisterPostInitializationOutput</c> unconditionally
///         emits a <c>WireOperationAttribute</c> definition into every compilation that references it, including
///         ones (like <c>Numos.CoreSim</c>/<c>Numos.API</c>) that have no use for it. If those two projects also
///         referenced the generator, <c>Numos.Serialization</c> -- which has <c>InternalsVisibleTo</c> access to both
///         and also references the generator directly -- would see three colliding declarations of the same internal
///         type and fail with CS0436. Keeping this assembly diagnostic- and dispatch-only avoids that.
///     </para>
///     <para>
///         A missing Apply overload for an operation is caught by <see cref="ApplySwitchGenerator" />'s
///         NUMOSREPLAYGEN010.
///     </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ReplayOpcodeConsistencyAnalyzer : DiagnosticAnalyzer
{
    private const string WireOperationAttributeMetadataName = "Numos.Replay.SourceGen.WireOperationAttribute";
    private const string SimOperationBaseMetadataName = "Numos.CoreSim.Replay.AtmosOperation";
    private const string SimOperationCodeMetadataName = "Numos.CoreSim.Replay.AtmosOperationCode";
    private const string WorldOperationBaseMetadataName = "Numos.API.AtmosWorldOperation";
    private const string WorldOperationCodeMetadataName = "Numos.API.AtmosWorldOperationCode";

    private static readonly SymbolDisplayFormat FullyQualified = SymbolDisplayFormat.FullyQualifiedFormat;

    private static readonly DiagnosticDescriptor UnregisteredCode = new(
        "NUMOSREPLAYGEN008",
        "Opcode has no wire codec registration",
        "'{0}.{1}' has no [WireOperation] registration; replaying it will fail at runtime instead of build time",
        "Numos.Replay.SourceGen",
        DiagnosticSeverity.Error,
        true,
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(UnregisteredCode);

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
        if (wireOperationAttribute is null || sim is null && world is null)
            return;

        context.RegisterSymbolAction(
            symbolContext => AnalyzeNamedType(symbolContext, wireOperationAttribute, sim, world), SymbolKind.NamedType);

        context.RegisterCompilationEndAction(endContext =>
        {
            ReportUnregisteredCodes(endContext, sim);
            ReportUnregisteredCodes(endContext, world);
        });
    }

    private static void AnalyzeNamedType(
        SymbolAnalysisContext context,
        INamedTypeSymbol wireOperationAttribute,
        FamilyState? sim,
        FamilyState? world)
    {
        if (context.Symbol is not INamedTypeSymbol namedType)
            return;

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

    private static string CodeKey(object? value)
    {
        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null";
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

    /// <summary>
    ///     Mutable, concurrency-safe scratch state for one operation family, accumulated across the compilation's
    ///     <see cref="SymbolKind.NamedType" /> symbol actions (which <c>EnableConcurrentExecution</c> may run on
    ///     multiple threads) and consumed once, single-threaded, from the compilation-end action.
    /// </summary>
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

        /// <summary>
        ///     Any one registration host's location, used to anchor a missing-registration diagnostic somewhere
        ///     fixable in source. Which host wins under a benign race is irrelevant -- they're all equally correct
        ///     places to point a developer at.
        /// </summary>
        public Location? RegistrationHostLocation { get; set; }

        public static FamilyState? Resolve(Compilation compilation, string baseMetadataName, string codeEnumMetadataName)
        {
            var @base = compilation.GetTypeByMetadataName(baseMetadataName);
            var codeEnum = compilation.GetTypeByMetadataName(codeEnumMetadataName);
            return @base != null && codeEnum != null ? new FamilyState(@base, codeEnum) : null;
        }
    }
}
