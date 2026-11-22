using System.Globalization;
using Microsoft.CodeAnalysis.CSharp;
using Numos.API.Generators.Common;

namespace Numos.API.Generators.State;

internal static class SolverCommandEmitter
{
    /// <summary>
    ///     The generated dispatch method, which a derived class with commands overrides.
    /// </summary>
    public const string HookName = "InvokeSolverCommand";

    private const string CommandsInterface = "global::Numos.API.IAtmosSolverCommands";
    private const string Scope = "global::Numos.API.AtmosSolverCommandScope";
    private const string Advanced = "global::Numos.CoreSim.State.Advanced.";

    public static string Emit(SolverCommandTypeModel model)
    {
        var writer = new CodeWriter();
        writer.Header();
        writer.OpenNamespace(model.Type);
        writer.OpenTypeChain(model.Type, model.Hook.IsOverride ? "" : " : " + CommandsInterface);

        foreach (SolverCommandModel command in model.Commands)
        {
            EmitCommand(writer, command);
            writer.Line();
        }

        EmitInvokeCommand(writer, model);
        writer.CloseTypeChain(model.Type);
        writer.CloseNamespace(model.Type);
        EmitWireNames(writer, model);
        return writer.ToString();
    }

    /// <summary>
    ///     Writes the implementation of one declared command method.
    /// </summary>
    /// <remarks>
    ///     The arguments are encoded before <c>Apply{Name}</c> runs, so a body that mutates an argument cannot change
    ///     what the recording replays. Fields go in ascending id order, which the reader requires.
    /// </remarks>
    private static void EmitCommand(CodeWriter writer, SolverCommandModel command)
    {
        string modifiers = command.Modifiers.Length == 0 ? "" : command.Modifiers + " ";
        string parameters = string.Join(
            ", ", command.Parameters.Select(static parameter => parameter.DeclaredType + " " + parameter.Name));

        writer.Line(CodeWriter.GeneratedCodeAttribute);
        writer.Line(modifiers + "partial void " + command.MethodName + "(" + parameters + ")");
        writer.Open();
        writer.Line("using " + Scope + " __numosScope = " + Scope + ".Enter(this);");
        writer.Line("byte[]? __numosPayload = null;");
        writer.Line("if (__numosScope.Records)");
        writer.Open();
        writer.Line("var __numosSink = new " + Advanced + "StateBinarySink();");
        writer.Line("__numosSink.BeginObject();");
        foreach (SolverCommandParameterModel parameter in command.Parameters.OrderBy(static parameter => parameter.Id))
        {
            writer.Line("__numosSink.BeginField(" + parameter.Id + ");");
            writer.Line(parameter.Shape.CodecExpression + ".Write(ref __numosSink, " + parameter.Name + ");");
            writer.Line("__numosSink.EndField();");
        }

        writer.Line("__numosSink.EndObject();");
        writer.Line("__numosPayload = __numosSink.WrittenSpan.ToArray();");
        writer.Close();
        writer.Line();
        writer.Line(ApplyCall(command, static parameter => parameter.Name));
        writer.Line("__numosScope.Complete(" + command.Id + ", __numosPayload);");
        writer.Close();
    }

    /// <summary>
    ///     Writes the dispatch replay calls. Each case decodes every argument before <c>Apply{Name}</c> runs, so a bad
    ///     payload throws before the command changes anything.
    /// </summary>
    /// <remarks>
    ///     A class whose base has commands overrides the base's hook and hands it every id it doesn't know.
    /// </remarks>
    private static void EmitInvokeCommand(CodeWriter writer, SolverCommandTypeModel model)
    {
        const string parameters =
            "(int command, global::System.ReadOnlySpan<byte> payload, global::Numos.CoreSim.IGasRegistry gases)";

        SolverCommandHookModel hook = model.Hook;
        if (!hook.IsOverride)
        {
            writer.Line(CodeWriter.GeneratedCodeAttribute);
            writer.Line("bool " + CommandsInterface + ".InvokeCommand" + parameters);
            writer.Open();
            writer.Line("return " + HookName + "(command, payload, gases);");
            writer.Close();
            writer.Line();

            writer.Line("/// <summary>");
            writer.Line("///     Decodes a recorded payload and runs the matching <c>Apply{Name}</c> method of this type.");
            writer.Line("/// </summary>");
            writer.Line("/// <param name=\"command\">The command id.</param>");
            writer.Line("/// <param name=\"payload\">The arguments as the command method recorded them.</param>");
            writer.Line("/// <param name=\"gases\">Resolves gas arguments by name.</param>");
            writer.Line("/// <returns><see langword=\"false\" /> when no command has the id.</returns>");
            writer.Line(CodeWriter.GeneratedCodeAttribute);
            writer.Line(
                hook.Accessibility + (hook.Accessibility == "private" ? "" : " virtual") + " bool " + HookName + parameters);
        }
        else
        {
            writer.Line("/// <inheritdoc />");
            writer.Line(CodeWriter.GeneratedCodeAttribute);
            writer.Line(hook.Accessibility + " override bool " + HookName + parameters);
        }

        writer.Open();
        writer.Line("switch (command)");
        writer.Open();
        foreach (SolverCommandModel command in model.Commands)
        {
            writer.Line("case " + command.Id + ":");
            writer.Open();
            EmitReadArguments(writer, command);
            if (command.PassesWorld)
            {
                // Replay already holds the world's gate, so this scope is nested and only finds the world.
                writer.Line("using (" + Scope + " __numosScope = " + Scope + ".Enter(this))");
                writer.Line("    " + ApplyCall(command, static parameter => "field" + parameter.Id));
            }
            else
            {
                writer.Line(ApplyCall(command, static parameter => "field" + parameter.Id));
            }

            writer.Line("return true;");
            writer.Close();
        }

        writer.Line("default:");
        writer.Line(hook.IsOverride ? "    return base." + HookName + "(command, payload, gases);" : "    return false;");
        writer.Close();
        writer.Close();
    }

    /// <summary>
    ///     Writes the <c>field{id}</c> locals and the loop that fills them, in the pattern of a state reader. A
    ///     parameter the payload lacks keeps its default, and an id no parameter has is skipped.
    /// </summary>
    private static void EmitReadArguments(CodeWriter writer, SolverCommandModel command)
    {
        foreach (SolverCommandParameterModel parameter in command.Parameters)
            writer.Line(parameter.Shape.TypeExpression + " field" + parameter.Id + " = default!;");

        writer.Line("var reader = new " + Advanced + "StateReader(payload);");
        writer.Line("reader.BeginObject();");
        if (command.Parameters.Count == 0)
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
            foreach (SolverCommandParameterModel parameter in command.Parameters)
            {
                writer.Line("case " + parameter.Id + ":");
                writer.Line("    field" + parameter.Id + " = " + parameter.Shape.CodecExpression + ".Read(ref reader, gases);");
                writer.Line("    break;");
            }

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
        writer.Line("if (!reader.IsAtEnd)");
        writer.Line(
            "    throw new global::System.IO.InvalidDataException(" +
            SymbolDisplay.FormatLiteral("The payload of solver command '" + command.WireName + "' has trailing data.", true) +
            ");");

        writer.Line();
    }

    /// <summary>
    ///     Writes the call of <c>Apply{Name}</c>. An apply method that takes the world gets it from the
    ///     <c>__numosScope</c> local, which every caller declares when <see cref="SolverCommandModel.PassesWorld" /> is
    ///     set.
    /// </summary>
    private static string ApplyCall(SolverCommandModel command, Func<SolverCommandParameterModel, string> argument)
    {
        IEnumerable<string> arguments = command.Parameters.Select(argument);
        if (command.PassesWorld)
            arguments = new[] { "__numosScope.World" }.Concat(arguments);

        return "this." + command.ApplyName + "(" + string.Join(", ", arguments) + ");";
    }

    /// <summary>
    ///     Writes a module initializer that registers the wire names, so a replay error names the command instead of
    ///     its id. Goes after the type, at the top level of the file.
    /// </summary>
    /// <remarks>
    ///     The initializer lives in its own top-level class, because the solver may be private or nested. A hash of the
    ///     type name keeps the class name unique.
    /// </remarks>
    private static void EmitWireNames(CodeWriter writer, SolverCommandTypeModel model)
    {
        ulong hash = 14695981039346656037;
        foreach (char character in model.TypeName)
        {
            hash ^= character;
            hash *= 1099511628211;
        }

        writer.Line();
        writer.OpenModuleInitializer(
            "Numos.CoreSim.State.Generated",
            "SolverCommandNames_" + hash.ToString("X16", CultureInfo.InvariantCulture),
            "RegisterCommandNames",
            "Replay errors print command names, which must be registered before any replay runs.");

        writer.Line(Advanced + "NumosState.RegisterFieldNames(");
        for (int index = 0; index < model.Commands.Count; index++)
        {
            string literal = SymbolDisplay.FormatLiteral(model.Commands[index].WireName, true);
            writer.Line("    " + literal + (index < model.Commands.Count - 1 ? "," : ");"));
        }

        writer.Close();
        writer.Close();
        writer.Close();
    }
}
