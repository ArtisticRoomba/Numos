using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Numos.API.Generators.Common;

namespace Numos.API.Generators.Module;

/// <summary>
///     Emits <c>NumosModule.EnsureRegistered()</c>, the call that makes an assembly's module initializer run, once per
///     assembly that has chunk fields, solver configurations, <c>[AtmosSolver]</c> classes, or
///     <c>[SimulationComponent]</c> classes to register.
/// </summary>
/// <remarks>
///     <para>
///         The chunk data, state, and solver generators each emit their own module initializer, and none of them can see
///         the others' output. This generator decides on its own whether the assembly has registrations, so the type is
///         emitted exactly once.
///     </para>
///     <para>
///         The decision is a little looser than the generators' own. A <c>[ChunkData]</c> property or a
///         <c>[ChunkComponent]</c> struct counts even when the chunk data generator rejects it. A
///         <c>[NumosSerialize]</c> member counts when its type implements <c>IAtmosSolverConfiguration</c>, even
///         without a constant key. An <c>[AtmosSolver]</c> or <c>[SimulationComponent]</c> class counts even
///         when its generator rejects it. In those cases the type exists and does nothing, and a diagnostic
///         already points at the cause.
///     </para>
///     <para>
///         The assembly that declares <c>IAtmosSolverConfiguration</c> gets no type, because any load path already runs
///         code in it.
///     </para>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class NumosModuleGenerator : IIncrementalGenerator
{
    internal const string HintName = "NumosModule.g.cs";
    internal const string TypeName = "NumosModule";
    internal const string FallbackNamespace = "NumosGenerated";
    internal const string NamespaceStepName = "NumosModuleNamespace";

    private const string ChunkDataMetadataName = "Numos.API.ChunkDataAttribute";
    private const string ChunkComponentMetadataName = "Numos.API.ChunkComponentAttribute";
    private const string SolverMetadataName = "Numos.API.AtmosSolverAttribute";
    private const string SimulationComponentMetadataName = "Numos.API.SimulationComponentAttribute";
    private const string SerializeMetadataName = "Numos.CoreSim.State.NumosSerializeAttribute";
    private const string SolverConfigurationMetadataName = "Numos.CoreSim.IAtmosSolverConfiguration";

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValueProvider<bool> hasChunkFields = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                ChunkDataMetadataName,
                static (node, _) => node is PropertyDeclarationSyntax,
                static (_, _) => true)
            .Collect()
            .Select(static (matches, _) => !matches.IsEmpty);

        IncrementalValueProvider<bool> hasChunkComponents = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                ChunkComponentMetadataName,
                static (node, _) => node is TypeDeclarationSyntax,
                static (_, _) => true)
            .Collect()
            .Select(static (matches, _) => !matches.IsEmpty);

        IncrementalValueProvider<bool> hasSolverConfigurations = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                SerializeMetadataName,
                static (node, _) => node is FieldDeclarationSyntax or PropertyDeclarationSyntax or ParameterSyntax,
                static (attributeContext, _) => IsInSolverConfiguration(attributeContext.TargetSymbol))
            .Collect()
            .Select(static (matches, _) => matches.Contains(true));

        IncrementalValueProvider<bool> hasSolvers = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                SolverMetadataName,
                static (node, _) => node is ClassDeclarationSyntax or RecordDeclarationSyntax,
                static (_, _) => true)
            .Collect()
            .Select(static (matches, _) => !matches.IsEmpty);

        IncrementalValueProvider<bool> hasSimulationComponents = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                SimulationComponentMetadataName,
                static (node, _) => node is ClassDeclarationSyntax or RecordDeclarationSyntax,
                static (_, _) => true)
            .Collect()
            .Select(static (matches, _) => !matches.IsEmpty);

        // Runs on every compilation change, but only the namespace flows on, so the output is cached through that.
        IncrementalValueProvider<string?> moduleNamespace = context.CompilationProvider
            .Select(static (compilation, _) => GetNamespace(compilation))
            .WithTrackingName(NamespaceStepName);

        IncrementalValueProvider<bool> hasRegistrations = hasChunkFields
            .Combine(hasChunkComponents)
            .Combine(hasSolverConfigurations)
            .Combine(hasSolvers)
            .Combine(hasSimulationComponents)
            .Select(static (input, _) =>
                input.Left.Left.Left.Left || input.Left.Left.Left.Right || input.Left.Left.Right || input.Left.Right ||
                input.Right);

        IncrementalValueProvider<string?> target = hasRegistrations
            .Combine(moduleNamespace)
            .Select(static (input, _) => input.Left ? input.Right : null);

        context.RegisterSourceOutput(
            target,
            static (production, @namespace) =>
            {
                if (@namespace is not null)
                    production.AddSource(HintName, Emit(@namespace));
            });
    }

    private static bool IsInSolverConfiguration(ISymbol target)
    {
        INamedTypeSymbol? type = target switch
        {
            IParameterSymbol parameter => parameter.ContainingSymbol?.ContainingType,
            _ => target.ContainingType,
        };

        if (type is null)
            return false;

        foreach (INamedTypeSymbol candidate in type.AllInterfaces)
        {
            if (PartialTypeModel.GetMetadataName(candidate) == SolverConfigurationMetadataName)
                return true;
        }

        return false;
    }

    /// <summary>
    ///     Gets the namespace to emit into, or null when the assembly defines the contract or already declares its own
    ///     <c>NumosModule</c> there.
    /// </summary>
    private static string? GetNamespace(Compilation compilation)
    {
        if (compilation.Assembly.GetTypeByMetadataName(SolverConfigurationMetadataName) is not null)
            return null;

        string name = SanitizeNamespace(compilation.AssemblyName);
        string metadataName = name.Replace("@", "") + "." + TypeName;
        return compilation.Assembly.GetTypeByMetadataName(metadataName) is null ? name : null;
    }

    /// <summary>
    ///     Turns an assembly name into a namespace. Each dot-separated part becomes an identifier, and an empty name
    ///     becomes <see cref="FallbackNamespace" />.
    /// </summary>
    /// <param name="assemblyName">The assembly name, or null.</param>
    internal static string SanitizeNamespace(string? assemblyName)
    {
        var parts = new List<string>();
        foreach (string part in (assemblyName ?? "").Split('.'))
        {
            if (part.Length == 0)
                continue;

            var builder = new StringBuilder(part.Length + 1);
            if (!SyntaxFacts.IsIdentifierStartCharacter(part[0]))
                builder.Append('_');

            foreach (char character in part)
                builder.Append(SyntaxFacts.IsIdentifierPartCharacter(character) ? character : '_');

            string identifier = builder.ToString();
            parts.Add(SyntaxFacts.GetKeywordKind(identifier) != SyntaxKind.None ? "@" + identifier : identifier);
        }

        return parts.Count == 0 ? FallbackNamespace : string.Join(".", parts);
    }

    private static string Emit(string @namespace)
    {
        var writer = new CodeWriter();
        writer.Header();
        writer.Line("namespace " + @namespace);
        writer.Open();
        writer.Line("/// <summary>");
        writer.Line(
            "/// Forces this assembly's Numos registrations (chunk fields, solver configurations, solvers, and simulation " +
            "components) to run.");
        writer.Line("/// </summary>");
        writer.Line(CodeWriter.GeneratedCodeAttribute);
        writer.Line("public static class " + TypeName);
        writer.Open();
        writer.Line("/// <summary>");
        writer.Line("/// Does nothing by itself. Calling it runs the assembly's module initializer if it has not run yet.");
        writer.Line("/// </summary>");
        writer.Line("[global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]");
        writer.Line("public static void EnsureRegistered()");
        writer.Open();
        writer.Close();
        writer.Close();
        writer.Close();
        return writer.ToString();
    }
}
