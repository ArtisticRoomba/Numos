using Microsoft.CodeAnalysis.CSharp;
using Numos.API.Generators.Common;

namespace Numos.API.Generators.State;

internal static class StateEmitter
{
    private const string StateInterface = "global::Numos.CoreSim.State.Advanced.INumosState";
    private const string SolverConfiguration = "global::Numos.CoreSim.IAtmosSolverConfiguration";
    private const string Advanced = "global::Numos.CoreSim.State.Advanced.";
    private const string TargetInterface = "global::Numos.CoreSim.State.Advanced.INumosStateTarget";
    private const string ScratchInterface = "global::Numos.CoreSim.State.Advanced.INumosScratchTarget";
    private const string BulkInterface = "global::Numos.CoreSim.State.Advanced.INumosBulkStateTarget";
    private const string ComponentInterface = "global::Numos.API.ISimulationComponentDefinition";
    private const string DescribedInterface = "global::Numos.CoreSim.State.INumosStateDescribed";
    internal const string SchemaInterface = "global::Numos.CoreSim.State.Advanced.INumosStateSchema";
    private const string SchemaField = "__numosSchemaFingerprint";
    private const string MemberType = "global::Numos.CoreSim.State.AtmosStateMember";
    private const string BulkNamesField = "__numosBulkMemberNames";
    private const string MembersField = "__numosStateMembers";
    private const string PendingType = "__NumosPendingState";

    public static string Emit(StateTypeModel model)
    {
        if (model.Hooks is not null)
            return EmitHooked(model);

        if (model.RestoresInPlace)
            return EmitInPlace(model);

        string self = model.TypeName;
        string stateInterface = StateInterface + "<" + self + ">";
        var writer = new CodeWriter();
        writer.Header();
        writer.OpenNamespace(model.Type);
        writer.OpenTypeChain(model.Type, " : " + stateInterface + ", " + SchemaInterface);

        EmitWalker(writer, model, stateInterface, walker => EmitReadState(walker, model, stateInterface));

        if (model.IsSolverConfiguration)
        {
            writer.Line();
            EmitSolverConfiguration(writer, model);
        }

        writer.CloseTypeChain(model.Type);
        writer.CloseNamespace(model.Type);
        EmitFieldNames(writer, model);
        return writer.ToString();
    }

    /// <summary>
    ///     Writes a module initializer that registers the type's wire names, so a state diff prints names instead of
    ///     field ids. Goes after the type, at the top level of the file.
    /// </summary>
    /// <remarks>
    ///     The initializer lives in its own top-level class, because the type itself may be private, nested, or generic,
    ///     and a module initializer may be none of those. A hash of the type name keeps the class name unique.
    /// </remarks>
    internal static void EmitFieldNames(CodeWriter writer, StateTypeModel model)
    {
        if (model.Fields.Count == 0)
            return;

        ulong hash = 14695981039346656037;
        foreach (char character in model.TypeName)
        {
            hash ^= character;
            hash *= 1099511628211;
        }

        writer.Line();
        writer.OpenModuleInitializer(
            "Numos.CoreSim.State.Generated",
            "StateFieldNames_" + hash.ToString("X16", System.Globalization.CultureInfo.InvariantCulture),
            "RegisterFieldNames",
            "State diffs print field names, which must be registered before any diff runs.");

        writer.Line("global::Numos.CoreSim.State.Advanced.NumosState.RegisterFieldNames(");
        for (int index = 0; index < model.Fields.Count; index++)
        {
            string literal = SymbolDisplay.FormatLiteral(model.Fields[index].WireName, true);
            writer.Line("    " + literal + (index < model.Fields.Count - 1 ? "," : ");"));
        }

        writer.Close();
        writer.Close();
        writer.Close();
    }

    /// <summary>
    ///     Writes the members <c>INumosState&lt;T&gt;</c> and <c>INumosStateSchema</c> need, in a fixed order with a
    ///     blank line between them.
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
        writer.Line();
        EmitSchema(writer, model);
    }

    /// <summary>
    ///     Writes the type's schema fingerprint as a constant, and <c>INumosStateSchema</c> over it.
    /// </summary>
    /// <remarks>
    ///     The constant lets a test read the value by reflection without an instance. Numos reads it through the
    ///     interface.
    /// </remarks>
    private static void EmitSchema(CodeWriter writer, StateTypeModel model)
    {
        ulong fingerprint = SchemaFingerprints.Compute(model.Fields);
        writer.Line(CodeWriter.GeneratedCodeAttribute);
        writer.Line("[global::System.Diagnostics.DebuggerBrowsable(global::System.Diagnostics.DebuggerBrowsableState.Never)]");
        writer.Line(
            "private const ulong " + SchemaField + " = 0x" +
            fingerprint.ToString("X16", System.Globalization.CultureInfo.InvariantCulture) + "UL;");

        writer.Line();
        writer.Line(CodeWriter.GeneratedCodeAttribute);
        writer.Line("ulong " + SchemaInterface + ".SchemaFingerprint => " + SchemaField + ";");
    }

    /// <summary>
    ///     Emits the state members of a class solver or a simulation component. Neither is constructed from data, so
    ///     instead of <c>INumosState&lt;T&gt;</c> it gets an instance writer and an in-place reader.
    /// </summary>
    /// <remarks>
    ///     A component also gets its key and factory. A type with members that are not host-editable also gets the
    ///     boundary writer that skips them and a hash of each one. A type with any marked member also gets the member
    ///     descriptors that tools read.
    /// </remarks>
    private static string EmitInPlace(StateTypeModel model)
    {
        StateFieldModel[] bulk = model.Fields.Where(static field => !field.HostEditable).ToArray();
        string baseList = " : " + TargetInterface;
        if (model.ComponentKey is not null)
            baseList += ", " + ComponentInterface + "<" + model.TypeName + ">";

        if (bulk.Length > 0)
            baseList += ", " + BulkInterface;

        if (model.Fields.Count > 0)
            baseList += ", " + DescribedInterface;

        baseList += ", " + SchemaInterface;

        var writer = new CodeWriter();
        writer.Header();
        writer.OpenNamespace(model.Type);
        writer.OpenTypeChain(model.Type, baseList);

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
        EmitInPlaceWrites(writer, model);
        writer.Close();

        if (model.ComponentKey is not null)
        {
            writer.Line();
            EmitComponentDefinition(writer, model);
        }

        if (bulk.Length > 0)
        {
            writer.Line();
            EmitBulk(writer, model, bulk);
        }

        if (model.Fields.Count > 0)
        {
            writer.Line();
            EmitDescriptors(writer, model);
        }

        writer.Line();
        EmitSchema(writer, model);
        writer.CloseTypeChain(model.Type);
        writer.CloseNamespace(model.Type);
        EmitFieldNames(writer, model);
        return writer.ToString();
    }

    /// <summary>
    ///     Emits a class that shares its state work with its base and derived classes through virtual hooks: a class
    ///     restored in place that isn't sealed or derives from a class with state, or a plain base class of one.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A class and its base classes write one wire object, whose field ids must ascend. The write hook takes an
    ///         id range, and an override writes the base's fields in the gaps between its own. The read hook decodes
    ///         one field into a pending object, and the apply hook writes every decoded value afterwards, base classes
    ///         first, so a malformed payload still leaves the instance unchanged.
    ///     </para>
    ///     <para>
    ///         A class restored in place implements each interface itself, even when a base class does, because the
    ///         member descriptors, the bulk member names, and the fingerprint are per type. A plain base class only
    ///         gets the hooks.
    ///     </para>
    /// </remarks>
    private static string EmitHooked(StateTypeModel model)
    {
        StateHooksModel hooks = model.Hooks!;
        bool inPlace = !hooks.IsLevelOnly;
        var interfaces = new List<string>();
        var sections = new List<Action<CodeWriter>>();
        if (inPlace)
        {
            if (hooks.ImplementsTarget)
            {
                interfaces.Add(TargetInterface);
                sections.Add(EmitHookedTarget);
            }

            if (model.ComponentKey is not null)
            {
                interfaces.Add(ComponentInterface + "<" + model.TypeName + ">");
                sections.Add(writer => EmitComponentDefinition(writer, model));
            }

            if (hooks.ChainHasBulk)
            {
                interfaces.Add(BulkInterface);
                sections.Add(EmitHookedBulk);
            }

            if (hooks.ChainHasFields)
            {
                interfaces.Add(DescribedInterface);
                sections.Add(EmitHookedDescriptors);
            }

            interfaces.Add(SchemaInterface);
        }

        if (hooks.Kind is StateHookKind.Introduce or StateHookKind.Override)
            sections.Add(writer => EmitStateHooks(writer, model, hooks.Kind == StateHookKind.Override));

        if (inPlace)
            sections.Add(writer => EmitHookedSchema(writer, hooks));

        var output = new CodeWriter();
        output.Header();
        output.OpenNamespace(model.Type);
        output.OpenTypeChain(model.Type, interfaces.Count == 0 ? "" : " : " + string.Join(", ", interfaces));
        for (int index = 0; index < sections.Count; index++)
        {
            if (index > 0)
                output.Line();

            sections[index](output);
        }

        output.CloseTypeChain(model.Type);
        output.CloseNamespace(model.Type);
        EmitFieldNames(output, model);
        return output.ToString();
    }

    /// <summary>
    ///     Writes <c>INumosStateTarget</c> over the hooks, so a derived class's override takes part.
    /// </summary>
    private static void EmitHookedTarget(CodeWriter writer)
    {
        writer.Line(CodeWriter.GeneratedCodeAttribute);
        writer.Line("void " + StateInterface + ".WriteState<TSink>(ref TSink sink)");
        writer.Open();
        writer.Line("sink.BeginObject();");
        writer.Line("this." + StateGenerator.WriteHookName + "(ref sink, 0, int.MaxValue, false);");
        writer.Line("sink.EndObject();");
        writer.Close();
        writer.Line();
        writer.Line(CodeWriter.GeneratedCodeAttribute);
        writer.Line(
            "void " + TargetInterface +
            ".ReadStateInto(ref global::Numos.CoreSim.State.Advanced.StateReader reader, global::Numos.CoreSim.IGasRegistry? gases)");

        writer.Open();
        writer.Line("object? pending = null;");
        writer.Line("reader.BeginObject();");
        writer.Line("while (reader.TryReadField(out int id))");
        writer.Open();
        writer.Line("if (this." + StateGenerator.ReadHookName + "(ref reader, id, gases, ref pending))");
        writer.Line("    reader.EndField();");
        writer.Line("else");
        writer.Line("    reader.SkipField();");
        writer.Close();
        writer.Line();
        writer.Line("reader.EndObject();");
        writer.Line("this." + StateGenerator.ApplyHookName + "(pending);");
        writer.Close();
    }

    /// <summary>
    ///     Writes <c>INumosBulkStateTarget</c> over the hooks.
    /// </summary>
    /// <remarks>
    ///     The names are built on first use and shared by every instance of the type. Two threads that ask first can
    ///     each build them, which costs one allocation.
    /// </remarks>
    private static void EmitHookedBulk(CodeWriter writer)
    {
        writer.Line(CodeWriter.GeneratedCodeAttribute);
        writer.Line("[global::System.Diagnostics.DebuggerBrowsable(global::System.Diagnostics.DebuggerBrowsableState.Never)]");
        writer.Line("private static string[]? " + BulkNamesField + ";");
        writer.Line();
        writer.Line(CodeWriter.GeneratedCodeAttribute);
        writer.Line("global::System.Collections.Generic.IReadOnlyList<string> " + BulkInterface + ".BulkMemberNames");
        writer.Open();
        writer.Line("get");
        writer.Open();
        writer.Line("if (" + BulkNamesField + " is null)");
        writer.Open();
        writer.Line("var names = new global::System.Collections.Generic.List<string>();");
        writer.Line("this." + StateGenerator.DescribeHookName + "(new global::System.Collections.Generic.List<" + MemberType + ">(), names);");
        writer.Line(BulkNamesField + " = names.ToArray();");
        writer.Close();
        writer.Line();
        writer.Line("return " + BulkNamesField + ";");
        writer.Close();
        writer.Close();
        writer.Line();
        writer.Line(CodeWriter.GeneratedCodeAttribute);
        writer.Line("void " + BulkInterface + ".WriteHostEditableState<TSink>(ref TSink sink)");
        writer.Open();
        writer.Line("sink.BeginObject();");
        writer.Line("this." + StateGenerator.WriteHookName + "(ref sink, 0, int.MaxValue, true);");
        writer.Line("sink.EndObject();");
        writer.Close();
        writer.Line();
        writer.Line(CodeWriter.GeneratedCodeAttribute);
        writer.Line("void " + BulkInterface + ".HashBulkMembers(global::System.Span<ulong> hashes) => this." + StateGenerator.HashHookName + "(hashes);");
    }

    /// <summary>
    ///     Writes <c>INumosStateDescribed</c> over the describe hook, built on first use and shared by every instance of
    ///     the type.
    /// </summary>
    private static void EmitHookedDescriptors(CodeWriter writer)
    {
        string list = "global::System.Collections.Generic.IReadOnlyList<" + MemberType + ">";
        writer.Line(CodeWriter.GeneratedCodeAttribute);
        writer.Line("[global::System.Diagnostics.DebuggerBrowsable(global::System.Diagnostics.DebuggerBrowsableState.Never)]");
        writer.Line("private static " + list + "? " + MembersField + ";");
        writer.Line();
        writer.Line(CodeWriter.GeneratedCodeAttribute);
        writer.Line(list + " " + DescribedInterface + ".StateMembers");
        writer.Open();
        writer.Line("get");
        writer.Open();
        writer.Line("if (" + MembersField + " is null)");
        writer.Open();
        writer.Line("var members = new global::System.Collections.Generic.List<" + MemberType + ">();");
        writer.Line("this." + StateGenerator.DescribeHookName + "(members, new global::System.Collections.Generic.List<string>());");
        writer.Line(MembersField + " = global::System.Array.AsReadOnly(members.ToArray());");
        writer.Close();
        writer.Line();
        writer.Line("return " + MembersField + ";");
        writer.Close();
        writer.Close();
    }

    /// <summary>
    ///     Writes the fingerprint over the whole chain, and for a type that isn't sealed, the schema entries a derived
    ///     class in another assembly reads.
    /// </summary>
    private static void EmitHookedSchema(CodeWriter writer, StateHooksModel hooks)
    {
        writer.Line(CodeWriter.GeneratedCodeAttribute);
        writer.Line("[global::System.Diagnostics.DebuggerBrowsable(global::System.Diagnostics.DebuggerBrowsableState.Never)]");
        writer.Line(
            "private const ulong " + SchemaField + " = 0x" +
            hooks.Fingerprint.ToString("X16", System.Globalization.CultureInfo.InvariantCulture) + "UL;");

        writer.Line();
        writer.Line(CodeWriter.GeneratedCodeAttribute);
        writer.Line("ulong " + SchemaInterface + ".SchemaFingerprint => " + SchemaField + ";");
        if (!hooks.EmitsSchemaFields)
            return;

        writer.Line();
        writer.Line("/// <summary>");
        writer.Line("///     The wire name and shape of every state field of this class and its base classes, for the generator of a");
        writer.Line("///     derived class in another assembly.");
        writer.Line("/// </summary>");
        writer.Line(CodeWriter.GeneratedCodeAttribute);
        writer.Line(CodeWriter.EditorBrowsableNeverAttribute);
        writer.Line("[global::System.Diagnostics.DebuggerBrowsable(global::System.Diagnostics.DebuggerBrowsableState.Never)]");
        writer.Line(
            "protected " + (hooks.HidesSchemaFields ? "new " : "") + "const string " + StateGenerator.SchemaFieldsName + " = " +
            SymbolDisplay.FormatLiteral(hooks.SchemaFields, true) + ";");
    }

    /// <summary>
    ///     Writes the state hooks of one class: the pending state type, then the write, read, apply, bulk hash, and
    ///     describe hooks.
    /// </summary>
    /// <param name="writer">The writer, inside the open type.</param>
    /// <param name="model">The class, with its own fields.</param>
    /// <param name="overrides">
    ///     Whether a base class has the hooks, so these override them and call the base. An override exists only for a
    ///     class with fields of its own.
    /// </param>
    private static void EmitStateHooks(CodeWriter writer, StateTypeModel model, bool overrides)
    {
        if (model.Fields.Count > 0)
        {
            EmitPendingState(writer, model, overrides);
            writer.Line();
        }

        EmitWriteHook(writer, model, overrides);
        writer.Line();
        EmitReadHook(writer, model, overrides);
        writer.Line();
        EmitApplyHook(writer, model, overrides);
        if (!overrides || model.Fields.Any(static field => !field.HostEditable))
        {
            writer.Line();
            EmitHashHook(writer, model, overrides);
        }

        writer.Line();
        EmitDescribeHook(writer, model, overrides);
    }

    private static void EmitPendingState(CodeWriter writer, StateTypeModel model, bool overrides)
    {
        writer.Line(CodeWriter.GeneratedCodeAttribute);
        writer.Line("private sealed class " + PendingType);
        writer.Open();
        if (overrides)
            writer.Line("public object? Base;");

        foreach (StateFieldModel field in model.Fields)
        {
            writer.Line("public " + field.Shape.TypeExpression + " field" + field.Id + " = default!;");
            writer.Line("public bool has" + field.Id + ";");
        }

        writer.Close();
    }

    /// <summary>
    ///     Writes the XML documentation, attributes, and declaration line of one hook.
    /// </summary>
    private static void OpenHook(CodeWriter writer, bool overrides, string signature, params string[] documentation)
    {
        if (overrides)
        {
            writer.Line("/// <inheritdoc />");
        }
        else
        {
            foreach (string line in documentation)
                writer.Line("/// " + line);
        }

        writer.Line(CodeWriter.GeneratedCodeAttribute);
        writer.Line(CodeWriter.EditorBrowsableNeverAttribute);
        writer.Line((overrides ? "protected override " : "protected virtual ") + signature);
    }

    private static void EmitWriteHook(CodeWriter writer, StateTypeModel model, bool overrides)
    {
        OpenHook(
            writer,
            overrides,
            "void " + StateGenerator.WriteHookName + "<TSink>(ref TSink sink, int afterId, int throughId, bool hostEditableOnly)",
            "<summary>",
            "    Writes each state field of this class and its base classes whose id is greater than",
            "    <paramref name=\"afterId\" /> and at most <paramref name=\"throughId\" />, in ascending id order.",
            "</summary>",
            "<param name=\"sink\">The sink to write to.</param>",
            "<param name=\"afterId\">The id just below the range.</param>",
            "<param name=\"throughId\">The last id in the range.</param>",
            "<param name=\"hostEditableOnly\">Whether to leave out the fields marked <c>HostEditable = false</c>.</param>",
            "<typeparam name=\"TSink\">The sink type.</typeparam>",
            "<remarks>",
            "    A derived class's fields share one wire object with these, so its override writes the base's fields in",
            "    the ranges between its own.",
            "</remarks>");

        if (!overrides)
            writer.Line("    where TSink : struct, global::Numos.CoreSim.State.Advanced.IStateSink");

        writer.Open();
        string after = "afterId";
        foreach (StateFieldModel field in model.Fields)
        {
            if (overrides)
            {
                writer.Line(
                    "base." + StateGenerator.WriteHookName + "(ref sink, " + after + ", global::System.Math.Min(throughId, " +
                    (field.Id - 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + "), hostEditableOnly);");
            }

            string range = "afterId < " + field.Id + " && " + field.Id + " <= throughId";
            writer.Line("if (" + (field.HostEditable ? range : "!hostEditableOnly && " + range) + ")");
            writer.Open();
            writer.Line("sink.BeginField(" + field.Id + ");");
            writer.Line(field.Shape.CodecExpression + ".Write(ref sink, this." + field.MemberName + ");");
            writer.Line("sink.EndField();");
            writer.Close();
            after = "global::System.Math.Max(afterId, " + field.Id + ")";
        }

        if (overrides)
            writer.Line("base." + StateGenerator.WriteHookName + "(ref sink, " + after + ", throughId, hostEditableOnly);");

        writer.Close();
    }

    private static void EmitReadHook(CodeWriter writer, StateTypeModel model, bool overrides)
    {
        OpenHook(
            writer,
            overrides,
            "bool " + StateGenerator.ReadHookName +
            "(ref global::Numos.CoreSim.State.Advanced.StateReader reader, int id, global::Numos.CoreSim.IGasRegistry? gases, ref object? pending)",
            "<summary>",
            "    Decodes the state field <paramref name=\"id\" /> of this class or a base class into",
            "    <paramref name=\"pending\" />, without changing the instance.",
            "</summary>",
            "<param name=\"reader\">The reader, inside the field.</param>",
            "<param name=\"id\">The field id.</param>",
            "<param name=\"gases\">Resolves gas references by name.</param>",
            "<param name=\"pending\">",
            "    Holds the decoded values until <see cref=\"" + StateGenerator.ApplyHookName + "\" />. Starts as <see langword=\"null\" />.",
            "</param>",
            "<returns>",
            "    <see langword=\"false\" /> when no class in the chain has a field with the id. The reader has then read",
            "    nothing.",
            "</returns>");

        writer.Open();
        if (model.Fields.Count == 0)
        {
            writer.Line("return false;");
            writer.Close();
            return;
        }

        string state = "(" + PendingType + ")(pending ??= new " + PendingType + "())";
        if (overrides)
            writer.Line("var state = " + state + ";");

        writer.Line("switch (id)");
        writer.Open();
        foreach (StateFieldModel field in model.Fields)
        {
            writer.Line("case " + field.Id + ":");
            writer.Open();
            if (!overrides)
                writer.Line("var state = " + state + ";");

            // A custom codec may be declared over the nullable form of the member's type.
            string forgive = field.Shape.Kind == StateShapeKind.Custom ? "!" : "";
            writer.Line("state.field" + field.Id + " = " + field.Shape.CodecExpression + ".Read(ref reader, gases)" + forgive + ";");
            if (!Assignable(field))
            {
                // Nothing is written until every field is decoded, so the check can run here.
                writer.Line("if (this." + field.MemberName + " is null)");
                writer.Line("    throw new global::System.InvalidOperationException(" + RefillMessage(model, field) + ");");
            }

            writer.Line("state.has" + field.Id + " = true;");
            writer.Line("return true;");
            writer.Close();
        }

        writer.Line("default:");
        writer.Line(
            overrides
                ? "    return base." + StateGenerator.ReadHookName + "(ref reader, id, gases, ref state.Base);"
                : "    return false;");

        writer.Close();
        writer.Close();
    }

    private static void EmitApplyHook(CodeWriter writer, StateTypeModel model, bool overrides)
    {
        OpenHook(
            writer,
            overrides,
            "void " + StateGenerator.ApplyHookName + "(object? pending)",
            "<summary>",
            "    Writes what <see cref=\"" + StateGenerator.ReadHookName + "\" /> decoded into this instance, base classes first.",
            "</summary>",
            "<param name=\"pending\">The decoded values, or <see langword=\"null\" /> when the payload held none.</param>");

        writer.Open();
        if (model.Fields.Count > 0)
        {
            writer.Line("if (pending is not " + PendingType + " state)");
            writer.Line("    return;");
            writer.Line();
            if (overrides)
                writer.Line("base." + StateGenerator.ApplyHookName + "(state.Base);");

            EmitAssignments(writer, model, "state.");
        }

        writer.Close();
    }

    private static void EmitHashHook(CodeWriter writer, StateTypeModel model, bool overrides)
    {
        OpenHook(
            writer,
            overrides,
            "int " + StateGenerator.HashHookName + "(global::System.Span<ulong> hashes)",
            "<summary>",
            "    Hashes each field of this class and its base classes marked <c>HostEditable = false</c>, base classes first.",
            "</summary>",
            "<param name=\"hashes\">Receives one hash per field.</param>",
            "<returns>The number of hashes written.</returns>");

        writer.Open();
        writer.Line(overrides ? "int index = base." + StateGenerator.HashHookName + "(hashes);" : "int index = 0;");
        foreach (StateFieldModel field in model.Fields.Where(static field => !field.HostEditable))
        {
            writer.Open();
            writer.Line("var sink = new " + Advanced + "StateHashSink();");
            writer.Line(field.Shape.CodecExpression + ".Write(ref sink, this." + field.MemberName + ");");
            writer.Line("hashes[index++] = sink.Value;");
            writer.Close();
        }

        writer.Line("return index;");
        writer.Close();
    }

    private static void EmitDescribeHook(CodeWriter writer, StateTypeModel model, bool overrides)
    {
        OpenHook(
            writer,
            overrides,
            "void " + StateGenerator.DescribeHookName +
            "(global::System.Collections.Generic.List<" + MemberType + "> members, global::System.Collections.Generic.List<string> bulkMemberNames)",
            "<summary>",
            "    Adds a descriptor for each state field of this class and its base classes, base classes first, and the",
            "    name of each field marked <c>HostEditable = false</c>.",
            "</summary>",
            "<param name=\"members\">Receives the descriptors.</param>",
            "<param name=\"bulkMemberNames\">Receives the names of the fields that are not host-editable.</param>");

        writer.Open();
        if (overrides)
            writer.Line("base." + StateGenerator.DescribeHookName + "(members, bulkMemberNames);");

        foreach (StateFieldModel field in model.Fields.OrderBy(static field => field.Order))
        {
            writer.Line("members.Add(");
            EmitDescriptor(writer, model, field, "    ", ");");
        }

        foreach (StateFieldModel field in model.Fields.Where(static field => !field.HostEditable))
            writer.Line("bulkMemberNames.Add(" + SymbolDisplay.FormatLiteral(field.Name, true) + ");");

        writer.Close();
    }

    /// <summary>
    ///     Writes the key and factory of <c>ISimulationComponentDefinition&lt;T&gt;</c>. Both are explicit, so they
    ///     can't collide with the class's own members.
    /// </summary>
    private static void EmitComponentDefinition(CodeWriter writer, StateTypeModel model)
    {
        string definition = ComponentInterface + "<" + model.TypeName + ">";
        writer.Line(CodeWriter.GeneratedCodeAttribute);
        writer.Line(
            "static string " + definition + ".ComponentKey => " + SymbolDisplay.FormatLiteral(model.ComponentKey!, true) + ";");

        writer.Line();
        writer.Line(CodeWriter.GeneratedCodeAttribute);
        writer.Line("static " + model.TypeName + " " + definition + ".CreateComponent() => new " + model.TypeName + "();");
    }

    /// <summary>
    ///     Writes <c>INumosBulkStateTarget</c>: the boundary writer over the host-editable fields, and one hash per
    ///     field that is not host-editable.
    /// </summary>
    private static void EmitBulk(CodeWriter writer, StateTypeModel model, StateFieldModel[] bulk)
    {
        writer.Line(CodeWriter.GeneratedCodeAttribute);
        writer.Line("[global::System.Diagnostics.DebuggerBrowsable(global::System.Diagnostics.DebuggerBrowsableState.Never)]");
        writer.Line(
            "private static readonly string[] " + BulkNamesField + " = { " +
            string.Join(", ", bulk.Select(static field => SymbolDisplay.FormatLiteral(field.Name, true))) + " };");

        writer.Line();
        writer.Line(CodeWriter.GeneratedCodeAttribute);
        writer.Line(
            "global::System.Collections.Generic.IReadOnlyList<string> " + BulkInterface + ".BulkMemberNames => " +
            BulkNamesField + ";");

        writer.Line();
        writer.Line(CodeWriter.GeneratedCodeAttribute);
        writer.Line("void " + BulkInterface + ".WriteHostEditableState<TSink>(ref TSink sink)");
        writer.Open();
        EmitFieldWrites(writer, model.Fields.Where(static field => field.HostEditable), "this");
        writer.Close();
        writer.Line();
        writer.Line(CodeWriter.GeneratedCodeAttribute);
        writer.Line("void " + BulkInterface + ".HashBulkMembers(global::System.Span<ulong> hashes)");
        writer.Open();
        for (int index = 0; index < bulk.Length; index++)
        {
            StateFieldModel field = bulk[index];
            writer.Open();
            writer.Line("var sink = new " + Advanced + "StateHashSink();");
            writer.Line(field.Shape.CodecExpression + ".Write(ref sink, this." + field.MemberName + ");");
            writer.Line("hashes[" + index + "] = sink.Value;");
            writer.Close();
        }

        writer.Close();
    }

    /// <summary>
    ///     Writes <c>INumosStateDescribed</c>: one <c>AtmosStateMember</c> per field in declaration order, built on first
    ///     use and shared by every instance.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Two threads that ask first can each build the list. Both lists are equal and one of them stays, so the
    ///         race costs one allocation.
    ///     </para>
    ///     <para>
    ///         A field gets a writer only when it is a scalar a tool can parse, the generated reader can assign it, and
    ///         it is host-editable. The recording boundary does not hash a field that is not host-editable, so a tool
    ///         write to it would never be recorded.
    ///     </para>
    /// </remarks>
    private static void EmitDescriptors(CodeWriter writer, StateTypeModel model)
    {
        string list = "global::System.Collections.Generic.IReadOnlyList<" + MemberType + ">";
        writer.Line(CodeWriter.GeneratedCodeAttribute);
        writer.Line("[global::System.Diagnostics.DebuggerBrowsable(global::System.Diagnostics.DebuggerBrowsableState.Never)]");
        writer.Line("private static " + list + "? " + MembersField + ";");
        writer.Line();
        writer.Line(CodeWriter.GeneratedCodeAttribute);
        writer.Line(list + " " + DescribedInterface + ".StateMembers =>");
        writer.Line("    " + MembersField + " ??= global::System.Array.AsReadOnly(new " + MemberType + "[]");
        writer.Line("    {");
        foreach (StateFieldModel field in model.Fields.OrderBy(static field => field.Order))
            EmitDescriptor(writer, model, field, "        ", ",");

        writer.Line("    });");
    }

    /// <summary>
    ///     Writes the <c>AtmosStateMember</c> construction for one field, with each line indented by
    ///     <paramref name="indent" /> and the expression followed by <paramref name="suffix" />.
    /// </summary>
    private static void EmitDescriptor(CodeWriter writer, StateTypeModel model, StateFieldModel field, string indent, string suffix)
    {
        string target = "((" + model.TypeName + ")target)." + field.MemberName;
        string setter = "null";
        if (field.Shape.IsToolScalar && Assignable(field) && field.HostEditable)
        {
            bool nullable = field.Shape.Kind is StateShapeKind.NullableValue or StateShapeKind.NullableReference;
            string value = nullable
                ? "(" + field.Shape.TypeExpression + ")value"
                : "(" + field.Shape.TypeExpression + ")(value ?? throw new global::System.ArgumentNullException(\"value\"))";

            setter = "static (target, value) => " + target + " = " + value;
        }

        writer.Line(indent + "new " + MemberType + "(");
        writer.Line(indent + "    " + SymbolDisplay.FormatLiteral(field.Name, true) + ",");
        writer.Line(indent + "    " + SymbolDisplay.FormatLiteral(field.WireName, true) + ",");
        writer.Line(indent + "    typeof(" + field.Shape.RuntimeTypeExpression + "),");
        writer.Line(indent + "    " + (field.Unit is null ? "null" : SymbolDisplay.FormatLiteral(field.Unit, true)) + ",");
        writer.Line(indent + "    static target => " + target + ",");
        writer.Line(indent + "    " + setter + ")" + suffix);
    }

    /// <summary>
    ///     Writes the decoded <c>field{id}</c> locals into <c>this</c>. A collection member that holds an instance keeps
    ///     it: a list, set, or dictionary is cleared and refilled, and an array of the same length is copied into.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Every write comes after the read loop, so a bad payload can't leave the instance half restored. A member
    ///         the generator can't assign is refilled, so a null one has nothing to refill. All of those are checked
    ///         before the first write.
    ///     </para>
    ///     <para>
    ///         A dictionary refills through the indexer, which can't throw partway. The decoded keys are distinct under
    ///         the default comparer, and the instance's own comparer may still merge two of them.
    ///     </para>
    /// </remarks>
    private static void EmitInPlaceWrites(CodeWriter writer, StateTypeModel model)
    {
        foreach (StateFieldModel field in model.Fields)
        {
            if (Assignable(field))
                continue;

            writer.Line("if (has" + field.Id + " && this." + field.MemberName + " is null)");
            writer.Line("    throw new global::System.InvalidOperationException(" + RefillMessage(model, field) + ");");
            writer.Line();
        }

        EmitAssignments(writer, model, "");
    }

    /// <summary>
    ///     Gets the message, as a C# literal, for a restore that finds nothing to refill in a member it can't assign.
    /// </summary>
    private static string RefillMessage(StateTypeModel model, StateFieldModel field)
    {
        string owner = model.TypeName.Replace("global::", "");
        return SymbolDisplay.FormatLiteral("'" + owner + "." + field.Name + "' is null, so the restore has no instance to refill.", true);
    }

    /// <summary>
    ///     Writes each decoded field into <c>this</c>, reading the <c>has{id}</c> and <c>field{id}</c> values with
    ///     <paramref name="prefix" /> in front: empty for locals, <c>state.</c> for a pending state object.
    /// </summary>
    private static void EmitAssignments(CodeWriter writer, StateTypeModel model, string prefix)
    {
        foreach (StateFieldModel field in model.Fields)
        {
            string member = "this." + field.MemberName;
            string local = prefix + "field" + field.Id;
            StateRefill refill = field.Shape.Refill;
            writer.Line("if (" + prefix + "has" + field.Id + ")");
            if (refill == StateRefill.None)
            {
                writer.Line("    " + member + " = " + local + ";");
                continue;
            }

            writer.Open();
            bool assignable = Assignable(field);
            bool nullable = field.Shape.Kind == StateShapeKind.NullableReference;
            if (refill == StateRefill.Array)
            {
                // Arrays are always assignable, because the generator rejects a readonly one.
                string reuse = member + " is not null && " + (nullable ? local + " is not null && " : "") +
                               member + ".Length == " + local + ".Length";

                writer.Line("if (" + reuse + ")");
                writer.Line("    global::System.Array.Copy(" + local + ", " + member + ", " + local + ".Length);");
                writer.Line("else");
                writer.Line("    " + member + " = " + local + ";");
                writer.Close();
                continue;
            }

            if (assignable)
            {
                string replace = nullable ? local + " is null || " + member + " is null" : member + " is null";
                writer.Line("if (" + replace + ")");
                writer.Line("    " + member + " = " + local + ";");
                writer.Line("else");
                writer.Open();
            }

            writer.Line(member + ".Clear();");
            switch (refill)
            {
                case StateRefill.List:
                    writer.Line(member + ".AddRange(" + local + ");");
                    break;
                case StateRefill.Set:
                    writer.Line(member + ".UnionWith(" + local + ");");
                    break;
                default:
                    writer.Line("foreach (var entry in " + local + ")");
                    writer.Line("    " + member + "[entry.Key] = entry.Value;");
                    break;
            }

            if (assignable)
                writer.Close();

            writer.Close();
        }
    }

    /// <summary>
    ///     Whether the generated reader can assign the member. The generator only lets an unassignable member through
    ///     when a restore can refill it.
    /// </summary>
    private static bool Assignable(StateFieldModel field) => field.Settable && !field.InitOnly;

    /// <summary>
    ///     Emits a class solver's <c>ResetScratch</c>, which Numos calls after a restore and before <c>OnRestored</c>.
    /// </summary>
    /// <remarks>
    ///     Copied initializers bind as they do in their own file, because the file's using directives come along. The
    ///     compiler already checked each initializer there, so the copy turns nullable warnings off.
    /// </remarks>
    public static string EmitScratch(ScratchTypeModel model)
    {
        bool copiesInitializers = model.Resets.Any(static reset => reset.Kind == ScratchResetKind.Initializer);
        var writer = new CodeWriter();
        writer.Header();
        if (copiesInitializers)
        {
            writer.Line("#nullable disable warnings");
            writer.Line();
        }

        foreach (string directive in model.FileUsings)
            writer.Line(directive);

        if (model.FileUsings.Count > 0)
            writer.Line();

        writer.OpenNamespace(model.Type);
        foreach (string directive in model.NamespaceUsings)
            writer.Line(directive);

        if (model.NamespaceUsings.Count > 0)
            writer.Line();

        writer.OpenTypeChain(model.Type, model.ImplementsInterface ? " : " + ScratchInterface : "");
        if (model.ImplementsInterface)
        {
            writer.Line(CodeWriter.GeneratedCodeAttribute);
            if (model.Hook != StateHookKind.None)
            {
                writer.Line("void " + ScratchInterface + ".ResetScratch() => this." + StateGenerator.ScratchHookName + "();");
            }
            else
            {
                writer.Line("void " + ScratchInterface + ".ResetScratch()");
                EmitResets(writer, model, false);
            }
        }

        if (model.Hook is StateHookKind.Introduce or StateHookKind.Override)
        {
            if (model.ImplementsInterface)
                writer.Line();

            bool overrides = model.Hook == StateHookKind.Override;
            if (overrides)
            {
                writer.Line("/// <inheritdoc />");
            }
            else
            {
                writer.Line("/// <summary>");
                writer.Line("///     Resets the <c>[NumosScratch]</c> members of this class and its base classes, base classes first.");
                writer.Line("/// </summary>");
            }

            writer.Line(CodeWriter.GeneratedCodeAttribute);
            writer.Line(CodeWriter.EditorBrowsableNeverAttribute);
            writer.Line((overrides ? "protected override" : "protected virtual") + " void " + StateGenerator.ScratchHookName + "()");
            EmitResets(writer, model, overrides);
        }

        writer.CloseTypeChain(model.Type);
        writer.CloseNamespace(model.Type);
        return writer.ToString();
    }

    /// <summary>
    ///     Writes a method body that resets each member of <paramref name="model" />, after the base class's resets
    ///     when <paramref name="callsBase" /> is set.
    /// </summary>
    private static void EmitResets(CodeWriter writer, ScratchTypeModel model, bool callsBase)
    {
        writer.Open();
        if (callsBase)
            writer.Line("base." + StateGenerator.ScratchHookName + "();");

        foreach (ScratchResetModel reset in model.Resets)
        {
            string statement = reset.Kind switch
            {
                ScratchResetKind.Initializer => "this." + reset.MemberName + " = " + reset.Initializer + ";",
                ScratchResetKind.Clear => "this." + reset.MemberName + ".Clear();",
                _ => "this." + reset.MemberName + " = default!;",
            };

            if (!reset.NeedsBlock)
            {
                writer.Line(statement);
                continue;
            }

            writer.Open();
            writer.Line(statement);
            writer.Close();
        }

        writer.Close();
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
        EmitFieldWrites(writer, model.Fields, receiver);
    }

    private static void EmitFieldWrites(CodeWriter writer, IEnumerable<StateFieldModel> fields, string receiver)
    {
        writer.Line("sink.BeginObject();");
        foreach (StateFieldModel field in fields)
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
    ///     Writes the assembly's module initializer that adds every simulation component class to
    ///     <c>SimulationComponentCatalog</c>, so a restore or a load can create one by key.
    /// </summary>
    /// <param name="types">The fully qualified component classes, in a stable order.</param>
    /// <remarks>
    ///     A key conflict with a component in another loaded assembly throws from here, the first time code in this
    ///     assembly runs.
    /// </remarks>
    public static string EmitComponentModuleInitializer(EquatableArray<string> types)
    {
        var writer = new CodeWriter();
        writer.Header();
        writer.OpenModuleInitializer(
            "Numos.API.Generated",
            "SimulationComponentModuleInitializer",
            "RegisterSimulationComponents",
            "Simulation components must be in the catalog before a restore or a load creates one by key.");

        foreach (string type in types)
            writer.Line("global::Numos.API.SimulationComponentCatalog.Register<" + type + ">();");

        writer.Close();
        writer.Close();
        writer.Close();
        return writer.ToString();
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
