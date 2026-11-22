using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Numos.API.Generators.Common;
using Numos.API.Generators.State;

namespace Numos.API.Generators.ChunkData;

// The per-struct transform for [ChunkComponent]. The cross-declaration checks live in ChunkDataGenerator.cs, because a
// component field and a [ChunkData] property can share a key or an accessor name.
public sealed partial class ChunkDataGenerator
{
    private const string ComponentAttributeMetadataName = "Numos.API.ChunkComponentAttribute";
    private const string PerChunkAttributeMetadataName = "Numos.API.PerChunkAttribute";

    private static ChunkComponentDeclaration? CreateComponentModel(
        GeneratorAttributeSyntaxContext context,
        CancellationToken cancellationToken)
    {
        if (context.TargetSymbol is not INamedTypeSymbol type || context.Attributes.IsDefaultOrEmpty)
            return null;

        var syntax = (TypeDeclarationSyntax)context.TargetNode;
        AttributeData attribute = context.Attributes[0];
        var diagnostics = new List<DiagnosticInfo>();

        LocationInfo? location = LocationInfo.From(syntax.Identifier.GetLocation());
        LocationInfo? attributeLocation =
            LocationInfo.From(attribute.ApplicationSyntaxReference?.GetSyntax(cancellationToken)) ?? location;

        List<(INamedTypeSymbol Type, bool IsPartial)> chain = PartialSyntax.GetContainingChain(type, syntax);
        string displayName = string.Join(".", chain.Select(static link => link.Type.Name));

        bool accessible = true;
        foreach ((INamedTypeSymbol link, bool isPartial) in chain)
        {
            string? problem = SymbolEqualityComparer.Default.Equals(link, type) && type.TypeKind != TypeKind.Struct
                ? "is not a struct"
                : link.IsFileLocal ? "is file-local"
                : !isPartial ? "is not partial"
                : link.TypeParameters.Length > 0 ? "is generic"
                : null;

            if (problem is not null)
            {
                diagnostics.Add(
                    DiagnosticInfo.Create(
                        ChunkComponentDiagnostics.NotPartialStruct,
                        location,
                        displayName,
                        link.ToDisplayString(),
                        problem));
            }

            if (!PartialSyntax.IsAccessibleFromNamespace(link.DeclaredAccessibility))
                accessible = false;
        }

        if (!accessible)
            diagnostics.Add(DiagnosticInfo.Create(ChunkComponentDiagnostics.Inaccessible, location, displayName));

        string? key = attribute.ConstructorArguments.Length == 1 ? attribute.ConstructorArguments[0].Value as string : null;
        if (string.IsNullOrWhiteSpace(key))
        {
            diagnostics.Add(DiagnosticInfo.Create(ChunkComponentDiagnostics.BlankKey, attributeLocation, displayName));
            key = null;
        }

        bool clearOnSleep = false;
        bool clearOnVacuum = false;
        string? accessorName = null;
        foreach (KeyValuePair<string, TypedConstant> argument in attribute.NamedArguments)
        {
            switch (argument.Key)
            {
                case "ClearOnSleep" when argument.Value.Value is bool value:
                    clearOnSleep = value;
                    break;
                case "ClearOnVacuum" when argument.Value.Value is bool value:
                    clearOnVacuum = value;
                    break;
                case "AccessorName":
                    accessorName = argument.Value.Value as string;
                    break;
            }
        }

        if (accessorName is not null && !SyntaxFacts.IsValidIdentifier(accessorName))
        {
            diagnostics.Add(
                DiagnosticInfo.Create(
                    ChunkComponentDiagnostics.InvalidAccessorName,
                    attributeLocation,
                    accessorName,
                    displayName));
        }

        var fields = new List<ChunkComponentFieldDeclaration>();
        var fieldByWireName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (ISymbol member in type.GetMembers())
        {
            // The generated Fields and View types would collide with the member, and with a field inside themselves.
            if (!member.IsImplicitlyDeclared &&
                member.Name is ChunkComponentEmitter.HolderName or ChunkComponentEmitter.ViewName)
            {
                diagnostics.Add(
                    DiagnosticInfo.Create(
                        ChunkComponentDiagnostics.ReservedMemberName,
                        LocationInfo.From(member.Locations.FirstOrDefault()) ?? location,
                        member.Name,
                        displayName));

                continue;
            }

            ChunkComponentFieldDeclaration? field = ReadComponentMember(
                member,
                displayName,
                key ?? "",
                location,
                diagnostics,
                out string? wireName);

            if (field is null)
                continue;

            if (fieldByWireName.TryGetValue(wireName!, out string? owner))
            {
                diagnostics.Add(
                    DiagnosticInfo.Create(
                        ChunkComponentDiagnostics.DuplicateWireName,
                        field.Location,
                        member.Name,
                        displayName,
                        wireName!,
                        owner,
                        field.Field.Key));

                continue;
            }

            fieldByWireName.Add(wireName!, member.Name);
            fields.Add(field);
        }

        if (fields.Count == 0 &&
            !diagnostics.Any(static diagnostic =>
                diagnostic.Descriptor == ChunkComponentDiagnostics.InvalidMark ||
                diagnostic.Descriptor == ChunkComponentDiagnostics.ReservedMemberName))
            diagnostics.Add(DiagnosticInfo.Create(ChunkComponentDiagnostics.NoFields, location, displayName));

        var model = new ChunkComponentModel(
            PartialTypeModel.From(type),
            type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            displayName,
            string.Concat(chain.Select(static link => link.Type.Name)) + "ComponentAccessors",
            accessorName ?? type.Name,
            chain.All(static link => link.Type.DeclaredAccessibility == Accessibility.Public),
            clearOnSleep,
            clearOnVacuum,
            fields.Select(static field => field.Field).ToEquatableArray(),
            false);

        return new ChunkComponentDeclaration(
            model,
            fields.ToEquatableArray(),
            location,
            diagnostics.ToEquatableArray());
    }

    /// <summary>
    ///     Checks one member of a component and returns the chunk field it lowers to, or null when it is not one.
    /// </summary>
    /// <remarks>
    ///     A field with an error still returns its model when the wire name is known, so a duplicate name is reported
    ///     once. The caller drops the whole component when any error was reported.
    /// </remarks>
    private static ChunkComponentFieldDeclaration? ReadComponentMember(
        ISymbol member, string componentName, string componentKey,
        LocationInfo? componentLocation, List<DiagnosticInfo> diagnostics,
        out string? wireName)
    {
        wireName = null;
        LocationInfo? location = LocationInfo.From(member.Locations.FirstOrDefault()) ?? componentLocation;

        // An auto-property's backing field carries a [field: ...] mark. Report it on the property.
        if (member is IFieldSymbol { IsImplicitlyDeclared: true, AssociatedSymbol: IPropertySymbol property })
        {
            if (HasStateMark(member))
                ReportInvalidMark(diagnostics, location, property.Name, componentName, "is a property; only instance fields can be component fields");

            return null;
        }

        if (member.IsImplicitlyDeclared)
            return null;

        if (member is not IFieldSymbol field)
        {
            if (member is IPropertySymbol && HasStateMark(member))
                ReportInvalidMark(diagnostics, location, member.Name, componentName, "is a property; only instance fields can be component fields");

            return null;
        }

        AttributeData? serialize = StateSymbols.FindSerialize(field);
        bool scratch = StateSymbols.IsScratch(field);
        bool ignored = StateSymbols.IsIgnored(field);

        if (field.IsStatic)
        {
            if (serialize is not null || scratch || ignored)
                ReportInvalidMark(diagnostics, location, field.Name, componentName, field.IsConst ? "is a constant" : "is static");

            return null;
        }

        if (ignored)
        {
            ReportInvalidMark(
                diagnostics,
                location,
                field.Name,
                componentName,
                "has [NumosIgnore]; a component field is [NumosSerialize] or [NumosScratch], so remove the field or mark it");

            return null;
        }

        if (serialize is null && !scratch)
        {
            diagnostics.Add(DiagnosticInfo.Create(ChunkComponentDiagnostics.UnmarkedField, location, field.Name, componentName));
            return null;
        }

        if (serialize is not null && scratch)
        {
            ReportInvalidMark(diagnostics, location, field.Name, componentName, "has both [NumosSerialize] and [NumosScratch]");
            return null;
        }

        if (HasInitializer(field))
            diagnostics.Add(DiagnosticInfo.Create(ChunkComponentDiagnostics.FieldInitializer, location, field.Name, componentName));

        wireName = field.Name;
        if (serialize is not null)
        {
            if (serialize.ConstructorArguments.Length == 1)
            {
                wireName = serialize.ConstructorArguments[0].Value as string;
                if (string.IsNullOrWhiteSpace(wireName))
                {
                    ReportInvalidMark(diagnostics, location, field.Name, componentName, "has a blank wire name");
                    wireName = null;
                    return null;
                }
            }

            foreach (string unsupported in new[] { "Codec", "Count" })
            {
                if (StateSymbols.HasNamedArgument(serialize, unsupported))
                {
                    ReportInvalidMark(
                        diagnostics,
                        location,
                        field.Name,
                        componentName,
                        "sets NumosSerialize." + unsupported + ", which a chunk field can't use");
                }
            }
        }

        if (field.RefKind != RefKind.None)
            ReportInvalidMark(diagnostics, location, field.Name, componentName, "is a ref field");

        if (field.IsFixedSizeBuffer)
            ReportInvalidMark(diagnostics, location, field.Name, componentName, "is a fixed-size buffer");

        ITypeSymbol elementType = field.Type;
        string elementDisplay = elementType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        // Mirrors the [ChunkData] checks: an error type is never portable-checked, but it also isn't unmanaged.
        if (!scratch &&
            (!elementType.IsUnmanagedType ||
             elementType.TypeKind != TypeKind.Error && !IsPortableElementType(elementType)))
        {
            diagnostics.Add(
                DiagnosticInfo.Create(
                    ChunkComponentDiagnostics.SavedWithUnportableType,
                    location,
                    field.Name,
                    componentName,
                    elementDisplay));
        }

        if (scratch && !elementType.IsUnmanagedType)
        {
            diagnostics.Add(
                DiagnosticInfo.Create(
                    ChunkComponentDiagnostics.ScratchWithManagedType,
                    location,
                    field.Name,
                    componentName,
                    elementDisplay));
        }

        int? length = null;
        if (FindAttribute(field, PerChunkAttributeMetadataName) is { } perChunk)
        {
            length = 1;
            foreach (KeyValuePair<string, TypedConstant> argument in perChunk.NamedArguments)
            {
                if (argument.Key == "Length" && argument.Value.Value is int value)
                    length = value;
            }

            if (length < 0)
            {
                diagnostics.Add(
                    DiagnosticInfo.Create(
                        ChunkComponentDiagnostics.NegativeLength,
                        location,
                        field.Name,
                        componentName,
                        length.Value.ToString(CultureInfo.InvariantCulture)));
            }
        }

        var model = new ChunkComponentFieldModel(
            field.Name,
            componentKey + "/" + wireName,
            elementType.ToDisplayString(PartialSyntax.FullyQualifiedWithNullability),
            scratch,
            length);

        return new ChunkComponentFieldDeclaration(model, elementDisplay, location);
    }

    private static bool HasInitializer(IFieldSymbol field)
    {
        foreach (SyntaxReference reference in field.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax() is VariableDeclaratorSyntax { Initializer: not null })
                return true;
        }

        return false;
    }

    private static bool HasStateMark(ISymbol member)
    {
        return StateSymbols.FindSerialize(member) is not null || StateSymbols.IsScratch(member) || StateSymbols.IsIgnored(member);
    }

    private static void ReportInvalidMark(
        List<DiagnosticInfo> diagnostics, LocationInfo? location,
        string memberName, string componentName, string problem)
    {
        diagnostics.Add(
            DiagnosticInfo.Create(ChunkComponentDiagnostics.InvalidMark, location, memberName, componentName, problem));
    }

    private static AttributeData? FindAttribute(ISymbol symbol, string metadataName)
    {
        foreach (AttributeData attribute in symbol.GetAttributes())
        {
            if (attribute.AttributeClass is { } attributeClass &&
                PartialTypeModel.GetMetadataName(attributeClass) == metadataName)
                return attribute;
        }

        return null;
    }
}
