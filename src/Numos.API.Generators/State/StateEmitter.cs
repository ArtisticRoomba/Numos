using Numos.API.Generators.Common;

namespace Numos.API.Generators.State;

internal static class StateEmitter
{
    private const string StateInterface = "global::Numos.CoreSim.State.INumosState";
    private const string SolverConfiguration = "global::Numos.CoreSim.IAtmosSolverConfiguration";

    public static string Emit(StateTypeModel model)
    {
        string self = model.TypeName;
        string stateInterface = StateInterface + "<" + self + ">";
        var writer = new CodeWriter();
        writer.Header();
        writer.OpenNamespace(model.Type);
        writer.OpenTypeChain(model.Type, " : " + stateInterface);

        EmitWriteState(writer, model, stateInterface);
        writer.Line();
        EmitStateEquals(writer, model, stateInterface);
        writer.Line();
        EmitCompareState(writer, model, stateInterface);

        if (model.IsSolverConfiguration)
        {
            writer.Line();
            EmitSolverConfiguration(writer, model);
        }

        writer.CloseTypeChain(model.Type);
        writer.CloseNamespace(model.Type);
        return writer.ToString();
    }

    private static void EmitWriteState(CodeWriter writer, StateTypeModel model, string stateInterface)
    {
        writer.Line(CodeWriter.GeneratedCodeAttribute);
        writer.Line("static void " + stateInterface + ".WriteState<TSink>(ref TSink sink, " + model.TypeName + " value)");
        writer.Open();
        writer.Line("sink.BeginObject();");
        foreach (StateFieldModel field in model.Fields)
        {
            writer.Line("sink.BeginField(" + field.Id + ");");
            writer.Line(field.Shape.CodecExpression + ".Write(ref sink, value." + field.MemberName + ");");
            writer.Line("sink.EndField();");
        }

        writer.Line("sink.EndObject();");
        writer.Close();
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
        writer.Line("       global::Numos.CoreSim.State.NumosState.StateEquals<" + model.TypeName + ">(this, typed);");
        writer.Close();
        writer.Line();
        writer.Line("/// <inheritdoc />");
        writer.Line(CodeWriter.GeneratedCodeAttribute);
        writer.Line("public ulong ComputeStateHash()");
        writer.Open();
        writer.Line("var sink = new global::Numos.CoreSim.State.StateHashSink();");
        writer.Line("sink.WriteString(((" + SolverConfiguration + ")this).Key);");
        writer.Line(
            "global::Numos.CoreSim.State.NumosState.Write<" + model.TypeName +
            ", global::Numos.CoreSim.State.StateHashSink>(ref sink, this);");

        writer.Line("return sink.Value;");
        writer.Close();
    }
}
