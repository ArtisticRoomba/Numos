using System.Text;
using Microsoft.CodeAnalysis;

namespace Numos.Replay.SourceGen;

/// <summary>
///     Generates the Apply-switch dispatch for both replay operation families
///     (<c>AtmosKernel.ApplyRecordedOperation</c>, <c>AtmosWorld.ApplyWorldOperation</c>) from their concrete
///     operation types and each host's hand-written <c>Apply(TOperation)</c> overloads, replacing the two
///     hand-maintained exhaustive switch statements those methods used to contain.
/// </summary>
/// <remarks>
///     Lives in <c>Numos.Replay.Analyzers</c> rather than <c>Numos.Replay.SourceGen</c> purely so it needs no new
///     project references: this assembly is already referenced as an analyzer from <c>Numos.CoreSim</c>,
///     <c>Numos.API</c>, and <c>Numos.Serialization</c> for <see cref="ReplayOpcodeConsistencyAnalyzer" />. It
///     self-scopes the same way that analyzer does -- only the compilation that declares both a family's base
///     operation type and its Apply host in source (<c>Numos.CoreSim</c> for the simulation family,
///     <c>Numos.API</c> for the world family) gets a dispatch generated; <c>Numos.Serialization</c> sees both types
///     only as compiled metadata and is skipped.
///     <para>
///         Only the dispatch itself is generated (the switch, plus each host's existing wrapping behavior around
///         it) -- each case's actual logic stays hand-written as an <c>Apply(TOperation)</c> overload, found by
///         exact parameter-type match the same way <c>ReplayCodecGenerator.FindMatchingMethod</c> resolves
///         <c>Write{Name}</c>/<c>Read{Name}</c> methods for custom wire operations. A concrete operation type with no
///         matching overload is <see cref="MissingApplyOverload" />, a build error, instead of a runtime
///         <see cref="ArgumentException" /> from a forgotten hand-written case.
///     </para>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class ApplySwitchGenerator : IIncrementalGenerator
{
    private const string SimOperationBase = "Numos.CoreSim.Replay.AtmosOperation";
    private const string SimApplyHost = "Numos.CoreSim.AtmosKernel";
    private const string WorldOperationBase = "Numos.API.AtmosWorldOperation";
    private const string WorldApplyHost = "Numos.API.AtmosWorld";

    private static readonly SymbolDisplayFormat FullyQualified = SymbolDisplayFormat.FullyQualifiedFormat;

    private static readonly DiagnosticDescriptor MissingApplyOverload = new(
        "NUMOSREPLAYGEN010",
        "Missing Apply overload for replay operation",
        "'{0}' has no accessible '{1}.Apply({0})' overload; replaying it will fail at runtime instead of build time",
        "Numos.Replay.SourceGen",
        DiagnosticSeverity.Error,
        true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterSourceOutput(context.CompilationProvider, static (production, compilation) =>
        {
            TryGenerateDispatch(
                production, compilation, SimOperationBase, SimApplyHost, "ApplyRecordedOperationGenerated");
            TryGenerateDispatch(
                production, compilation, WorldOperationBase, WorldApplyHost, "ApplyWorldOperationGenerated");
        });
    }

    private static void TryGenerateDispatch(
        SourceProductionContext production,
        Compilation compilation,
        string baseTypeMetadataName,
        string hostTypeMetadataName,
        string generatedMethodName)
    {
        var baseType = compilation.GetTypeByMetadataName(baseTypeMetadataName);
        var hostType = compilation.GetTypeByMetadataName(hostTypeMetadataName);

        // Both types must have in-source syntax in this compilation: a metadata-only reference (e.g. seeing
        // Numos.CoreSim.AtmosKernel from Numos.Serialization, which references CoreSim) means this isn't the
        // compilation responsible for this family's dispatch.
        if (baseType is null || hostType is null ||
            hostType.DeclaringSyntaxReferences.Length == 0 || baseType.DeclaringSyntaxReferences.Length == 0)
        {
            return;
        }

        var operations = new List<INamedTypeSymbol>();
        CollectConcreteOperations(compilation.Assembly.GlobalNamespace, baseType, operations);
        if (operations.Count == 0)
            return;

        var cases = new List<(string TypeFqn, string ParameterName)>();
        foreach (var operation in operations)
        {
            var applyMethod = hostType.GetMembers("Apply").OfType<IMethodSymbol>().FirstOrDefault(candidate =>
                candidate.Parameters.Length == 1 &&
                SymbolEqualityComparer.Default.Equals(candidate.Parameters[0].Type, operation));

            if (applyMethod is null)
            {
                production.ReportDiagnostic(Diagnostic.Create(
                    MissingApplyOverload, Location.None, operation.ToDisplayString(FullyQualified), hostTypeMetadataName));
                continue;
            }

            cases.Add((operation.ToDisplayString(FullyQualified), "op"));
        }

        if (cases.Count == 0)
            return;

        production.AddSource(
            $"Numos.Replay.{hostType.Name}.ApplyDispatch.g.cs",
            Generate(hostType, baseType, generatedMethodName, cases));
    }

    private static void CollectConcreteOperations(
        INamespaceSymbol root, INamedTypeSymbol baseType, List<INamedTypeSymbol> results)
    {
        var stack = new Stack<INamespaceOrTypeSymbol>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var container = stack.Pop();
            foreach (var member in container.GetMembers())
            {
                switch (member)
                {
                    case INamespaceSymbol nestedNamespace:
                        stack.Push(nestedNamespace);
                        break;
                    case INamedTypeSymbol type:
                        if (!type.IsAbstract && InheritsFrom(type, baseType))
                            results.Add(type);

                        stack.Push(type);
                        break;
                }
            }
        }
    }

    private static bool InheritsFrom(ITypeSymbol type, INamedTypeSymbol baseType)
    {
        for (var current = type.BaseType; current != null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, baseType))
                return true;
        }

        return false;
    }

    private static string Generate(
        INamedTypeSymbol hostType,
        INamedTypeSymbol baseType,
        string generatedMethodName,
        List<(string TypeFqn, string ParameterName)> cases)
    {
        string hostNamespace = hostType.ContainingNamespace.ToDisplayString();
        string baseTypeFqn = baseType.ToDisplayString(FullyQualified);
        string accessibility = hostType.DeclaredAccessibility == Accessibility.Public ? "public" : "internal";
        string sealedModifier = hostType.IsSealed ? "sealed " : "";

        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated/>");
        builder.AppendLine("#nullable enable");
        builder.AppendLine();
        builder.Append("namespace ").AppendLine(hostNamespace);
        builder.AppendLine("{");
        builder.Append("    ").Append(accessibility).Append(' ').Append(sealedModifier).Append("partial class ").AppendLine(hostType.Name);
        builder.AppendLine("    {");
        builder.Append("        private void ").Append(generatedMethodName).Append('(').Append(baseTypeFqn).AppendLine(" operation)");
        builder.AppendLine("        {");
        builder.AppendLine("            switch (operation)");
        builder.AppendLine("            {");
        foreach (var (typeFqn, parameterName) in cases)
        {
            builder.Append("                case ").Append(typeFqn).Append(' ').Append(parameterName).AppendLine(":");
            builder.Append("                    Apply(").Append(parameterName).AppendLine(");");
            builder.AppendLine("                    return;");
        }

        builder.AppendLine("                default:");
        builder.Append(
            "                    throw new global::System.ArgumentException($\"Unsupported replay operation code {operation.Code}.\", nameof(operation));");
        builder.AppendLine();
        builder.AppendLine("            }");
        builder.AppendLine("        }");
        builder.AppendLine("    }");
        builder.AppendLine("}");
        return builder.ToString();
    }
}
