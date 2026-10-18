using Microsoft.CodeAnalysis.CSharp;
using Numos.API.Generators.Common;

namespace Numos.API.Generators.SolverDependency;

/// <summary>
///     Writes the generated file for one class that declares <c>[SolverDependency]</c> properties.
/// </summary>
internal static class SolverDependencyEmitter
{
    private const string FieldPrefix = "__numosDependency_";
    private const string InterfaceType = "global::Numos.API.IAtmosSolverDependencies";
    private const string ResolverType = "global::Numos.API.AtmosSolverDependencyResolver";

    public static string Emit(SolverDependencyTypeModel type)
    {
        EquatableArray<SolverDependencyPropertyModel> members = type.Members;
        DependencyHookModel hook = type.Hook;
        var writer = new CodeWriter();
        writer.Header();
        writer.OpenNamespace(type.Type);
        writer.OpenTypeChain(type.Type, hook.IsOverride ? "" : " : " + InterfaceType);

        foreach (SolverDependencyPropertyModel model in members)
        {
            string initializer = model.PropertyTypeIsAnnotated ? "" : " = null!";
            writer.Line(CodeWriter.GeneratedCodeAttribute);
            writer.Line("[global::System.Diagnostics.DebuggerBrowsable(global::System.Diagnostics.DebuggerBrowsableState.Never)]");
            writer.Line("private " + model.PropertyTypeDisplay + " " + FieldName(model) + initializer + ";");
            writer.Line();
        }

        foreach (SolverDependencyPropertyModel model in members)
        {
            string modifiers = model.PropertyModifiers.Length > 0 ? model.PropertyModifiers + " " : "";
            writer.Line(modifiers + "partial " + model.PropertyTypeDisplay + " " +
                        CodeWriter.Escape(model.PropertyName) + " => " + FieldName(model) + ";");
        }

        writer.Line();
        if (!hook.IsOverride)
        {
            writer.Line("void " + InterfaceType + ".InjectDependencies(" + ResolverType + " resolver)");
            writer.Open();
            writer.Line("InjectSolverDependencies(resolver);");
            writer.Close();
            writer.Line();

            writer.Line("/// <summary>");
            writer.Line("///     Assigns the <c>[SolverDependency]</c> properties declared on this type.");
            writer.Line("/// </summary>");
            writer.Line("/// <param name=\"resolver\">Looks up the services and solvers being injected.</param>");
            writer.Line(CodeWriter.GeneratedCodeAttribute);
            writer.Line(hook.Accessibility + (hook.Accessibility == "private" ? "" : " virtual") +
                        " void InjectSolverDependencies(" + ResolverType + " resolver)");
        }
        else
        {
            writer.Line("/// <inheritdoc />");
            writer.Line(CodeWriter.GeneratedCodeAttribute);
            writer.Line(hook.Accessibility + " override void InjectSolverDependencies(" + ResolverType +
                        " resolver)");
        }

        writer.Open();
        if (hook.CallsBase)
            writer.Line("base.InjectSolverDependencies(resolver);");

        foreach (SolverDependencyPropertyModel model in members)
            writer.Line(FieldName(model) + " = " + ResolveExpression(model) + ";");

        writer.Close();
        writer.CloseTypeChain(type.Type);
        writer.CloseNamespace(type.Type);
        return writer.ToString();
    }

    private static string FieldName(SolverDependencyPropertyModel model)
    {
        return FieldPrefix + model.PropertyName;
    }

    private static string ResolveExpression(SolverDependencyPropertyModel model)
    {
        if (!model.Optional)
        {
            return "resolver.Resolve<" + model.ResolveTypeDisplay + ">(" +
                   SymbolDisplay.FormatLiteral(model.PropertyName, true) + ")";
        }

        // A non-nullable optional property already earned NUMOSDEP006; the '!' keeps that the only warning.
        string suffix = model.PropertyTypeIsAnnotated ? "" : "!";
        return "resolver.ResolveOptional<" + model.ResolveTypeDisplay + ">()" + suffix;
    }
}
