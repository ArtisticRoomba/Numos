using System.Globalization;
using System.Text;
using Linguini.Syntax.Ast;

namespace Numos.LocaleLint;

/// <summary>
///     Checks the viewer's Fluent files and the code that reads them.
/// </summary>
/// <remarks>
///     <c>en</c> is the reference culture. The linter compares every other culture directory with it, scans the
///     viewer's C# source for <c>Locale.GetString</c> calls, and checks <c>credits.ftl</c>. The rule table is in
///     <c>docs/localization.md</c>.
/// </remarks>
internal static class LocaleLinter
{
    private const string ReferenceCulture = "en";
    private const string CreditsFile = "credits.ftl";
    private readonly static string[] ViewerPath = ["src", "Numos.Viewer"];

    private readonly static Dictionary<string, Severity> Severities = new(StringComparer.Ordinal)
    {
        ["LOC001"] = Severity.Error, ["LOC002"] = Severity.Error, ["LOC003"] = Severity.Error,
        ["LOC004"] = Severity.Error, ["LOC005"] = Severity.Error, ["LOC006"] = Severity.Error,
        ["LOC007"] = Severity.Error, ["LOC008"] = Severity.Error, ["LOC009"] = Severity.Error,
        ["LOC010"] = Severity.Error, ["LOC011"] = Severity.Error, ["LOC012"] = Severity.Error,
        ["LOC101"] = Severity.Warning, ["LOC102"] = Severity.Warning, ["LOC103"] = Severity.Warning,
        ["LOC104"] = Severity.Warning, ["LOC105"] = Severity.Warning, ["LOC106"] = Severity.Warning,
        ["LOC201"] = Severity.Note
    };

    /// <summary>
    ///     Runs every rule and returns the findings in a deterministic order.
    /// </summary>
    /// <param name="options">The repo root and the output filters.</param>
    /// <exception cref="LocaleLintSetupException">The locale directory is missing or the culture is unknown.</exception>
    public static LocaleLintResult Run(LocaleLintOptions options)
    {
        string root = Path.GetFullPath(options.Root);
        string viewerDirectory = Path.Combine(root, Path.Combine(ViewerPath));
        string localeDirectory = Path.Combine(viewerDirectory, "locale");

        if (!Directory.Exists(localeDirectory))
            throw new LocaleLintSetupException($"The locale directory '{localeDirectory}' does not exist.");

        string[] cultures = Directory.GetDirectories(localeDirectory)
            .Select(static d => Path.GetFileName(d))
            .Order(StringComparer.Ordinal)
            .ToArray();

        if (!cultures.Contains(ReferenceCulture))
            throw new LocaleLintSetupException($"The reference culture directory '{ReferenceCulture}' does not exist.");

        if (options.Culture is not null && !cultures.Contains(options.Culture))
        {
            throw new LocaleLintSetupException(
                $"The culture '{options.Culture}' has no directory. Cultures: {string.Join(", ", cultures)}.");
        }

        var fonts = options.Fonts ?? FontCoverage.Load(Path.Combine(viewerDirectory, "assets"));
        var context = new Context(root, localeDirectory, cultures, fonts);

        var catalogs = new Dictionary<string, FluentCatalog>(StringComparer.Ordinal);
        foreach (string culture in cultures)
            catalogs[culture] = FluentCatalog.Load(culture, Path.Combine(localeDirectory, culture));

        var english = catalogs[ReferenceCulture];
        foreach (string culture in cultures)
        {
            var catalog = catalogs[culture];
            CheckFiles(context, catalog);
            CheckReferences(context, catalog);
            CheckGlyphs(context, catalog);
            CheckPlurals(context, catalog);
            if (culture != ReferenceCulture)
                Compare(context, english, catalog);
        }

        CheckCredits(context);
        CheckCode(context, english, CodeScanner.Scan(viewerDirectory, root));

        IEnumerable<Finding> findings = context.Findings;
        if (!options.Untranslated)
            findings = findings.Where(static f => f.Rule != "LOC201");

        if (options.Culture is { } only)
            findings = findings.Where(f => f.Culture == only || f.Culture is null && only == ReferenceCulture);

        List<Finding> sorted = findings
            .OrderBy(static f => f.Path, StringComparer.Ordinal)
            .ThenBy(static f => f.Line)
            .ThenBy(static f => f.Column)
            .ThenBy(static f => f.Rule, StringComparer.Ordinal)
            .ThenBy(static f => f.Message, StringComparer.Ordinal)
            .ToList();

        return new LocaleLintResult(sorted, options.Culture is null ? cultures : [options.Culture]);
    }

    private static void CheckFiles(Context context, FluentCatalog catalog)
    {
        string culture = catalog.Culture;

        foreach (string file in catalog.Files)
        {
            string path = context.CulturePath(culture, file);
            CheckBytes(context, path, culture, Path.Combine(context.LocaleDirectory, culture, file));
        }

        foreach ((string file, int row, string message) in catalog.ParseErrors)
            context.Add("LOC001", context.CulturePath(culture, file), row, 1, message, culture);

        foreach (var (duplicate, first) in catalog.Duplicates)
        {
            string kind = duplicate.IsTerm ? "term" : "message";
            string name = (duplicate.IsTerm ? "-" : "") + duplicate.Id;
            context.Add(
                "LOC002",
                context.CulturePath(culture, duplicate.File),
                duplicate.Line,
                1,
                $"{kind} {name} is already defined in {first.File}:{first.Line}",
                culture);
        }
    }

    private static void CheckBytes(Context context, string path, string? culture, string fullPath)
    {
        byte[] bytes = File.ReadAllBytes(fullPath);
        bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        bool crlf = false;
        for (int i = 0; i + 1 < bytes.Length; i++)
        {
            if (bytes[i] == '\r' && bytes[i + 1] == '\n')
            {
                crlf = true;
                break;
            }
        }

        if (bom)
            context.Add("LOC104", path, 1, 1, "file starts with a byte order mark", culture);

        if (crlf)
            context.Add("LOC104", path, 1, 1, "file uses CRLF line endings", culture);
    }

    private static void CheckReferences(Context context, FluentCatalog catalog)
    {
        foreach (var entry in AllEntries(catalog))
        {
            foreach (var part in Parts(entry))
            {
                foreach (var reference in part.References.Distinct())
                {
                    Dictionary<string, FluentEntry> table = reference.IsTerm ? catalog.Terms : catalog.Messages;
                    string? problem = null;
                    if (!table.TryGetValue(reference.Id, out var target))
                    {
                        problem =
                            $"{(reference.IsTerm ? "term" : "message")} {new FluentReference(reference.IsTerm, reference.Id, null)}";
                    }
                    else if (reference.Attribute is not null && !target.Attributes.ContainsKey(reference.Attribute))
                    {
                        problem = $"attribute {reference}";
                    }

                    if (problem is not null)
                    {
                        context.Add(
                            "LOC007",
                            context.CulturePath(catalog.Culture, entry.File),
                            entry.Line,
                            1,
                            $"{Label(entry, part)} references {problem}, which does not exist in {catalog.Culture}",
                            catalog.Culture);
                    }
                }
            }
        }
    }

    private static void CheckGlyphs(Context context, FluentCatalog catalog)
    {
        foreach (var entry in AllEntries(catalog))
        {
            foreach (var part in Parts(entry))
            {
                SortedSet<int> missing = MissingGlyphs(context.Fonts, part.AllText);
                if (missing.Count > 0)
                {
                    context.Add(
                        "LOC009",
                        context.CulturePath(catalog.Culture, entry.File),
                        entry.Line,
                        1,
                        $"{Label(entry, part)} has no glyph for {DescribeCodepoints(missing)}",
                        catalog.Culture);
                }
            }
        }
    }

    private static SortedSet<int> MissingGlyphs(FontCoverage fonts, string text)
    {
        var missing = new SortedSet<int>();
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value >= 0x20 && !fonts.Has(rune.Value))
                missing.Add(rune.Value);
        }

        return missing;
    }

    private static string DescribeCodepoints(IEnumerable<int> codepoints)
    {
        return string.Join(", ", codepoints.Select(static c => $"U+{c:X4} \"{char.ConvertFromUtf32(c)}\""));
    }

    private static void CheckPlurals(Context context, FluentCatalog catalog)
    {
        IReadOnlySet<string>? categories = null;
        bool unavailable = false;

        foreach (var entry in catalog.Messages.Values)
        {
            foreach (var part in Parts(entry))
            {
                foreach (var select in part.Selects)
                {
                    if (!select.VariantKeys.Any(Plurals.CategoryNames.Contains))
                        continue;

                    if (categories is null && !unavailable)
                    {
                        try
                        {
                            categories = Plurals.IntegerCategories(CultureInfo.GetCultureInfo(catalog.Culture));
                        }
                        catch (CultureNotFoundException)
                        {
                            unavailable = true;
                        }
                    }

                    if (categories is null)
                        continue;

                    string[] lacking = categories.Where(c => !select.VariantKeys.Contains(c)).ToArray();
                    if (lacking.Length > 0)
                    {
                        context.Add(
                            "LOC105",
                            context.CulturePath(catalog.Culture, entry.File),
                            entry.Line,
                            1,
                            $"{Label(entry, part)} has a plural select without {string.Join(", ", lacking)}, " +
                            $"which {catalog.Culture} uses (categories: {string.Join(", ", categories)})",
                            catalog.Culture);
                    }
                }
            }
        }
    }

    private static void Compare(Context context, FluentCatalog english, FluentCatalog catalog)
    {
        string culture = catalog.Culture;

        foreach (var entry in english.Messages.Values.OrderBy(static e => e.File, StringComparer.Ordinal)
                     .ThenBy(static e => e.Line))
        {
            if (!catalog.Messages.ContainsKey(entry.Id))
            {
                context.Add(
                    "LOC003",
                    context.CulturePath(culture, entry.File),
                    1,
                    1,
                    $"message {entry.Id} is missing (en defines it at {entry.File}:{entry.Line})",
                    culture);
            }
        }

        foreach (var entry in catalog.Messages.Values)
        {
            string path = context.CulturePath(culture, entry.File);
            if (!english.Messages.TryGetValue(entry.Id, out var reference))
            {
                context.Add("LOC004", path, entry.Line, 1, $"message {entry.Id} does not exist in en", culture);
                continue;
            }

            if (entry.File != reference.File)
            {
                context.Add(
                    "LOC101",
                    path,
                    entry.Line,
                    1,
                    $"message {entry.Id} is in {entry.File} but en has it in {reference.File}",
                    culture);
            }

            CompareShape(context, culture, path, entry, reference);

            foreach (var part in Parts(entry))
            {
                var referencePart = reference.GetPart(part.Name.Length == 0 ? null : part.Name);
                if (referencePart is not null)
                    CompareParts(context, culture, path, entry, part, referencePart);
            }
        }
    }

    private static void CompareShape(Context context, string culture, string path, FluentEntry entry, FluentEntry reference)
    {
        var problems = new List<string>();
        if (entry.Value is null && reference.Value is not null)
            problems.Add("has no value but en has one");

        if (entry.Value is not null && reference.Value is null)
            problems.Add("has a value but en has none");

        string[] missing = reference.Attributes.Keys.Except(entry.Attributes.Keys).ToArray();
        string[] extra = entry.Attributes.Keys.Except(reference.Attributes.Keys).ToArray();
        if (missing.Length > 0)
            problems.Add($"lacks attribute {string.Join(", ", missing.Select(static a => "." + a))}");

        if (extra.Length > 0)
            problems.Add($"has extra attribute {string.Join(", ", extra.Select(static a => "." + a))}");

        if (problems.Count > 0)
            context.Add("LOC005", path, entry.Line, 1, $"message {entry.Id} {string.Join("; ", problems)}", culture);
    }

    private static void CompareParts(
        Context context, string culture, string path, FluentEntry entry, FluentPart part, FluentPart reference)
    {
        string label = Label(entry, part);

        if (!part.Variables.SetEquals(reference.Variables))
        {
            string[] missing = reference.Variables.Except(part.Variables).ToArray();
            string[] extra = part.Variables.Except(reference.Variables).ToArray();
            var details = new List<string>();
            if (missing.Length > 0)
                details.Add("missing " + string.Join(", ", missing.Select(static v => "$" + v)));

            if (extra.Length > 0)
                details.Add("extra " + string.Join(", ", extra.Select(static v => "$" + v)));

            context.Add("LOC006", path, entry.Line, 1, $"{label} variables differ from en ({string.Join("; ", details)})", culture);
        }

        string? suffix = part.IdSuffix;
        string? referenceSuffix = reference.IdSuffix;
        if (suffix != referenceSuffix)
        {
            string Describe(string? value)
            {
                return value is null ? "no ## suffix" : $"suffix \"##{value}\"";
            }

            context.Add(
                "LOC008",
                path,
                entry.Line,
                1,
                $"{label} has {Describe(suffix)} but en has {Describe(referenceSuffix)}",
                culture);
        }

        if (!part.HasTopLevelSelect && !reference.HasTopLevelSelect)
        {
            string text = part.DisplayText;
            string referenceText = reference.DisplayText;
            if (!text.Contains('\n') && !referenceText.Contains('\n') && text.Length > 0 && referenceText.Length > 0)
            {
                char? punctuation = Punctuation(text[^1]);
                char? referencePunctuation = Punctuation(referenceText[^1]);
                if (punctuation != referencePunctuation)
                {
                    context.Add(
                        "LOC102",
                        path,
                        entry.Line,
                        1,
                        $"{label} ends with {DescribeEnd(text[^1])} but en ends with {DescribeEnd(referenceText[^1])}",
                        culture);
                }
            }
        }

        if (part.Signature == reference.Signature && part.AllText.Any(char.IsLetter))
            context.Add("LOC201", path, entry.Line, 1, $"{label} is identical to en", culture);
    }

    private static char? Punctuation(char last)
    {
        return last is '.' or '!' or '?' or ':' or '…' ? last : null;
    }

    private static string DescribeEnd(char last)
    {
        return last == '￼' ? "a placeable" : $"\"{last}\"";
    }

    private static void CheckCredits(Context context)
    {
        string full = Path.Combine(context.LocaleDirectory, CreditsFile);
        string path = context.LocalePath(CreditsFile);
        if (!File.Exists(full))
        {
            context.Add("LOC012", path, 1, 1, $"{CreditsFile} is missing, and the viewer fails at startup without it", null);
            return;
        }

        CheckBytes(context, path, null, full);

        var resource = FluentCatalog.ParseFile(full);
        foreach (var error in resource.Errors)
            context.Add("LOC001", path, error.Row, 1, error.Message, null);

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var lines = new LineIndex(full);
        foreach (var entry in resource.Entries)
        {
            string id = entry switch
            {
                AstMessage m => m.GetId(),
                AstTerm term => "-" + term.GetId().TrimStart('-'),
                _ => ""
            };

            if (id.Length == 0)
                continue;

            int line = lines.Next(id);
            if (entry is not AstMessage message)
                continue;

            if (!ids.Add(id))
                context.Add("LOC012", path, line, 1, $"credit {id} is defined twice", null);

            if (message.Value is null)
            {
                context.Add("LOC012", path, line, 1, $"credit {id} has no value", null);
            }
            else
            {
                CheckCreditText(context, path, line, id, message.Value, null);
            }

            bool hasEnglish = false;
            foreach (var attribute in message.Attributes)
            {
                string name = attribute.Id.ToString();
                hasEnglish |= name == ReferenceCulture;
                if (!context.Cultures.Contains(name))
                {
                    context.Add(
                        "LOC012",
                        path,
                        line,
                        1,
                        $"credit {id} has attribute .{name}, which names no culture directory",
                        null);
                }

                CheckCreditText(context, path, line, $"{id}.{name}", attribute.Value, context.Cultures.Contains(name) ? name : null);
            }

            if (!hasEnglish)
                context.Add("LOC012", path, line, 1, $"credit {id} has no .en attribute", null);
        }
    }

    private static void CheckCreditText(
        Context context, string path, int line, string label, Pattern pattern, string? culture)
    {
        var text = new StringBuilder();
        foreach (var element in pattern.Elements)
        {
            if (element is TextLiteral literal)
            {
                text.Append(literal);
            }
            else
            {
                context.Add("LOC012", path, line, 1, $"credit {label} must be plain text, without placeables", culture);
                return;
            }
        }

        SortedSet<int> missing = MissingGlyphs(context.Fonts, text.ToString());
        if (missing.Count > 0)
            context.Add("LOC009", path, line, 1, $"credit {label} has no glyph for {DescribeCodepoints(missing)}", culture);
    }

    private static void CheckCode(Context context, FluentCatalog english, CodeScan scan)
    {
        foreach (var call in scan.Calls)
        {
            int dot = call.Key.IndexOf('.');
            string id = dot < 0 ? call.Key : call.Key[..dot];
            string? attribute = dot < 0 ? null : call.Key[(dot + 1)..];

            if (!english.Messages.TryGetValue(id, out var entry))
            {
                context.Add("LOC010", call.Path, call.Line, call.Column, $"key \"{call.Key}\" does not exist in en", null);
                continue;
            }

            var part = entry.GetPart(attribute);
            if (part is null)
            {
                context.Add(
                    "LOC010",
                    call.Path,
                    call.Line,
                    call.Column,
                    $"key \"{call.Key}\" does not exist in en, because {id} has no attribute {attribute}",
                    null);

                continue;
            }

            if (call.Arguments is null)
                continue;

            string[] missing = part.Variables.Except(call.Arguments).ToArray();
            string[] extra = call.Arguments.Except(part.Variables).Order(StringComparer.Ordinal).ToArray();
            if (missing.Length > 0)
            {
                context.Add(
                    "LOC011",
                    call.Path,
                    call.Line,
                    call.Column,
                    $"call for \"{call.Key}\" does not pass {string.Join(", ", missing.Select(static v => "$" + v))}",
                    null);
            }

            if (extra.Length > 0)
            {
                context.Add(
                    "LOC106",
                    call.Path,
                    call.Line,
                    call.Column,
                    $"call for \"{call.Key}\" passes {string.Join(", ", extra.Select(static v => "$" + v))}, which en does not use",
                    null);
            }
        }

        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (string literal in scan.Literals)
        {
            used.Add(literal);
            int dot = literal.IndexOf('.');
            if (dot > 0)
                used.Add(literal[..dot]);
        }

        foreach (var entry in english.Messages.Values)
        {
            foreach (var part in Parts(entry))
            {
                foreach (var reference in part.References)
                {
                    if (!reference.IsTerm)
                        used.Add(reference.Id);
                }
            }
        }

        foreach (var entry in english.Messages.Values)
        {
            if (!used.Contains(entry.Id))
            {
                context.Add(
                    "LOC103",
                    context.CulturePath(english.Culture, entry.File),
                    entry.Line,
                    1,
                    $"message {entry.Id} appears in no string literal in viewer source",
                    null);
            }
        }
    }

    private static IEnumerable<FluentEntry> AllEntries(FluentCatalog catalog)
    {
        return catalog.Messages.Values.Concat(catalog.Terms.Values);
    }

    private static IEnumerable<FluentPart> Parts(FluentEntry entry)
    {
        if (entry.Value is not null)
            yield return entry.Value;

        foreach (var attribute in entry.Attributes.Values)
            yield return attribute;
    }

    private static string Label(FluentEntry entry, FluentPart part)
    {
        string id = (entry.IsTerm ? "-" : "") + entry.Id;
        return part.Name.Length == 0 ? id : $"{id}.{part.Name}";
    }

    private sealed class Context(string root, string localeDirectory, string[] cultures, FontCoverage fonts)
    {
        public string LocaleDirectory { get; } = localeDirectory;

        public IReadOnlyList<string> Cultures { get; } = cultures;

        public FontCoverage Fonts { get; } = fonts;

        public List<Finding> Findings { get; } = [];

        public string LocalePath(string file)
        {
            return Path.GetRelativePath(root, Path.Combine(LocaleDirectory, file)).Replace('\\', '/');
        }

        public string CulturePath(string culture, string file)
        {
            return LocalePath(Path.Combine(culture, file));
        }

        public void Add(string rule, string path, int line, int column, string message, string? culture)
        {
            Findings.Add(new Finding(rule, Severities[rule], path, line, column, message, culture));
        }
    }
}