using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Numos.API.Generators.Common;

namespace Numos.API.Generators.State;

/// <summary>
///     Implements <c>[SolverCommand]</c> partial methods and the <c>IAtmosSolverCommands</c> dispatch that replays
///     them.
/// </summary>
/// <remarks>
///     <para>
///         Output is one file per class. Each command method encodes its arguments with the state codecs, runs the
///         author's <c>Apply{Name}</c> inside an <c>AtmosSolverCommandScope</c>, and hands the payload to the scope.
///         <c>InvokeCommand</c> switches on the command id, decodes the payload the same way, and calls
///         <c>Apply{Name}</c>. A module initializer registers the wire names, so a replay error can print them.
///     </para>
///     <para>
///         <c>Apply{Name}</c> may take a leading <c>AtmosWorld</c>. Both calls then pass the world from the scope, so a
///         catalog solver that a restore recreated can still reach its simulations. The world is not in the payload.
///     </para>
///     <para>
///         The switch lives in an <c>InvokeSolverCommand</c> hook, the way <c>InjectSolverDependencies</c> works for
///         dependencies. A class whose base classes have no commands implements the interface and introduces the hook
///         (<c>protected virtual</c>, or <c>private</c> when sealed). A class deriving from one that has commands
///         overrides it and falls back to the base for an id it doesn't know, so a base command still replays.
///     </para>
///     <para>
///         Parameter types are classified with <see cref="StateShape" />, the same as <c>[NumosSerialize]</c> members,
///         so a command accepts exactly what a state field accepts. Quantity aliases stay unbound until the step after
///         <c>Collect</c> combines them with the units catalog (see <see cref="QuantityAliasCatalog" />).
///     </para>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class SolverCommandGenerator : IIncrementalGenerator
{
    internal const string MethodsStepName = "SolverCommandMethods";
    internal const string TypesStepName = "SolverCommandTypes";

    private const string AttributeMetadataName = "Numos.API.SolverCommandAttribute";
    private const string WorldSolverMetadataName = "Numos.API.IAtmosWorldSolver";
    private const string WorldMetadataName = "Numos.API.AtmosWorld";

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<SolverCommandDeclaration> methods = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                AttributeMetadataName,
                static (node, _) => node is MethodDeclarationSyntax,
                static (attributeContext, cancellationToken) => CreateDeclaration(attributeContext, cancellationToken))
            .Where(static declaration => declaration is not null)
            .Select(static (declaration, _) => declaration!)
            .WithTrackingName(MethodsStepName);

        IncrementalValueProvider<GeneratorOutput<SolverCommandTypeModel>> output = methods
            .Collect()
            .Combine(QuantityAliasCatalog.Provider(context))
            .Select(static (pair, _) => Analyze(pair.Left, pair.Right));

        GeneratorOutput.Register(
            context,
            output,
            TypesStepName,
            static type => (type.Type.HintName + ".NumosCommands.g.cs", SolverCommandEmitter.Emit(type)));
    }

    private static SolverCommandDeclaration? CreateDeclaration(
        GeneratorAttributeSyntaxContext context,
        CancellationToken cancellationToken)
    {
        if (context.TargetSymbol is not IMethodSymbol method || context.Attributes.IsDefaultOrEmpty)
            return null;

        var syntax = (MethodDeclarationSyntax)context.TargetNode;
        INamedTypeSymbol type = method.ContainingType;
        AttributeData attribute = context.Attributes[0];
        string typeDisplayName = type.ToDisplayString();
        string displayName = typeDisplayName + "." + method.Name;
        LocationInfo? location = LocationInfo.From(syntax.Identifier.GetLocation());
        LocationInfo? typeLocation = syntax.Parent is BaseTypeDeclarationSyntax declaration
            ? LocationInfo.From(declaration.Identifier.GetLocation())
            : location;

        var diagnostics = new List<DiagnosticInfo>();
        var typeDiagnostics = new List<DiagnosticInfo>();
        foreach ((INamedTypeSymbol containing, bool isPartial) in PartialSyntax.GetContainingChain(type, syntax))
        {
            if (containing.IsFileLocal)
            {
                typeDiagnostics.Add(
                    DiagnosticInfo.Create(
                        SolverCommandDiagnostics.NotSolverClass,
                        typeLocation,
                        displayName,
                        containing.ToDisplayString(),
                        "is file-local, so generated code cannot reopen it"));
            }

            if (!isPartial)
            {
                typeDiagnostics.Add(
                    DiagnosticInfo.Create(
                        SolverCommandDiagnostics.NotSolverClass,
                        typeLocation,
                        displayName,
                        containing.ToDisplayString(),
                        "is not partial"));
            }
        }

        if (type.TypeKind != TypeKind.Class)
        {
            typeDiagnostics.Add(
                DiagnosticInfo.Create(
                    SolverCommandDiagnostics.NotSolverClass, typeLocation, displayName, typeDisplayName, "is not a class"));
        }
        else if (!Implements(type, WorldSolverMetadataName))
        {
            typeDiagnostics.Add(
                DiagnosticInfo.Create(
                    SolverCommandDiagnostics.NotSolverClass,
                    typeLocation,
                    displayName,
                    typeDisplayName,
                    "does not implement IAtmosWorldSolver"));
        }

        if (method.IsStatic ||
            !method.ReturnsVoid ||
            method.IsGenericMethod ||
            !method.IsPartialDefinition ||
            method.PartialImplementationPart is not null)
        {
            diagnostics.Add(
                DiagnosticInfo.Create(SolverCommandDiagnostics.NotPartialDefinition, location, displayName, method.Name));
        }

        string wireName = method.Name;
        if (attribute.ConstructorArguments.Length == 1)
        {
            wireName = attribute.ConstructorArguments[0].Value as string ?? "";
            if (string.IsNullOrWhiteSpace(wireName))
            {
                LocationInfo? attributeLocation =
                    LocationInfo.From(attribute.ApplicationSyntaxReference?.GetSyntax(cancellationToken)) ?? location;

                diagnostics.Add(DiagnosticInfo.Create(SolverCommandDiagnostics.BlankWireName, attributeLocation, displayName));
            }
        }

        var parameters = new List<SolverCommandParameterDeclaration>();
        var ids = new Dictionary<int, string>();
        foreach (IParameterSymbol parameter in method.Parameters)
        {
            LocationInfo? parameterLocation = LocationInfo.From(parameter.Locations.FirstOrDefault()) ?? location;
            string? reason = null;
            int id = FieldIds.Compute(parameter.Name);
            if (parameter.RefKind != RefKind.None)
                reason = "is passed by ref, out, or in; command arguments are recorded by value";
            else if (parameter.IsParams)
                reason = "is a params parameter; declare it as an array or a list instead";
            else if (ids.TryGetValue(id, out string? other))
                reason = "has the same field id as parameter '" + other + "'; rename one of them";

            ids[id] = parameter.Name;
            if (reason is not null)
            {
                diagnostics.Add(
                    DiagnosticInfo.Create(
                        SolverCommandDiagnostics.UnsupportedParameter, parameterLocation, parameter.Name, displayName, reason));

                continue;
            }

            parameters.Add(
                new SolverCommandParameterDeclaration(
                    new SolverCommandParameterModel(
                        CodeWriter.Escape(parameter.Name),
                        id,
                        parameter.Type.ToDisplayString(PartialSyntax.FullyQualifiedWithNullability),
                        StateShape.From(parameter.Type)),
                    parameterLocation));
        }

        string applyName = "Apply" + method.Name;
        bool? passesWorld = FindApply(type, applyName, method.Parameters);
        if (passesWorld is null)
        {
            string types = string.Join(", ", method.Parameters.Select(static parameter => parameter.Type.ToDisplayString()));
            diagnostics.Add(
                DiagnosticInfo.Create(
                    SolverCommandDiagnostics.MissingApply,
                    location,
                    displayName,
                    applyName,
                    types,
                    types.Length == 0 ? "AtmosWorld" : "AtmosWorld, " + types));
        }

        bool failed = diagnostics.Count > 0;
        SolverCommandModel? command = failed
            ? null
            : new SolverCommandModel(
                CodeWriter.Escape(method.Name),
                PartialSyntax.ModifiersWithoutPartial(syntax.Modifiers),
                wireName,
                FieldIds.Compute(wireName),
                CodeWriter.Escape(applyName),
                passesWorld == true,
                EquatableArray<SolverCommandParameterModel>.Empty);

        (SolverCommandHookModel hook, EquatableArray<SolverCommandBaseCommand> baseCommands) =
            AnalyzeBases(type, attribute.AttributeClass);

        return new SolverCommandDeclaration(
            PartialTypeModel.From(type),
            type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            typeDisplayName,
            displayName,
            command,
            parameters.ToEquatableArray(),
            location,
            typeLocation,
            diagnostics.ToEquatableArray(),
            typeDiagnostics.ToEquatableArray(),
            hook,
            baseCommands);
    }

    /// <summary>
    ///     Works out whether the class introduces the dispatch hook or overrides a base class's, and collects the base
    ///     commands that the class's own commands must not share an id with.
    /// </summary>
    /// <remarks>
    ///     The nearest base class with an overridable hook, or with commands in this compilation, decides the hook. A
    ///     base in this compilation gets its hook in the same generator run, where the hook isn't visible yet, so its
    ///     <c>[SolverCommand]</c> methods stand in for it.
    /// </remarks>
    private static (SolverCommandHookModel Hook, EquatableArray<SolverCommandBaseCommand> BaseCommands) AnalyzeBases(
        INamedTypeSymbol type, INamedTypeSymbol? attributeType)
    {
        SolverCommandHookModel? hook = null;
        var commands = new List<SolverCommandBaseCommand>();
        for (INamedTypeSymbol? current = type.TypeKind == TypeKind.Class ? type.BaseType : null;
             current is not null && current.SpecialType != SpecialType.System_Object;
             current = current.BaseType)
        {
            bool hasCommands = false;
            foreach (ISymbol member in current.GetMembers())
            {
                if (member is not IMethodSymbol method || FindAttribute(method, attributeType) is not { } command)
                    continue;

                hasCommands = true;
                string wireName = command.ConstructorArguments.Length == 1
                    ? command.ConstructorArguments[0].Value as string ?? ""
                    : method.Name;

                // A blank name is the base's own NUMOSCOMMAND006.
                if (!string.IsNullOrWhiteSpace(wireName))
                    commands.Add(new SolverCommandBaseCommand(FieldIds.Compute(wireName), current.ToDisplayString() + "." + method.Name));
            }

            if (hook is not null)
                continue;

            if (FindOverridableHook(current, type) is { } existing)
                hook = new SolverCommandHookModel(true, OverrideAccessibility(existing, type));
            else if (hasCommands && !current.DeclaringSyntaxReferences.IsDefaultOrEmpty)
                hook = new SolverCommandHookModel(true, "protected");
        }

        return (hook ?? new SolverCommandHookModel(false, type.IsSealed ? "private" : "protected"), commands.ToEquatableArray());
    }

    private static IMethodSymbol? FindOverridableHook(INamedTypeSymbol baseType, INamedTypeSymbol derived)
    {
        foreach (ISymbol member in baseType.GetMembers(SolverCommandEmitter.HookName))
        {
            if (member is not IMethodSymbol
                {
                    IsStatic: false,
                    IsSealed: false,
                    IsGenericMethod: false,
                    Parameters.Length: 3,
                    ReturnType.SpecialType: SpecialType.System_Boolean,
                } method ||
                !(method.IsVirtual || method.IsOverride || method.IsAbstract))
            {
                continue;
            }

            bool sameAssembly = SymbolEqualityComparer.Default.Equals(baseType.ContainingAssembly, derived.ContainingAssembly);
            bool visible = method.DeclaredAccessibility switch
            {
                Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal => true,
                Accessibility.Internal or Accessibility.ProtectedAndInternal => sameAssembly,
                _ => false,
            };

            if (visible)
                return method;
        }

        return null;
    }

    private static string OverrideAccessibility(IMethodSymbol method, INamedTypeSymbol derived)
    {
        bool sameAssembly = SymbolEqualityComparer.Default.Equals(method.ContainingAssembly, derived.ContainingAssembly);
        return method.DeclaredAccessibility switch
        {
            Accessibility.Public => "public",
            Accessibility.Internal => "internal",
            Accessibility.ProtectedAndInternal => "private protected",
            // Overriding a protected internal member from another assembly has to drop the internal part.
            Accessibility.ProtectedOrInternal => sameAssembly ? "protected internal" : "protected",
            _ => "protected",
        };
    }

    private static AttributeData? FindAttribute(IMethodSymbol method, INamedTypeSymbol? attributeType)
    {
        if (attributeType is null)
            return null;

        foreach (AttributeData attribute in method.GetAttributes())
        {
            if (SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, attributeType))
                return attribute;
        }

        return null;
    }

    /// <summary>
    ///     Finds the non-generic instance method <paramref name="name" /> of <paramref name="type" /> whose parameters
    ///     are by-value and of the same types as <paramref name="parameters" />, optionally after a leading
    ///     <c>AtmosWorld</c>.
    /// </summary>
    /// <returns>
    ///     <see langword="null" /> when there is no such method. Otherwise whether the method takes the world. When both
    ///     forms exist, the one with the world wins.
    /// </returns>
    /// <remarks>
    ///     An unbound quantity alias is an error type, which never equals another symbol, so error types compare by
    ///     name.
    /// </remarks>
    private static bool? FindApply(INamedTypeSymbol type, string name, ImmutableArray<IParameterSymbol> parameters)
    {
        bool? found = null;
        foreach (ISymbol member in type.GetMembers(name))
        {
            if (member is not IMethodSymbol { IsStatic: false, MethodKind: MethodKind.Ordinary, IsGenericMethod: false } apply)
                continue;

            int offset = apply.Parameters.Length - parameters.Length;
            if (offset == 1)
            {
                IParameterSymbol world = apply.Parameters[0];
                if (world.RefKind != RefKind.None ||
                    world.Type is not INamedTypeSymbol worldType ||
                    PartialTypeModel.GetMetadataName(worldType) != WorldMetadataName)
                {
                    continue;
                }
            }
            else if (offset != 0)
            {
                continue;
            }

            bool matches = true;
            for (int index = 0; index < parameters.Length && matches; index++)
            {
                IParameterSymbol candidate = apply.Parameters[offset + index];
                matches = candidate.RefKind == RefKind.None && SameType(candidate.Type, parameters[index].Type);
            }

            if (!matches)
                continue;

            if (offset == 1)
                return true;

            found = false;
        }

        return found;
    }

    private static bool SameType(ITypeSymbol left, ITypeSymbol right)
    {
        if (left.TypeKind == TypeKind.Error || right.TypeKind == TypeKind.Error)
            return left.ToDisplayString() == right.ToDisplayString();

        return SymbolEqualityComparer.Default.Equals(left, right);
    }

    private static bool Implements(INamedTypeSymbol type, string interfaceMetadataName)
    {
        foreach (INamedTypeSymbol candidate in type.AllInterfaces)
        {
            if (PartialTypeModel.GetMetadataName(candidate) == interfaceMetadataName)
                return true;
        }

        return false;
    }

    /// <summary>
    ///     Groups the commands by class, resolves their parameter shapes, and checks the rules that span a class.
    /// </summary>
    private static GeneratorOutput<SolverCommandTypeModel> Analyze(
        ImmutableArray<SolverCommandDeclaration> declarations,
        EquatableArray<QuantityAlias> aliases)
    {
        if (declarations.IsDefaultOrEmpty)
            return GeneratorOutput<SolverCommandTypeModel>.Empty;

        var diagnostics = new List<DiagnosticInfo>();
        var types = new List<SolverCommandTypeModel>();
        foreach (IGrouping<string, SolverCommandDeclaration> type in declarations
                     .GroupBy(static declaration => declaration.Type.MetadataName, StringComparer.Ordinal)
                     .OrderBy(static group => group.Key, StringComparer.Ordinal))
        {
            SolverCommandDeclaration[] members = type
                .OrderBySource(static declaration => declaration.Location)
                .ThenBy(static declaration => declaration.DisplayName, StringComparer.Ordinal)
                .ToArray();

            var reportedTypeDiagnostics = new HashSet<(DiagnosticDescriptor, string, string)>();
            bool typeHasErrors = false;
            var valid = new List<(SolverCommandModel Command, SolverCommandDeclaration Declaration)>();
            foreach (SolverCommandDeclaration declaration in members)
            {
                foreach (DiagnosticInfo diagnostic in declaration.TypeDiagnostics)
                {
                    typeHasErrors = true;

                    // One report per containing type and reason, whichever command found it first.
                    if (reportedTypeDiagnostics.Add((diagnostic.Descriptor, diagnostic.Arguments[1], diagnostic.Arguments[2])))
                        diagnostics.Add(diagnostic);
                }

                diagnostics.AddRange(declaration.Diagnostics);
                if (declaration.Command is not { } command)
                    continue;

                var parameters = new List<SolverCommandParameterModel>();
                bool resolved = true;
                foreach (SolverCommandParameterDeclaration parameter in declaration.Parameters)
                {
                    StateShape shape = parameter.Parameter.Shape.Resolve(aliases);
                    if (shape.FindUnsupported() is { } unsupported)
                    {
                        diagnostics.Add(
                            DiagnosticInfo.Create(
                                SolverCommandDiagnostics.UnsupportedParameter,
                                parameter.Location,
                                parameter.Parameter.Name.TrimStart('@'),
                                declaration.DisplayName,
                                "has type '" + unsupported + "', which the state schema does not handle"));

                        resolved = false;
                        continue;
                    }

                    parameters.Add(parameter.Parameter with { Shape = shape });
                }

                if (resolved)
                    valid.Add((command with { Parameters = parameters.ToEquatableArray() }, declaration));
            }

            var duplicates = new HashSet<int>();
            for (int index = 0; index < valid.Count; index++)
            {
                // The base's dispatch already has the id, so only the derived command is dropped.
                if (members[0].BaseCommands.FirstOrDefault(entry => entry.Id == valid[index].Command.Id) is { } inherited)
                {
                    diagnostics.Add(
                        DiagnosticInfo.Create(
                            SolverCommandDiagnostics.DuplicateId,
                            valid[index].Declaration.Location,
                            inherited.DisplayName,
                            valid[index].Declaration.DisplayName,
                            valid[index].Declaration.TypeDisplayName));

                    duplicates.Add(valid[index].Command.Id);
                    continue;
                }

                for (int other = 0; other < index; other++)
                {
                    if (valid[other].Command.Id != valid[index].Command.Id)
                        continue;

                    diagnostics.Add(
                        DiagnosticInfo.Create(
                            SolverCommandDiagnostics.DuplicateId,
                            valid[index].Declaration.Location,
                            valid[other].Declaration.DisplayName,
                            valid[index].Declaration.DisplayName,
                            valid[index].Declaration.TypeDisplayName));

                    duplicates.Add(valid[index].Command.Id);
                    break;
                }
            }

            SolverCommandModel[] commands = valid
                .Select(static entry => entry.Command)
                .Where(command => !duplicates.Contains(command.Id))
                .ToArray();

            if (typeHasErrors || commands.Length == 0)
                continue;

            types.Add(
                new SolverCommandTypeModel(
                    members[0].Type,
                    members[0].TypeName,
                    commands.ToEquatableArray(),
                    members[0].Hook));
        }

        return new GeneratorOutput<SolverCommandTypeModel>(diagnostics.ToEquatableArray(), types.ToEquatableArray());
    }
}
