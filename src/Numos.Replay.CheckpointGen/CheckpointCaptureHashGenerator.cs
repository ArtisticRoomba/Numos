using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Numos.Replay.CheckpointGen;

/// <summary>
///     Generates <c>Numos.CoreSim.Replay.GeneratedCheckpointFields</c>: chunk-array capture/restore, and the
///     hashing segments for chunk arrays, <c>AtmosConfigSnapshot</c> scalars, and <c>GasProperties</c> fields, all
///     driven by <c>[ChunkCheckpointField]</c>/<c>[ConfigCheckpointField]</c>/<c>[GasCheckpointField]</c>
///     attributes declared directly on those types.
/// </summary>
/// <remarks>
///     Runs inside <c>Numos.CoreSim</c>'s own compilation, where every attributed member has in-source syntax, so
///     this uses the fully-incremental <c>ForAttributeWithMetadataName</c> API. The wire codec counterpart of this
///     generator (<see cref="CheckpointWireCodecGenerator" />) instead runs inside <c>Numos.Serialization</c>,
///     which only sees these types as compiled metadata, and has to use a heavier compilation-wide symbol walk.
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class CheckpointCaptureHashGenerator : IIncrementalGenerator
{
    private const string ChunkFieldAttribute = "Numos.CoreSim.Replay.ChunkCheckpointFieldAttribute";
    private const string ConfigFieldAttribute = "Numos.CoreSim.Replay.ConfigCheckpointFieldAttribute";
    private const string GasFieldAttribute = "Numos.CoreSim.Replay.GasCheckpointFieldAttribute";

    private static readonly DiagnosticDescriptor UnsupportedFieldType = new(
        "NUMOSCHECKPTGEN001",
        "Unsupported checkpoint field type",
        "Member '{0}' has type '{1}', which the checkpoint field generator does not know how to hash; " +
        "leave it untagged and hand-write its capture/hash/wire code instead",
        "Numos.Replay.CheckpointGen",
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor DuplicateOrder = new(
        "NUMOSCHECKPTGEN002",
        "Duplicate checkpoint field order",
        "More than one member in '{0}' declares checkpoint field order {1}",
        "Numos.Replay.CheckpointGen",
        DiagnosticSeverity.Error,
        true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var chunkFields = context.SyntaxProvider.ForAttributeWithMetadataName(
            ChunkFieldAttribute,
            static (node, _) => node is Microsoft.CodeAnalysis.CSharp.Syntax.PropertyDeclarationSyntax,
            static (ctx, _) => ctx);

        var configFields = context.SyntaxProvider.ForAttributeWithMetadataName(
            ConfigFieldAttribute,
            static (node, _) => node is Microsoft.CodeAnalysis.CSharp.Syntax.PropertyDeclarationSyntax,
            static (ctx, _) => ctx);

        var gasFields = context.SyntaxProvider.ForAttributeWithMetadataName(
            GasFieldAttribute,
            static (node, _) => node is Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclaratorSyntax,
            static (ctx, _) => ctx);

        var combined = chunkFields.Collect().Combine(configFields.Collect()).Combine(gasFields.Collect());

        context.RegisterSourceOutput(combined, static (production, data) =>
        {
            var ((chunks, configs), gases) = data;
            var diagnostics = new List<Diagnostic>();

            var chunkPlans = CollectChunkFields(chunks, diagnostics);
            var configPlans = CollectScalarFields(configs, ConfigFieldAttribute, diagnostics);
            var gasPlans = CollectScalarFields(gases, GasFieldAttribute, diagnostics);

            foreach (var diagnostic in diagnostics)
                production.ReportDiagnostic(diagnostic);

            if (chunkPlans.Count == 0 && configPlans.Count == 0 && gasPlans.Count == 0)
                return;

            production.AddSource(
                "Numos.Replay.GeneratedCheckpointFields.g.cs",
                Generate(chunkPlans, configPlans, gasPlans));
        });
    }

    private static List<ChunkFieldPlan> CollectChunkFields(
        ImmutableArray<GeneratorAttributeSyntaxContext> hosts, List<Diagnostic> diagnostics)
    {
        var plans = new List<ChunkFieldPlan>();
        var seenOrders = new HashSet<int>();
        foreach (var host in hosts)
        {
            if (host.TargetSymbol is not IPropertySymbol property)
                continue;

            var attribute = host.Attributes[0];
            if (attribute.ConstructorArguments.Length != 2)
                continue;

            string liveMember = attribute.ConstructorArguments[0].Value as string ?? property.Name;
            int order = attribute.ConstructorArguments[1].Value is int value ? value : 0;

            if (!seenOrders.Add(order))
            {
                diagnostics.Add(Diagnostic.Create(
                    DuplicateOrder, Location.None, property.ContainingType.ToDisplayString(), order));
            }

            if (property.Type is not INamedTypeSymbol { Name: "IReadOnlyList", TypeArguments.Length: 1 } listType ||
                !TryResolvePrimitiveKeyword(listType.TypeArguments[0], out string elementTypeKeyword))
            {
                diagnostics.Add(Diagnostic.Create(
                    UnsupportedFieldType, Location.None, property.Name, property.Type.ToDisplayString()));
                continue;
            }

            plans.Add(new ChunkFieldPlan(property.Name, liveMember, elementTypeKeyword, order));
        }

        plans.Sort(static (a, b) => a.Order.CompareTo(b.Order));
        return plans;
    }

    private static List<ScalarFieldPlan> CollectScalarFields(
        ImmutableArray<GeneratorAttributeSyntaxContext> hosts, string attributeMetadataName, List<Diagnostic> diagnostics)
    {
        var plans = new List<ScalarFieldPlan>();
        var seenOrders = new HashSet<int>();
        foreach (var host in hosts)
        {
            ISymbol? member = host.TargetSymbol;
            ITypeSymbol? type = member switch
            {
                IPropertySymbol property => property.Type,
                IFieldSymbol field => field.Type,
                _ => null
            };

            if (member is null || type is null)
                continue;

            var attribute = host.Attributes.FirstOrDefault(a =>
                a.AttributeClass?.ToDisplayString() == attributeMetadataName);
            if (attribute is null || attribute.ConstructorArguments.Length != 1)
                continue;

            int order = attribute.ConstructorArguments[0].Value is int value ? value : 0;
            if (!seenOrders.Add(order))
            {
                diagnostics.Add(Diagnostic.Create(
                    DuplicateOrder, Location.None, member.ContainingType.ToDisplayString(), order));
            }

            // This generator runs inside Numos.CoreSim's own compilation, in the same generation pass as
            // Numos.Units.Analyzers -- one generator's output is never visible to another generator's view of the
            // compilation within a single pass, so a property typed through a Numos.Units quantity alias (Kelvin,
            // Pascal, ...) still looks unresolved here, even though it compiles fine once every generator's output
            // is merged for the final build. Reading the syntactic type name instead of the resolved symbol sidesteps
            // that ordering problem: every quantity alias used on a checkpoint field backs onto float today (the
            // catalog's "64" suffix marks the only double-backed variants, none of which are used here), so treat
            // any identifier other than the built-in keywords below as a float-backed quantity alias.
            if (!TryResolveKeywordFromSyntax(GetTypeSyntaxText(host), out string typeKeyword))
            {
                diagnostics.Add(Diagnostic.Create(UnsupportedFieldType, Location.None, member.Name, type.ToDisplayString()));
                continue;
            }

            plans.Add(new ScalarFieldPlan(member.Name, typeKeyword, order));
        }

        plans.Sort(static (a, b) => a.Order.CompareTo(b.Order));
        return plans;
    }

    private static string? GetTypeSyntaxText(GeneratorAttributeSyntaxContext host)
    {
        return host.TargetNode switch
        {
            Microsoft.CodeAnalysis.CSharp.Syntax.PropertyDeclarationSyntax property => property.Type.ToString(),
            Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclaratorSyntax declarator =>
                (declarator.Parent?.Parent as Microsoft.CodeAnalysis.CSharp.Syntax.FieldDeclarationSyntax)?.Declaration.Type.ToString(),
            _ => null
        };
    }

    private static bool TryResolvePrimitiveKeyword(ITypeSymbol type, out string keyword)
    {
        switch (type.SpecialType)
        {
            case SpecialType.System_Int32: keyword = "int"; return true;
            case SpecialType.System_Single: keyword = "float"; return true;
            default:
                keyword = "";
                return false;
        }
    }

    private static bool TryResolveKeywordFromSyntax(string? typeText, out string keyword)
    {
        switch (typeText)
        {
            case "int": keyword = "int"; return true;
            case "bool": keyword = "bool"; return true;
            case "string": keyword = "string"; return true;
            case "float": keyword = "float"; return true;
            case null: keyword = ""; return false;
            default:
                // Any other identifier is a Numos.Units quantity alias (Kelvin, Pascal, Scalar, ...), all of which
                // back onto float for the fields tagged today.
                keyword = "float";
                return true;
        }
    }

    private static string Generate(
        List<ChunkFieldPlan> chunkFields, List<ScalarFieldPlan> configFields, List<ScalarFieldPlan> gasFields)
    {
        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated/>");
        builder.AppendLine("#nullable enable");
        builder.AppendLine();
        builder.AppendLine("namespace Numos.CoreSim.Replay");
        builder.AppendLine("{");
        builder.AppendLine("    internal static class GeneratedCheckpointFields");
        builder.AppendLine("    {");

        if (chunkFields.Count > 0)
        {
            string tupleType = string.Join(", ", chunkFields.Select(static f =>
                $"global::System.Collections.Generic.IReadOnlyList<{f.ElementTypeKeyword}> {f.PropertyName}"));

            builder.Append("        internal static (").Append(tupleType).AppendLine(") CaptureChunkArrays(global::Numos.CoreSim.AtmosChunk chunk)");
            builder.AppendLine("        {");
            builder.AppendLine("            return (");
            for (int i = 0; i < chunkFields.Count; i++)
            {
                string comma = i == chunkFields.Count - 1 ? "" : ",";
                builder.Append("                global::System.Array.AsReadOnly(chunk.").Append(chunkFields[i].LiveMember)
                    .Append(".ToArray())").AppendLine(comma);
            }

            builder.AppendLine("            );");
            builder.AppendLine("        }");
            builder.AppendLine();

            builder.AppendLine(
                "        internal static void RestoreChunkArrays(global::Numos.CoreSim.AtmosChunk chunk, int index, global::Numos.CoreSim.Replay.AtmosChunkCheckpoint source)");
            builder.AppendLine("        {");
            foreach (var field in chunkFields)
            {
                builder.Append("            chunk.").Append(field.LiveMember).Append("[index] = source.")
                    .Append(field.PropertyName).AppendLine("[index];");
            }

            builder.AppendLine("        }");
            builder.AppendLine();

            builder.AppendLine(
                "        internal static void AppendChunkFields(ref global::Numos.CoreSim.Replay.AtmosStateHasher hash, global::Numos.CoreSim.Replay.AtmosChunkCheckpoint chunk)");
            builder.AppendLine("        {");
            foreach (var field in chunkFields)
            {
                builder.Append("            foreach (").Append(field.ElementTypeKeyword).Append(" value in chunk.")
                    .Append(field.PropertyName).AppendLine(") hash.Add(value);");
            }

            builder.AppendLine("        }");
            builder.AppendLine();
        }

        if (configFields.Count > 0)
        {
            builder.AppendLine(
                "        internal static void AppendConfigFields(ref global::Numos.CoreSim.Replay.AtmosStateHasher hash, global::Numos.CoreSim.AtmosConfigSnapshot config)");
            builder.AppendLine("        {");
            foreach (var field in configFields)
                builder.Append("            hash.Add(config.").Append(field.MemberName).AppendLine(");");
            builder.AppendLine("        }");
            builder.AppendLine();
        }

        if (gasFields.Count > 0)
        {
            builder.AppendLine(
                "        internal static void AppendGasFields(ref global::Numos.CoreSim.Replay.AtmosStateHasher hash, global::Numos.CoreSim.GasProperties gas)");
            builder.AppendLine("        {");
            foreach (var field in gasFields)
                builder.Append("            hash.Add(gas.").Append(field.MemberName).AppendLine(");");
            builder.AppendLine("        }");
        }

        builder.AppendLine("    }");
        builder.AppendLine("}");
        return builder.ToString();
    }

    private sealed class ChunkFieldPlan(string propertyName, string liveMember, string elementTypeKeyword, int order)
    {
        public string PropertyName { get; } = propertyName;
        public string LiveMember { get; } = liveMember;
        public string ElementTypeKeyword { get; } = elementTypeKeyword;
        public int Order { get; } = order;
    }

    private sealed class ScalarFieldPlan(string memberName, string typeKeyword, int order)
    {
        public string MemberName { get; } = memberName;
        public string TypeKeyword { get; } = typeKeyword;
        public int Order { get; } = order;
    }
}
