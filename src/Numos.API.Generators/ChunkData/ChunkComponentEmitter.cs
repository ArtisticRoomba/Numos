using System.Globalization;
using Microsoft.CodeAnalysis.CSharp;
using Numos.API.Generators.Common;

namespace Numos.API.Generators.ChunkData;

/// <summary>
///     Writes the generated file for one <c>[ChunkComponent]</c> struct.
/// </summary>
internal static class ChunkComponentEmitter
{
    /// <summary>
    ///     The nested class that registers the fields. The module initializer runs its static initializer.
    /// </summary>
    internal const string HolderName = "Fields";

    /// <summary>
    ///     The nested view type.
    /// </summary>
    internal const string ViewName = "View";
    private const string SolverChunkType = "global::Numos.API.AtmosSolverChunk";
    private const string SolverChunksType = "global::Numos.API.AtmosSolverChunks";
    private const string GasMixturePageType = "global::Numos.API.AtmosSolverGasMixturePage";

    public static string Emit(ChunkComponentModel component)
    {
        var writer = new CodeWriter();
        writer.Header();
        writer.OpenNamespace(component.Type);
        writer.OpenTypeChain(component.Type);

        WriteFields(writer, component);
        writer.Line();
        WriteView(writer, component);

        writer.CloseTypeChain(component.Type);

        if (component.HasAccessors)
        {
            writer.Line();
            WriteAccessorClass(writer, component);
        }

        writer.CloseNamespace(component.Type);
        return writer.ToString();
    }

    private static void WriteFields(CodeWriter writer, ChunkComponentModel component)
    {
        // Every initializer sits in this one generated class, so they run in declaration order on first access, and
        // nothing in the user's own declarations can observe a field before it is registered.
        writer.Line("/// <summary>");
        writer.Line("///     The registered chunk fields of <see cref=\"" + component.TypeDisplay + "\" />, one per component field.");
        writer.Line("/// </summary>");
        writer.Line("/// <remarks>");
        writer.Line("///     Host code passes these to <see cref=\"global::Numos.API.AtmosSimulation.TryGetChunkField{T}\" /> and to the");
        writer.Line("///     recorded chunk data calls such as <see cref=\"global::Numos.API.AtmosSimulation.SetChunkData{T}\" />.");
        writer.Line("/// </remarks>");
        writer.Line(CodeWriter.GeneratedCodeAttribute);
        writer.Line("public static class " + HolderName);
        writer.Open();
        for (int i = 0; i < component.Fields.Count; i++)
        {
            ChunkComponentFieldModel field = component.Fields[i];
            string fieldType = "global::Numos.API.ChunkField<" + field.ElementTypeDisplay + ">";
            string length = field.Length?.ToString(CultureInfo.InvariantCulture) ?? "null";
            if (i > 0)
                writer.Line();

            writer.Line("/// <summary>");
            writer.Line("///     Gets the chunk field that stores <see cref=\"" + FieldCref(component, field) + "\" />.");
            writer.Line("/// </summary>");
            writer.Line("public static " + fieldType + " " + CodeWriter.Escape(field.Name) + " { get; } =");
            writer.Line(
                "    " +
                fieldType +
                ".Register(" +
                SymbolDisplay.FormatLiteral(field.Key, true) +
                ", rollback: " +
                (field.Scratch ? "false" : "true") +
                ", length: " +
                length +
                (component.ClearOnSleep ? ", clearOnSleep: true" : "") +
                (component.ClearOnVacuum ? ", clearOnVacuum: true" : "") +
                ");");
        }

        writer.Close();
    }

    private static void WriteView(CodeWriter writer, ChunkComponentModel component)
    {
        writer.Line("/// <summary>");
        writer.Line("///     The storage of every field of <see cref=\"" + component.TypeDisplay + "\" /> in one chunk or mixture page.");
        writer.Line("/// </summary>");
        writer.Line("/// <remarks>");
        writer.Line("///     The spans are live, so writes land in the chunk's storage. They stay valid only until the tick that produced");
        writer.Line("///     the view ends. A per-voxel field has one value per voxel, or one per cell on a mixture page.");
        writer.Line("/// </remarks>");
        writer.Line(CodeWriter.GeneratedCodeAttribute);
        writer.Line("public readonly ref struct " + ViewName);
        writer.Open();
        WriteViewConstructor(writer, component, SolverChunkType, "chunk");
        writer.Line();
        WriteViewConstructor(writer, component, GasMixturePageType, "page");

        foreach (ChunkComponentFieldModel field in component.Fields)
        {
            writer.Line();
            writer.Line("/// <summary>");
            writer.Line(
                "///     Gets the values of <see cref=\"" +
                FieldCref(component, field) +
                "\" />, " +
                (field.Length is { } length
                    ? (length == 1 ? "one" : length.ToString(CultureInfo.InvariantCulture)) + " per chunk."
                    : "one per voxel."));
            writer.Line("/// </summary>");
            writer.Line("public global::System.Span<" + field.ElementTypeDisplay + "> " + CodeWriter.Escape(field.Name) + " { get; }");
        }

        writer.Close();
    }

    private static void WriteViewConstructor(
        CodeWriter writer, ChunkComponentModel component,
        string sourceType, string sourceName)
    {
        writer.Line("internal " + ViewName + "(" + sourceType + " " + sourceName + ")");
        writer.Open();
        foreach (ChunkComponentFieldModel field in component.Fields)
        {
            string name = CodeWriter.Escape(field.Name);
            writer.Line(
                "this." + name + " = " + sourceName + ".Get(" + FieldReference(component, field) + ").AsSpan();");
        }

        writer.Close();
    }

    private static void WriteAccessorClass(CodeWriter writer, ChunkComponentModel component)
    {
        string name = component.AccessorName;
        string view = component.TypeDisplay + "." + ViewName;

        // A restore keeps saved storage and drops scratch storage, so only a saved field gives Has a stable answer. A
        // scratch field would hide every chunk with saved state until something calls Get.
        ChunkComponentFieldModel anchor =
            component.Fields.FirstOrDefault(static field => !field.Scratch) ?? component.Fields[0];
        string first = FieldReference(component, anchor);
        string firstCref = FieldCref(component, anchor);
        string enumerable = CodeWriter.Escape(name + "Chunks");
        string item = CodeWriter.Escape(name + "Chunk");
        string access = component.IsPublic ? "public" : "internal";

        writer.Line("/// <summary>");
        writer.Line("///     Chunk view accessors for the <see cref=\"" + component.TypeDisplay + "\" /> component.");
        writer.Line("/// </summary>");
        writer.Line(CodeWriter.GeneratedCodeAttribute);
        writer.Line(access + " static partial class " + component.AccessorClassName);
        writer.Open();

        WriteSourceAccessors(writer, SolverChunkType, "chunk", "chunk", name, view, first, firstCref);
        writer.Line();
        WriteSourceAccessors(writer, GasMixturePageType, "page", "mixture page", name, view, first, firstCref);
        writer.Line();

        writer.Line("extension(" + SolverChunksType + " chunks)");
        writer.Open();
        writer.Line("/// <summary>");
        writer.Line("///     Enumerates the chunks that have storage for <see cref=\"" + firstCref + "\" />, in grid order, with their views.");
        writer.Line("/// </summary>");
        writer.Line("/// <remarks>");
        writer.Line("///     A chunk without that storage is skipped and gains nothing. Each yielded view creates the storage of any other");
        writer.Line("///     field that the chunk lacks, as <c>Get" + name + "()</c> does.");
        writer.Line("/// </remarks>");
        writer.Line("public " + enumerable + " With" + name + "() => new(chunks);");
        writer.Close();

        writer.Line();
        WriteEnumerable(writer, enumerable, item, view, first);
        writer.Line();
        WriteItem(writer, item, name, view);

        writer.Close();
    }

    private static void WriteSourceAccessors(
        CodeWriter writer, string sourceType, string sourceName,
        string noun, string name, string view, string first, string firstCref)
    {
        writer.Line("extension(" + sourceType + " " + sourceName + ")");
        writer.Open();

        writer.Line("/// <summary>");
        writer.Line("///     Gets the " + noun + "'s storage for every field of the component, creating the storage of each field the " + noun + " lacks.");
        writer.Line("/// </summary>");
        writer.Line("public " + view + " Get" + name + "() => new(" + sourceName + ");");
        writer.Line();

        writer.Line("/// <summary>");
        writer.Line("///     Gets the " + noun + "'s storage for every field of the component if the " + noun + " has storage for");
        writer.Line("///     <see cref=\"" + firstCref + "\" />.");
        writer.Line("/// </summary>");
        writer.Line("/// <param name=\"view\">The view, or <see langword=\"default\" /> when this returns <see langword=\"false\" />.</param>");
        writer.Line("/// <returns><see langword=\"false\" /> when the " + noun + " has no storage for that field. Nothing is created then.</returns>");
        writer.Line("/// <remarks>");
        writer.Line("///     When it returns <see langword=\"true\" />, it creates the storage of any other field that the " + noun + " lacks.");
        writer.Line("/// </remarks>");
        writer.Line("public bool TryGet" + name + "(out " + view + " view)");
        writer.Open();
        writer.Line("if (!" + sourceName + ".Has(" + first + "))");
        writer.Open();
        writer.Line("view = default;");
        writer.Line("return false;");
        writer.Close();
        writer.Line();
        writer.Line("view = new " + view + "(" + sourceName + ");");
        writer.Line("return true;");
        writer.Close();
        writer.Line();

        writer.Line("/// <summary>");
        writer.Line("///     Gets whether the " + noun + " has storage for <see cref=\"" + firstCref + "\" />. Never creates storage.");
        writer.Line("/// </summary>");
        writer.Line("public bool Has" + name + " => " + sourceName + ".Has(" + first + ");");

        writer.Close();
    }

    private static void WriteEnumerable(CodeWriter writer, string enumerable, string item, string view, string first)
    {
        writer.Line("/// <summary>");
        writer.Line("///     The chunks of one simulation that have the component. Use it in a <see langword=\"foreach\" /> loop.");
        writer.Line("/// </summary>");
        writer.Line(CodeWriter.GeneratedCodeAttribute);
        writer.Line("public readonly struct " + enumerable);
        writer.Open();
        writer.Line("private readonly " + SolverChunksType + " _chunks;");
        writer.Line();
        writer.Line("internal " + enumerable + "(" + SolverChunksType + " chunks)");
        writer.Open();
        writer.Line("_chunks = chunks;");
        writer.Close();
        writer.Line();
        writer.Line("/// <summary>");
        writer.Line("///     Returns an allocation-free enumerator over the chunks.");
        writer.Line("/// </summary>");
        writer.Line("public Enumerator GetEnumerator() => new(_chunks);");
        writer.Line();
        writer.Line("/// <summary>");
        writer.Line("///     Steps through the chunks that have the component.");
        writer.Line("/// </summary>");
        writer.Line("public struct Enumerator");
        writer.Open();
        writer.Line("private readonly " + SolverChunksType + " _chunks;");
        writer.Line("private readonly int _count;");
        writer.Line("private int _index;");
        writer.Line("private " + SolverChunkType + " _current;");
        writer.Line();
        writer.Line("internal Enumerator(" + SolverChunksType + " chunks)");
        writer.Open();
        writer.Line("_chunks = chunks;");
        writer.Line("_count = chunks.Count;");
        writer.Line("_index = -1;");
        writer.Line("_current = default;");
        writer.Close();
        writer.Line();
        writer.Line("/// <summary>");
        writer.Line("///     Gets the chunk at the enumerator's position, with its view.");
        writer.Line("/// </summary>");
        writer.Line("public readonly " + item + " Current => new(_current, new " + view + "(_current));");
        writer.Line();
        writer.Line("/// <summary>");
        writer.Line("///     Advances to the next chunk that has the component.");
        writer.Line("/// </summary>");
        writer.Line("/// <returns><see langword=\"false\" /> after the last such chunk.</returns>");
        writer.Line("public bool MoveNext()");
        writer.Open();
        writer.Line("while (++_index < _count)");
        writer.Open();
        writer.Line(SolverChunkType + " chunk = _chunks[_index];");
        writer.Line("if (chunk.Has(" + first + "))");
        writer.Open();
        writer.Line("_current = chunk;");
        writer.Line("return true;");
        writer.Close();
        writer.Close();
        writer.Line();
        writer.Line("_current = default;");
        writer.Line("return false;");
        writer.Close();
        writer.Close();
        writer.Close();
    }

    private static void WriteItem(CodeWriter writer, string item, string name, string view)
    {
        string property = CodeWriter.Escape(name);
        writer.Line("/// <summary>");
        writer.Line("///     One chunk that has the component, with its view.");
        writer.Line("/// </summary>");
        writer.Line(CodeWriter.GeneratedCodeAttribute);
        writer.Line("public readonly ref struct " + item);
        writer.Open();
        writer.Line("internal " + item + "(" + SolverChunkType + " chunk, " + view + " view)");
        writer.Open();
        writer.Line("Chunk = chunk;");
        writer.Line("this." + property + " = view;");
        writer.Close();
        writer.Line();
        writer.Line("/// <summary>");
        writer.Line("///     Gets the chunk.");
        writer.Line("/// </summary>");
        writer.Line("public " + SolverChunkType + " Chunk { get; }");
        writer.Line();
        writer.Line("/// <summary>");
        writer.Line("///     Gets the chunk's storage for every field of the component.");
        writer.Line("/// </summary>");
        writer.Line("public " + view + " " + property + " { get; }");
        writer.Line();
        writer.Line("/// <summary>");
        writer.Line("///     Splits the item into the chunk and the view.");
        writer.Line("/// </summary>");
        writer.Line("/// <param name=\"chunk\">The chunk.</param>");
        writer.Line("/// <param name=\"view\">The chunk's storage for every field of the component.</param>");
        writer.Line("public void Deconstruct(out " + SolverChunkType + " chunk, out " + view + " view)");
        writer.Open();
        writer.Line("chunk = Chunk;");
        writer.Line("view = this." + property + ";");
        writer.Close();
        writer.Close();
    }

    private static string FieldReference(ChunkComponentModel component, ChunkComponentFieldModel field)
    {
        return component.TypeDisplay + "." + HolderName + "." + CodeWriter.Escape(field.Name);
    }

    private static string FieldCref(ChunkComponentModel component, ChunkComponentFieldModel field)
    {
        return component.TypeDisplay + "." + CodeWriter.Escape(field.Name);
    }
}
