using Numos.API.Generators.Common;

namespace Numos.API.Generators.State;

/// <summary>
///     Writes a snapshot's members, capture, restore, and state walker.
/// </summary>
/// <remarks>
///     The walker (write, read, equality, ordering) is <see cref="StateEmitter" />'s, over every schema field, so a
///     snapshot hashes and saves exactly like any other serializable type.
/// </remarks>
internal static class SnapshotEmitter
{
    private const string Advanced = "global::Numos.CoreSim.State.Advanced.";
    private const string StateArray = Advanced + "StateArray";
    private const string NeverBrowsable =
        "[global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]";

    public static (string HintName, string Source) Emit(SnapshotModel model)
    {
        string stateInterface = Advanced + "INumosState<" + model.TypeName + ">";
        string simpleName = model.Type.ContainingTypes[model.Type.ContainingTypes.Count - 1].Declaration;
        string live = model.LiveTypeName;

        var writer = new CodeWriter();
        writer.Header();
        writer.OpenNamespace(model.Type);
        writer.OpenTypeChain(model.Type, " : " + stateInterface);

        foreach (SnapshotMemberModel member in model.Members)
        {
            EmitMember(writer, model, member);
            writer.Line();
        }

        if (model.EmitConstructor)
        {
            writer.Line(CodeWriter.GeneratedCodeAttribute);
            writer.Line("private " + simpleName + "()");
            writer.Open();
            writer.Close();
            writer.Line();
        }

        EmitCapture(writer, model, live);
        writer.Line();
        if (model.Restorable)
        {
            EmitRestore(writer, model, live);
            writer.Line();
        }

        writer.Line("/// <summary>");
        writer.Line("///     Runs at the end of <see cref=\"Capture\" />, to fill members the snapshot declares itself.");
        writer.Line("/// </summary>");
        writer.Line("partial void OnCaptured(" + live + " live);");
        writer.Line();
        writer.Line("/// <summary>");
        writer.Line("///     Runs after the snapshot is read from bytes, to validate what was decoded.");
        writer.Line("/// </summary>");
        writer.Line("partial void OnDecoded();");
        writer.Line();

        StateEmitter.EmitStateKey(writer, model.State, stateInterface);
        writer.Line();
        StateEmitter.EmitWriteState(writer, model.State, stateInterface);
        writer.Line();
        StateEmitter.EmitInstanceWriteState(writer, model.State);
        writer.Line();
        EmitReadState(writer, model, stateInterface);
        writer.Line();
        StateEmitter.EmitStateEquals(writer, model.State, stateInterface);
        writer.Line();
        StateEmitter.EmitCompareState(writer, model.State, stateInterface);

        writer.CloseTypeChain(model.Type);
        writer.CloseNamespace(model.Type);
        return (model.Type.HintName + ".NumosSnapshot.g.cs", writer.ToString());
    }

    private static void EmitMember(CodeWriter writer, SnapshotModel model, SnapshotMemberModel member)
    {
        string type = member.Shape.TypeExpression;
        string backing = SnapshotGenerator.BackingField(member.Name);

        // Capture and the reader always assign the member; this only keeps the constructor cheap and non-null.
        string initializer = member.IsList
            ? " = " + StateArray + "<" + member.ElementType + ">.Empty"
            : member.Shape.Kind == StateShapeKind.String
                ? " = \"\""
                : member.IsReference
                    ? " = null!"
                    : "";

        writer.Line(CodeWriter.GeneratedCodeAttribute);
        writer.Line("private " + type + " " + backing + initializer + ";");
        writer.Line();
        if (member.DeclaredModifiers is { } modifiers)
        {
            // The snapshot's own partial declaration carries the visibility and docs.
            writer.Line(CodeWriter.GeneratedCodeAttribute);
            writer.Line((modifiers.Length > 0 ? modifiers + " " : "") + "partial " + type + " " + member.Name);
        }
        else
        {
            EmitDocumentation(writer, model, member);
            writer.Line(CodeWriter.GeneratedCodeAttribute);
            writer.Line(member.Accessibility + " " + type + " " + member.Name);
        }

        writer.Open();
        writer.Line("get => " + backing + ";");
        writer.Close();
    }

    /// <summary>
    ///     Gets the statement that stores <paramref name="value" /> in a generated member, wrapping a list so the
    ///     snapshot owns it.
    /// </summary>
    private static string Store(string target, SnapshotMemberModel member, string value)
    {
        string backing = target + "." + SnapshotGenerator.BackingField(member.Name);
        return member.IsList
            ? backing + " = " + StateArray + ".Wrap<" + member.ElementType + ">(" + value + ");"
            : backing + " = " + value + ";";
    }

    private static void EmitDocumentation(CodeWriter writer, SnapshotModel model, SnapshotMemberModel member)
    {
        if (member.Documentation.Count > 0)
        {
            foreach (string line in member.Documentation)
            {
                bool tag = line.StartsWith("<summary", StringComparison.Ordinal) ||
                           line.StartsWith("</summary", StringComparison.Ordinal) ||
                           line.StartsWith("<remarks", StringComparison.Ordinal) ||
                           line.StartsWith("</remarks", StringComparison.Ordinal);

                writer.Line((tag ? "/// " : "///     ") + line);
            }

            return;
        }

        string liveType = model.LiveTypeName.Substring(model.LiveTypeName.LastIndexOf('.') + 1).Replace("global::", "");
        writer.Line("/// <summary>");
        writer.Line("///     Gets the captured <c>" + liveType + "." + member.LiveName.TrimStart('@') + "</c>.");
        writer.Line("/// </summary>");
    }

    private static void EmitCapture(CodeWriter writer, SnapshotModel model, string live)
    {
        writer.Line("/// <summary>");
        writer.Line("///     Copies the <c>[NumosSerialize]</c> members of <paramref name=\"live\" /> into a new, detached snapshot.");
        writer.Line("/// </summary>");
        writer.Line("/// <param name=\"live\">The object to capture.</param>");
        writer.Line("/// <returns>A snapshot that shares no mutable storage with <paramref name=\"live\" />.</returns>");
        writer.Line(CodeWriter.GeneratedCodeAttribute);
        writer.Line("internal static " + model.TypeName + " Capture(" + live + " live)");
        writer.Open();
        writer.Line("global::System.ArgumentNullException.ThrowIfNull(live);");
        writer.Line("var snapshot = new " + model.TypeName + "();");
        foreach (SnapshotMemberModel member in model.Members)
        {
            string source = "live." + member.LiveName;
            switch (member.Kind)
            {
                case SnapshotMemberKind.Value:
                    writer.Line(Store("snapshot", member, source));
                    break;
                case SnapshotMemberKind.Nested:
                    writer.Line(Store("snapshot", member, member.Nested + ".Capture(" + source + ")"));
                    break;
                case SnapshotMemberKind.Array:
                    if (member.Storage == LiveStorage.Adapter && !member.IsFlatArray && member.Count is null)
                    {
                        // An adapter's getter hands over a fresh array, so the snapshot can own it without a copy.
                        writer.Line(Store("snapshot", member, source));
                    }
                    else
                    {
                        writer.Line(Store("snapshot", member, StateArray + ".Copy<" + member.ElementType + ">(" + ReadSpan(member, source) + ")"));
                    }

                    break;
                case SnapshotMemberKind.NestedList:
                    writer.Open();
                    writer.Line("var source = " + source + ";");
                    string count = member.Count is not null
                        ? "live." + member.Count
                        : member.Collection == LiveCollection.Array ? "source.Length" : "source.Count";

                    writer.Line("int count = " + count + ";");
                    writer.Line("var items = new " + member.Nested + "[count];");
                    writer.Line("for (int index = 0; index < count; index++)");
                    writer.Line("    items[index] = " + member.Nested + ".Capture(source[index]);");
                    writer.Line();
                    writer.Line(Store("snapshot", member, "items"));
                    writer.Close();
                    break;
            }
        }

        writer.Line("snapshot.OnCaptured(live);");
        writer.Line("return snapshot;");
        writer.Close();
    }

    private static string ReadSpan(SnapshotMemberModel member, string source)
    {
        string span = member.IsFlatArray
            ? source + ".AsSpan()"
            : "new global::System.ReadOnlySpan<" + member.ElementType + ">(" + source + ")";

        return member.Count is null ? span : span + ".Slice(0, live." + member.Count + ")";
    }

    private static string WriteSpan(SnapshotMemberModel member, string source)
    {
        return member.IsFlatArray ? source + ".AsSpan()" : "global::System.MemoryExtensions.AsSpan(" + source + ")";
    }

    private static void EmitRestore(CodeWriter writer, SnapshotModel model, string live)
    {
        writer.Line("/// <summary>");
        writer.Line("///     Writes this snapshot into an existing live object, in the live type's declaration order.");
        writer.Line("/// </summary>");
        writer.Line("/// <param name=\"live\">The object to restore. Its arrays must have the captured lengths.</param>");
        writer.Line("/// <remarks>");
        writer.Line("///     Every check runs before anything is written, so a mismatch leaves <paramref name=\"live\" /> unchanged.");
        writer.Line("/// </remarks>");
        writer.Line("/// <exception cref=\"global::System.ArgumentException\">");
        writer.Line("///     A read-only member or an array length doesn't match <paramref name=\"live\" />.");
        writer.Line("/// </exception>");
        writer.Line(CodeWriter.GeneratedCodeAttribute);
        writer.Line("internal void RestoreInto(" + live + " live)");
        writer.Open();
        writer.Line("global::System.ArgumentNullException.ThrowIfNull(live);");
        writer.Line("CheckRestoreInto(live);");
        writer.Line("ApplyRestoreInto(live);");
        writer.Close();
        writer.Line();

        writer.Line("/// <summary>");
        writer.Line("///     The checks <see cref=\"RestoreInto\" /> runs before writing anything.");
        writer.Line("/// </summary>");
        writer.Line(NeverBrowsable);
        writer.Line(CodeWriter.GeneratedCodeAttribute);
        writer.Line("internal void CheckRestoreInto(" + live + " live)");
        writer.Open();
        foreach (SnapshotMemberModel member in model.Members)
        {
            string source = "live." + member.LiveName;
            string label = member.Name.TrimStart('@');
            switch (member.Restore)
            {
                case RestoreMode.Check:
                    writer.Line("if (!" + member.Shape.CodecExpression + ".StateEquals(" + source + ", " + member.Name + "))");
                    writer.Line("    throw new global::System.ArgumentException(");
                    writer.Line("        \"The snapshot's " + label + " doesn't match the live object, which can't change it.\", nameof(live));");
                    writer.Line();
                    break;
                case RestoreMode.CopyInPlace:
                    string comparison = member.Count is null ? " != " : " > ";
                    writer.Line("if (" + member.Name + ".Count" + comparison + source + ".Length)");
                    writer.Line("    throw new global::System.ArgumentException(");
                    writer.Line("        $\"The snapshot's " + label + " holds {" + member.Name + ".Count} elements, but the live array holds {" +
                                source + ".Length}.\", nameof(live));");

                    writer.Line();
                    break;
                case RestoreMode.Nested:
                    writer.Line(member.Name + ".CheckRestoreInto(" + source + ");");
                    break;
            }
        }

        writer.Close();
        writer.Line();

        writer.Line("/// <summary>");
        writer.Line("///     The writes <see cref=\"RestoreInto\" /> makes once its checks pass, ending with <c>OnRestored</c>.");
        writer.Line("/// </summary>");
        writer.Line(NeverBrowsable);
        writer.Line(CodeWriter.GeneratedCodeAttribute);
        writer.Line("internal void ApplyRestoreInto(" + live + " live)");
        writer.Open();
        foreach (SnapshotMemberModel member in model.Members)
        {
            string target = "live." + member.LiveName;
            switch (member.Restore)
            {
                case RestoreMode.Assign:
                    writer.Line(target + " = " + member.Name + ";");
                    break;
                case RestoreMode.AssignArray:
                    writer.Line(target + " = " + StateArray + ".ToArray<" + member.ElementType + ">(" + member.Name + ");");
                    break;
                case RestoreMode.CopyInPlace:
                    writer.Line(StateArray + ".CopyTo<" + member.ElementType + ">(" + member.Name + ", " + WriteSpan(member, target) + ");");
                    if (member.Count is not null)
                        writer.Line("live." + member.Count + " = " + member.Name + ".Count;");

                    break;
                case RestoreMode.Nested:
                    writer.Line(member.Name + ".ApplyRestoreInto(" + target + ");");
                    break;
            }
        }

        writer.Line("OnRestored(live);");
        writer.Close();
        writer.Line();
        writer.Line("/// <summary>");
        writer.Line("///     Runs at the end of <see cref=\"RestoreInto\" />, to restore members the snapshot declares itself or rebuild");
        writer.Line("///     derived live state.");
        writer.Line("/// </summary>");
        writer.Line("partial void OnRestored(" + live + " live);");
    }

    private static void EmitReadState(CodeWriter writer, SnapshotModel model, string stateInterface)
    {
        writer.Line(CodeWriter.GeneratedCodeAttribute);
        writer.Line(
            "static " + model.TypeName + " " + stateInterface +
            ".ReadState(ref global::Numos.CoreSim.State.Advanced.StateReader reader, global::Numos.CoreSim.IGasRegistry? gases)");

        writer.Open();
        StateEmitter.EmitReadFields(writer, model.State);
        if (model.InitOnly.Count == 0)
        {
            writer.Line("var result = new " + model.TypeName + "();");
        }
        else
        {
            writer.Line("var result = new " + model.TypeName + "()");
            writer.Open();
            foreach (StateFieldModel field in model.InitOnly)
            {
                writer.Line(
                    field.MemberName + " = has" + field.Id + " ? field" + field.Id + " : " + Advanced + "StateCodecs.DefaultValue<" +
                    field.Shape.TypeExpression + ", " + field.Shape.CodecExpression + ">(),");
            }

            writer.Close(";");
        }

        foreach (StateFieldModel field in model.State.Fields)
        {
            if (field.InitOnly)
                continue;

            writer.Line("if (has" + field.Id + ")");
            SnapshotMemberModel? generated = model.Members.FirstOrDefault(member => member.Name == field.MemberName);
            if (generated is null)
            {
                writer.Line("    result." + field.MemberName + " = field" + field.Id + ";");
                continue;
            }

            writer.Line("    " + Store("result", generated, "field" + field.Id));
            if (generated is { IsReference: true, IsList: false } && generated.Shape.Kind != StateShapeKind.String)
            {
                // Data from before the member existed: give it the codec's default rather than leave it null.
                writer.Line("else");
                writer.Line("    " + Store(
                    "result", generated,
                    Advanced + "StateCodecs.DefaultValue<" + generated.Shape.TypeExpression + ", " + generated.Shape.CodecExpression + ">()!"));
            }
        }

        writer.Line("result.OnDecoded();");
        writer.Line("return result;");
        writer.Close();
    }
}
