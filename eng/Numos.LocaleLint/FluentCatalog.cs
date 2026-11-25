using System.Text;
using Linguini.Syntax.Ast;
using Linguini.Syntax.Parser;
using Attribute = Linguini.Syntax.Ast.Attribute;

namespace Numos.LocaleLint;

/// <summary>
///     A reference from one pattern to a message, an attribute, or a term.
/// </summary>
/// <param name="IsTerm">Whether the target is a term.</param>
/// <param name="Id">The target id, without a leading dash.</param>
/// <param name="Attribute">The attribute name, or <see langword="null" />.</param>
internal readonly record struct FluentReference(bool IsTerm, string Id, string? Attribute)
{
    /// <summary>
    ///     Formats the reference the way Fluent source writes it.
    /// </summary>
    public override string ToString()
    {
        return (IsTerm ? "-" : "") + Id + (Attribute is null ? "" : "." + Attribute);
    }
}

/// <summary>
///     A select expression, reduced to what the rules need.
/// </summary>
/// <param name="VariantKeys">The variant keys, in source order. Numeric keys keep their literal text.</param>
/// <param name="DefaultKey">The key of the default variant.</param>
internal sealed record FluentSelect(IReadOnlyList<string> VariantKeys, string? DefaultKey);

/// <summary>
///     The value or one attribute of a message or term.
/// </summary>
internal sealed class FluentPart
{
    private const char PlaceableMarker = '￼';
    private readonly StringBuilder _allText = new();
    private readonly StringBuilder _signature = new();

    private readonly StringBuilder _top = new();
    private bool _pastIdSuffix;

    /// <summary>
    ///     Builds a part by walking a pattern.
    /// </summary>
    public FluentPart(string name, Pattern pattern)
    {
        Name = name;
        Walk(pattern, true, _signature);
    }

    /// <summary>
    ///     Gets the attribute name, or an empty string for the value.
    /// </summary>
    public string Name { get; }

    /// <summary>
    ///     Gets every variable the part uses, including selectors, function arguments, and all select variants.
    /// </summary>
    public SortedSet<string> Variables { get; } = new(StringComparer.Ordinal);

    /// <summary>
    ///     Gets the references the part makes, in source order.
    /// </summary>
    public List<FluentReference> References { get; } = [];

    /// <summary>
    ///     Gets the selects in the part, including nested ones.
    /// </summary>
    public List<FluentSelect> Selects { get; } = [];

    /// <summary>
    ///     Gets the text of the top-level pattern with each placeable replaced by a marker character.
    ///     Text inside select variants is not included.
    /// </summary>
    public string TopLevelText => _top.ToString();

    /// <summary>
    ///     Gets every literal text fragment in the part, including select variants, joined with newlines. The
    ///     top-level <c>##</c> suffix is left out, because ImGui never draws it.
    /// </summary>
    public string AllText => _allText.ToString();

    /// <summary>
    ///     Gets a string that is equal for two parts exactly when their literals, variables, and structure match.
    /// </summary>
    public string Signature => _signature.ToString();

    /// <summary>
    ///     Gets whether the top level holds a select expression.
    /// </summary>
    public bool HasTopLevelSelect { get; private set; }

    /// <summary>
    ///     Gets the text after the first <c>##</c> in the top-level text, or <see langword="null" /> when none exists.
    /// </summary>
    public string? IdSuffix
    {
        get
        {
            string text = TopLevelText;
            int index = text.IndexOf("##", StringComparison.Ordinal);
            return index < 0 ? null : text[(index + 2)..];
        }
    }

    /// <summary>
    ///     Gets the top-level text without the <c>##</c> suffix and trailing whitespace.
    /// </summary>
    public string DisplayText
    {
        get
        {
            string text = TopLevelText;
            int index = text.IndexOf("##", StringComparison.Ordinal);
            return (index < 0 ? text : text[..index]).TrimEnd();
        }
    }

    private void Walk(Pattern pattern, bool topLevel, StringBuilder signature)
    {
        foreach (var element in pattern.Elements)
        {
            switch (element)
            {
                case TextLiteral text:
                    string value = text.ToString();
                    if (topLevel)
                        _top.Append(value);

                    if (!topLevel)
                    {
                        _allText.Append(value).Append('\n');
                    }
                    else if (!_pastIdSuffix)
                    {
                        int suffix = value.IndexOf("##", StringComparison.Ordinal);
                        _pastIdSuffix = suffix >= 0;
                        _allText.Append(suffix >= 0 ? value[..suffix] : value).Append('\n');
                    }

                    signature.Append('"').Append(value).Append('"');
                    break;
                case Placeable placeable:
                    if (topLevel)
                        _top.Append(PlaceableMarker);

                    signature.Append('{');
                    WalkExpression(placeable.Expression, topLevel, signature);
                    signature.Append('}');
                    break;
            }
        }
    }

    private void WalkExpression(IExpression expression, bool topLevel, StringBuilder signature)
    {
        switch (expression)
        {
            case SelectExpression select:
                if (topLevel)
                    HasTopLevelSelect = true;

                signature.Append("select ");
                WalkInline(select.Selector, signature);
                var keys = new List<string>();
                string? defaultKey = null;
                foreach (var variant in select.Variants)
                {
                    string key = variant.Key.ToString();
                    keys.Add(key);
                    if (variant.IsDefault)
                        defaultKey = key;

                    signature.Append(variant.IsDefault ? " *[" : " [").Append(key).Append("] ");
                    Walk(variant.Value, false, signature);
                }

                Selects.Add(new FluentSelect(keys, defaultKey));
                break;
            case IInlineExpression inline:
                WalkInline(inline, signature);
                break;
        }
    }

    private void WalkInline(IInlineExpression expression, StringBuilder signature)
    {
        switch (expression)
        {
            case VariableReference variable:
                Variables.Add(variable.Id.ToString());
                signature.Append('$').Append(variable.Id);
                break;
            case MessageReference message:
                References.Add(new FluentReference(false, message.Id.ToString(), message.Attribute?.ToString()));
                signature.Append(message.Id);
                break;
            case TermReference term:
                References.Add(new FluentReference(true, term.Id.ToString().TrimStart('-'), term.Attribute?.ToString()));
                signature.Append('-').Append(term.Id.ToString().TrimStart('-'));
                WalkArguments(term.Arguments, signature);
                break;
            case FunctionReference function:
                signature.Append(function.Id).Append('(');
                WalkArguments(function.Arguments, signature);
                signature.Append(')');
                break;
            case NumberLiteral number:
                signature.Append(number);
                break;
            case TextLiteral text:
                signature.Append('"').Append(text).Append('"');
                _allText.Append(text).Append('\n');
                break;
            case Placeable nested:
                signature.Append('{');
                WalkExpression(nested.Expression, false, signature);
                signature.Append('}');
                break;
        }
    }

    private void WalkArguments(CallArguments? arguments, StringBuilder signature)
    {
        if (arguments is null)
            return;

        foreach (var positional in arguments.Value.PositionalArgs)
        {
            WalkInline(positional, signature);
            signature.Append(',');
        }

        foreach (var named in arguments.Value.NamedArgs)
        {
            signature.Append(named.Name).Append(':');
            WalkInline(named.Value, signature);
            signature.Append(',');
        }
    }
}

/// <summary>
///     A message or a term with its parts.
/// </summary>
internal sealed class FluentEntry
{
    /// <summary>
    ///     Gets the id, without a leading dash for terms.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    ///     Gets whether the entry is a term.
    /// </summary>
    public required bool IsTerm { get; init; }

    /// <summary>
    ///     Gets the path of the file, relative to the culture directory.
    /// </summary>
    public required string File { get; init; }

    /// <summary>
    ///     Gets the 1-based line of the entry.
    /// </summary>
    public required int Line { get; init; }

    /// <summary>
    ///     Gets the value part, or <see langword="null" /> when the entry has only attributes.
    /// </summary>
    public FluentPart? Value { get; init; }

    /// <summary>
    ///     Gets the attribute parts by name.
    /// </summary>
    public required SortedDictionary<string, FluentPart> Attributes { get; init; }

    /// <summary>
    ///     Gets the value part or the named attribute part, or <see langword="null" />.
    /// </summary>
    public FluentPart? GetPart(string? attribute)
    {
        return attribute is null ? Value : Attributes.GetValueOrDefault(attribute);
    }
}

/// <summary>
///     Everything one culture directory defines, parsed the way the viewer parses it.
/// </summary>
internal sealed class FluentCatalog
{
    /// <summary>
    ///     Gets the culture directory name.
    /// </summary>
    public required string Culture { get; init; }

    /// <summary>
    ///     Gets the first definition of each message.
    /// </summary>
    public Dictionary<string, FluentEntry> Messages { get; } = new(StringComparer.Ordinal);

    /// <summary>
    ///     Gets the first definition of each term.
    /// </summary>
    public Dictionary<string, FluentEntry> Terms { get; } = new(StringComparer.Ordinal);

    /// <summary>
    ///     Gets the second and later definitions of a message or term.
    /// </summary>
    public List<(FluentEntry Duplicate, FluentEntry First)> Duplicates { get; } = [];

    /// <summary>
    ///     Gets the parse errors as (file, row, message).
    /// </summary>
    public List<(string File, int Row, string Message)> ParseErrors { get; } = [];

    /// <summary>
    ///     Gets the file names in the culture directory, sorted by ordinal comparison.
    /// </summary>
    public List<string> Files { get; } = [];

    /// <summary>
    ///     Reads and parses every <c>.ftl</c> file directly under a culture directory.
    /// </summary>
    public static FluentCatalog Load(string culture, string directory)
    {
        var catalog = new FluentCatalog { Culture = culture };
        string[] files = Directory.GetFiles(directory, "*.ftl", SearchOption.TopDirectoryOnly);
        Array.Sort(files, StringComparer.Ordinal);

        foreach (string path in files)
        {
            string file = Path.GetFileName(path);
            catalog.Files.Add(file);

            var resource = ParseFile(path);
            foreach (var error in resource.Errors)
                catalog.ParseErrors.Add((file, error.Row, error.Message));

            var lines = new LineIndex(path);
            foreach (var entry in resource.Entries)
                catalog.Add(file, entry, lines);
        }

        return catalog;
    }

    /// <summary>
    ///     Parses a file with the same call the viewer makes.
    /// </summary>
    /// <remarks>
    ///     <see cref="LinguiniParser.FromFile" /> reads the file through a stream reader that drops a UTF-8 BOM, so a
    ///     BOM does not hide the first message. The BOM is reported separately as LOC104.
    /// </remarks>
    public static Resource ParseFile(string path)
    {
        return LinguiniParser.FromFile(path).Parse();
    }

    private void Add(string file, IEntry entry, LineIndex lines)
    {
        FluentEntry built;
        string id;
        bool isTerm;
        switch (entry)
        {
            case AstMessage message:
                id = message.GetId();
                isTerm = false;
                built = Build(id, false, file, lines.Next(id), message.Value, message.Attributes);
                break;
            case AstTerm term:
                id = term.GetId().TrimStart('-');
                isTerm = true;
                built = Build(id, true, file, lines.Next('-' + id), term.Value, term.Attributes);
                break;
            default:
                return;
        }

        Dictionary<string, FluentEntry> table = isTerm ? Terms : Messages;
        if (table.TryGetValue(id, out var first))
            Duplicates.Add((built, first));
        else
            table.Add(id, built);
    }

    private static FluentEntry Build(
        string id, bool isTerm, string file, int row, Pattern? value, List<Attribute> attributes)
    {
        var parts = new SortedDictionary<string, FluentPart>(StringComparer.Ordinal);
        foreach (var attribute in attributes)
            parts.TryAdd(attribute.Id.ToString(), new FluentPart(attribute.Id.ToString(), attribute.Value));

        return new FluentEntry
        {
            Id = id,
            IsTerm = isTerm,
            File = file,
            Line = row,
            Value = value is null ? null : new FluentPart("", value),
            Attributes = parts
        };
    }
}