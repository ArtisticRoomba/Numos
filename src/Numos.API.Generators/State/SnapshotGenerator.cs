using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Numos.API.Generators.Common;

namespace Numos.API.Generators.State;

/// <summary>
///     Fills every <c>[NumosSnapshotOf(typeof(TLive))]</c> partial class with one member per <c>[NumosSerialize]</c>
///     member of its live type, plus <c>Capture</c>, <c>RestoreInto</c>, and the <c>INumosState&lt;T&gt;</c> walker.
/// </summary>
/// <remarks>
///     <para>
///         The transform reads one snapshot and its live type. A live member may itself be a live type with a snapshot
///         (a chunk inside a kernel). Numos knows that only after it has read every snapshot in the project. Members
///         therefore keep the type names that decide it, and the cross-snapshot pass resolves them.
///     </para>
///     <para>
///         Generated code lives in the snapshot, so it can read only live members that the snapshot can see. That keeps
///         the live type free of generated code. It needs no attribute and does not have to be partial.
///         <see cref="StateGenerator" /> leaves both types alone.
///     </para>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class SnapshotGenerator : IIncrementalGenerator
{
    internal const string SnapshotOfMetadataName = "Numos.CoreSim.State.NumosSnapshotOfAttribute";
    internal const string SnapshotsStepName = "NumosSnapshots";

    private const string FlatArrayMetadataName = "Numos.Collections.FlatArray`1";

    /// <summary>
    ///     The members the generator writes on every snapshot, besides one per live member.
    /// </summary>
    internal static readonly string[] OwnedMembers = ["Capture", "RestoreInto", "CheckRestoreInto", "ApplyRestoreInto"];

    private static readonly HashSet<string> ListTypes = new(StringComparer.Ordinal)
    {
        "System.Collections.Generic.IReadOnlyList`1",
        "System.Collections.Generic.IList`1",
        "System.Collections.Generic.List`1",
    };

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValueProvider<GeneratorOutput<SnapshotModel>> output = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                SnapshotOfMetadataName,
                static (node, _) => node is TypeDeclarationSyntax,
                static (syntaxContext, cancellationToken) => Parse(syntaxContext, cancellationToken))
            .Collect()
            .Combine(QuantityAliasCatalog.Provider(context))
            .Select(static (pair, _) => Resolve(pair.Left, pair.Right));

        GeneratorOutput.Register(context, output, SnapshotsStepName, SnapshotEmitter.Emit);
    }

    /// <summary>
    ///     Gets the snapshot and live type names a <c>[NumosSnapshotOf]</c> declaration claims, so
    ///     <see cref="StateGenerator" /> can skip them.
    /// </summary>
    internal static EquatableArray<string> ParticipantNames(GeneratorAttributeSyntaxContext context)
    {
        var snapshot = (INamedTypeSymbol)context.TargetSymbol;
        var names = new List<string> { snapshot.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) };
        if (LiveTypeOf(context.Attributes[0]) is { } live)
            names.Add(live.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));

        return names.ToEquatableArray();
    }

    private static INamedTypeSymbol? LiveTypeOf(AttributeData attribute)
    {
        return attribute.ConstructorArguments.Length == 1 && attribute.ConstructorArguments[0].Value is INamedTypeSymbol
        {
            TypeKind: not TypeKind.Error,
        } live
            ? live
            : null;
    }

    private static SnapshotDeclaration Parse(GeneratorAttributeSyntaxContext context, CancellationToken cancellationToken)
    {
        var snapshot = (INamedTypeSymbol)context.TargetSymbol;
        var syntax = (TypeDeclarationSyntax)context.TargetNode;
        Compilation compilation = context.SemanticModel.Compilation;
        LocationInfo? location = LocationInfo.From(syntax.Identifier.GetLocation());
        string displayName = snapshot.ToDisplayString();
        string typeName = snapshot.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var diagnostics = new List<DiagnosticInfo>();
        bool canGenerate = true;

        // The innermost type reports first, so walk the outermost-first chain backwards.
        List<(INamedTypeSymbol Type, bool IsPartial)> chain = PartialSyntax.GetContainingChain(snapshot, syntax);
        bool genericChain = false;
        for (int i = chain.Count - 1; i >= 0; i--)
        {
            (INamedTypeSymbol current, bool isPartial) = chain[i];
            if (!isPartial)
            {
                diagnostics.Add(
                    DiagnosticInfo.Create(StateDiagnostics.SnapshotNotPartial, location, current.ToDisplayString(), displayName));

                canGenerate = false;
            }

            genericChain |= current.TypeParameters.Length > 0;
        }

        if (snapshot.TypeKind != TypeKind.Class || snapshot.IsStatic || snapshot.IsAbstract || snapshot.IsRecord ||
            genericChain || snapshot.BaseType is not { SpecialType: SpecialType.System_Object })
        {
            diagnostics.Add(DiagnosticInfo.Create(StateDiagnostics.SnapshotShape, location, displayName));
            canGenerate = false;
        }

        INamedTypeSymbol? live = LiveTypeOf(context.Attributes[0]);
        if (live is null)
            return Skipped(snapshot, typeName, displayName, "", "", diagnostics, location);

        string liveName = live.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        string liveDisplay = live.ToDisplayString();
        bool liveInProject = SymbolEqualityComparer.Default.Equals(live.ContainingAssembly, compilation.Assembly);
        bool liveGeneric = false;
        for (INamedTypeSymbol? current = live; current is not null; current = current.ContainingType)
            liveGeneric |= current.TypeParameters.Length > 0;

        if (live.TypeKind != TypeKind.Class || liveGeneric || !liveInProject)
        {
            diagnostics.Add(DiagnosticInfo.Create(StateDiagnostics.LiveTypeInvalid, location, liveDisplay, displayName));
            return Skipped(snapshot, typeName, displayName, liveName, liveDisplay, diagnostics, location);
        }

        // Partial properties the snapshot declares itself, to choose a generated member's visibility or document it.
        var declared = new Dictionary<string, string>(StringComparer.Ordinal);
        var declaredWithSetter = new HashSet<string>(StringComparer.Ordinal);
        foreach (ISymbol member in snapshot.GetMembers())
        {
            if (member is IPropertySymbol { IsPartialDefinition: true, PartialImplementationPart: null } property &&
                property.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken) is
                    PropertyDeclarationSyntax declaration)
            {
                declared[property.Name] = PartialSyntax.ModifiersWithoutPartial(declaration.Modifiers);
                if (property.SetMethod is not null)
                    declaredWithSetter.Add(property.Name);
            }
        }

        var ids = new Dictionary<int, string>();
        var members = new List<LiveMemberDeclaration>();
        bool anyMarked = false;
        foreach (ISymbol member in live.GetMembers())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (member is not (IFieldSymbol or IPropertySymbol) || member.IsImplicitlyDeclared ||
                StateSymbols.FindSerialize(member) is not { } serialize)
            {
                continue;
            }

            anyMarked = true;
            LiveMemberDeclaration? declaration = ParseLiveMember(
                compilation, snapshot, live, member, serialize, declared, ids, diagnostics, location, cancellationToken);

            if (declaration is null)
                canGenerate = false;
            else
                members.Add(declaration);
        }

        if (!anyMarked)
        {
            diagnostics.Add(DiagnosticInfo.Create(StateDiagnostics.LiveTypeEmpty, location, liveDisplay, displayName));
            canGenerate = false;
        }

        var handWritten = new List<StateFieldDeclaration>();
        var ownNames = new HashSet<string>(StringComparer.Ordinal);
        bool hasParameterlessConstructor = false;
        foreach (ISymbol member in snapshot.GetMembers())
        {
            if (member.IsImplicitlyDeclared)
                continue;

            if (member is IPropertySymbol { IsPartialDefinition: true } partial && !declaredWithSetter.Contains(partial.Name) &&
                members.Any(live => live.Name == partial.Name))
            {
                continue;
            }

            if (member is IMethodSymbol { MethodKind: MethodKind.Constructor } constructor)
            {
                hasParameterlessConstructor |= constructor.Parameters.Length == 0;
                continue;
            }

            if (member is IMethodSymbol { MethodKind: not MethodKind.Ordinary })
                continue;

            ownNames.Add(member.Name);
            LocationInfo? memberLocation = LocationInfo.From(member.Locations.FirstOrDefault()) ?? location;
            if (Array.IndexOf(OwnedMembers, member.Name) >= 0)
            {
                diagnostics.Add(DiagnosticInfo.Create(StateDiagnostics.HandWritten, memberLocation, displayName, member.Name));
                canGenerate = false;
                continue;
            }

            if (ParseHandWritten(snapshot, member, ids, diagnostics, memberLocation) is { } field)
                handWritten.Add(field);
        }

        foreach (LiveMemberDeclaration member in members)
        {
            if (ownNames.Contains(member.Name) || ownNames.Contains(BackingField(member.Name)))
            {
                diagnostics.Add(
                    DiagnosticInfo.Create(
                        StateDiagnostics.SnapshotNameClash, member.Location, displayName, member.Name, member.DisplayName));

                canGenerate = false;
            }
        }

        return new SnapshotDeclaration(
            PartialTypeModel.From(snapshot),
            typeName,
            displayName,
            liveName,
            liveDisplay,
            !hasParameterlessConstructor,
            canGenerate,
            members.ToEquatableArray(),
            handWritten.OrderBy(static field => field.Field.Id).ToEquatableArray(),
            diagnostics.ToEquatableArray(),
            location);
    }

    private static SnapshotDeclaration Skipped(
        INamedTypeSymbol snapshot, string typeName, string displayName, string liveName, string liveDisplay,
        List<DiagnosticInfo> diagnostics, LocationInfo? location)
    {
        return new SnapshotDeclaration(
            PartialTypeModel.From(snapshot),
            typeName,
            displayName,
            liveName,
            liveDisplay,
            false,
            false,
            EquatableArray<LiveMemberDeclaration>.Empty,
            EquatableArray<StateFieldDeclaration>.Empty,
            diagnostics.ToEquatableArray(),
            location);
    }

    internal static string BackingField(string name) => "__numos" + name.TrimStart('@');

    private static LiveMemberDeclaration? ParseLiveMember(
        Compilation compilation, INamedTypeSymbol snapshot, INamedTypeSymbol live, ISymbol member, AttributeData serialize,
        Dictionary<string, string> declared, Dictionary<int, string> ids, List<DiagnosticInfo> diagnostics,
        LocationInfo? typeLocation, CancellationToken cancellationToken)
    {
        LocationInfo? location = LocationInfo.From(member.Locations.FirstOrDefault()) ?? typeLocation;
        SerializedMember? parsed = SerializedMember.TryParse(
            member, serialize, hasStorage: false, live.ToDisplayString(), location, WireNameChecks.Identifier, ids,
            diagnostics);

        if (parsed is null)
            return null;

        string displayName = parsed.DisplayName;
        string name = parsed.WireName;
        int id = parsed.Id;
        string snapshotDisplay = snapshot.ToDisplayString();
        ITypeSymbol type;
        LiveStorage storage;
        bool readable;
        bool assignable;
        switch (member)
        {
            case IFieldSymbol field:
                type = field.Type;
                storage = LiveStorage.Field;
                readable = compilation.IsSymbolAccessibleWithin(field, snapshot);
                assignable = !field.IsReadOnly;
                break;
            case IPropertySymbol { IsIndexer: false } property:
                type = property.Type;
                storage = StateSymbols.HasBackingField(live, property) ? LiveStorage.AutoProperty : LiveStorage.Adapter;
                readable = property.GetMethod is { } getter && compilation.IsSymbolAccessibleWithin(getter, snapshot);
                assignable = property.SetMethod is { IsInitOnly: false } setter &&
                             compilation.IsSymbolAccessibleWithin(setter, snapshot);
                break;
            default:
                return null;
        }

        if (!readable)
        {
            diagnostics.Add(DiagnosticInfo.Create(StateDiagnostics.LiveMemberInaccessible, location, displayName, snapshotDisplay));
            return null;
        }

        string? count = null;
        if (StateSymbols.NamedString(serialize, "Count") is { } countName)
        {
            if (!IsCountMember(compilation, snapshot, live, countName))
            {
                diagnostics.Add(
                    DiagnosticInfo.Create(StateDiagnostics.BadCountMember, location, displayName, countName, live.ToDisplayString()));

                return null;
            }

            count = CodeWriter.Escape(countName);
        }

        ITypeSymbol? codec = parsed.Codec;
        StateShape shape = StateShape.ForMember(type, codec);

        StateShape? arrayElement = null;
        bool arrayElementImmutable = false;
        bool isFlatArray = false;
        LiveCollection collection = LiveCollection.None;
        ITypeSymbol? element = null;
        if (codec is null)
        {
            if (type is IArrayTypeSymbol { IsSZArray: true } array)
            {
                element = array.ElementType;
                collection = LiveCollection.Array;
            }
            else if (type is INamedTypeSymbol { IsGenericType: true } named)
            {
                string metadataName = PartialTypeModel.GetMetadataName(named.OriginalDefinition);
                if (metadataName == FlatArrayMetadataName)
                {
                    element = named.TypeArguments[0];
                    isFlatArray = true;
                }
                else if (ListTypes.Contains(metadataName))
                {
                    element = named.TypeArguments[0];
                    collection = LiveCollection.List;
                }
            }

            if (element is not null && collection != LiveCollection.List)
            {
                arrayElement = StateShape.From(element);
                arrayElementImmutable = Immutability.IsImmutable(element);
            }
        }

        string accessibility = member.DeclaredAccessibility == Accessibility.Public ? "public" : "internal";
        return new LiveMemberDeclaration(
            CodeWriter.Escape(member.Name),
            name,
            id,
            accessibility,
            storage,
            assignable,
            shape,
            codec is null && Immutability.IsImmutable(type),
            type.IsValueType || type.SpecialType == SpecialType.System_String || type.TypeKind == TypeKind.Error,
            codec is not null,
            type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            arrayElement,
            arrayElementImmutable,
            isFlatArray,
            collection,
            element is INamedTypeSymbol { TypeKind: TypeKind.Class }
                ? element.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                : null,
            count,
            Documentation(member, cancellationToken),
            declared.TryGetValue(name, out string? modifiers) ? modifiers : null,
            displayName,
            type.ToDisplayString(),
            location);
    }

    private static bool IsCountMember(Compilation compilation, INamedTypeSymbol snapshot, INamedTypeSymbol live, string name)
    {
        foreach (ISymbol candidate in live.GetMembers(name))
        {
            switch (candidate)
            {
                case IFieldSymbol { IsStatic: false, IsReadOnly: false, Type.SpecialType: SpecialType.System_Int32 } field:
                    return compilation.IsSymbolAccessibleWithin(field, snapshot);
                case IPropertySymbol
                {
                    IsStatic: false,
                    Type.SpecialType: SpecialType.System_Int32,
                    GetMethod: { } getter,
                    SetMethod: { IsInitOnly: false } setter,
                }:
                    return compilation.IsSymbolAccessibleWithin(getter, snapshot) &&
                           compilation.IsSymbolAccessibleWithin(setter, snapshot);
            }
        }

        return false;
    }

    /// <summary>
    ///     Reads one of the snapshot's own members. A marked member joins the schema. A mutable unmarked member gets the
    ///     same warning a serializable type would.
    /// </summary>
    private static StateFieldDeclaration? ParseHandWritten(
        INamedTypeSymbol snapshot, ISymbol member, Dictionary<int, string> ids, List<DiagnosticInfo> diagnostics,
        LocationInfo? location)
    {
        if (!StateSymbols.TryGetMember(snapshot, member, out ITypeSymbol? type, out bool hasStorage))
            return null;

        SerializedMember? parsed = SerializedMember.TryParse(
            member, StateSymbols.FindSerialize(member), hasStorage, snapshot.ToDisplayString(), location,
            WireNameChecks.NullIsMemberName, ids, diagnostics);

        if (parsed is null)
            return null;

        if (!parsed.Settable)
        {
            diagnostics.Add(DiagnosticInfo.Create(StateDiagnostics.NotAssignable, location, parsed.DisplayName));
            return null;
        }

        StateShape shape = StateShape.ForMember(type!, parsed.Codec);
        var field = new StateFieldModel(parsed.Id, CodeWriter.Escape(member.Name), shape, member.Name, true, parsed.InitOnly);
        return new StateFieldDeclaration(field, parsed.DisplayName, location);
    }

    private static GeneratorOutput<SnapshotModel> Resolve(
        System.Collections.Immutable.ImmutableArray<SnapshotDeclaration> declarations, EquatableArray<QuantityAlias> aliases)
    {
        if (declarations.IsEmpty)
            return GeneratorOutput<SnapshotModel>.Empty;

        var diagnostics = new List<DiagnosticInfo>();
        var byLive = new Dictionary<string, SnapshotDeclaration>(StringComparer.Ordinal);
        foreach (SnapshotDeclaration declaration in
                 declarations.OrderBy(static declaration => declaration.TypeName, StringComparer.Ordinal))
        {
            diagnostics.AddRange(declaration.Diagnostics);
            if (declaration.LiveTypeName.Length == 0)
                continue;

            if (byLive.TryGetValue(declaration.LiveTypeName, out SnapshotDeclaration? other))
            {
                diagnostics.Add(
                    DiagnosticInfo.Create(
                        StateDiagnostics.DuplicateSnapshot,
                        declaration.Location,
                        other.DisplayName,
                        declaration.DisplayName,
                        declaration.LiveDisplayName));

                continue;
            }

            byLive.Add(declaration.LiveTypeName, declaration);
        }

        var plans = new Dictionary<
            string,
            (SnapshotDeclaration Declaration, List<SnapshotMemberModel> Members, List<string> Nested, bool OwnRestorable)>(
            StringComparer.Ordinal);

        foreach (SnapshotDeclaration declaration in byLive.Values)
        {
            if (!declaration.CanGenerate)
                continue;

            var members = new List<SnapshotMemberModel>();
            var nested = new List<string>();
            bool restorable = true;
            bool failed = false;
            foreach (LiveMemberDeclaration member in declaration.Members)
            {
                SnapshotMemberModel? model = ResolveMember(member, byLive, aliases, diagnostics);
                if (model is null)
                {
                    failed = true;
                    continue;
                }

                members.Add(model);
                restorable &= model.Restore != RestoreMode.None;
                if (model.Kind == SnapshotMemberKind.Nested)
                    nested.Add(model.Nested);
            }

            if (!failed)
                plans.Add(declaration.TypeName, (declaration, members, nested, restorable));
        }

        // A snapshot that restores a nested one in place is only restorable if that one is.
        var restorableTypes = new HashSet<string>(
            plans.Where(static plan => plan.Value.OwnRestorable).Select(static plan => plan.Key),
            StringComparer.Ordinal);

        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (string type in restorableTypes.ToArray())
            {
                if (plans[type].Nested.Any(nested => !restorableTypes.Contains(nested)))
                {
                    restorableTypes.Remove(type);
                    changed = true;
                }
            }
        }

        var models = new List<SnapshotModel>();
        foreach (var plan in plans.Values.OrderBy(static plan => plan.Declaration.TypeName, StringComparer.Ordinal))
        {
            SnapshotDeclaration declaration = plan.Declaration;
            var fields = new List<StateFieldModel>();
            var initOnly = new List<StateFieldModel>();
            bool failed = false;
            foreach (StateFieldDeclaration handWritten in declaration.HandWritten)
            {
                if (handWritten.Resolve(aliases, diagnostics, false) is not { } field)
                {
                    failed = true;
                    continue;
                }

                fields.Add(field);
                if (field.InitOnly)
                    initOnly.Add(field);
            }

            if (failed)
                continue;

            foreach (SnapshotMemberModel member in plan.Members)
            {
                LiveMemberDeclaration source = declaration.Members.First(candidate => CodeWriter.Escape(candidate.Name) == member.Name);
                fields.Add(new StateFieldModel(source.Id, member.Name, member.Shape, source.Name, true, false));
            }

            var state = new StateTypeModel(
                declaration.Type,
                declaration.TypeName,
                true,
                false,
                false,
                null,
                SnapshotKind.None,
                fields.OrderBy(static field => field.Id).ToEquatableArray(),
                new StateReadPlan(EquatableArray<string>.Empty, EquatableArray<StateInitializerModel>.Empty));

            models.Add(
                new SnapshotModel(
                    declaration.Type,
                    declaration.TypeName,
                    declaration.LiveTypeName,
                    declaration.EmitConstructor,
                    restorableTypes.Contains(declaration.TypeName),
                    plan.Members.ToEquatableArray(),
                    state,
                    initOnly.ToEquatableArray()));
        }

        return new GeneratorOutput<SnapshotModel>(diagnostics.ToEquatableArray(), models.ToEquatableArray());
    }

    private static SnapshotMemberModel? ResolveMember(
        LiveMemberDeclaration member, Dictionary<string, SnapshotDeclaration> byLive, EquatableArray<QuantityAlias> aliases,
        List<DiagnosticInfo> diagnostics)
    {
        string name = CodeWriter.Escape(member.Name);
        bool adapter = member.Storage == LiveStorage.Adapter;

        SnapshotMemberModel Model(
            SnapshotMemberKind kind, StateShape shape, string elementType, bool isReference, string nested, RestoreMode restore) =>
            new(
                member.LiveName,
                name,
                member.Accessibility,
                kind,
                member.Storage,
                shape,
                elementType,
                isReference,
                member.IsFlatArray,
                member.Collection,
                nested,
                member.Count,
                restore,
                member.Documentation,
                member.DeclaredModifiers);

        bool IsCollectionKind(SnapshotMemberKind kind) => kind is SnapshotMemberKind.Array or SnapshotMemberKind.NestedList;

        SnapshotMemberModel? Checked(SnapshotMemberModel model)
        {
            if (model.Count is not null && !IsCollectionKind(model.Kind))
            {
                diagnostics.Add(DiagnosticInfo.Create(StateDiagnostics.CountWithoutCollection, member.Location, member.DisplayName));
                return null;
            }

            return model;
        }

        if (!member.HasCodec && byLive.TryGetValue(member.TypeName, out SnapshotDeclaration? nestedSnapshot))
        {
            // Restored in place through the nested snapshot, so the live member itself is never reassigned.
            return Checked(
                Model(
                    SnapshotMemberKind.Nested,
                    StateShape.Snapshot(nestedSnapshot.TypeName),
                    "",
                    true,
                    nestedSnapshot.TypeName,
                    RestoreMode.Nested));
        }

        if (!member.HasCodec && member.ElementTypeName is { } elementName &&
            member.Collection != LiveCollection.None && byLive.TryGetValue(elementName, out SnapshotDeclaration? elementSnapshot))
        {
            // The owner has to create or drop live children, so a list of them is captured but not restored.
            return Model(
                SnapshotMemberKind.NestedList,
                StateShape.ReadOnlyList(StateShape.Snapshot(elementSnapshot.TypeName)),
                elementSnapshot.TypeName,
                true,
                elementSnapshot.TypeName,
                RestoreMode.None);
        }

        if (member.ArrayElement is { } arrayElement && (member.ArrayElementImmutable || adapter))
        {
            StateShape element = arrayElement.Resolve(aliases);
            if (element.FindUnsupported() is null)
            {
                RestoreMode restore = member.Storage != LiveStorage.Adapter
                    ? RestoreMode.CopyInPlace
                    : member.Assignable && !member.IsFlatArray && member.Count is null
                        ? RestoreMode.AssignArray
                        : RestoreMode.None;

                return Model(SnapshotMemberKind.Array, StateShape.ReadOnlyList(element), element.TypeExpression, true, "", restore);
            }
        }

        StateShape shape = member.Shape.Resolve(aliases);
        if (shape.FindUnsupported() is null && (member.Immutable || member.HasCodec || adapter))
        {
            RestoreMode restore = member.Assignable ? RestoreMode.Assign : member.ValueType ? RestoreMode.Check : RestoreMode.None;
            if (shape.Kind == StateShapeKind.List && shape.Name == StateShape.ReadOnlyList(shape.Arguments[0]).Name)
            {
                // Held in a list the snapshot owns, so a caller can't keep a mutable alias to what the getter returned.
                return Checked(
                    Model(SnapshotMemberKind.Value, shape, shape.Arguments[0].TypeExpression, true, "", restore));
            }

            bool isReference = shape.Kind == StateShapeKind.String ||
                               (!member.ValueType && shape.Kind != StateShapeKind.NullableReference);
            return Checked(Model(SnapshotMemberKind.Value, shape, "", isReference, "", restore));
        }

        diagnostics.Add(
            DiagnosticInfo.Create(StateDiagnostics.LiveMemberUnsupported, member.Location, member.DisplayName, member.TypeDisplay));
        return null;
    }

    /// <summary>
    ///     Copies the summary and remarks of a live member for the generated snapshot member. It turns each <c>cref</c>
    ///     into plain code text, because the references resolve against the live type.
    /// </summary>
    private static EquatableArray<string> Documentation(ISymbol member, CancellationToken cancellationToken)
    {
        string xml = member.GetDocumentationCommentXml(cancellationToken: cancellationToken) ?? "";
        if (xml.Trim().Length == 0)
            return EquatableArray<string>.Empty;

        try
        {
            XElement root = XElement.Parse(xml, LoadOptions.PreserveWhitespace);
            foreach (XElement reference in root.Descendants().Where(static element => element.Attribute("cref") is not null).ToArray())
            {
                if (reference.Name.LocalName == "seealso")
                {
                    reference.Remove();
                    continue;
                }

                reference.ReplaceWith(new XElement("c", CrefName(reference.Attribute("cref")!.Value)));
            }

            var lines = new List<string>();
            foreach (XElement section in root.Elements().Where(static element => element.Name.LocalName is "summary" or "remarks"))
            {
                foreach (string line in section.ToString(SaveOptions.DisableFormatting).Split('\n'))
                {
                    string trimmed = line.Trim();
                    if (trimmed.Length > 0)
                        lines.Add(trimmed);
                }
            }

            return lines.ToEquatableArray();
        }
        catch (System.Xml.XmlException)
        {
            return EquatableArray<string>.Empty;
        }
    }

    private static string CrefName(string cref)
    {
        int colon = cref.IndexOf(':');
        string name = colon >= 0 ? cref.Substring(colon + 1) : cref;
        int parameters = name.IndexOf('(');
        if (parameters >= 0)
            name = name.Substring(0, parameters);

        string[] parts = name.Split('.');
        bool isMember = colon == 1 && cref[0] is not 'T' and not 'N';
        return isMember && parts.Length >= 2 ? parts[parts.Length - 2] + "." + parts[parts.Length - 1] : parts[parts.Length - 1];
    }
}
