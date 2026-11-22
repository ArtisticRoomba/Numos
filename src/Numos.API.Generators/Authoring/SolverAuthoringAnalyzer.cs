using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Numos.API.Generators.Authoring;

/// <summary>
///     Reports custom solver code that compiles but breaks replay or a tick without an error.
/// </summary>
/// <remarks>
///     <para>
///         A solver lambda is a lambda or anonymous method passed directly as the <c>solver</c> argument of the
///         delegate <c>AtmosWorldSolverPipeline.Register</c> overload. A delegate held in a variable or a method group
///         is not a solver lambda. Solver code is a solver lambda, including the lambdas nested in it, or any member
///         of a type that implements <c>IAtmosWorldSolver</c>. The checks do not follow calls into other types.
///     </para>
///     <para>
///         A Dangerous chunk view from host code is reported too. An <c>Apply{Name}</c> method of a
///         <c>[SolverCommand]</c> in the same class counts as solver code for that check, because replay re-runs it.
///     </para>
///     <para>
///         The analyzer registers nothing in a compilation that cannot see <c>Numos.API.AtmosWorldSolverPipeline</c>,
///         and skips the Dangerous check when <c>Numos.API.Dangerous</c> is not referenced.
///     </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SolverAuthoringAnalyzer : DiagnosticAnalyzer
{
    private const string PipelineMetadataName = "Numos.API.AtmosWorldSolverPipeline";
    private const string SolverDelegateMetadataName = "Numos.API.AtmosWorldSolver";
    private const string WorldSolverMetadataName = "Numos.API.IAtmosWorldSolver";
    private const string ContextMetadataName = "Numos.API.AtmosWorldSolverContext";
    private const string SolverAttributeMetadataName = "Numos.API.AtmosSolverAttribute";
    private const string SelectorAttributeMetadataName = "Numos.API.SolverNeighborSelectorAttribute";
    private const string CommandAttributeMetadataName = "Numos.API.SolverCommandAttribute";
    private const string DangerousApiMetadataName = "Numos.API.Dangerous.AtmosDangerousApi";
    private const string CommandApplyPrefix = "Apply";

    private static readonly string[] ViewMetadataNames =
    [
        ContextMetadataName,
        "Numos.API.AtmosSolverChunk",
        "Numos.API.AtmosSolverChunks",
        "Numos.API.AtmosSolverGasMixtures",
        "Numos.API.AtmosSolverGasMixturePage",
    ];

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
    [
        SolverAuthoringDiagnostics.CapturedVariableWritten,
        SolverAuthoringDiagnostics.TopologyWithoutNeighbors,
        SolverAuthoringDiagnostics.NondeterministicApi,
        SolverAuthoringDiagnostics.ViewStoredInMember,
        SolverAuthoringDiagnostics.DangerousAccessOutsideSolver,
    ];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(static start =>
        {
            if (Symbols.Create(start.Compilation) is not { } symbols)
                return;

            start.RegisterOperationAction(symbols.AnalyzeRegister, OperationKind.Invocation);
            start.RegisterOperationAction(symbols.AnalyzeClassTopology, OperationKind.PropertyReference);
            start.RegisterOperationAction(
                symbols.AnalyzeNondeterminism,
                OperationKind.Invocation,
                OperationKind.ObjectCreation,
                OperationKind.PropertyReference,
                OperationKind.MethodReference);
            start.RegisterSymbolAction(symbols.AnalyzeMember, SymbolKind.Field, SymbolKind.Property);
            if (symbols.HasDangerousApi)
                start.RegisterOperationAction(symbols.AnalyzeDangerousAccess, OperationKind.Invocation);
        });
    }

    private sealed class Symbols
    {
        private readonly IMethodSymbol _register;
        private readonly IParameterSymbol _solverParameter;
        private readonly IParameterSymbol? _neighborsParameter;
        private readonly IPropertySymbol? _topology;
        private readonly INamedTypeSymbol? _worldSolver;
        private readonly INamedTypeSymbol? _solverAttribute;
        private readonly INamedTypeSymbol? _selectorAttribute;
        private readonly INamedTypeSymbol? _commandAttribute;
        private readonly INamedTypeSymbol? _dangerousApi;
        private readonly ImmutableArray<INamedTypeSymbol> _views;
        private readonly INamedTypeSymbol? _random;
        private readonly INamedTypeSymbol? _guid;
        private readonly INamedTypeSymbol? _dateTime;
        private readonly INamedTypeSymbol? _dateTimeOffset;
        private readonly INamedTypeSymbol? _environment;
        private readonly INamedTypeSymbol? _hashCode;
        private readonly INamedTypeSymbol _string;

        private Symbols(Compilation compilation, IMethodSymbol register, IParameterSymbol solverParameter)
        {
            _register = register;
            _solverParameter = solverParameter;
            _neighborsParameter = register.Parameters.FirstOrDefault(static parameter => parameter.Name == "neighbors");
            INamedTypeSymbol? context = compilation.GetTypeByMetadataName(ContextMetadataName);
            _topology = context?.GetMembers("Topology").OfType<IPropertySymbol>().FirstOrDefault();
            _worldSolver = compilation.GetTypeByMetadataName(WorldSolverMetadataName);
            _solverAttribute = compilation.GetTypeByMetadataName(SolverAttributeMetadataName);
            _selectorAttribute = compilation.GetTypeByMetadataName(SelectorAttributeMetadataName);
            _commandAttribute = compilation.GetTypeByMetadataName(CommandAttributeMetadataName);
            _dangerousApi = compilation.GetTypeByMetadataName(DangerousApiMetadataName);
            _views = [..ViewMetadataNames.Select(compilation.GetTypeByMetadataName).OfType<INamedTypeSymbol>()];
            _random = compilation.GetTypeByMetadataName("System.Random");
            _guid = compilation.GetTypeByMetadataName("System.Guid");
            _dateTime = compilation.GetTypeByMetadataName("System.DateTime");
            _dateTimeOffset = compilation.GetTypeByMetadataName("System.DateTimeOffset");
            _environment = compilation.GetTypeByMetadataName("System.Environment");
            _hashCode = compilation.GetTypeByMetadataName("System.HashCode");
            _string = compilation.GetSpecialType(SpecialType.System_String);
        }

        public bool HasDangerousApi => _dangerousApi is not null;

        public static Symbols? Create(Compilation compilation)
        {
            INamedTypeSymbol? pipeline = compilation.GetTypeByMetadataName(PipelineMetadataName);
            INamedTypeSymbol? solverDelegate = compilation.GetTypeByMetadataName(SolverDelegateMetadataName);
            if (pipeline is null || solverDelegate is null)
                return null;

            foreach (IMethodSymbol method in pipeline.GetMembers("Register").OfType<IMethodSymbol>())
            {
                IParameterSymbol? solver = method.Parameters.FirstOrDefault(static parameter => parameter.Name == "solver");
                if (solver is not null && SymbolEqualityComparer.Default.Equals(solver.Type, solverDelegate))
                    return new Symbols(compilation, method, solver);
            }

            return null;
        }

        public void AnalyzeRegister(OperationAnalysisContext context)
        {
            var invocation = (IInvocationOperation)context.Operation;
            if (!SymbolEqualityComparer.Default.Equals(invocation.TargetMethod.OriginalDefinition, _register))
                return;

            IAnonymousFunctionOperation? lambda = null;
            IArgumentOperation? neighbors = null;
            foreach (IArgumentOperation argument in invocation.Arguments)
            {
                if (argument.Parameter is not { } parameter)
                    continue;

                if (parameter.Ordinal == _solverParameter.Ordinal)
                    lambda = AsLambda(argument.Value);
                else if (_neighborsParameter is not null && parameter.Ordinal == _neighborsParameter.Ordinal)
                    neighbors = argument;
            }

            if (lambda is null)
                return;

            ReportCapturedWrites(context, lambda);

            if (_topology is not null && PassesNoNeighbors(neighbors))
            {
                foreach (IOperation operation in lambda.Body.Descendants())
                {
                    if (operation is IPropertyReferenceOperation reference &&
                        SymbolEqualityComparer.Default.Equals(reference.Property.OriginalDefinition, _topology))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(
                            SolverAuthoringDiagnostics.TopologyWithoutNeighbors,
                            reference.Syntax.GetLocation(),
                            "lambda",
                            "its Register call passes no neighbors",
                            "pass an AtmosNeighborSelection as the neighbors argument"));
                    }
                }
            }
        }

        /// <summary>
        ///     Reports a <c>Topology</c> read in a member of an <c>[AtmosSolver]</c> class that declares no neighbors.
        /// </summary>
        /// <remarks>
        ///     Only the class's own members count. A nested type has its own declaration, or none.
        /// </remarks>
        public void AnalyzeClassTopology(OperationAnalysisContext context)
        {
            var reference = (IPropertyReferenceOperation)context.Operation;
            if (_topology is null ||
                _solverAttribute is null ||
                !SymbolEqualityComparer.Default.Equals(reference.Property.OriginalDefinition, _topology) ||
                context.ContainingSymbol.ContainingType is not { } type ||
                FindAttribute(type, _solverAttribute) is not { } attribute)
            {
                return;
            }

            foreach (KeyValuePair<string, TypedConstant> argument in attribute.NamedArguments)
            {
                if (argument.Key == "Neighbors" && argument.Value.Value is byte mode && mode != 0)
                    return;
            }

            // A solver lambda registered from inside the class has its own neighbors, checked in AnalyzeRegister.
            for (IOperation? current = reference.Parent; current is not null; current = current.Parent)
            {
                if (current is IAnonymousFunctionOperation lambda && IsSolverLambda(lambda))
                    return;
            }

            if (_selectorAttribute is not null)
            {
                foreach (ISymbol member in type.GetMembers())
                {
                    if (member is IMethodSymbol && FindAttribute(member, _selectorAttribute) is not null)
                        return;
                }
            }

            context.ReportDiagnostic(Diagnostic.Create(
                SolverAuthoringDiagnostics.TopologyWithoutNeighbors,
                reference.Syntax.GetLocation(),
                "class '" + type.Name + "'",
                "its [AtmosSolver] attribute sets no Neighbors and it has no [SolverNeighborSelector] method",
                "set Neighbors or add a selector method"));
        }

        public void AnalyzeNondeterminism(OperationAnalysisContext context)
        {
            string? display = context.Operation switch
            {
                IInvocationOperation invocation => DescribeMethod(invocation.TargetMethod),
                IMethodReferenceOperation reference => DescribeMethod(reference.Method),
                IObjectCreationOperation creation => DescribeCreation(creation.Type),
                IPropertyReferenceOperation reference => DescribeProperty(reference.Property),
                _ => null,
            };

            if (display is null || !IsInSolverCode(context.Operation, context.ContainingSymbol))
                return;

            context.ReportDiagnostic(Diagnostic.Create(
                SolverAuthoringDiagnostics.NondeterministicApi,
                context.Operation.Syntax.GetLocation(),
                display));
        }

        public void AnalyzeMember(SymbolAnalysisContext context)
        {
            ITypeSymbol? type;
            switch (context.Symbol)
            {
                case IFieldSymbol field when !field.IsImplicitlyDeclared:
                    type = field.Type;
                    break;
                case IPropertySymbol property when HasBackingField(property):
                    type = property.Type;
                    break;
                default:
                    return;
            }

            // A ref struct can't outlive the callback that created it, so holding a view in one is fine.
            if (context.Symbol.ContainingType is { IsRefLikeType: true })
                return;

            if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
                type = nullable.TypeArguments[0];

            if (!_views.Contains(type, SymbolEqualityComparer.Default) || context.Symbol.Locations.IsDefaultOrEmpty)
                return;

            context.ReportDiagnostic(Diagnostic.Create(
                SolverAuthoringDiagnostics.ViewStoredInMember,
                context.Symbol.Locations[0],
                context.Symbol.Name,
                type.Name));
        }

        /// <summary>
        ///     Reports a call to <c>AtmosDangerousApi.GetChunk</c> or <c>GetChunkField</c> outside solver code.
        /// </summary>
        /// <remarks>
        ///     Replay re-runs solver code, so a deterministic write there lands again. A host write does not.
        /// </remarks>
        public void AnalyzeDangerousAccess(OperationAnalysisContext context)
        {
            var invocation = (IInvocationOperation)context.Operation;
            IMethodSymbol method = invocation.TargetMethod;
            if (method.Name is not ("GetChunk" or "GetChunkField") ||
                !SymbolEqualityComparer.Default.Equals(method.ContainingType, _dangerousApi) ||
                IsInSolverCode(invocation, context.ContainingSymbol) ||
                IsCommandApply(context.ContainingSymbol))
            {
                return;
            }

            Location location = invocation.Syntax is InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax access,
            }
                ? access.Name.GetLocation()
                : invocation.Syntax.GetLocation();

            context.ReportDiagnostic(Diagnostic.Create(
                SolverAuthoringDiagnostics.DangerousAccessOutsideSolver,
                location,
                method.Name));
        }

        /// <summary>
        ///     Is <paramref name="symbol" /> an <c>Apply{Name}</c> method for a <c>[SolverCommand]</c> method
        ///     <c>Name</c> of the same class?
        /// </summary>
        private bool IsCommandApply(ISymbol symbol)
        {
            if (_commandAttribute is null ||
                symbol is not IMethodSymbol { ContainingType: { } type } method ||
                method.Name.Length <= CommandApplyPrefix.Length ||
                !method.Name.StartsWith(CommandApplyPrefix, StringComparison.Ordinal))
            {
                return false;
            }

            string command = method.Name.Substring(CommandApplyPrefix.Length);
            foreach (ISymbol member in type.GetMembers(command))
            {
                if (member is IMethodSymbol && FindAttribute(member, _commandAttribute) is not null)
                    return true;
            }

            return false;
        }

        private static void ReportCapturedWrites(OperationAnalysisContext context, IAnonymousFunctionOperation lambda)
        {
            if (lambda.Syntax is not AnonymousFunctionExpressionSyntax syntax || lambda.SemanticModel is not { } model)
                return;

            DataFlowAnalysis? flow = model.AnalyzeDataFlow(syntax);
            if (flow is null || !flow.Succeeded)
                return;

            var written = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            foreach (ISymbol symbol in flow.WrittenInside)
            {
                // The lambda's own parameters and locals, including ones a nested lambda captures, are per-call.
                if (flow.Captured.Contains(symbol, SymbolEqualityComparer.Default) &&
                    !IsDeclaredInside(symbol, syntax))
                {
                    written.Add(symbol);
                }
            }

            if (written.Count == 0)
                return;

            foreach (IOperation operation in lambda.Body.Descendants())
            {
                ISymbol? target = operation switch
                {
                    ILocalReferenceOperation local => local.Local,
                    IParameterReferenceOperation parameter => parameter.Parameter,
                    _ => null,
                };

                if (target is not null && written.Contains(target) && IsWrite(operation))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        SolverAuthoringDiagnostics.CapturedVariableWritten,
                        operation.Syntax.GetLocation(),
                        target.Name));
                }
            }
        }

        private static bool IsDeclaredInside(ISymbol symbol, SyntaxNode lambda)
        {
            foreach (SyntaxReference reference in symbol.DeclaringSyntaxReferences)
            {
                if (reference.SyntaxTree == lambda.SyntaxTree && lambda.Span.Contains(reference.Span))
                    return true;
            }

            return false;
        }

        private static bool IsWrite(IOperation reference)
        {
            IOperation? parent = reference.Parent;
            switch (parent)
            {
                case IAssignmentOperation assignment when assignment.Target == reference:
                case IIncrementOrDecrementOperation:
                    return true;
                case IArgumentOperation { Parameter.RefKind: RefKind.Ref or RefKind.Out }:
                    return true;
            }

            // A deconstruction writes every variable in its target tuple, however deeply nested.
            IOperation current = reference;
            while (parent is ITupleOperation)
            {
                current = parent;
                parent = parent.Parent;
            }

            return current != reference &&
                   parent is IDeconstructionAssignmentOperation deconstruction &&
                   deconstruction.Target == current;
        }

        private static bool PassesNoNeighbors(IArgumentOperation? neighbors)
        {
            if (neighbors is null || neighbors.ArgumentKind == ArgumentKind.DefaultValue)
                return true;

            IOperation value = neighbors.Value;
            while (value is IConversionOperation conversion)
                value = conversion.Operand;

            return value is ILiteralOperation { ConstantValue: { HasValue: true, Value: null } };
        }

        private static IAnonymousFunctionOperation? AsLambda(IOperation value)
        {
            while (value is IConversionOperation conversion)
                value = conversion.Operand;

            return value is IDelegateCreationOperation { Target: IAnonymousFunctionOperation lambda } ? lambda : null;
        }

        private bool IsInSolverCode(IOperation operation, ISymbol containingSymbol)
        {
            if (_worldSolver is not null &&
                containingSymbol.ContainingType is { } type &&
                type.AllInterfaces.Contains(_worldSolver, SymbolEqualityComparer.Default))
            {
                return true;
            }

            for (IOperation? current = operation.Parent; current is not null; current = current.Parent)
            {
                if (current is IAnonymousFunctionOperation lambda && IsSolverLambda(lambda))
                    return true;
            }

            return false;
        }

        private bool IsSolverLambda(IAnonymousFunctionOperation lambda)
        {
            if (lambda.Parent is not IDelegateCreationOperation creation)
                return false;

            IOperation? parent = creation.Parent;
            while (parent is IConversionOperation)
                parent = parent.Parent;

            return parent is IArgumentOperation { Parameter: { } parameter } argument &&
                   parameter.Ordinal == _solverParameter.Ordinal &&
                   argument.Parent is IInvocationOperation invocation &&
                   SymbolEqualityComparer.Default.Equals(invocation.TargetMethod.OriginalDefinition, _register);
        }

        private string? DescribeMethod(IMethodSymbol method)
        {
            INamedTypeSymbol type = method.ContainingType;
            bool banned = (Is(type, _guid) && method.Name == "NewGuid") ||
                          Is(type, _hashCode) ||
                          (Is(type, _string) && method.Name == "GetHashCode");
            return banned ? Display(type) + "." + method.Name : null;
        }

        private string? DescribeCreation(ITypeSymbol? type)
        {
            return type is not null && (Is(type, _random) || Is(type, _hashCode))
                ? "new " + Display(type) + "(...)"
                : null;
        }

        private string? DescribeProperty(IPropertySymbol property)
        {
            INamedTypeSymbol type = property.ContainingType;
            bool banned = property.Name switch
            {
                "Shared" => Is(type, _random),
                "Now" or "UtcNow" => Is(type, _dateTime) || Is(type, _dateTimeOffset),
                "Today" => Is(type, _dateTime),
                "TickCount" or "TickCount64" => Is(type, _environment),
                _ => Is(type, _hashCode),
            };

            return banned ? Display(type) + "." + property.Name : null;
        }

        private static bool HasBackingField(IPropertySymbol property)
        {
            foreach (ISymbol member in property.ContainingType.GetMembers())
            {
                if (member is IFieldSymbol { AssociatedSymbol: { } associated } &&
                    SymbolEqualityComparer.Default.Equals(associated, property))
                {
                    return true;
                }
            }

            return false;
        }

        private static AttributeData? FindAttribute(ISymbol symbol, INamedTypeSymbol attributeType)
        {
            foreach (AttributeData attribute in symbol.GetAttributes())
            {
                if (SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, attributeType))
                    return attribute;
            }

            return null;
        }

        private static bool Is(ITypeSymbol type, INamedTypeSymbol? expected)
        {
            return expected is not null && SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, expected);
        }

        private static string Display(ITypeSymbol type)
        {
            return type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        }
    }
}
