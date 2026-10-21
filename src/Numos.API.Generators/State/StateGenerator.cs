using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Numos.API.Generators.Common;

namespace Numos.API.Generators.State;

/// <summary>
///     Implements <c>INumosState&lt;T&gt;</c> for <c>[NumosState]</c> types, plus <c>IAtmosSolverConfiguration</c>'s
///     equality and hash when the type is a solver configuration.
/// </summary>
/// <remarks>
///     <para>
///         Membership is opt-out: every instance field and every property with a backing field must say
///         <c>[StateField(id)]</c> or <c>[NotState]</c>. Computed properties are ignored unless tagged.
///     </para>
///     <para>
///         Quantity aliases are error types in a project that owns a units catalog (see
///         <see cref="QuantityAliasCatalog" />), so shapes keep them unbound until they're combined with the catalog.
///         A second pass over <c>[StateField]</c> members catches the ones nothing would otherwise read: on a type
///         without <c>[NumosState]</c>, or on a world solver.
///     </para>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class StateGenerator : IIncrementalGenerator
{
    internal const string TypesStepName = "NumosStateTypes";

    private const string NumosStateMetadataName = "Numos.CoreSim.State.NumosStateAttribute";
    private const string StateFieldMetadataName = "Numos.CoreSim.State.StateFieldAttribute";
    private const string NotStateMetadataName = "Numos.CoreSim.State.NotStateAttribute";
    private const string RetiredMetadataName = "Numos.CoreSim.State.RetiredStateFieldsAttribute";
    private const string SolverConfigurationMetadataName = "Numos.CoreSim.IAtmosSolverConfiguration";
    private const string WorldSolverMetadataName = "Numos.API.IAtmosWorldSolver";

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<StateTypeResult> types = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                NumosStateMetadataName,
                static (node, _) => node is TypeDeclarationSyntax,
                static (attributeContext, cancellationToken) => CreateDeclaration(attributeContext, cancellationToken))
            .Combine(QuantityAliasCatalog.Provider(context))
            .Select(static (pair, _) => Resolve(pair.Left, pair.Right))
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

        IncrementalValuesProvider<EquatableArray<DiagnosticInfo>> strayFields = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                StateFieldMetadataName,
                static (node, _) => node is PropertyDeclarationSyntax or VariableDeclaratorSyntax,
                static (attributeContext, _) => CheckStrayField(attributeContext));

        context.RegisterSourceOutput(
            strayFields,
            static (production, diagnostics) =>
            {
                foreach (DiagnosticInfo diagnostic in diagnostics)
                    production.ReportDiagnostic(diagnostic.ToDiagnostic());
            });
    }

    private static StateTypeDeclaration CreateDeclaration(
        GeneratorAttributeSyntaxContext context,
        CancellationToken cancellationToken)
    {
        var type = (INamedTypeSymbol)context.TargetSymbol;
        var syntax = (TypeDeclarationSyntax)context.TargetNode;
        LocationInfo? typeLocation = LocationInfo.From(syntax.Identifier.GetLocation());
        string typeDisplayName = type.ToDisplayString();
        var diagnostics = new List<DiagnosticInfo>();
        bool canGenerate = true;

        // The chain helper starts from a node inside the declaration; the attribute list is always there.
        foreach ((INamedTypeSymbol containing, bool isPartial) in PartialSyntax.GetContainingChain(type, syntax.AttributeLists[0]))
        {
            if (!isPartial)
            {
                diagnostics.Add(
                    DiagnosticInfo.Create(StateDiagnostics.TypeNotPartial, typeLocation, containing.ToDisplayString(), typeDisplayName));

                canGenerate = false;
            }

            if (containing.TypeParameters.Length > 0)
            {
                // Field types would mention type parameters no codec covers, so stop before reporting them too.
                diagnostics.Add(DiagnosticInfo.Create(StateDiagnostics.Generic, typeLocation, typeDisplayName));
                return Skipped(type, diagnostics);
            }
        }

        if (type.TypeKind == TypeKind.Class &&
            type.BaseType is { SpecialType: not SpecialType.System_Object } baseType)
        {
            diagnostics.Add(
                DiagnosticInfo.Create(StateDiagnostics.DerivedClass, typeLocation, typeDisplayName, baseType.ToDisplayString()));

            canGenerate = false;
        }

        bool isSolverConfiguration = Implements(type, SolverConfigurationMetadataName);
        if (Implements(type, WorldSolverMetadataName))
        {
            // Its fields would only produce noise about tags that can't fix anything.
            diagnostics.Add(DiagnosticInfo.Create(StateDiagnostics.WorldSolver, typeLocation, "[NumosState]", typeDisplayName));
            return Skipped(type, diagnostics);
        }

        foreach (string generated in OwnedMemberNames(isSolverConfiguration))
        {
            foreach (ISymbol member in type.GetMembers())
            {
                if (member.IsImplicitlyDeclared || member is not IMethodSymbol { MethodKind: MethodKind.Ordinary or MethodKind.ExplicitInterfaceImplementation })
                    continue;

                if (member.Name == generated || member.Name.EndsWith("." + generated, StringComparison.Ordinal))
                {
                    diagnostics.Add(
                        DiagnosticInfo.Create(
                            StateDiagnostics.HandWritten,
                            LocationInfo.From(member.Locations.FirstOrDefault()) ?? typeLocation,
                            typeDisplayName,
                            generated));

                    canGenerate = false;
                    break;
                }
            }
        }

        HashSet<int> retired = RetiredIds(type);
        var fields = new List<StateFieldDeclaration>();
        var ids = new Dictionary<int, string>();
        foreach (ISymbol member in type.GetMembers())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryGetStorage(type, member, out ITypeSymbol? memberType, out bool hasStorage))
                continue;

            LocationInfo? location = LocationInfo.From(member.Locations.FirstOrDefault()) ?? typeLocation;
            string displayName = typeDisplayName + "." + member.Name;
            AttributeData? stateField = FindAttribute(member, StateFieldMetadataName);
            bool notState = FindAttribute(member, NotStateMetadataName) is not null;

            if (member.IsStatic || member is IFieldSymbol { IsConst: true })
            {
                // Static members are never state; only flag the ones claiming otherwise.
                if (stateField is not null)
                    diagnostics.Add(DiagnosticInfo.Create(StateDiagnostics.StaticField, location, displayName));

                continue;
            }

            if (stateField is not null && notState)
            {
                diagnostics.Add(DiagnosticInfo.Create(StateDiagnostics.BothTags, location, displayName));
                continue;
            }

            if (notState)
                continue;

            if (stateField is null)
            {
                if (hasStorage)
                    diagnostics.Add(DiagnosticInfo.Create(StateDiagnostics.Untagged, location, displayName, typeDisplayName));

                continue;
            }

            int id = stateField.ConstructorArguments.Length == 1 && stateField.ConstructorArguments[0].Value is int value
                ? value
                : 0;

            if (id <= 0)
            {
                diagnostics.Add(
                    DiagnosticInfo.Create(StateDiagnostics.NonPositiveId, location, displayName, id.ToString(CultureInfo.InvariantCulture)));

                continue;
            }

            if (retired.Contains(id))
            {
                diagnostics.Add(
                    DiagnosticInfo.Create(StateDiagnostics.RetiredId, location, displayName, id.ToString(CultureInfo.InvariantCulture), typeDisplayName));

                continue;
            }

            if (ids.TryGetValue(id, out string? existing))
            {
                diagnostics.Add(
                    DiagnosticInfo.Create(StateDiagnostics.DuplicateId, location, displayName, id.ToString(CultureInfo.InvariantCulture), existing));

                continue;
            }

            ids.Add(id, member.Name);
            var field = new StateFieldModel(id, CodeWriter.Escape(member.Name), StateShape.From(memberType!));
            fields.Add(new StateFieldDeclaration(field, displayName, location));
        }

        return new StateTypeDeclaration(
            PartialTypeModel.From(type),
            type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            type.IsReferenceType,
            isSolverConfiguration,
            canGenerate,
            fields.OrderBy(static field => field.Field.Id).ToEquatableArray(),
            diagnostics.ToEquatableArray());
    }

    private static StateTypeDeclaration Skipped(INamedTypeSymbol type, List<DiagnosticInfo> diagnostics)
    {
        return new StateTypeDeclaration(
            PartialTypeModel.From(type),
            type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            type.IsReferenceType,
            false,
            false,
            EquatableArray<StateFieldDeclaration>.Empty,
            diagnostics.ToEquatableArray());
    }

    /// <summary>
    ///     Decides whether <paramref name="member" /> is a field or property the opt-out rule applies to.
    /// </summary>
    /// <param name="type">The member's type.</param>
    /// <param name="member">A member of <paramref name="type" />.</param>
    /// <param name="memberType">The value type of the field or property.</param>
    /// <param name="hasStorage">
    ///     Whether the member stores a value: any declared field, or a property with a backing field (auto-properties,
    ///     positional record properties, and <c>field</c>-keyword properties). Only these must be tagged.
    /// </param>
    /// <returns>False for members that are never state, such as methods, indexers, and backing fields.</returns>
    private static bool TryGetStorage(
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
                foreach (ISymbol candidate in type.GetMembers())
                {
                    if (candidate is IFieldSymbol backing &&
                        SymbolEqualityComparer.Default.Equals(backing.AssociatedSymbol, property))
                    {
                        hasStorage = true;
                        break;
                    }
                }

                // Compiler-synthesized record members such as EqualityContract have no storage and can't be tagged.
                return hasStorage || !property.IsImplicitlyDeclared;

            default:
                return false;
        }
    }

    private static IEnumerable<string> OwnedMemberNames(bool isSolverConfiguration)
    {
        yield return "AppendHash";
        if (!isSolverConfiguration)
            yield break;

        yield return "SemanticallyEquals";
        yield return "ComputeStateHash";
    }

    private static HashSet<int> RetiredIds(INamedTypeSymbol type)
    {
        var retired = new HashSet<int>();
        AttributeData? attribute = FindAttribute(type, RetiredMetadataName);
        if (attribute is null)
            return retired;

        foreach (TypedConstant argument in attribute.ConstructorArguments)
        {
            if (argument.Kind == TypedConstantKind.Array)
            {
                foreach (TypedConstant element in argument.Values)
                {
                    if (element.Value is int id)
                        retired.Add(id);
                }
            }
            else if (argument.Value is int id)
            {
                retired.Add(id);
            }
        }

        return retired;
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
        StateTypeModel? model = declaration.CanGenerate && !hasErrors
            ? new StateTypeModel(
                declaration.Type,
                declaration.TypeName,
                declaration.IsReferenceType,
                declaration.IsSolverConfiguration,
                fields.ToEquatableArray())
            : null;

        return new StateTypeResult(model, diagnostics.ToEquatableArray());
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

    private static EquatableArray<DiagnosticInfo> CheckStrayField(GeneratorAttributeSyntaxContext context)
    {
        ISymbol member = context.TargetSymbol;
        INamedTypeSymbol? type = member.ContainingType;
        if (type is null)
            return EquatableArray<DiagnosticInfo>.Empty;

        LocationInfo? location = LocationInfo.From(member.Locations.FirstOrDefault());
        string displayName = type.ToDisplayString() + "." + member.Name;

        // A [NumosState] solver already gets one type-level report from the main pass.
        if (Implements(type, WorldSolverMetadataName))
        {
            if (StateShape.HasStateAttribute(type))
                return EquatableArray<DiagnosticInfo>.Empty;

            return new EquatableArray<DiagnosticInfo>(
                new[] { DiagnosticInfo.Create(StateDiagnostics.WorldSolver, location, displayName, type.ToDisplayString()) });
        }

        if (!StateShape.HasStateAttribute(type))
        {
            return new EquatableArray<DiagnosticInfo>(
                new[] { DiagnosticInfo.Create(StateDiagnostics.NotStateType, location, displayName, type.ToDisplayString()) });
        }

        return EquatableArray<DiagnosticInfo>.Empty;
    }
}
