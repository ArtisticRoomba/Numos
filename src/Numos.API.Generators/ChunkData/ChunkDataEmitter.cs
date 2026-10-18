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
    private const string FlatArrayType = "global::Numos.Collections.FlatArray";

    public static string Emit(ChunkDataTypeModel type)
    {
        ChunkDataPropertyModel first = type.Members[0].Property;
        var writer = new CodeWriter();
        writer.Header();
        writer.OpenNamespace(first.Type);

        WriteDeclaringType(writer, first.Type, type.Members.Select(static member => member.Property).ToList());

        var withAccessors = type.Members
            .Where(static member => member.HasAccessor)
            .Select(static member => member.Property)
            .ToList();

        if (withAccessors.Count > 0)
        {
            writer.Line();
            WriteAccessorClass(writer, first, withAccessors);
        }

        writer.CloseNamespace(first.Type);
        return writer.ToString();
    }

    private static void WriteDeclaringType(
        CodeWriter writer, PartialTypeModel type,
        IReadOnlyList<ChunkDataPropertyModel> members)
    {
        writer.OpenTypeChain(type);

        foreach (ChunkDataPropertyModel model in members)
        {
            writer.Line(
                model.PropertyModifiers +
                " partial " +
                model.PropertyTypeDisplay +
                " " +
                Escape(model.PropertyName) +
                " => " +
                HolderName +
                "." +
                Escape(model.PropertyName) +
                ";");
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
            writer.Line(
                "    " +
                fieldType +
                ".Register(" +
                SymbolDisplay.FormatLiteral(model.Key!, true) +
                ", rollback: " +
                (model.Rollback ? "true" : "false") +
                ", length: " +
                length +
                ");");
        }

        writer.Close();
        writer.CloseTypeChain(type);
    }

    private static void WriteAccessorClass(
        CodeWriter writer, ChunkDataPropertyModel first,
        List<ChunkDataPropertyModel> accessors)
    {
        bool isPublic = accessors.Any(static accessor => accessor.AccessorIsPublic);
        writer.Line("/// <summary>");
        writer.Line("///     Chunk view accessors for the fields declared on <see cref=\"" + first.TypeDisplay + "\" />.");
        writer.Line("/// </summary>");
        writer.Line(CodeWriter.GeneratedCodeAttribute);
        writer.Line((isPublic ? "public" : "internal") + " static partial class " + first.AccessorClassName);
        writer.Open();
        writer.Line("extension(" + SolverChunkType + " chunk)");
        writer.Open();
        for (int i = 0; i < accessors.Count; i++)
        {
            ChunkDataPropertyModel model = accessors[i];
            string field = model.TypeDisplay + "." + Escape(model.PropertyName);
            if (model.PropertyTypeIsAnnotated)
                field += "!";

            if (i > 0)
                writer.Line();

            writer.Line("/// <inheritdoc cref=\"" + model.TypeDisplay + "." + Escape(model.PropertyName) + "\" />");
            writer.Line(
                (model.AccessorIsPublic ? "public" : "internal") +
                " " +
                FlatArrayType +
                "<" +
                model.ElementTypeDisplay +
                "> " +
                Escape(model.AccessorName) +
                " => chunk.Get(" +
                field +
                ");");
        }

        writer.Close();
        writer.Close();
    }

    private static string Escape(string identifier)
    {
        return CodeWriter.Escape(identifier);
    }
}