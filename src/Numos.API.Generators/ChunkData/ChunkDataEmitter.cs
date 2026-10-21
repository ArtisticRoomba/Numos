using System.Globalization;
using Microsoft.CodeAnalysis.CSharp;
using Numos.API.Generators.Common;

namespace Numos.API.Generators.ChunkData;

/// <summary>
///     Writes the generated file for one type that declares <c>[ChunkData]</c> properties.
/// </summary>
internal static class ChunkDataEmitter
{
    internal const string ModuleInitializerHintName = "NumosChunkDataModuleInitializer.g.cs";

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
        // holder initializes on first access instead. It's internal only so the module initializer can run it early.
        writer.Line();
        writer.Line(CodeWriter.GeneratedCodeAttribute);
        writer.Line("[global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]");
        writer.Line("internal static class " + HolderName);
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
                (model.ClearOnSleep ? ", clearOnSleep: true" : "") +
                (model.ClearOnVacuum ? ", clearOnVacuum: true" : "") +
                ");");
        }

        writer.Close();
        writer.CloseTypeChain(type);
    }

    /// <summary>
    ///     Writes the assembly's module initializer, which registers every <c>[ChunkData]</c> field when the assembly
    ///     loads.
    /// </summary>
    /// <param name="typeDisplays">The fully qualified declaring types, in a stable order.</param>
    /// <remarks>
    ///     A file can't be loaded until each saved key in it is registered, and a fresh process may load one before
    ///     anything has read the properties. Running the holder's class constructor registers through the same
    ///     static initializer as the lazy path, so it happens once either way, and the declaring type's own static
    ///     constructor doesn't run early.
    /// </remarks>
    public static string EmitModuleInitializer(EquatableArray<string> typeDisplays)
    {
        var writer = new CodeWriter();
        writer.Header();
        writer.Line("namespace Numos.API.Generated");
        writer.Open();
        writer.Line(CodeWriter.GeneratedCodeAttribute);
        writer.Line("[global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]");
        writer.Line("internal static class ChunkDataModuleInitializer");
        writer.Open();
        writer.Line("[global::System.Runtime.CompilerServices.ModuleInitializer]");
        writer.Line(
            "[global::System.Diagnostics.CodeAnalysis.SuppressMessage(\"Usage\", \"CA2255\", " +
            "Justification = \"Saved chunk fields must be registered before a file that holds them can load.\")]");
        writer.Line("internal static void RegisterChunkFields()");
        writer.Open();
        foreach (string type in typeDisplays)
        {
            writer.Line(
                "Register(typeof(" + type + "." + HolderName + "), " +
                SymbolDisplay.FormatLiteral(type.Replace("global::", ""), true) + ");");
        }

        writer.Close();
        writer.Line();
        writer.Line("private static void Register(global::System.Type holder, string declaringType)");
        writer.Open();
        writer.Line("try");
        writer.Open();
        writer.Line("global::System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(holder.TypeHandle);");
        writer.Close();
        writer.Line("catch (global::System.TypeInitializationException exception) when (exception.InnerException is not null)");
        writer.Open();
        writer.Line("throw new global::System.InvalidOperationException(");
        writer.Line("    \"Registering the [ChunkData] fields of '\" + declaringType + \"' failed: \" + exception.InnerException.Message,");
        writer.Line("    exception.InnerException);");
        writer.Close();
        writer.Close();
        writer.Close();
        writer.Close();
        return writer.ToString();
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