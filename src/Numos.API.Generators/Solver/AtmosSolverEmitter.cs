using Microsoft.CodeAnalysis.CSharp;
using Numos.API.Generators.Common;

namespace Numos.API.Generators.Solver;

/// <summary>
///     Writes the descriptor partial for one <c>[AtmosSolver]</c> class, and the assembly's module initializer.
/// </summary>
internal static class AtmosSolverEmitter
{
    public const string ModuleInitializerHintName = "AtmosSolverModuleInitializer.g.cs";

    private const string FieldName = "__numosSolverDescriptor";
    private const string DescriptorType = "global::Numos.API.AtmosSolverDescriptor";
    private const string DefinitionType = "global::Numos.API.IAtmosSolverDefinition";

    public static string Emit(AtmosSolverModel model)
    {
        var writer = new CodeWriter();
        writer.Header();
        writer.OpenNamespace(model.Type);
        writer.OpenTypeChain(model.Type, " : " + DefinitionType + "<" + model.TypeDisplay + ">");

        writer.Line(CodeWriter.GeneratedCodeAttribute);
        writer.Line("[global::System.Diagnostics.DebuggerBrowsable(global::System.Diagnostics.DebuggerBrowsableState.Never)]");
        writer.Line("private static " + DescriptorType + "? " + FieldName + ";");
        writer.Line();
        writer.Line("/// <summary>");
        writer.Line("///     Gets the descriptor declared by this class's <c>[AtmosSolver]</c> attribute.");
        writer.Line("/// </summary>");
        writer.Line(CodeWriter.GeneratedCodeAttribute);
        writer.Line("public static " + (model.HidesInherited ? "new " : "") + DescriptorType + " Descriptor =>");
        writer.Line("    global::System.Threading.LazyInitializer.EnsureInitialized(");
        writer.Line("        ref " + FieldName + ",");
        writer.Line("        static () => new " + DescriptorType + "(");

        var arguments = new List<string>
        {
            Literal(model.Name),
            "typeof(" + model.TypeDisplay + ")",
            "static () => new " + model.TypeDisplay + "()",
        };

        if (model.After is not null)
            arguments.Add("after: " + Literal(model.After));

        if (model.Before is not null)
            arguments.Add("before: " + Literal(model.Before));

        if (model.RunsPerSecond != 0)
            arguments.Add("runsPerSecond: " + model.RunsPerSecond);

        if (model.Interval != 0)
            arguments.Add("interval: " + model.Interval);

        if (model.Neighbors is { } neighbors)
        {
            arguments.Add(
                "neighbors: new global::Numos.API.AtmosNeighborSelection(" +
                Literal(neighbors.Key) +
                ", " +
                (neighbors.IncludeCartesian ? "true" : "false") +
                ", " +
                neighbors.Selector +
                ")");
        }

        for (int i = 0; i < arguments.Count; i++)
            writer.Line("            " + arguments[i] + (i == arguments.Count - 1 ? "));" : ","));

        writer.CloseTypeChain(model.Type);
        writer.CloseNamespace(model.Type);
        return writer.ToString();
    }

    /// <summary>
    ///     Writes the assembly's module initializer, which adds every descriptor to <c>AtmosSolverCatalog</c> when the
    ///     assembly loads.
    /// </summary>
    /// <param name="typeDisplays">The fully qualified solver classes, in a stable order.</param>
    /// <remarks>
    ///     A name conflict with a solver in another loaded assembly throws from here, the first time code in this
    ///     assembly runs.
    /// </remarks>
    public static string EmitModuleInitializer(EquatableArray<string> typeDisplays)
    {
        var writer = new CodeWriter();
        writer.Header();
        writer.OpenModuleInitializer(
            "Numos.API.Generated",
            "AtmosSolverModuleInitializer",
            "RegisterSolvers",
            "Solver descriptors must be in the catalog before a host looks a solver up by name.");

        foreach (string type in typeDisplays)
            writer.Line("global::Numos.API.AtmosSolverCatalog.Register(" + type + ".Descriptor);");

        writer.Close();
        writer.Close();
        writer.Close();
        return writer.ToString();
    }

    private static string Literal(string value)
    {
        return SymbolDisplay.FormatLiteral(value, true);
    }
}
