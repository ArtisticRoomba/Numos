using System.Globalization;
using Microsoft.CodeAnalysis.CSharp;
using Numos.API.Generators.Common;

namespace Numos.API.Generators.ChunkData;

/// <summary>
///     Writes the generated file for one type that declares <c>[ChunkData]</c> properties.
/// </summary>
internal static class ChunkDataEmitter
{
    private const string HolderName = "__NumosChunkFields";
    private const string SolverChunkType = "global::Numos.API.AtmosSolverChunk";
    private const string DangerousChunkType = "global::Numos.API.Dangerous.AtmosDangerousChunk";
    private const string FlatArrayType = "global::Numos.Collections.FlatArray";
    private const string SpanType = "global::System.Span";

    public static string Emit(ChunkDataTypeModel type)
    {
        ChunkDataPropertyModel first = type.Members[0].Property;
        var writer = new CodeWriter();
        writer.Header();
        writer.OpenNamespace(first.Type);

        WriteDeclaringType(writer, first.Type, type.Members.Select(static member => member.Property).ToList());

        var withAccessors = type.Members
            .Where(static member => member.Accessor is not null)
            .Select(static member => (Model: member.Property, Targets: member.Accessor!.Value))
            .ToList();

        if (withAccessors.Count > 0)
        {
            writer.Line();
            WriteAccessorClass(writer, first, withAccessors, type.UseExtensionBlocks, type.HasDangerous);
        }

        writer.CloseNamespace(first.Type);
        return writer.ToString();
    }

    private static void WriteDeclaringType(CodeWriter writer, PartialTypeModel type,
        IReadOnlyList<ChunkDataPropertyModel> members)
    {
        writer.OpenTypeChain(type);

        foreach (ChunkDataPropertyModel model in members)
        {
            writer.Line(model.PropertyModifiers + " partial " + model.PropertyTypeDisplay + " " +
                        Escape(model.PropertyName) + " => " + HolderName + "." + Escape(model.PropertyName) + ";");
        }

        // Static initializer order across partial declarations is unspecified, so a backing field on the declaring
        // type could still be null when one of the user's own static initializers reads the property. The nested
        // holder initializes on first access instead.
        writer.Line();
        writer.Line(CodeWriter.GeneratedCodeAttribute);
        writer.Line("private static class " + HolderName);
        writer.Open();
        for (int i = 0; i < members.Count; i++)
        {
            ChunkDataPropertyModel model = members[i];
            string fieldType = "global::Numos.API.ChunkField<" + model.ElementTypeDisplay + ">";
            string length = model.Length?.ToString(CultureInfo.InvariantCulture) ?? "null";
            if (i > 0)
                writer.Line();
            writer.Line("internal static readonly " + fieldType + " " + Escape(model.PropertyName) + " =");
            writer.Line("    " + fieldType + ".Register(" + SymbolDisplay.FormatLiteral(model.Key!, true) +
                        ", rollback: " + (model.Rollback ? "true" : "false") + ", length: " + length + ");");
        }

        writer.Close();
        writer.CloseTypeChain(type);
    }

    private static void WriteAccessorClass(CodeWriter writer, ChunkDataPropertyModel first,
        List<(ChunkDataPropertyModel Model, AccessorTargets Targets)> accessors, bool useExtensionBlocks,
        bool hasDangerous)
    {
        bool isPublic = accessors.Any(static accessor => accessor.Model.AccessorIsPublic);
        writer.Line("/// <summary>");
        writer.Line("///     Chunk view accessors for the fields declared on <see cref=\"" + first.TypeDisplay + "\" />.");
        writer.Line("/// </summary>");
        writer.Line(CodeWriter.GeneratedCodeAttribute);
        writer.Line((isPublic ? "public" : "internal") + " static partial class " + first.AccessorClassName);
        writer.Open();

        if (useExtensionBlocks)
        {
            WriteExtensionBlock(writer, SolverChunkType,
                accessors.Where(static accessor => accessor.Targets.OnSolverChunk).ToList());

            if (hasDangerous)
            {
                var dangerous = accessors.Where(static accessor => accessor.Targets.OnDangerousChunk).ToList();
                if (dangerous.Count > 0)
                {
                    writer.Line();
                    WriteExtensionBlock(writer, DangerousChunkType, dangerous);
                }
            }
        }
        else
        {
            bool firstMethod = true;
            foreach ((ChunkDataPropertyModel model, AccessorTargets targets) in accessors)
            {
                if (targets.OnSolverChunk)
                    WriteExtensionMethod(writer, SolverChunkType, model, targets, ref firstMethod);

                if (hasDangerous && targets.OnDangerousChunk)
                    WriteExtensionMethod(writer, DangerousChunkType, model, targets, ref firstMethod);
            }
        }

        writer.Close();
    }

    private static void WriteExtensionBlock(CodeWriter writer, string receiverType,
        List<(ChunkDataPropertyModel Model, AccessorTargets Targets)> accessors)
    {
        if (accessors.Count == 0)
            return;

        writer.Line("extension(" + receiverType + " chunk)");
        writer.Open();
        for (int i = 0; i < accessors.Count; i++)
        {
            (ChunkDataPropertyModel model, AccessorTargets targets) = accessors[i];
            if (i > 0)
                writer.Line();
            writer.Line("/// <inheritdoc cref=\"" + model.TypeDisplay + "." + Escape(model.PropertyName) + "\" />");
            writer.Line(Accessibility(model) + " " + ReturnType(model) + " " + Escape(targets.MemberName) + " => " +
                        ReadExpression(model) + ";");
        }

        writer.Close();
    }

    private static void WriteExtensionMethod(CodeWriter writer, string receiverType, ChunkDataPropertyModel model,
        AccessorTargets targets, ref bool firstMethod)
    {
        if (!firstMethod)
            writer.Line();
        firstMethod = false;

        writer.Line("/// <inheritdoc cref=\"" + model.TypeDisplay + "." + Escape(model.PropertyName) + "\" />");
        writer.Line(Accessibility(model) + " static " + ReturnType(model) + " " + Escape(targets.MemberName) +
                    "(this " + receiverType + " chunk) => " + ReadExpression(model) + ";");
    }

    private static string Accessibility(ChunkDataPropertyModel model)
    {
        return model.AccessorIsPublic ? "public" : "internal";
    }

    private static string ReturnType(ChunkDataPropertyModel model)
    {
        string container = model.Length is null ? FlatArrayType : SpanType;
        return container + "<" + model.ElementTypeDisplay + ">";
    }

    private static string ReadExpression(ChunkDataPropertyModel model)
    {
        string method = model.Length is null ? "Get" : "GetSpan";
        string field = model.TypeDisplay + "." + Escape(model.PropertyName);
        if (model.PropertyTypeIsAnnotated)
            field += "!";
        return "chunk." + method + "(" + field + ")";
    }

    private static string Escape(string identifier)
    {
        return CodeWriter.Escape(identifier);
    }
}
