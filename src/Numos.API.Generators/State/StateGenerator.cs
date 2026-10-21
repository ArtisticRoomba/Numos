using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Numos.API.Generators.Common;

namespace Numos.API.Generators.State;

/// <summary>
///     Implements <c>INumosState&lt;T&gt;</c> for every type with <c>[NumosSerialize]</c> members, plus
///     <c>IAtmosSolverConfiguration</c>'s equality, hash, and snapshot when the type is a solver configuration.
/// </summary>
/// <remarks>
///     <para>
///         A type takes part as soon as one of its fields or properties is marked; there's no type-level attribute.
///         Membership is opt-in, with a warning for each mutable member that's neither marked nor
///         <c>[NumosIgnore]</c>.
///     </para>
///     <para>
///         Quantity aliases are error types in a project that owns a units catalog (see
///         <see cref="QuantityAliasCatalog" />), so shapes keep them unbound until they're combined with the catalog.
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

    private const string SerializeMetadataName = "Numos.CoreSim.State.NumosSerializeAttribute";
    private const string IgnoreMetadataName = "Numos.CoreSim.State.NumosIgnoreAttribute";
    private const string SolverConfigurationMetadataName = "Numos.CoreSim.IAtmosSolverConfiguration";
    private const string WorldSolverMetadataName = "Numos.API.IAtmosWorldSolver";

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<StateTypeResult> types = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is TypeDeclarationSyntax declaration && HasMarkedMemberSyntax(declaration),
                static (syntaxContext, cancellationToken) => CreateDeclaration(syntaxContext, cancellationToken))
            .Where(static declaration => declaration is not null)
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

        context.RegisterSourceOutput(
            registrations,
            static (production, names) =>
            {
                if (names.Count > 0)
                    production.AddSource(ModuleInitializerHintName, StateEmitter.EmitModuleInitializer(names));
            });
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

        // A partial type can mark members in several declarations; only the first of those speaks for the type.
        foreach (SyntaxReference reference in type.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax(cancellationToken) is TypeDeclarationSyntax candidate && HasMarkedMemberSyntax(candidate))
            {
                if (candidate != syntax)
                    return null;

                break;
            }
        }

        if (!HasSerializedMembers(type))
            return null;

        Compilation compilation = context.SemanticModel.Compilation;
        LocationInfo? typeLocation = LocationInfo.From(syntax.Identifier.GetLocation());
        string typeDisplayName = type.ToDisplayString();
        var diagnostics = new List<DiagnosticInfo>();
        bool canGenerate = true;

        SyntaxNode? node = syntax;
        for (INamedTypeSymbol? current = type; current is not null; current = current.ContainingType)
        {
            while (node is not null and not TypeDeclarationSyntax)
                node = node.Parent;

            if (node is not TypeDeclarationSyntax declaration || !declaration.Modifiers.Any(SyntaxKind.PartialKeyword))
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

            node = node?.Parent;
        }

        if (Implements(type, WorldSolverMetadataName))
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
        foreach (string generated in OwnedMemberNames(isSolverConfiguration))
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
            if (!TryGetMember(type, member, out ITypeSymbol? memberType, out bool hasStorage))
                continue;

            LocationInfo? location = LocationInfo.From(member.Locations.FirstOrDefault()) ?? typeLocation;
            string displayName = typeDisplayName + "." + member.Name;
            AttributeData? serialize = FindAttribute(member, SerializeMetadataName);
            bool ignored = FindAttribute(member, IgnoreMetadataName) is not null;

            if (member.IsStatic || member is IFieldSymbol { IsConst: true })
            {
                // Static members are never state; only flag the ones claiming otherwise.
                if (serialize is not null)
                    diagnostics.Add(DiagnosticInfo.Create(StateDiagnostics.StaticField, location, displayName));

                continue;
            }

            if (serialize is not null && ignored)
            {
                diagnostics.Add(DiagnosticInfo.Create(StateDiagnostics.BothTags, location, displayName));
                continue;
            }

            if (serialize is null)
            {
                if (!ignored && hasStorage && !member.IsImplicitlyDeclared && IsMutable(member))
                    diagnostics.Add(DiagnosticInfo.Create(StateDiagnostics.Unmarked, location, displayName, typeDisplayName));

                continue;
            }

            string wireName = member.Name;
            if (serialize.ConstructorArguments.Length == 1)
            {
                string? declared = serialize.ConstructorArguments[0].Value as string;
                if (declared is null || declared.Trim().Length == 0)
                {
                    diagnostics.Add(DiagnosticInfo.Create(StateDiagnostics.BlankWireName, location, displayName));
                    continue;
                }

                wireName = declared;
            }

            int id = FieldIds.Compute(wireName);
            if (ids.TryGetValue(id, out string? existing))
            {
                diagnostics.Add(
                    DiagnosticInfo.Create(
                        StateDiagnostics.IdCollision,
                        location,
                        displayName,
                        typeDisplayName + "." + existing,
                        id.ToString(CultureInfo.InvariantCulture)));

                continue;
            }

            ids.Add(id, member.Name);
            bool settable = member switch
            {
                IFieldSymbol storage => !storage.IsReadOnly,
                IPropertySymbol property => property.SetMethod is not null,
                _ => false,
            };

            bool initOnly = member is IPropertySymbol { SetMethod.IsInitOnly: true };
            ITypeSymbol? codec = CodecOf(serialize);
            StateShape shape = codec is not null ? StateShape.FromCodec(memberType!, codec) : StateShape.From(memberType!);
            if (codec is not null || (settable && !initOnly) || !Immutability.IsImmutable(memberType!))
                immutable = false;

            var field = new StateFieldModel(id, CodeWriter.Escape(member.Name), shape, member.Name, settable, initOnly);
            fields.Add(new StateFieldDeclaration(field, displayName, location));
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
            null,
            SnapshotKind.None,
            null,
            false,
            EquatableArray<StateFieldDeclaration>.Empty,
            EquatableArray<StateConstructorModel>.Empty,
            diagnostics.ToEquatableArray());
    }

    /// <summary>
    ///     Whether a member left unmarked could hold state: a writable field, or an auto-property with a setter.
    /// </summary>
    /// <remarks>
    ///     Readonly fields and get-only auto-properties are left alone. They're set once in a constructor, which on a
    ///     participating type usually means a cache derived from the marked members.
    /// </remarks>
    private static bool IsMutable(ISymbol member)
    {
        return member switch
        {
            IFieldSymbol field => !field.IsReadOnly,
            IPropertySymbol property => property.SetMethod is not null,
            _ => false,
        };
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
            if (property.SetMethod is not null || HasBackingField(type, property))
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

    private static bool HasBackingField(INamedTypeSymbol type, IPropertySymbol property)
    {
        foreach (ISymbol candidate in type.GetMembers())
        {
            if (candidate is IFieldSymbol backing && SymbolEqualityComparer.Default.Equals(backing.AssociatedSymbol, property))
                return true;
        }

        return false;
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
    ///     Gets every constructor the generated reader could call. Accessibility doesn't matter, since the reader is
    ///     generated into the type; only a record's copy constructor is left out.
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

    /// <summary>
    ///     Decides whether <paramref name="member" /> is a field or property the generator looks at.
    /// </summary>
    /// <param name="type">The member's type.</param>
    /// <param name="member">A member of <paramref name="type" />.</param>
    /// <param name="memberType">The value type of the field or property.</param>
    /// <param name="hasStorage">
    ///     Whether the member stores a value: any declared field, or a property with a backing field (auto-properties,
    ///     positional record properties, and <c>field</c>-keyword properties).
    /// </param>
    /// <returns>False for members that are never state, such as methods, indexers, and backing fields.</returns>
    private static bool TryGetMember(
        INamedTypeSymbol type, ISymbol member,
        out ITypeSymbol? memberType, out bool hasStorage)
    {
        memberType = null;
        hasStorage = false;
        switch (member)
        {
            case IFieldSymbol field:
                // Backing fields are covered through their property; other synthesized fields aren't the user's.
                if (field.AssociatedSymbol is not null || field.IsImplicitlyDeclared)
                    return false;

                memberType = field.Type;
                hasStorage = true;
                return true;

            case IPropertySymbol { IsIndexer: false } property:
                memberType = property.Type;
                hasStorage = HasBackingField(type, property);

                // Compiler-synthesized record members such as EqualityContract have no storage and can't be marked.
                return hasStorage || !property.IsImplicitlyDeclared;

            default:
                return false;
        }
    }

    private static IEnumerable<string> OwnedMemberNames(bool isSolverConfiguration)
    {
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

    private static AttributeData? FindAttribute(ISymbol symbol, string metadataName)
    {
        foreach (AttributeData attribute in symbol.GetAttributes())
        {
            if (attribute.AttributeClass is { } attributeClass && StateShape.MetadataName(attributeClass) == metadataName)
                return attribute;
        }

        return null;
    }

    private static ITypeSymbol? CodecOf(AttributeData serialize)
    {
        foreach (KeyValuePair<string, TypedConstant> argument in serialize.NamedArguments)
        {
            if (argument.Key == "Codec" && argument.Value.Value is ITypeSymbol codec)
                return codec;
        }

        return null;
    }

    /// <summary>
    ///     Whether <paramref name="type" /> has a field or property marked <c>[NumosSerialize]</c>.
    /// </summary>
    internal static bool HasSerializedMembers(INamedTypeSymbol type)
    {
        foreach (ISymbol member in type.GetMembers())
        {
            if (member is IFieldSymbol or IPropertySymbol && FindAttribute(member, SerializeMetadataName) is not null)
                return true;
        }

        return false;
    }

    /// <summary>
    ///     Gets <paramref name="member" />'s <c>[NumosSerialize]</c>, and whether it names a codec.
    /// </summary>
    internal static bool IsSerialized(ISymbol member, out bool hasCodec)
    {
        AttributeData? attribute = FindAttribute(member, SerializeMetadataName);
        hasCodec = attribute is not null && CodecOf(attribute) is not null;
        return attribute is not null;
    }

    private static bool Implements(INamedTypeSymbol type, string interfaceMetadataName)
    {
        foreach (INamedTypeSymbol candidate in type.AllInterfaces)
        {
            if (StateShape.MetadataName(candidate) == interfaceMetadataName)
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
            StateShape shape = field.Field.Shape.Resolve(aliases);
            if (shape.FindUnsupported() is { } unsupported)
            {
                diagnostics.Add(
                    DiagnosticInfo.Create(
                        StateDiagnostics.UnsupportedType,
                        field.Location,
                        field.DisplayName,
                        shape.Kind == StateShapeKind.Unsupported ? unsupported : DescribeDeclared(shape),
                        unsupported));

                continue;
            }

            fields.Add(field.Field with { Shape = shape });
        }

        bool hasErrors = diagnostics.Any(static diagnostic => diagnostic.Descriptor.DefaultSeverity == DiagnosticSeverity.Error);
        if (!declaration.CanGenerate || hasErrors)
            return new StateTypeResult(null, null, diagnostics.ToEquatableArray());

        // Only checked once the fields themselves are fine, so a bad field type isn't reported twice.
        StateReadPlan? plan = StateBinding.Bind(declaration.Constructors, fields, aliases, out string reason);
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
            declaration.StateKey,
            declaration.Snapshot,
            fields.ToEquatableArray(),
            plan);

        return new StateTypeResult(model, declaration.Registration, diagnostics.ToEquatableArray());
    }

    private static string DescribeDeclared(StateShape shape)
    {
        // Unsupported shapes can't render a codec, but their outer type still reads fine for a message.
        return shape.Kind switch
        {
            StateShapeKind.Array => DescribeDeclared(shape.Arguments[0]) + "[]",
            StateShapeKind.NullableReference or StateShapeKind.NullableValue => DescribeDeclared(shape.Arguments[0]) + "?",
            StateShapeKind.List or StateShapeKind.Set or StateShapeKind.Dictionary =>
                shape.Name.Replace("global::", "") + "<" +
                string.Join(", ", shape.Arguments.Select(DescribeDeclared)) + ">",
            StateShapeKind.Unsupported => shape.Name,
            _ => shape.TypeExpression.Replace("global::", ""),
        };
    }
}
