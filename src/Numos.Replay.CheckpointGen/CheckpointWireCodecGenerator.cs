using System.Text;
using Microsoft.CodeAnalysis;

namespace Numos.Replay.CheckpointGen;

/// <summary>
///     Generates the wire read/write glue for the same checkpoint fields
///     <see cref="CheckpointCaptureHashGenerator" /> covers, as partial members of
///     <c>Numos.Serialization.NumosReplaySerializer</c>.
/// </summary>
/// <remarks>
///     Runs inside <c>Numos.Serialization</c>, which only sees <c>AtmosChunkCheckpoint</c>/
///     <c>AtmosConfigSnapshot</c>/<c>GasProperties</c> as compiled metadata from a referenced assembly -- there is no
///     in-source syntax for <c>ForAttributeWithMetadataName</c> to match against, so this walks
///     <see cref="IncrementalGeneratorInitializationContext.CompilationProvider" /> directly instead. That trades
///     fine-grained incrementality (this pass reruns on any compilation change in this project) for the ability to
///     see attributes applied in a different compilation; acceptable given how rarely these field lists change.
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class CheckpointWireCodecGenerator : IIncrementalGenerator
{
    private const string ChunkCheckpointMetadataName = "Numos.CoreSim.Replay.AtmosChunkCheckpoint";
    private const string ConfigSnapshotMetadataName = "Numos.CoreSim.AtmosConfigSnapshot";
    private const string GasPropertiesMetadataName = "Numos.CoreSim.GasProperties";
    private const string HostTypeMetadataName = "Numos.Serialization.NumosReplaySerializer";
    private const string ChunkFieldAttribute = "Numos.CoreSim.Replay.ChunkCheckpointFieldAttribute";
    private const string ConfigFieldAttribute = "Numos.CoreSim.Replay.ConfigCheckpointFieldAttribute";
    private const string GasFieldAttribute = "Numos.CoreSim.Replay.GasCheckpointFieldAttribute";

    private static readonly DiagnosticDescriptor UnsupportedFieldType = new(
        "NUMOSCHECKPTGEN003",
        "Unsupported checkpoint field type",
        "Member '{0}' has type '{1}', which the checkpoint field generator does not know how to wire-encode; " +
        "leave it untagged and hand-write its capture/hash/wire code instead",
        "Numos.Replay.CheckpointGen",
        DiagnosticSeverity.Error,
        true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterSourceOutput(context.CompilationProvider, static (production, compilation) =>
        {
            // Numos.Replay.CheckpointGen is referenced as an analyzer from both Numos.CoreSim and
            // Numos.Serialization, but this generator's output (a partial member of NumosReplaySerializer) is only
            // meaningful -- and only compiles -- inside Numos.Serialization, which is the only compilation that can
            // see that host type. Numos.CoreSim can already see AtmosChunkCheckpoint/AtmosConfigSnapshot/
            // GasProperties (it declares them), so checking for those types alone would not be enough to skip it.
            if (compilation.GetTypeByMetadataName(HostTypeMetadataName) is null)
                return;

            var chunkType = compilation.GetTypeByMetadataName(ChunkCheckpointMetadataName);
            var configType = compilation.GetTypeByMetadataName(ConfigSnapshotMetadataName);
            var gasType = compilation.GetTypeByMetadataName(GasPropertiesMetadataName);
            if (chunkType is null && configType is null && gasType is null)
                return;

            var diagnostics = new List<Diagnostic>();
            var chunkFields = chunkType is null
                ? []
                : CollectChunkFields(chunkType, diagnostics);
            var configFields = configType is null
                ? []
                : CollectScalarFields(configType.GetMembers().OfType<IPropertySymbol>(), ConfigFieldAttribute, diagnostics);
            var gasFields = gasType is null
                ? []
                : CollectScalarFields(gasType.GetMembers().OfType<IFieldSymbol>(), GasFieldAttribute, diagnostics);

            foreach (var diagnostic in diagnostics)
                production.ReportDiagnostic(diagnostic);

            if (chunkFields.Count == 0 && configFields.Count == 0 && gasFields.Count == 0)
                return;

            production.AddSource(
                "Numos.Replay.GeneratedCheckpointWireCodec.g.cs",
                Generate(chunkFields, configFields, gasFields));
        });
    }

    private static List<ChunkFieldPlan> CollectChunkFields(INamedTypeSymbol chunkType, List<Diagnostic> diagnostics)
    {
        var plans = new List<ChunkFieldPlan>();
        foreach (var property in chunkType.GetMembers().OfType<IPropertySymbol>())
        {
            var attribute = property.GetAttributes()
                .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == ChunkFieldAttribute);
            if (attribute is null || attribute.ConstructorArguments.Length != 2)
                continue;

            string liveMember = attribute.ConstructorArguments[0].Value as string ?? property.Name;
            int order = attribute.ConstructorArguments[1].Value is int value ? value : 0;

            if (property.Type is not INamedTypeSymbol { Name: "IReadOnlyList", TypeArguments.Length: 1 } listType ||
                !TryResolveKind(listType.TypeArguments[0], out var kind))
            {
                diagnostics.Add(Diagnostic.Create(UnsupportedFieldType, Location.None, property.Name, property.Type.ToDisplayString()));
                continue;
            }

            plans.Add(new ChunkFieldPlan(property.Name, liveMember, kind, order));
        }

        plans.Sort(static (a, b) => a.Order.CompareTo(b.Order));
        return plans;
    }

    private static List<ScalarFieldPlan> CollectScalarFields(
        IEnumerable<ISymbol> members, string attributeMetadataName, List<Diagnostic> diagnostics)
    {
        var plans = new List<ScalarFieldPlan>();
        foreach (var member in members)
        {
            var attribute = member.GetAttributes()
                .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == attributeMetadataName);
            if (attribute is null || attribute.ConstructorArguments.Length != 1)
                continue;

            ITypeSymbol type = member switch
            {
                IPropertySymbol property => property.Type,
                IFieldSymbol field => field.Type,
                _ => throw new InvalidOperationException("Unexpected member kind.")
            };

            int order = attribute.ConstructorArguments[0].Value is int value ? value : 0;
            if (!TryResolveKind(type, out var kind))
            {
                diagnostics.Add(Diagnostic.Create(UnsupportedFieldType, Location.None, member.Name, type.ToDisplayString()));
                continue;
            }

            plans.Add(new ScalarFieldPlan(member.Name, kind, order));
        }

        plans.Sort(static (a, b) => a.Order.CompareTo(b.Order));
        return plans;
    }

    private static bool TryResolveKind(ITypeSymbol type, out WireKind kind)
    {
        switch (type.SpecialType)
        {
            case SpecialType.System_Int32: kind = WireKind.Int32; return true;
            case SpecialType.System_Single: kind = WireKind.Single; return true;
            case SpecialType.System_Boolean: kind = WireKind.Boolean; return true;
            case SpecialType.System_String: kind = WireKind.NullableString; return true;
            default:
                kind = default;
                return false;
        }
    }

    private static string ElementKeyword(WireKind kind) => kind switch
    {
        WireKind.Int32 => "int",
        WireKind.Single => "float",
        WireKind.Boolean => "bool",
        WireKind.NullableString => "string",
        _ => throw new InvalidOperationException("Unexpected wire kind.")
    };

    private static string ReadExpression(WireKind kind) => kind switch
    {
        WireKind.Int32 => "reader.ReadInt32()",
        WireKind.Single => "reader.ReadSingle()",
        WireKind.Boolean => "reader.ReadBoolean()",
        WireKind.NullableString => "ReadNullableString(reader, options)!",
        _ => throw new InvalidOperationException("Unexpected wire kind.")
    };

    private static string Generate(
        List<ChunkFieldPlan> chunkFields, List<ScalarFieldPlan> configFields, List<ScalarFieldPlan> gasFields)
    {
        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated/>");
        builder.AppendLine("#nullable enable");
        builder.AppendLine();
        builder.AppendLine("namespace Numos.Serialization");
        builder.AppendLine("{");
        builder.AppendLine("    public static partial class NumosReplaySerializer");
        builder.AppendLine("    {");

        if (chunkFields.Count > 0)
        {
            builder.AppendLine(
                "        internal static void WriteGeneratedChunkFields(global::System.IO.BinaryWriter writer, global::Numos.CoreSim.Replay.AtmosChunkCheckpoint chunk)");
            builder.AppendLine("        {");
            foreach (var field in chunkFields)
            {
                builder.Append("            foreach (").Append(ElementKeyword(field.Kind)).Append(" value in chunk.")
                    .Append(field.PropertyName).AppendLine(") writer.Write(value);");
            }

            builder.AppendLine("        }");
            builder.AppendLine();

            string tupleType = string.Join(", ", chunkFields.Select(static f => $"{ElementKeyword(f.Kind)}[] {f.PropertyName}"));
            builder.Append("        internal static (").Append(tupleType)
                .AppendLine(") ReadGeneratedChunkFields(global::System.IO.BinaryReader reader, int voxels)");
            builder.AppendLine("        {");
            builder.AppendLine("            return (");
            for (int i = 0; i < chunkFields.Count; i++)
            {
                string comma = i == chunkFields.Count - 1 ? "" : ",";
                string readMethod = chunkFields[i].Kind switch
                {
                    WireKind.Int32 => "static r => r.ReadInt32()",
                    WireKind.Single => "static r => r.ReadSingle()",
                    _ => throw new InvalidOperationException("Unsupported chunk array element kind.")
                };

                builder.Append("                ReadArray(reader, voxels, ").Append(readMethod).Append(')').AppendLine(comma);
            }

            builder.AppendLine("            );");
            builder.AppendLine("        }");
            builder.AppendLine();
        }

        if (configFields.Count > 0)
        {
            builder.AppendLine(
                "        internal static void WriteGeneratedConfigFields(global::System.IO.BinaryWriter writer, global::Numos.CoreSim.AtmosConfigSnapshot config)");
            builder.AppendLine("        {");
            foreach (var field in configFields)
                builder.Append("            writer.Write(config.").Append(field.MemberName).AppendLine(");");
            builder.AppendLine("        }");
            builder.AppendLine();

            builder.AppendLine(
                "        internal static void ReadGeneratedConfigFields(global::System.IO.BinaryReader reader, global::Numos.CoreSim.AtmosConfig config)");
            builder.AppendLine("        {");
            foreach (var field in configFields)
                builder.Append("            config.").Append(field.MemberName).Append(" = ").Append(ReadExpression(field.Kind)).AppendLine(";");
            builder.AppendLine("        }");
            builder.AppendLine();
        }

        if (gasFields.Count > 0)
        {
            builder.AppendLine(
                "        internal static void WriteGeneratedGasFields(global::System.IO.BinaryWriter writer, global::Numos.CoreSim.GasProperties gas)");
            builder.AppendLine("        {");
            foreach (var field in gasFields)
            {
                if (field.Kind == WireKind.NullableString)
                    builder.Append("            WriteNullableString(writer, gas.").Append(field.MemberName).AppendLine(");");
                else
                    builder.Append("            writer.Write(gas.").Append(field.MemberName).AppendLine(");");
            }

            builder.AppendLine("        }");
            builder.AppendLine();

            builder.AppendLine(
                "        internal static global::Numos.CoreSim.GasProperties ReadGeneratedGasFields(global::System.IO.BinaryReader reader, global::Numos.Serialization.NumosReplayReadOptions options)");
            builder.AppendLine("        {");
            builder.AppendLine("            var gas = new global::Numos.CoreSim.GasProperties();");
            foreach (var field in gasFields)
                builder.Append("            gas.").Append(field.MemberName).Append(" = ").Append(ReadExpression(field.Kind)).AppendLine(";");
            builder.AppendLine("            return gas;");
            builder.AppendLine("        }");
        }

        builder.AppendLine("    }");
        builder.AppendLine("}");
        return builder.ToString();
    }

    private enum WireKind
    {
        Int32,
        Single,
        Boolean,
        NullableString
    }

    private sealed class ChunkFieldPlan(string propertyName, string liveMember, WireKind kind, int order)
    {
        public string PropertyName { get; } = propertyName;
        public string LiveMember { get; } = liveMember;
        public WireKind Kind { get; } = kind;
        public int Order { get; } = order;
    }

    private sealed class ScalarFieldPlan(string memberName, WireKind kind, int order)
    {
        public string MemberName { get; } = memberName;
        public WireKind Kind { get; } = kind;
        public int Order { get; } = order;
    }
}
