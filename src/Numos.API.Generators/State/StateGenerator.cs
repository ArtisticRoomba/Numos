using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Numos.API.Generators.Common;

namespace Numos.API.Generators.State;

/// <summary>
///     Implements <c>INumosState&lt;T&gt;</c> for every type with <c>[NumosSerialize]</c> members, plus
///     <c>IAtmosSolverConfiguration</c>'s equality, hash, and snapshot when the type is a solver configuration. A class
///     solver gets <c>INumosStateTarget</c> instead, which reads state back into the registered instance.
/// </summary>
/// <remarks>
///     <para>
///         A type takes part as soon as one of its fields or properties is marked, and there is no type-level
///         attribute. Membership is opt-in, with a warning for each mutable member that is neither marked nor
///         <c>[NumosIgnore]</c>.
///     </para>
///     <para>
///         Quantity aliases are error types in a project that owns a units catalog (see
///         <see cref="QuantityAliasCatalog" />). Shapes therefore keep them unbound until Numos combines them with the
///         catalog.
///     </para>
///     <para>
///         Every solver configuration with a constant key also lands in one module initializer per assembly, which
///         registers it for loading.
///     </para>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class StateGenerator : IIncrementalGenerator
{
    internal const string TypesStepName = "NumosStateTypes";
    internal const string ModuleInitializerHintName = "NumosSolverConfigurationModuleInitializer.g.cs";

    private const string SolverConfigurationMetadataName = "Numos.CoreSim.IAtmosSolverConfiguration";
    private const string WorldSolverMetadataName = "Numos.API.IAtmosWorldSolver";

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Snapshots and their live types belong to SnapshotGenerator, which reads the same attributes differently.
        IncrementalValueProvider<EquatableArray<string>> snapshotParticipants = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                SnapshotGenerator.SnapshotOfMetadataName,
                static (node, _) => node is TypeDeclarationSyntax,
                static (syntaxContext, _) => SnapshotGenerator.ParticipantNames(syntaxContext))
            .SelectMany(static (names, _) => names)
            .Collect()
            .Select(static (names, _) => names.Distinct(StringComparer.Ordinal).OrderBy(static name => name, StringComparer.Ordinal).ToEquatableArray());

        IncrementalValuesProvider<StateTypeResult> types = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is TypeDeclarationSyntax declaration && HasMarkedMemberSyntax(declaration),
                static (syntaxContext, cancellationToken) => CreateDeclaration(syntaxContext, cancellationToken))
            .Where(static declaration => declaration is not null)
            .Combine(snapshotParticipants)
            .Where(static pair => !pair.Right.Contains(pair.Left!.TypeName))
            .Select(static (pair, _) => pair.Left)
            .Combine(QuantityAliasCatalog.Provider(context))
            .Select(static (pair, _) => Resolve(pair.Left!, pair.Right))
            .WithTrackingName(TypesStepName);

        context.RegisterSourceOutput(
            types.Select(static (result, _) => result.Diagnostics),
            static (production, diagnostics) =>
            {
                foreach (DiagnosticInfo diagnostic in diagnostics)
                    production.ReportDiagnostic(diagnostic.ToDiagnostic());
            });

        context.RegisterSourceOutput(
            types.Select(static (result, _) => result.Model).Where(static model => model is not null),
            static (production, model) =>
                production.AddSource(model!.Type.HintName + ".NumosState.g.cs", StateEmitter.Emit(model)));

        IncrementalValueProvider<EquatableArray<string>> registrations = types
            .Select(static (result, _) => result.Registration)
            .Where(static registration => registration is not null)
            .Collect()
            .Select(static (names, _) => names
                .Select(static name => name!)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static name => name, StringComparer.Ordinal)
                .ToEquatableArray());

        GeneratorOutput.RegisterModuleInitializer(
            context,
            registrations,
            ModuleInitializerHintName,
            StateEmitter.EmitModuleInitializer);
    }

    /// <summary>
    ///     A cheap syntactic filter: does this declaration itself mark a field, property, or positional parameter?
    /// </summary>
    /// <remarks>
    ///     Matches on the attribute's simple name, so an alias of the attribute isn't seen. The transform confirms the
    ///     match against the real attribute type.
    /// </remarks>
    private static bool HasMarkedMemberSyntax(TypeDeclarationSyntax declaration)
    {
        if (declaration.ParameterList is { } parameters)
        {
            foreach (ParameterSyntax parameter in parameters.Parameters)
            {
                if (HasSerializeName(parameter.AttributeLists))
                    return true;
            }
        }

        foreach (MemberDeclarationSyntax member in declaration.Members)
        {
            if (member is FieldDeclarationSyntax or PropertyDeclarationSyntax && HasSerializeName(member.AttributeLists))
                return true;
        }

        return false;
    }

    private static bool HasSerializeName(SyntaxList<AttributeListSyntax> lists)
    {
        foreach (AttributeListSyntax list in lists)
        {
            foreach (AttributeSyntax attribute in list.Attributes)
            {
                string name = attribute.Name switch
                {
                    QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
                    AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText,
                    SimpleNameSyntax simple => simple.Identifier.ValueText,
                    _ => "",
                };

                if (name is "NumosSerialize" or "NumosSerializeAttribute")
                    return true;
            }
        }

        return false;
    }

    private static StateTypeDeclaration? CreateDeclaration(GeneratorSyntaxContext context, CancellationToken cancellationToken)
    {
        var syntax = (TypeDeclarationSyntax)context.Node;
        if (context.SemanticModel.GetDeclaredSymbol(syntax, cancellationToken) is not INamedTypeSymbol type)
            return null;

        // A partial type can mark members in several declarations. Only the first of those speaks for the type.
        foreach (SyntaxReference reference in type.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax(cancellationToken) is TypeDeclarationSyntax candidate && HasMarkedMemberSyntax(candidate))
            {
                if (candidate != syntax)
                    return null;

                break;
            }
        }

        if (!StateSymbols.HasSerializedMembers(type))
            return null;

        Compilation compilation = context.SemanticModel.Compilation;
        LocationInfo? typeLocation = LocationInfo.From(syntax.Identifier.GetLocation());
        string typeDisplayName = type.ToDisplayString();
        var diagnostics = new List<DiagnosticInfo>();
        bool canGenerate = true;

        // The innermost type reports first, so walk the outermost-first chain backwards.
        List<(INamedTypeSymbol Type, bool IsPartial)> chain = PartialSyntax.GetContainingChain(type, syntax);
        for (int i = chain.Count - 1; i >= 0; i--)
        {
            (INamedTypeSymbol current, bool isPartial) = chain[i];
            if (!isPartial)
            {
                diagnostics.Add(
                    DiagnosticInfo.Create(StateDiagnostics.TypeNotPartial, typeLocation, current.ToDisplayString(), typeDisplayName));

                canGenerate = false;
            }

            if (current.TypeParameters.Length > 0)
            {
                // Field types would mention type parameters no codec covers, so stop before reporting them too.
                diagnostics.Add(DiagnosticInfo.Create(StateDiagnostics.Generic, typeLocation, typeDisplayName));
                return Skipped(type, diagnostics);
            }
        }

        bool isWorldSolver = Implements(type, WorldSolverMetadataName);
        if (isWorldSolver && type.TypeKind != TypeKind.Class)
        {
            // Its fields would only produce noise about attributes that can't fix anything.
            diagnostics.Add(DiagnosticInfo.Create(StateDiagnostics.WorldSolver, typeLocation, typeDisplayName));
            return Skipped(type, diagnostics);
        }

        if (type.TypeKind == TypeKind.Class &&
            type.BaseType is { SpecialType: not SpecialType.System_Object } baseType)
        {
            diagnostics.Add(
                DiagnosticInfo.Create(StateDiagnostics.DerivedClass, typeLocation, typeDisplayName, baseType.ToDisplayString()));

            canGenerate = false;
        }

        bool isSolverConfiguration = Implements(type, SolverConfigurationMetadataName);
        foreach (string generated in OwnedMemberNames(isSolverConfiguration, isWorldSolver))
        {
            if (FindMethod(type, generated) is { } member)
            {
                diagnostics.Add(
                    DiagnosticInfo.Create(
                        StateDiagnostics.HandWritten,
                        LocationInfo.From(member.Locations.FirstOrDefault()) ?? typeLocation,
                        typeDisplayName,
                        generated));

                canGenerate = false;
            }
        }

        string? stateKey = ReadConstantKey(type, compilation, cancellationToken, out IPropertySymbol? keyProperty);
        if (stateKey is not null && string.IsNullOrWhiteSpace(stateKey))
        {
            diagnostics.Add(
                DiagnosticInfo.Create(
                    StateDiagnostics.BlankKey,
                    LocationInfo.From(keyProperty?.Locations.FirstOrDefault()) ?? typeLocation,
                    typeDisplayName));

            canGenerate = false;
            stateKey = null;
        }
        else if (stateKey is null && isSolverConfiguration)
        {
            diagnostics.Add(
                DiagnosticInfo.Create(
                    StateDiagnostics.NonConstantKey,
                    LocationInfo.From(keyProperty?.Locations.FirstOrDefault()) ?? typeLocation,
                    typeDisplayName));

            canGenerate = false;
        }

        var fields = new List<StateFieldDeclaration>();
        var ids = new Dictionary<int, string>();
        bool immutable = true;
        foreach (ISymbol member in type.GetMembers())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!StateSymbols.TryGetMember(type, member, out ITypeSymbol? memberType, out bool hasStorage))
                continue;

            LocationInfo? location = LocationInfo.From(member.Locations.FirstOrDefault()) ?? typeLocation;
            SerializedMember? parsed = SerializedMember.TryParse(
                member, StateSymbols.FindSerialize(member), hasStorage, typeDisplayName, location, WireNameChecks.None,
                ids, diagnostics);

            if (parsed is null)
                continue;

            if (isWorldSolver && (!parsed.Settable || parsed.InitOnly))
            {
                // A restore writes into the registered instance, so there's no constructor or initializer to use.
                diagnostics.Add(DiagnosticInfo.Create(StateDiagnostics.NotAssignable, location, parsed.DisplayName));
                canGenerate = false;
                continue;
            }

            if (StateSymbols.HasNamedArgument(parsed.Attribute, "Count"))
            {
                diagnostics.Add(DiagnosticInfo.Create(StateDiagnostics.CountOutsideSnapshot, location, parsed.DisplayName));
                canGenerate = false;
                continue;
            }

            ITypeSymbol? codec = parsed.Codec;
            StateShape shape = StateShape.ForMember(memberType!, codec);
            if (codec is not null || (parsed.Settable && !parsed.InitOnly) || !Immutability.IsImmutable(memberType!))
                immutable = false;

            var field = new StateFieldModel(
                parsed.Id, CodeWriter.Escape(member.Name), shape, member.Name, parsed.Settable, parsed.InitOnly);

            fields.Add(new StateFieldDeclaration(field, parsed.DisplayName, location));
        }

        SnapshotKind snapshot = SnapshotKind.None;
        if (isSolverConfiguration && FindMethod(type, "CreateSnapshot") is null)
            snapshot = immutable ? SnapshotKind.Self : SnapshotKind.Copy;

        string? registration = null;
        if (isSolverConfiguration && stateKey is not null)
        {
            if (IsReachableFromAssembly(type))
                registration = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            else
                diagnostics.Add(DiagnosticInfo.Create(StateDiagnostics.NotRegistrable, typeLocation, typeDisplayName));
        }

        return new StateTypeDeclaration(
            PartialTypeModel.From(type),
            type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            typeDisplayName,
            typeLocation,
            type.IsReferenceType,
            isSolverConfiguration,
            isWorldSolver,
            stateKey,
            snapshot,
            registration,
            canGenerate,
            fields.OrderBy(static field => field.Field.Id).ToEquatableArray(),
            Constructors(type),
            diagnostics.ToEquatableArray());
    }

    private static StateTypeDeclaration Skipped(INamedTypeSymbol type, List<DiagnosticInfo> diagnostics)
    {
        return new StateTypeDeclaration(
            PartialTypeModel.From(type),
            type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            type.ToDisplayString(),
            null,
            type.IsReferenceType,
            false,
            false,
            null,
            SnapshotKind.None,
            null,
            false,
            EquatableArray<StateFieldDeclaration>.Empty,
            EquatableArray<StateConstructorModel>.Empty,
            diagnostics.ToEquatableArray());
    }

    /// <summary>
    ///     Reads the constant a get-only <c>Key</c> property returns, such as <c>public string Key =&gt; "my-mod/fire";</c>.
    /// </summary>
    /// <returns>The key, or null when there's no such property or it doesn't return a compile-time constant.</returns>
    private static string? ReadConstantKey(
        INamedTypeSymbol type, Compilation compilation, CancellationToken cancellationToken,
        out IPropertySymbol? keyProperty)
    {
        keyProperty = null;
        foreach (ISymbol member in type.GetMembers())
        {
            if (member is not IPropertySymbol { IsStatic: false, IsIndexer: false, Type.SpecialType: SpecialType.System_String } property ||
                property.IsImplicitlyDeclared ||
                (property.Name != "Key" && !property.Name.EndsWith(".Key", StringComparison.Ordinal)))
            {
                continue;
            }

            keyProperty = property;
            if (property.SetMethod is not null || StateSymbols.HasBackingField(type, property))
                return null;

            foreach (SyntaxReference reference in property.DeclaringSyntaxReferences)
            {
                if (reference.GetSyntax(cancellationToken) is not PropertyDeclarationSyntax declaration)
                    continue;

                ExpressionSyntax? expression = declaration.ExpressionBody?.Expression;
                if (expression is null && declaration.AccessorList is { } accessors)
                {
                    foreach (AccessorDeclarationSyntax accessor in accessors.Accessors)
                    {
                        if (!accessor.IsKind(SyntaxKind.GetAccessorDeclaration))
                            continue;

                        expression = accessor.ExpressionBody?.Expression;
                        if (expression is null &&
                            accessor.Body is { Statements.Count: 1 } body &&
                            body.Statements[0] is ReturnStatementSyntax { Expression: { } returned })
                        {
                            expression = returned;
                        }
                    }
                }

                if (expression is null)
                    return null;

                SemanticModel model = compilation.GetSemanticModel(declaration.SyntaxTree);
                Optional<object?> constant = model.GetConstantValue(expression, cancellationToken);
                return constant is { HasValue: true, Value: string key } ? key : null;
            }

            return null;
        }

        return null;
    }

    /// <summary>
    ///     Whether the generated module initializer, which sits outside the type, can name it.
    /// </summary>
    private static bool IsReachableFromAssembly(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? current = type; current is not null; current = current.ContainingType)
        {
            if (current.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal))
                return false;
        }

        return true;
    }

    /// <summary>
    ///     Gets every constructor the generated reader could call. Accessibility does not matter, because the reader is
    ///     generated into the type. Only the copy constructor of a record is left out.
    /// </summary>
    private static EquatableArray<StateConstructorModel> Constructors(INamedTypeSymbol type)
    {
        var constructors = new List<StateConstructorModel>();
        foreach (IMethodSymbol constructor in type.InstanceConstructors)
        {
            if (type.IsRecord &&
                constructor.Parameters.Length == 1 &&
                SymbolEqualityComparer.Default.Equals(constructor.Parameters[0].Type, type))
            {
                continue;
            }

            constructors.Add(
                new StateConstructorModel(
                    constructor.Parameters
                        .Select(static parameter => new StateParameterModel(
                            parameter.Name,
                            StateShape.FromParameter(parameter.Type),
                            parameter.Type.ToDisplayString(),
                            parameter.HasExplicitDefaultValue ? DefaultLiteral(parameter.ExplicitDefaultValue) : null))
                        .ToEquatableArray()));
        }

        return constructors.ToEquatableArray();
    }

    private static string DefaultLiteral(object? value)
    {
        return value switch
        {
            null => "default",
            float.NaN => "float.NaN",
            float.PositiveInfinity => "float.PositiveInfinity",
            float.NegativeInfinity => "float.NegativeInfinity",
            double.NaN => "double.NaN",
            double.PositiveInfinity => "double.PositiveInfinity",
            double.NegativeInfinity => "double.NegativeInfinity",
            _ => SymbolDisplay.FormatPrimitive(value, true, false) ?? "default",
        };
    }

    private static IEnumerable<string> OwnedMemberNames(bool isSolverConfiguration, bool isWorldSolver)
    {
        if (isWorldSolver)
        {
            yield return "WriteState";
            yield return "ReadStateInto";
        }

        if (!isSolverConfiguration)
            yield break;

        yield return "SemanticallyEquals";
        yield return "ComputeStateHash";
    }

    private static ISymbol? FindMethod(INamedTypeSymbol type, string name)
    {
        foreach (ISymbol member in type.GetMembers())
        {
            if (member.IsImplicitlyDeclared ||
                member is not IMethodSymbol { MethodKind: MethodKind.Ordinary or MethodKind.ExplicitInterfaceImplementation })
            {
                continue;
            }

            if (member.Name == name || member.Name.EndsWith("." + name, StringComparison.Ordinal))
                return member;
        }

        return null;
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

    private static StateTypeResult Resolve(StateTypeDeclaration declaration, EquatableArray<QuantityAlias> aliases)
    {
        var diagnostics = new List<DiagnosticInfo>(declaration.Diagnostics);
        var fields = new List<StateFieldModel>();
        foreach (StateFieldDeclaration field in declaration.Fields)
        {
            if (field.Resolve(aliases, diagnostics, true) is { } resolved)
                fields.Add(resolved);
        }

        bool hasErrors = diagnostics.Any(static diagnostic => diagnostic.Descriptor.DefaultSeverity == DiagnosticSeverity.Error);
        if (!declaration.CanGenerate || hasErrors)
            return new StateTypeResult(null, null, diagnostics.ToEquatableArray());

        // Only checked once the fields themselves are fine, so a bad field type isn't reported twice. A solver is
        // restored in place, so it needs no constructor the reader can call.
        string reason = "";
        StateReadPlan? plan = declaration.IsWorldSolver
            ? new StateReadPlan(EquatableArray<string>.Empty, EquatableArray<StateInitializerModel>.Empty)
            : StateBinding.Bind(declaration.Constructors, fields, aliases, out reason);

        if (plan is null)
        {
            diagnostics.Add(DiagnosticInfo.Create(StateDiagnostics.NoConstructor, declaration.Location, declaration.DisplayName, reason));
            return new StateTypeResult(null, null, diagnostics.ToEquatableArray());
        }

        var model = new StateTypeModel(
            declaration.Type,
            declaration.TypeName,
            declaration.IsReferenceType,
            declaration.IsSolverConfiguration,
            declaration.IsWorldSolver,
            declaration.StateKey,
            declaration.Snapshot,
            fields.ToEquatableArray(),
            plan);

        return new StateTypeResult(model, declaration.Registration, diagnostics.ToEquatableArray());
    }
}
