using Microsoft.CodeAnalysis.CSharp;
using Numos.API.Generators.Common;

namespace Numos.API.Generators.State;

internal static class StateEmitter
{
    private const string StateInterface = "global::Numos.CoreSim.State.Advanced.INumosState";
    private const string SolverConfiguration = "global::Numos.CoreSim.IAtmosSolverConfiguration";
    private const string Advanced = "global::Numos.CoreSim.State.Advanced.";
    private const string TargetInterface = "global::Numos.CoreSim.State.Advanced.INumosStateTarget";

    public static string Emit(StateTypeModel model)
    {
        if (model.IsWorldSolver)
            return EmitWorldSolver(model);

        string self = model.TypeName;
        string stateInterface = StateInterface + "<" + self + ">";
        var writer = new CodeWriter();
        writer.Header();
        writer.OpenNamespace(model.Type);
        writer.OpenTypeChain(model.Type, " : " + stateInterface);

        EmitWalker(writer, model, stateInterface, walker => EmitReadState(walker, model, stateInterface));

        if (model.IsSolverConfiguration)
        {
            writer.Line();
            EmitSolverConfiguration(writer, model);
        }

        writer.CloseTypeChain(model.Type);
        writer.CloseNamespace(model.Type);
        return writer.ToString();
    }

    /// <summary>
    ///     Writes the members <c>INumosState&lt;T&gt;</c> needs, in a fixed order with a blank line between them.
    /// </summary>
    /// <param name="writer">The writer, inside the open type.</param>
    /// <param name="model">The type whose fields the members cover.</param>
    /// <param name="stateInterface">The interface to implement, with its type argument.</param>
    /// <param name="emitReadState">Writes <c>ReadState</c>, which a snapshot builds differently.</param>
    internal static void EmitWalker(
        CodeWriter writer, StateTypeModel model, string stateInterface, Action<CodeWriter> emitReadState)
    {
        EmitStateKey(writer, model, stateInterface);
        writer.Line();
        EmitWriteState(writer, model, stateInterface);
        writer.Line();
        EmitInstanceWriteState(writer, model);
        writer.Line();
        emitReadState(writer);
        writer.Line();
        EmitStateEquals(writer, model, stateInterface);
        writer.Line();
        EmitCompareState(writer, model, stateInterface);
    }

    /// <summary>
    ///     Emits a class solver's state members. A solver is never constructed from data, so instead of
    ///     <c>INumosState&lt;T&gt;</c> it gets an instance writer and an in-place reader.
    /// </summary>
    private static string EmitWorldSolver(StateTypeModel model)
    {
        var writer = new CodeWriter();
        writer.Header();
        writer.OpenNamespace(model.Type);
        writer.OpenTypeChain(model.Type, " : " + TargetInterface);

        writer.Line(CodeWriter.GeneratedCodeAttribute);
        writer.Line("void " + StateInterface + ".WriteState<TSink>(ref TSink sink)");
        writer.Open();
        EmitFieldWrites(writer, model, "this");
        writer.Close();
        writer.Line();
        writer.Line(CodeWriter.GeneratedCodeAttribute);
        writer.Line(
            "void " + TargetInterface +
            ".ReadStateInto(ref global::Numos.CoreSim.State.Advanced.StateReader reader, global::Numos.CoreSim.IGasRegistry? gases)");

        writer.Open();
        EmitReadFields(writer, model);

        // Assigned only once everything decoded, so a bad payload can't leave the solver half restored.
        foreach (StateFieldModel field in model.Fields)
        {
            writer.Line("if (has" + field.Id + ")");
            writer.Line("    this." + field.MemberName + " = field" + field.Id + ";");
        }

        writer.Close();
        writer.CloseTypeChain(model.Type);
        writer.CloseNamespace(model.Type);
        return writer.ToString();
    }

    private static void EmitWriteState(CodeWriter writer, StateTypeModel model, string stateInterface)
    {
        writer.Line(CodeWriter.GeneratedCodeAttribute);
        writer.Line("static void " + stateInterface + ".WriteState<TSink>(ref TSink sink, " + model.TypeName + " value)");
        writer.Open();
        EmitFieldWrites(writer, model, "value");
        writer.Close();
    }

    /// <summary>
    ///     Writes the statements that put each field of <paramref name="receiver" /> into <c>sink</c> as one object.
    /// </summary>
    private static void EmitFieldWrites(CodeWriter writer, StateTypeModel model, string receiver)
    {
        writer.Line("sink.BeginObject();");
        foreach (StateFieldModel field in model.Fields)
        {
            writer.Line("sink.BeginField(" + field.Id + ");");
            writer.Line(field.Shape.CodecExpression + ".Write(ref sink, " + receiver + "." + field.MemberName + ");");
            writer.Line("sink.EndField();");
        }

        writer.Line("sink.EndObject();");
    }

    private static void EmitStateKey(CodeWriter writer, StateTypeModel model, string stateInterface)
    {
        string key = model.StateKey is null ? "null" : SymbolDisplay.FormatLiteral(model.StateKey, true);
        writer.Line(CodeWriter.GeneratedCodeAttribute);
        writer.Line("static string? " + stateInterface + ".StateKey => " + key + ";");
    }

    private static void EmitInstanceWriteState(CodeWriter writer, StateTypeModel model)
    {
        writer.Line(CodeWriter.GeneratedCodeAttribute);
        writer.Line("void " + StateInterface + ".WriteState<TSink>(ref TSink sink)");
        writer.Open();
        writer.Line("global::Numos.CoreSim.State.Advanced.NumosState.Write<" + model.TypeName + ", TSink>(ref sink, this);");
        writer.Close();
    }

    private static void EmitReadState(CodeWriter writer, StateTypeModel model, string stateInterface)
    {
        writer.Line(CodeWriter.GeneratedCodeAttribute);
        writer.Line(
            "static " + model.TypeName + " " + stateInterface +
            ".ReadState(ref global::Numos.CoreSim.State.Advanced.StateReader reader, global::Numos.CoreSim.IGasRegistry? gases)");

        writer.Open();
        EmitReadFields(writer, model);
        StateReadPlan plan = model.Read;
        StateInitializerModel[] inline = plan.Initializers.Where(static initializer => !initializer.AfterConstruction).ToArray();
        StateInitializerModel[] after = plan.Initializers.Where(static initializer => initializer.AfterConstruction).ToArray();
        string construction = (after.Length == 0 ? "return new " : "var result = new ") + model.TypeName + "(" +
                              string.Join(", ", plan.Arguments) + ")";

        if (inline.Length == 0)
        {
            writer.Line(construction + ";");
        }
        else
        {
            writer.Line(construction);
            writer.Open();
            foreach (StateInitializerModel initializer in inline)
                writer.Line(initializer.MemberName + " = " + initializer.Expression + ",");

            writer.Close(";");
        }

        if (after.Length > 0)
        {
            foreach (StateInitializerModel initializer in after)
            {
                writer.Line("if (has" + initializer.Id + ")");
                writer.Line("    result." + initializer.MemberName + " = " + initializer.Expression + ";");
            }

            writer.Line("return result;");
        }

        writer.Close();
    }

    /// <summary>
    ///     Writes the <c>field{id}</c>/<c>has{id}</c> locals and the loop that fills them from <c>reader</c>, ending
    ///     with the object closed.
    /// </summary>
    internal static void EmitReadFields(CodeWriter writer, StateTypeModel model)
    {
        foreach (StateFieldModel field in model.Fields)
        {
            writer.Line(field.Shape.TypeExpression + " field" + field.Id + " = default!;");
            writer.Line("bool has" + field.Id + " = false;");
        }

        if (model.Fields.Count > 0)
            writer.Line();

        writer.Line("reader.BeginObject();");
        if (model.Fields.Count == 0)
        {
            writer.Line("while (reader.TryReadField(out _))");
            writer.Line("    reader.SkipField();");
        }
        else
        {
            writer.Line("while (reader.TryReadField(out int id))");
            writer.Open();
            writer.Line("switch (id)");
            writer.Open();
            foreach (StateFieldModel field in model.Fields)
            {
                writer.Line("case " + field.Id + ":");
                // A custom codec may be declared over the nullable form of the member's type.
                string forgive = field.Shape.Kind == StateShapeKind.Custom ? "!" : "";
                writer.Line("    field" + field.Id + " = " + field.Shape.CodecExpression + ".Read(ref reader, gases)" + forgive + ";");
                writer.Line("    has" + field.Id + " = true;");
                writer.Line("    break;");
            }

            // Ids from a newer version of the type. Skipping them lets old readers load new data.
            writer.Line("default:");
            writer.Line("    reader.SkipField();");
            writer.Line("    continue;");
            writer.Close();
            writer.Line();
            writer.Line("reader.EndField();");
            writer.Close();
        }

        writer.Line();
        writer.Line("reader.EndObject();");
    }

    private static void EmitStateEquals(CodeWriter writer, StateTypeModel model, string stateInterface)
    {
        writer.Line(CodeWriter.GeneratedCodeAttribute);
        writer.Line("static bool " + stateInterface + ".StateEquals(" + model.TypeName + " left, " + model.TypeName + " right)");
        writer.Open();
        if (model.IsReferenceType)
        {
            writer.Line("if (global::System.Object.ReferenceEquals(left, right))");
            writer.Line("    return true;");
            writer.Line();
            writer.Line("if (left is null || right is null)");
            writer.Line("    return false;");
            writer.Line();
        }

        if (model.Fields.Count == 0)
        {
            writer.Line("return true;");
            writer.Close();
            return;
        }

        for (int i = 0; i < model.Fields.Count; i++)
        {
            StateFieldModel field = model.Fields[i];
            string comparison = field.Shape.CodecExpression + ".StateEquals(left." + field.MemberName + ", right." +
                                field.MemberName + ")";

            string prefix = i == 0 ? "return " : "    && ";
            string suffix = i == model.Fields.Count - 1 ? ";" : "";
            writer.Line(prefix + comparison + suffix);
        }

        writer.Close();
    }

    private static void EmitCompareState(CodeWriter writer, StateTypeModel model, string stateInterface)
    {
        writer.Line(CodeWriter.GeneratedCodeAttribute);
        writer.Line("static int " + stateInterface + ".CompareState(" + model.TypeName + " left, " + model.TypeName + " right)");
        writer.Open();
        if (model.IsReferenceType)
        {
            writer.Line("if (global::System.Object.ReferenceEquals(left, right))");
            writer.Line("    return 0;");
            writer.Line();
            writer.Line("if (left is null || right is null)");
            writer.Line("    return left is null ? -1 : 1;");
            writer.Line();
        }

        if (model.Fields.Count > 0)
            writer.Line("int result;");

        foreach (StateFieldModel field in model.Fields)
        {
            writer.Line("result = " + field.Shape.CodecExpression + ".Compare(left." + field.MemberName + ", right." +
                        field.MemberName + ");");

            writer.Line("if (result != 0)");
            writer.Line("    return result;");
            writer.Line();
        }

        writer.Line("return 0;");
        writer.Close();
    }

    private static void EmitSolverConfiguration(CodeWriter writer, StateTypeModel model)
    {
        writer.Line("/// <inheritdoc />");
        writer.Line(CodeWriter.GeneratedCodeAttribute);
        writer.Line("public bool SemanticallyEquals(" + SolverConfiguration + " other)");
        writer.Open();
        writer.Line("return other is " + model.TypeName + " typed &&");
        writer.Line("       " + Advanced + "NumosState.StateEquals<" + model.TypeName + ">(this, typed);");
        writer.Close();
        writer.Line();
        writer.Line("/// <inheritdoc />");
        writer.Line(CodeWriter.GeneratedCodeAttribute);
        writer.Line("public ulong ComputeStateHash()");
        writer.Open();
        writer.Line("var sink = new " + Advanced + "StateHashSink();");
        writer.Line("sink.WriteString(((" + SolverConfiguration + ")this).Key);");
        writer.Line(Advanced + "NumosState.Write<" + model.TypeName + ", " + Advanced + "StateHashSink>(ref sink, this);");
        writer.Line("return sink.Value;");
        writer.Close();

        if (model.Snapshot == SnapshotKind.None)
            return;

        writer.Line();
        writer.Line("/// <summary>");
        writer.Line("///     Runs on the snapshot <see cref=\"CreateSnapshot\" /> returns, for validation against the gas registry.");
        writer.Line("/// </summary>");
        writer.Line("/// <param name=\"gases\">The registry the snapshot is for.</param>");
        writer.Line("partial void OnSnapshot(global::Numos.CoreSim.IGasRegistry gases);");
        writer.Line();
        writer.Line("/// <inheritdoc />");
        writer.Line(CodeWriter.GeneratedCodeAttribute);
        writer.Line("public " + SolverConfiguration + " CreateSnapshot(global::Numos.CoreSim.IGasRegistry gases)");
        writer.Open();
        writer.Line("global::System.ArgumentNullException.ThrowIfNull(gases);");
        if (model.Snapshot == SnapshotKind.Self)
        {
            // Every member is immutable, so sharing the instance can't let a later edit reach the snapshot.
            writer.Line("OnSnapshot(gases);");
            writer.Line("return this;");
        }
        else
        {
            // A round trip copies every collection and resolves each gas against the registry being captured for.
            writer.Line("var sink = new " + Advanced + "StateBinarySink();");
            writer.Line(Advanced + "NumosState.Write<" + model.TypeName + ", " + Advanced + "StateBinarySink>(ref sink, this);");
            writer.Line("var reader = new " + Advanced + "StateReader(sink.WrittenSpan);");
            writer.Line("var snapshot = " + Advanced + "NumosState.Read<" + model.TypeName + ">(ref reader, gases);");
            writer.Line("snapshot.OnSnapshot(gases);");
            writer.Line("return snapshot;");
        }

        writer.Close();
    }

    /// <summary>
    ///     Writes the assembly's module initializer, which registers every solver configuration it can name so a world
    ///     archive holding one loads without the host registering anything.
    /// </summary>
    /// <param name="types">The fully qualified solver configuration types, in a stable order.</param>
    public static string EmitModuleInitializer(EquatableArray<string> types)
    {
        var writer = new CodeWriter();
        writer.Header();
        writer.OpenModuleInitializer(
            "Numos.CoreSim.State.Generated",
            "SolverConfigurationModuleInitializer",
            "RegisterSolverConfigurations",
            "Solver configurations must be registered before a file that holds them can load.");

        foreach (string type in types)
            writer.Line("global::Numos.CoreSim.State.SolverConfigurationTypes.RegisterDefault<" + type + ">();");

        writer.Close();
        writer.Close();
        writer.Close();
        return writer.ToString();
    }
}
