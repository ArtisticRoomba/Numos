using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Numos.API.Generators.Common;

namespace Numos.Replay.Generators;

/// <summary>
///     Generates <c>CaptureLiveArrays</c> and <c>RestoreLiveArrays</c> on the type that declares
///     <c>[LiveChunkArray]</c> properties (<c>AtmosChunkCheckpoint</c> in Numos.CoreSim).
/// </summary>
/// <remarks>
///     The state schema already generates the checkpoint's hash, equality, and wire code from its <c>[StateField]</c>
///     ids. This covers the one thing it can't know: which live <c>AtmosChunk</c> array each per-voxel property is
///     copied from and restored into. Each property's attribute names its live member, so the mapping is declared once.
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class ChunkArrayGenerator : IIncrementalGenerator
{
    private const string LiveChunkArrayAttribute = "Numos.CoreSim.Replay.LiveChunkArrayAttribute";

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValueProvider<ImmutableArray<LiveArray>> arrays = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                LiveChunkArrayAttribute,
                static (node, _) => node is PropertyDeclarationSyntax,
                static (syntaxContext, _) => Transform(syntaxContext))
            .Collect();

        GeneratorOutput.Register(context, arrays.Select(static (input, _) => Plan(input)), "LiveChunkArrays", Emit);
    }

    private static LiveArray Transform(GeneratorAttributeSyntaxContext context)
    {
        var property = (IPropertySymbol)context.TargetSymbol;
        AttributeData attribute = context.Attributes[0];
        string liveMember = attribute.ConstructorArguments.Length == 1 && attribute.ConstructorArguments[0].Value is string name
            ? name
            : property.Name;

        // Only a whole per-voxel array with a setter can be captured into: IReadOnlyList<T> over one value per voxel.
        bool supported = property.Type is INamedTypeSymbol { Name: "IReadOnlyList", TypeArguments.Length: 1 } &&
                         property.SetMethod is not null;

        return new LiveArray(
            PartialTypeModel.From(property.ContainingType),
            property.Name,
            liveMember,
            supported,
            property.Type.ToDisplayString(),
            LocationInfo.From(property.Locations.FirstOrDefault()));
    }

    private static GeneratorOutput<LiveArrayPlan> Plan(ImmutableArray<LiveArray> arrays)
    {
        if (arrays.IsEmpty)
            return GeneratorOutput<LiveArrayPlan>.Empty;

        var diagnostics = new List<DiagnosticInfo>();
        var plans = new List<LiveArrayPlan>();
        foreach (IGrouping<PartialTypeModel, LiveArray> type in arrays.GroupBy(static array => array.Type))
        {
            var members = new List<LiveArrayMember>();
            foreach (LiveArray array in type.OrderBy(static array => array.Member, StringComparer.Ordinal))
            {
                if (!array.Supported)
                {
                    diagnostics.Add(
                        DiagnosticInfo.Create(
                            ReplayDiagnostics.UnsupportedCheckpointField,
                            array.Location,
                            array.Member,
                            array.TypeDisplay));

                    continue;
                }

                members.Add(new LiveArrayMember(array.Member, array.LiveMember));
            }

            plans.Add(new LiveArrayPlan(type.Key, members.ToEquatableArray()));
        }

        return new GeneratorOutput<LiveArrayPlan>(diagnostics.ToEquatableArray(), plans.ToEquatableArray());
    }

    private static (string HintName, string Source) Emit(LiveArrayPlan plan)
    {
        var writer = new CodeWriter();
        writer.Header();
        writer.OpenNamespace(plan.Type);
        writer.OpenTypeChain(plan.Type);

        writer.Line(CodeWriter.GeneratedCodeAttribute);
        writer.Line("private partial void CaptureLiveArrays(global::Numos.CoreSim.AtmosChunk chunk)");
        writer.Open();
        foreach (LiveArrayMember member in plan.Members)
            writer.Line($"{member.Member} = chunk.{member.LiveMember}.ToArray();");

        writer.Close();
        writer.Line();

        writer.Line(CodeWriter.GeneratedCodeAttribute);
        writer.Line("private partial void RestoreLiveArrays(global::Numos.CoreSim.AtmosChunk chunk)");
        writer.Open();
        foreach (LiveArrayMember member in plan.Members)
        {
            writer.Line(
                $"global::Numos.CoreSim.Replay.CheckpointArray.CopyTo({member.Member}, chunk.{member.LiveMember}.AsSpan());");
        }

        writer.Close();

        writer.CloseTypeChain(plan.Type);
        writer.CloseNamespace(plan.Type);
        return (plan.Type.HintName + ".LiveChunkArrays.g.cs", writer.ToString());
    }

    private sealed record LiveArray(
        PartialTypeModel Type,
        string Member,
        string LiveMember,
        bool Supported,
        string TypeDisplay,
        LocationInfo? Location);

    private sealed record LiveArrayMember(string Member, string LiveMember);

    private sealed record LiveArrayPlan(PartialTypeModel Type, EquatableArray<LiveArrayMember> Members);
}
