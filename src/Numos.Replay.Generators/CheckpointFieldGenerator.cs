using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Numos.API.Generators.Common;

namespace Numos.Replay.Generators;

/// <summary>
///     Generates <c>Numos.CoreSim.Replay.GeneratedCheckpointFields</c> from the <c>[ChunkCheckpointField]</c>,
///     <c>[ConfigCheckpointField]</c>, and <c>[GasCheckpointField]</c> members in Numos.CoreSim.
/// </summary>
/// <remarks>
///     One field list per group drives everything that has to agree about it: chunk-array capture and restore, state
///     hashing, the wire encoding, and (for config and gas fields) equality. Fields are emitted in their declared order,
///     which the golden replay bytes pin.
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class CheckpointFieldGenerator : IIncrementalGenerator
{
    private const string ChunkFieldAttribute = "Numos.CoreSim.Replay.ChunkCheckpointFieldAttribute";
    private const string ConfigFieldAttribute = "Numos.CoreSim.Replay.ConfigCheckpointFieldAttribute";
    private const string GasFieldAttribute = "Numos.CoreSim.Replay.GasCheckpointFieldAttribute";
    private const string Codec = "global::Numos.CoreSim.Replay.AtmosReplayCodec";

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValueProvider<ImmutableArray<CheckpointField>> chunks = Collect(
            context,
            ChunkFieldAttribute,
            CheckpointGroup.Chunk,
            static node => node is PropertyDeclarationSyntax);

        IncrementalValueProvider<ImmutableArray<CheckpointField>> configs = Collect(
            context,
            ConfigFieldAttribute,
            CheckpointGroup.Config,
            static node => node is PropertyDeclarationSyntax);

        IncrementalValueProvider<ImmutableArray<CheckpointField>> gases = Collect(
            context,
            GasFieldAttribute,
            CheckpointGroup.Gas,
            static node => node is VariableDeclaratorSyntax);

        IncrementalValueProvider<GeneratorOutput<CheckpointPlan>> output = chunks
            .Combine(configs)
            .Combine(gases)
            .Combine(QuantityAliasCatalog.Provider(context))
            .Select(static (input, _) => Plan(
                input.Left.Left.Left.AddRange(input.Left.Left.Right).AddRange(input.Left.Right),
                input.Right));

        GeneratorOutput.Register(context, output, "CheckpointFields", Emit);
    }

    private static IncrementalValueProvider<ImmutableArray<CheckpointField>> Collect(
        IncrementalGeneratorInitializationContext context, string attribute, CheckpointGroup group,
        Func<SyntaxNode, bool> predicate)
    {
        return context.SyntaxProvider
            .ForAttributeWithMetadataName(
                attribute,
                (node, _) => predicate(node),
                (syntaxContext, _) => Transform(syntaxContext, group))
            .Collect();
    }

    private static CheckpointField Transform(GeneratorAttributeSyntaxContext context, CheckpointGroup group)
    {
        ISymbol member = context.TargetSymbol;
        AttributeData attribute = context.Attributes[0];
        ITypeSymbol type = member is IPropertySymbol property ? property.Type : ((IFieldSymbol)member).Type;
        LocationInfo? location = LocationInfo.From(member.Locations.FirstOrDefault());
        string containing = member.ContainingType.ToDisplayString();

        if (group != CheckpointGroup.Chunk)
        {
            int order = attribute.ConstructorArguments.Length == 1 && attribute.ConstructorArguments[0].Value is int value ? value : 0;
            return new CheckpointField(
                group,
                member.Name,
                member.Name,
                order,
                WireType.From(type),
                type.ToDisplayString(),
                containing,
                location);
        }

        string liveMember = attribute.ConstructorArguments.Length == 2
            ? attribute.ConstructorArguments[0].Value as string ?? member.Name
            : member.Name;

        int chunkOrder = attribute.ConstructorArguments.Length == 2 && attribute.ConstructorArguments[1].Value is int chunkValue
            ? chunkValue
            : 0;

        // Chunk fields are IReadOnlyList<T> views over one value per voxel; the element type is what gets copied.
        WireType element = type is INamedTypeSymbol { Name: "IReadOnlyList", TypeArguments.Length: 1 } list
            ? WireType.From(list.TypeArguments[0])
            : new WireType(WireTypeKind.Other, type.ToDisplayString());

        return new CheckpointField(
            group,
            member.Name,
            liveMember,
            chunkOrder,
            element,
            type.ToDisplayString(),
            containing,
            location);
    }

    private static GeneratorOutput<CheckpointPlan> Plan(
        ImmutableArray<CheckpointField> fields,
        EquatableArray<QuantityAlias> aliases)
    {
        if (fields.IsEmpty)
            return GeneratorOutput<CheckpointPlan>.Empty;

        var diagnostics = new List<DiagnosticInfo>();

        EquatableArray<PlannedField> PlanGroup(CheckpointGroup group)
        {
            var planned = new List<(int Order, PlannedField Field)>();
            var orders = new HashSet<int>();
            foreach (CheckpointField field in fields.Where(field => field.Group == group))
            {
                if (!orders.Add(field.Order))
                {
                    diagnostics.Add(
                        DiagnosticInfo.Create(
                            ReplayDiagnostics.DuplicateCheckpointOrder,
                            field.Location,
                            field.ContainingType,
                            field.Order.ToString(CultureInfo.InvariantCulture)));
                }

                WireType type = field.Type.Resolve(aliases);
                bool supported = type.Kind switch
                {
                    WireTypeKind.Primitive => type.Name is "Int32" or "Single" ||
                                              group != CheckpointGroup.Chunk && type.Name == "Boolean",
                    WireTypeKind.String => group != CheckpointGroup.Chunk,
                    _ => false
                };

                // Config and gas fields also get generated equality, so a type has to be comparable as well.
                if (group != CheckpointGroup.Chunk && CompareScalar("a", "b", type) == null)
                    supported = false;

                if (!supported)
                {
                    diagnostics.Add(
                        DiagnosticInfo.Create(
                            ReplayDiagnostics.UnsupportedCheckpointField,
                            field.Location,
                            field.Member,
                            field.TypeDisplay));

                    continue;
                }

                planned.Add((field.Order, new PlannedField(field.Member, field.LiveMember, type)));
            }

            return planned.OrderBy(static entry => entry.Order).Select(static entry => entry.Field).ToEquatableArray();
        }

        var plan = new CheckpointPlan(
            PlanGroup(CheckpointGroup.Chunk),
            PlanGroup(CheckpointGroup.Config),
            PlanGroup(CheckpointGroup.Gas));

        return new GeneratorOutput<CheckpointPlan>(
            diagnostics.ToEquatableArray(),
            new EquatableArray<CheckpointPlan>(new[] { plan }));
    }

    private static (string HintName, string Source) Emit(CheckpointPlan plan)
    {
        var writer = new CodeWriter();
        writer.Header();
        writer.Line("namespace Numos.CoreSim.Replay");
        writer.Open();
        writer.Line("internal static class GeneratedCheckpointFields");
        writer.Open();

        if (plan.Chunk.Count > 0)
            EmitChunk(writer, plan.Chunk);

        if (plan.Config.Count > 0)
            EmitConfig(writer, plan.Config);

        if (plan.Gas.Count > 0)
            EmitGas(writer, plan.Gas);

        writer.Close();
        writer.Close();
        return ("Numos.CoreSim.Replay.GeneratedCheckpointFields.g.cs", writer.ToString());
    }

    private static void EmitChunk(CodeWriter writer, EquatableArray<PlannedField> fields)
    {
        string tuple = string.Join(
            ", ",
            fields.Select(static field =>
                $"global::System.Collections.Generic.IReadOnlyList<{field.Type.Keyword}> {field.Member}"));

        writer.Line($"internal static ({tuple}) CaptureChunkArrays(global::Numos.CoreSim.AtmosChunk chunk)");
        writer.Open();
        writer.Line("return (");
        for (int index = 0; index < fields.Count; index++)
        {
            writer.Line(
                $"    new global::Numos.CoreSim.Replay.CheckpointArray<{fields[index].Type.Keyword}>" +
                $"(chunk.{fields[index].LiveMember}.ToArray()){(index == fields.Count - 1 ? "" : ",")}");
        }

        writer.Line(");");
        writer.Close();
        writer.Line();

        writer.Line(
            "internal static void RestoreChunkArrays(global::Numos.CoreSim.AtmosChunk chunk, " +
            "global::Numos.CoreSim.Replay.AtmosChunkCheckpoint source)");

        writer.Open();
        foreach (PlannedField field in fields)
            writer.Line(
                $"global::Numos.CoreSim.Replay.CheckpointArray.CopyTo(source.{field.Member}, chunk.{field.LiveMember}.AsSpan());");

        writer.Close();
        writer.Line();

        writer.Line(
            "internal static void AppendChunkFields(ref global::Numos.CoreSim.Replay.AtmosStateHasher hash, " +
            "global::Numos.CoreSim.Replay.AtmosChunkCheckpoint chunk)");

        writer.Open();
        foreach (PlannedField field in fields)
            writer.Line($"foreach ({field.Type.Keyword} value in chunk.{field.Member}) hash.Add(value);");

        writer.Close();
        writer.Line();

        writer.Line(
            "internal static void WriteChunkFields(global::System.IO.BinaryWriter writer, " +
            "global::Numos.CoreSim.Replay.AtmosChunkCheckpoint chunk)");

        writer.Open();
        foreach (PlannedField field in fields)
            writer.Line($"{Codec}.WriteArray(writer, chunk.{field.Member});");

        writer.Close();
        writer.Line();

        string arrays = string.Join(", ", fields.Select(static field => $"{field.Type.Keyword}[] {field.Member}"));
        writer.Line($"internal static ({arrays}) ReadChunkFields(global::System.IO.BinaryReader reader, int voxels)");
        writer.Open();
        writer.Line("return (");
        for (int index = 0; index < fields.Count; index++)
        {
            writer.Line(
                $"    {Codec}.ReadArray<{fields[index].Type.Keyword}>(reader, voxels)" +
                (index == fields.Count - 1 ? "" : ","));
        }

        writer.Line(");");
        writer.Close();
        writer.Line();
    }

    private static void EmitConfig(CodeWriter writer, EquatableArray<PlannedField> fields)
    {
        writer.Line(
            "internal static void AppendConfigFields(ref global::Numos.CoreSim.Replay.AtmosStateHasher hash, " +
            "global::Numos.CoreSim.AtmosConfigSnapshot config)");

        writer.Open();
        foreach (PlannedField field in fields)
            writer.Line($"hash.Add(config.{field.Member});");

        writer.Close();
        writer.Line();

        writer.Line(
            "internal static void WriteConfigFields(global::System.IO.BinaryWriter writer, " +
            "global::Numos.CoreSim.AtmosConfigSnapshot config)");

        writer.Open();
        foreach (PlannedField field in fields)
            writer.Line(WriteScalar("config." + field.Member, field.Type));

        writer.Close();
        writer.Line();

        EmitEquality(writer, "ConfigFieldsEqual", "global::Numos.CoreSim.AtmosConfigSnapshot", fields);

        writer.Line(
            "internal static void ReadConfigFields(global::System.IO.BinaryReader reader, " +
            "global::Numos.CoreSim.AtmosConfig config, int maxStringBytes)");

        writer.Open();
        foreach (PlannedField field in fields)
            writer.Line($"config.{field.Member} = {ReadScalar(field.Type)};");

        writer.Close();
        writer.Line();
    }

    private static void EmitGas(CodeWriter writer, EquatableArray<PlannedField> fields)
    {
        writer.Line(
            "internal static void AppendGasFields(ref global::Numos.CoreSim.Replay.AtmosStateHasher hash, " +
            "global::Numos.CoreSim.GasProperties gas)");

        writer.Open();
        foreach (PlannedField field in fields)
            writer.Line($"hash.Add(gas.{field.Member});");

        writer.Close();
        writer.Line();

        writer.Line(
            "internal static void WriteGasFields(global::System.IO.BinaryWriter writer, " +
            "global::Numos.CoreSim.GasProperties gas)");

        writer.Open();
        foreach (PlannedField field in fields)
            writer.Line(WriteScalar("gas." + field.Member, field.Type));

        writer.Close();
        writer.Line();

        EmitEquality(writer, "GasFieldsEqual", "global::Numos.CoreSim.GasProperties", fields);

        writer.Line(
            "internal static global::Numos.CoreSim.GasProperties ReadGasFields(" +
            "global::System.IO.BinaryReader reader, int maxStringBytes)");

        writer.Open();
        writer.Line("var gas = new global::Numos.CoreSim.GasProperties();");
        foreach (PlannedField field in fields)
            writer.Line($"gas.{field.Member} = {ReadScalar(field.Type)};");

        writer.Line("return gas;");
        writer.Close();
    }

    private static void EmitEquality(CodeWriter writer, string name, string type, EquatableArray<PlannedField> fields)
    {
        writer.Line($"internal static bool {name}({type} left, {type} right)");
        writer.Open();
        writer.Line("return");
        for (int index = 0; index < fields.Count; index++)
        {
            string member = fields[index].Member;
            writer.Line(
                $"    {CompareScalar("left." + member, "right." + member, fields[index].Type)}" +
                (index == fields.Count - 1 ? ";" : " &&"));
        }

        writer.Close();
        writer.Line();
    }

    // Floats compare by bits, the same way AtmosStateHasher hashes them, so equal values always hash equal. That
    // makes 0f and -0f different and a NaN equal to itself (when the payloads match), unlike float.Equals.
    private static string? CompareScalar(string left, string right, WireType type)
    {
        return type.Kind switch
        {
            WireTypeKind.String => $"string.Equals({left}, {right}, global::System.StringComparison.Ordinal)",
            WireTypeKind.Primitive => type.Name switch
            {
                "Single" => $"global::System.BitConverter.SingleToUInt32Bits({left}) == " +
                            $"global::System.BitConverter.SingleToUInt32Bits({right})",
                "Double" => $"global::System.BitConverter.DoubleToUInt64Bits({left}) == " +
                            $"global::System.BitConverter.DoubleToUInt64Bits({right})",
                _ => $"{left} == {right}"
            },
            _ => null
        };
    }

    // Strings go through the nullable encoding so a scalar string field keeps the same layout whether or not it can
    // be null.
    private static string WriteScalar(string value, WireType type)
    {
        return type.Kind == WireTypeKind.String
            ? $"{Codec}.WriteNullableString(writer, {value});"
            : $"writer.Write({value});";
    }

    private static string ReadScalar(WireType type)
    {
        return type.Kind == WireTypeKind.String
            ? $"{Codec}.ReadNullableString(reader, maxStringBytes)!"
            : $"reader.Read{type.Name}()";
    }

    private enum CheckpointGroup
    {
        Chunk,
        Config,
        Gas
    }

    private sealed record CheckpointField(
        CheckpointGroup Group,
        string Member,
        string LiveMember,
        int Order,
        WireType Type,
        string TypeDisplay,
        string ContainingType,
        LocationInfo? Location);

    private sealed record PlannedField(string Member, string LiveMember, WireType Type);

    private sealed record CheckpointPlan(
        EquatableArray<PlannedField> Chunk,
        EquatableArray<PlannedField> Config,
        EquatableArray<PlannedField> Gas);
}