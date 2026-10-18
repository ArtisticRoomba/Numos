using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Numos.API.Generators.Common;

namespace Numos.Replay.Generators;

/// <summary>
///     Generates <c>WriteOperation</c>/<c>ReadOperation</c> for every concrete replay operation in the compilation.
/// </summary>
/// <remarks>
///     <para>
///         An operation's opcode is read from its <c>Code</c> override, so the operation type is the only
///         registration. Numos.CoreSim owns the simulation family and Numos.API the world family; each compilation
///         only generates the family whose base type it declares.
///     </para>
///     <para>
///         Fields are encoded in primary-constructor order. Primitives and Numos.Units quantity aliases go straight
///         through <see cref="System.IO.BinaryWriter" />; any other field type needs a <c>Write{TypeName}</c>/
///         <c>Read{TypeName}</c> pair on the family's codec or on <c>AtmosReplayCodec</c>. When the codec declares
///         <c>Write{OperationName}</c>/<c>Read{OperationName}</c>, those encode the operation instead.
///     </para>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class OperationCodecGenerator : IIncrementalGenerator
{
    private const string SharedCodec = "Numos.CoreSim.Replay.AtmosReplayCodec";

    private static readonly OperationFamily[] Families =
    [
        new("Numos.CoreSim.Replay.AtmosOperation", "Numos.CoreSim.Replay.AtmosOperationCode",
            "Numos.CoreSim.Replay.AtmosReplayCodec", "Replay opcode"),
        new("Numos.API.AtmosWorldOperation", "Numos.API.AtmosWorldOperationCode",
            "Numos.API.AtmosWorldReplayCodec", "World replay opcode"),
    ];

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<OperationModel> operations = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is RecordDeclarationSyntax or ClassDeclarationSyntax &&
                                    ((TypeDeclarationSyntax)node).BaseList is not null,
                static (syntaxContext, cancellationToken) => Transform(syntaxContext, cancellationToken))
            .Where(static model => model is not null)!;

        IncrementalValueProvider<EquatableArray<FamilyCodes>> codes = context.CompilationProvider
            .Select(static (compilation, _) => CollectCodes(compilation));

        IncrementalValueProvider<GeneratorOutput<FamilyCodec>> output = operations.Collect()
            .Combine(codes)
            .Combine(WireType.QuantityAliases(context))
            .Select(static (input, _) => Plan(input.Left.Left, input.Left.Right, input.Right));

        GeneratorOutput.Register(context, output, "ReplayOperationCodecs", Emit);
    }

    private static OperationModel? Transform(GeneratorSyntaxContext context, CancellationToken cancellationToken)
    {
        var declaration = (TypeDeclarationSyntax)context.Node;
        if (context.SemanticModel.GetDeclaredSymbol(declaration, cancellationToken) is not INamedTypeSymbol type ||
            type.IsAbstract ||
            type.DeclaringSyntaxReferences[0].SyntaxTree != declaration.SyntaxTree ||
            type.DeclaringSyntaxReferences[0].Span != declaration.Span)
        {
            return null;
        }

        int familyIndex = FindFamily(type);
        if (familyIndex < 0)
            return null;

        OperationFamily family = Families[familyIndex];
        Compilation compilation = context.SemanticModel.Compilation;
        INamedTypeSymbol? codec = compilation.GetTypeByMetadataName(family.Codec);
        INamedTypeSymbol? shared = compilation.GetTypeByMetadataName(SharedCodec);
        INamedTypeSymbol? writerType = compilation.GetTypeByMetadataName("System.IO.BinaryWriter");
        INamedTypeSymbol? readerType = compilation.GetTypeByMetadataName("System.IO.BinaryReader");
        LocationInfo? location = LocationInfo.From(declaration.Identifier.GetLocation());
        string display = type.ToDisplayString();
        var diagnostics = new List<DiagnosticInfo>();

        (string? codeMember, ulong codeValue) = ReadCode(type, family, context.SemanticModel, cancellationToken);
        if (codeMember is null)
        {
            diagnostics.Add(DiagnosticInfo.Create(ReplayDiagnostics.NonConstantCode, location, display,
                ShortName(family.CodeEnum)));
        }

        string? customWrite = null;
        string? customRead = null;
        var fields = new List<OperationField>();
        if (codec is not null &&
            (codec.GetMembers("Write" + type.Name).Length > 0 || codec.GetMembers("Read" + type.Name).Length > 0))
        {
            IMethodSymbol? write = FindWrite(codec, "Write" + type.Name, type, writerType);
            IMethodSymbol? read = FindRead(codec, "Read" + type.Name, type, readerType);
            if (write is null || read is null)
            {
                string name = write is null ? "Write" + type.Name : "Read" + type.Name;
                string expected = write is null
                    ? $"static void {name}(BinaryWriter writer, {type.Name} operation)"
                    : $"static {type.Name} {name}(BinaryReader reader[, int maxStringBytes])";
                diagnostics.Add(DiagnosticInfo.Create(ReplayDiagnostics.MismatchedCustomMethod, location, display, name,
                    codec.Name, expected));
            }
            else
            {
                customWrite = Qualified(codec) + "." + write.Name;
                customRead = ReadCall(codec, read);
            }
        }
        else
        {
            IMethodSymbol[] constructors = type.InstanceConstructors
                .Where(constructor => !(constructor.Parameters.Length == 1 &&
                                        SymbolEqualityComparer.Default.Equals(constructor.Parameters[0].Type, type)))
                .ToArray();

            if (constructors.Length != 1)
            {
                diagnostics.Add(DiagnosticInfo.Create(ReplayDiagnostics.AmbiguousConstructor, location, display,
                    type.Name));
            }
            else
            {
                foreach (IParameterSymbol parameter in constructors[0].Parameters)
                {
                    WireType wireType = WireType.From(parameter.Type);
                    if (wireType.Kind is WireTypeKind.Primitive or WireTypeKind.Alias)
                    {
                        fields.Add(new OperationField(parameter.Name, wireType, null, null));
                        continue;
                    }

                    INamedTypeSymbol?[] containers = [codec, shared];
                    (string Write, string Read)? helper = containers
                        .Where(static container => container is not null)
                        .Select(container => FindHelper(container!, parameter.Type, writerType, readerType))
                        .FirstOrDefault(static found => found is not null);

                    if (helper is null)
                    {
                        diagnostics.Add(DiagnosticInfo.Create(ReplayDiagnostics.UnsupportedFieldType, location, display,
                            parameter.Name, parameter.Type.ToDisplayString(), parameter.Type.Name,
                            ShortName(family.Codec), type.Name));
                        continue;
                    }

                    fields.Add(new OperationField(parameter.Name, wireType, helper.Value.Write, helper.Value.Read));
                }
            }
        }

        return new OperationModel(
            familyIndex,
            type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            type.Name,
            display,
            codeMember,
            codeValue,
            customWrite,
            customRead,
            fields.ToEquatableArray(),
            diagnostics.ToEquatableArray(),
            location);
    }

    private static int FindFamily(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? current = type.BaseType; current is not null; current = current.BaseType)
        {
            string name = current.ToDisplayString();
            for (int index = 0; index < Families.Length; index++)
            {
                // A family belongs to the compilation that declares its base type, so Numos.API doesn't regenerate
                // the simulation codec it already sees in Numos.CoreSim.
                if (Families[index].BaseType == name)
                    return current.Locations.Any(static location => location.IsInSource) ? index : -1;
            }
        }

        return -1;
    }

    private static (string? Member, ulong Value) ReadCode(INamedTypeSymbol type, OperationFamily family,
        SemanticModel model, CancellationToken cancellationToken)
    {
        foreach (IPropertySymbol property in type.GetMembers("Code").OfType<IPropertySymbol>())
        {
            foreach (SyntaxReference reference in property.DeclaringSyntaxReferences)
            {
                if (reference.SyntaxTree != model.SyntaxTree)
                    continue;

                ExpressionSyntax? expression = reference.GetSyntax(cancellationToken) switch
                {
                    PropertyDeclarationSyntax { ExpressionBody: { } body } => body.Expression,
                    PropertyDeclarationSyntax { AccessorList: { } accessors } => accessors.Accessors
                        .FirstOrDefault(static accessor => accessor.IsKind(SyntaxKind.GetAccessorDeclaration))
                        ?.ExpressionBody?.Expression,
                    _ => null
                };

                if (expression is not null &&
                    model.GetSymbolInfo(expression, cancellationToken).Symbol is IFieldSymbol { HasConstantValue: true } member &&
                    member.ContainingType.ToDisplayString() == family.CodeEnum)
                {
                    return (member.Name, Convert.ToUInt64(member.ConstantValue, CultureInfo.InvariantCulture));
                }
            }
        }

        return (null, 0);
    }

    private static (string Write, string Read)? FindHelper(INamedTypeSymbol container, ITypeSymbol type,
        INamedTypeSymbol? writerType, INamedTypeSymbol? readerType)
    {
        IMethodSymbol? write = FindWrite(container, "Write" + type.Name, type, writerType);
        IMethodSymbol? read = FindRead(container, "Read" + type.Name, type, readerType);
        if (write is null || read is null)
            return null;

        return (Qualified(container) + "." + write.Name, ReadCall(container, read));
    }

    private static IMethodSymbol? FindWrite(INamedTypeSymbol container, string name, ITypeSymbol type,
        INamedTypeSymbol? writerType)
    {
        return container.GetMembers(name).OfType<IMethodSymbol>().FirstOrDefault(method =>
            method.IsStatic &&
            method.ReturnsVoid &&
            method.Parameters.Length == 2 &&
            SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type, writerType) &&
            SymbolEqualityComparer.Default.Equals(method.Parameters[1].Type, type));
    }

    /// <summary>
    ///     Finds a static reader that takes the <see cref="System.IO.BinaryReader" /> and, if it reads strings, the
    ///     caller's string byte limit.
    /// </summary>
    private static IMethodSymbol? FindRead(INamedTypeSymbol container, string name, ITypeSymbol type,
        INamedTypeSymbol? readerType)
    {
        return container.GetMembers(name).OfType<IMethodSymbol>().FirstOrDefault(method =>
            method.IsStatic &&
            SymbolEqualityComparer.Default.Equals(method.ReturnType, type) &&
            method.Parameters.Length is 1 or 2 &&
            SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type, readerType) &&
            (method.Parameters.Length == 1 || method.Parameters[1].Type.SpecialType == SpecialType.System_Int32));
    }

    private static string ReadCall(INamedTypeSymbol container, IMethodSymbol read)
    {
        return Qualified(container) + "." + read.Name +
               (read.Parameters.Length == 1 ? "(reader)" : "(reader, maxStringBytes)");
    }

    private static string Qualified(INamedTypeSymbol type)
    {
        return type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    }

    private static string ShortName(string metadataName)
    {
        return metadataName.Substring(metadataName.LastIndexOf('.') + 1);
    }

    private static EquatableArray<FamilyCodes> CollectCodes(Compilation compilation)
    {
        var families = new List<FamilyCodes>();
        for (int index = 0; index < Families.Length; index++)
        {
            INamedTypeSymbol? codeEnum = compilation.GetTypeByMetadataName(Families[index].CodeEnum);
            if (codeEnum is null || !codeEnum.Locations.Any(static location => location.IsInSource))
                continue;

            families.Add(new FamilyCodes(index, codeEnum.GetMembers()
                .OfType<IFieldSymbol>()
                .Where(static member => member.HasConstantValue)
                .Select(static member => new CodeMember(
                    member.Name,
                    Convert.ToUInt64(member.ConstantValue, CultureInfo.InvariantCulture),
                    LocationInfo.From(member.Locations.FirstOrDefault())))
                .ToEquatableArray()));
        }

        return families.ToEquatableArray();
    }

    private static GeneratorOutput<FamilyCodec> Plan(ImmutableArray<OperationModel> operations,
        EquatableArray<FamilyCodes> codes, EquatableArray<QuantityAlias> aliases)
    {
        var diagnostics = new List<DiagnosticInfo>();
        var codecs = new List<FamilyCodec>();
        foreach (FamilyCodes family in codes)
        {
            OperationFamily info = Families[family.Family];
            var claimed = new Dictionary<ulong, OperationModel>();
            var cases = new List<OperationCase>();
            IEnumerable<OperationModel> members = operations
                .Where(operation => operation.Family == family.Family)
                .OrderBy(static operation => operation.CodeValue)
                .ThenBy(static operation => operation.TypeName, StringComparer.Ordinal);

            foreach (OperationModel operation in members)
            {
                diagnostics.AddRange(operation.Diagnostics);
                if (operation.CodeMember is null)
                    continue;

                if (claimed.TryGetValue(operation.CodeValue, out OperationModel? first))
                {
                    diagnostics.Add(DiagnosticInfo.Create(ReplayDiagnostics.DuplicateCode, operation.Location,
                        operation.DisplayName, ShortName(info.CodeEnum) + "." + operation.CodeMember, first.DisplayName));
                    continue;
                }

                claimed.Add(operation.CodeValue, operation);
                if (operation.Diagnostics.Count == 0 && TryBuildCase(operation, info, aliases, diagnostics) is { } built)
                    cases.Add(built);
            }

            foreach (CodeMember member in family.Members)
            {
                if (!claimed.ContainsKey(member.Value))
                {
                    diagnostics.Add(DiagnosticInfo.Create(ReplayDiagnostics.CodeWithoutOperation, member.Location,
                        ShortName(info.CodeEnum), member.Name));
                }
            }

            if (cases.Count > 0)
                codecs.Add(new FamilyCodec(family.Family, cases.ToEquatableArray()));
        }

        return new GeneratorOutput<FamilyCodec>(diagnostics.ToEquatableArray(), codecs.ToEquatableArray());
    }

    private static OperationCase? TryBuildCase(OperationModel operation, OperationFamily family,
        EquatableArray<QuantityAlias> aliases, List<DiagnosticInfo> diagnostics)
    {
        string codeLabel = "global::" + family.CodeEnum + "." + operation.CodeMember;
        if (operation.CustomWrite is not null)
        {
            return new OperationCase(operation.TypeName, codeLabel,
                new EquatableArray<string>(new[] { operation.CustomWrite + "(writer, op);" }), operation.CustomRead!);
        }

        var writes = new List<string>();
        var reads = new List<string>();
        foreach (OperationField field in operation.Fields)
        {
            if (field.Write is not null)
            {
                writes.Add(field.Write + "(writer, op." + field.Name + ");");
                reads.Add(field.Read!);
                continue;
            }

            WireType type = field.Type.Resolve(aliases);
            if (type.Kind != WireTypeKind.Primitive)
            {
                diagnostics.Add(DiagnosticInfo.Create(ReplayDiagnostics.UnsupportedFieldType, operation.Location,
                    operation.DisplayName, field.Name, type.Name, type.Name, ShortName(family.Codec),
                    operation.Name));
                return null;
            }

            writes.Add("writer.Write(op." + field.Name + ");");
            reads.Add("reader.Read" + type.Name + "()");
        }

        return new OperationCase(operation.TypeName, codeLabel, writes.ToEquatableArray(),
            "new " + operation.TypeName + "(" + string.Join(", ", reads) + ")");
    }

    private static (string HintName, string Source) Emit(FamilyCodec codec)
    {
        OperationFamily family = Families[codec.Family];
        string codecNamespace = family.Codec.Substring(0, family.Codec.LastIndexOf('.'));
        string baseType = "global::" + family.BaseType;
        var writer = new CodeWriter();
        writer.Header();
        writer.Line("namespace " + codecNamespace);
        writer.Open();
        writer.Line("static partial class " + ShortName(family.Codec));
        writer.Open();

        writer.Line("internal static void WriteOperation(global::System.IO.BinaryWriter writer, " + baseType + " operation)");
        writer.Open();
        writer.Line("switch (operation)");
        writer.Open();
        foreach (OperationCase operation in codec.Cases)
        {
            writer.Line("case " + operation.TypeName + " op:");
            foreach (string line in operation.WriteLines)
                writer.Line("    " + line);
            writer.Line("    return;");
        }

        writer.Line("default:");
        writer.Line("    throw new global::System.NotSupportedException($\"" + family.Label +
                    " {operation.Code} is not serializable.\");");
        writer.Close();
        writer.Close();
        writer.Line();

        writer.Line("internal static " + baseType +
                    " ReadOperation(global::System.IO.BinaryReader reader, ushort code, int maxStringBytes)");
        writer.Open();
        writer.Line("switch ((global::" + family.CodeEnum + ")code)");
        writer.Open();
        foreach (OperationCase operation in codec.Cases)
        {
            writer.Line("case " + operation.CodeLabel + ":");
            writer.Line("    return " + operation.ReadExpression + ";");
        }

        writer.Line("default:");
        writer.Line("    throw new global::System.IO.InvalidDataException($\"" + family.Label + " {code} is unsupported.\");");
        writer.Close();
        writer.Close();

        writer.Close();
        writer.Close();
        return (family.Codec + ".Operations.g.cs", writer.ToString());
    }

    private sealed record OperationFamily(string BaseType, string CodeEnum, string Codec, string Label);
}

/// <summary>
///     One primary-constructor field. <paramref name="Write" /> is the qualified helper called as
///     <c>Write(writer, value)</c> and <paramref name="Read" /> the complete read expression; both are null for a
///     primitive or quantity alias, which <see cref="System.IO.BinaryWriter" /> handles directly.
/// </summary>
internal sealed record OperationField(string Name, WireType Type, string? Write, string? Read);

internal sealed record OperationModel(
    int Family,
    string TypeName,
    string Name,
    string DisplayName,
    string? CodeMember,
    ulong CodeValue,
    string? CustomWrite,
    string? CustomRead,
    EquatableArray<OperationField> Fields,
    EquatableArray<DiagnosticInfo> Diagnostics,
    LocationInfo? Location);

internal sealed record CodeMember(string Name, ulong Value, LocationInfo? Location);

internal sealed record FamilyCodes(int Family, EquatableArray<CodeMember> Members);

internal sealed record OperationCase(
    string TypeName,
    string CodeLabel,
    EquatableArray<string> WriteLines,
    string ReadExpression);

internal sealed record FamilyCodec(int Family, EquatableArray<OperationCase> Cases);
