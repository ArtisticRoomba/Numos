using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Numos.API.Generators.Common;
using Numos.API.Generators.State;

namespace Numos.API.Generators.ChunkData;

/// <summary>
///     Suppresses the compiler's unused-field warnings on the marked fields of a <c>[ChunkComponent]</c> struct.
/// </summary>
/// <remarks>
///     The struct is a schema. Nothing ever reads or writes its fields, because the generated <c>Fields</c> and
///     <c>View</c> members point at chunk storage instead. Without this, an internal or private component field is
///     CS0649 or CS0169, and a project that treats warnings as errors fails to build. An unmarked field keeps its
///     warnings, because <c>NUMOSCOMPONENT004</c> already reports it.
/// </remarks>
[DiagnosticAnalyzer(Microsoft.CodeAnalysis.LanguageNames.CSharp)]
public sealed class ChunkComponentFieldSuppressor : DiagnosticSuppressor
{
    private const string ComponentAttributeMetadataName = "Numos.API.ChunkComponentAttribute";

    private const string Justification =
        "A [ChunkComponent] field describes a chunk field. The generated Fields and View members hold its values.";

    private static readonly SuppressionDescriptor NeverAssigned = new("NUMOSCOMPONENTSPR0649", "CS0649", Justification);
    private static readonly SuppressionDescriptor NeverUsed = new("NUMOSCOMPONENTSPR0169", "CS0169", Justification);
    private static readonly SuppressionDescriptor AssignedNeverRead = new("NUMOSCOMPONENTSPR0414", "CS0414", Justification);

    /// <inheritdoc />
    public override ImmutableArray<SuppressionDescriptor> SupportedSuppressions { get; } =
        [NeverAssigned, NeverUsed, AssignedNeverRead];

    /// <inheritdoc />
    public override void ReportSuppressions(SuppressionAnalysisContext context)
    {
        foreach (Diagnostic diagnostic in context.ReportedDiagnostics)
        {
            SuppressionDescriptor? descriptor = diagnostic.Id switch
            {
                "CS0649" => NeverAssigned,
                "CS0169" => NeverUsed,
                "CS0414" => AssignedNeverRead,
                _ => null,
            };

            if (descriptor is null || diagnostic.Location.SourceTree is not { } tree)
                continue;

            SyntaxNode node = tree.GetRoot(context.CancellationToken).FindNode(diagnostic.Location.SourceSpan);
            if (node.FirstAncestorOrSelf<VariableDeclaratorSyntax>() is not { } declarator)
                continue;

            SemanticModel model = context.GetSemanticModel(tree);
            if (model.GetDeclaredSymbol(declarator, context.CancellationToken) is IFieldSymbol field &&
                IsComponentField(field))
                context.ReportSuppression(Suppression.Create(descriptor, diagnostic));
        }
    }

    private static bool IsComponentField(IFieldSymbol field)
    {
        if (field.IsStatic || StateSymbols.FindSerialize(field) is null && !StateSymbols.IsScratch(field))
            return false;

        return field.ContainingType.GetAttributes().Any(static attribute =>
            attribute.AttributeClass is { } attributeClass &&
            PartialTypeModel.GetMetadataName(attributeClass) == ComponentAttributeMetadataName);
    }
}
