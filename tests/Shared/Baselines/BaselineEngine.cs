using System.Globalization;
using System.Text;

namespace Numos.Tests.Baselines;

/// <summary>
///     Everything the comparison needs to know about the test and its environment. <see cref="Baseline" /> builds one
///     from the running NUnit test; the mechanism's own tests build one over a temporary directory.
/// </summary>
internal sealed class BaselineContext
{
    /// <summary>
    ///     The full path of the <c>.json</c> baseline file.
    /// </summary>
    public required string BaselinePath { get; init; }

    /// <summary>
    ///     The NUnit full name, matched against update filters.
    /// </summary>
    public required string TestFullName { get; init; }

    /// <summary>
    ///     The <c>Fixture.Method</c> text used in the <c>dotnet test --filter</c> part of the regenerate command.
    /// </summary>
    public required string TestFilter { get; init; }

    /// <summary>
    ///     The test project path used in the regenerate command, relative to the repository root.
    /// </summary>
    public required string ProjectPath { get; init; }

    /// <summary>
    ///     The lowercase architecture name of the running process, used as the slot key for per-architecture values.
    /// </summary>
    public required string Architecture { get; init; }

    public required Func<string, string?> GetEnvironmentVariable { get; init; }

    public string ReceivedPath => Path.ChangeExtension(BaselinePath, ".received.json");
}

internal enum BaselineOutcome
{
    Passed,
    Warning,
    Updated,
    Failed
}

internal readonly record struct BaselineVerdict(BaselineOutcome Outcome, string Message);

/// <summary>
///     Compares or rewrites a baseline file and reports a verdict, without touching NUnit's assertion state.
/// </summary>
/// <remarks>
///     <para>
///         Compare mode never writes the baseline. When something is off it writes <c>&lt;Test&gt;.received.json</c>
///         beside it, holding the full file as update mode would write it, so accepting a change is a copy or a
///         regenerate run and the diff is reviewable either way.
///     </para>
///     <para>
///         A missing file, entry, or architecture slot is a warning rather than a failure. That is what lets an arm64 CI
///         leg run a digest test that only has an x64 baseline: the test's own self-consistency checks still run, and
///         the received file it uploads becomes the arm64 baseline.
///     </para>
/// </remarks>
internal static class BaselineEngine
{
    internal const string UpdateVariable = "NUMOS_UPDATE_BASELINES";

    // Arrays can be long, and the first few bad elements are enough to see what moved.
    private const int MaxRowsPerKey = 24;

    internal static BaselineVerdict Verify(BaselineValues values, BaselineContext context, string? guard = null)
    {
        string? update = context.GetEnvironmentVariable(UpdateVariable)?.Trim();
        bool updateRequested = !string.IsNullOrEmpty(update)
                               && update != "0"
                               && !update.Equals("false", StringComparison.OrdinalIgnoreCase);

        if (updateRequested && IsContinuousIntegration(context))
        {
            return new BaselineVerdict(
                BaselineOutcome.Failed,
                $"{UpdateVariable} is set while CI is set. Baselines are regenerated locally and committed; CI only " +
                "compares against them.");
        }

        bool updating = updateRequested && Selects(update!, context, guard);
        string regenerate = RegenerateCommand(context, guard);

        BaselineDocument? existing = null;
        string? existingText = null;
        if (File.Exists(context.BaselinePath))
        {
            existingText = File.ReadAllText(context.BaselinePath);
            try
            {
                existing = BaselineDocument.Parse(existingText);
            }
            catch (FormatException exception)
            {
                return new BaselineVerdict(
                    BaselineOutcome.Failed,
                    $"Could not read baseline {context.BaselinePath}: {exception.Message}");
            }
        }

        string updatedText = BuildUpdated(existing, values, context.Architecture, regenerate).Write();

        if (updating)
        {
            DeleteReceived(context);
            if (existingText == updatedText)
                return new BaselineVerdict(BaselineOutcome.Updated, $"baseline unchanged: {context.BaselinePath}");

            Directory.CreateDirectory(Path.GetDirectoryName(context.BaselinePath)!);
            File.WriteAllText(context.BaselinePath, updatedText);
            return new BaselineVerdict(BaselineOutcome.Updated, $"baseline updated: {context.BaselinePath}");
        }

        if (existing is null)
        {
            WriteReceived(context, updatedText);
            return new BaselineVerdict(
                BaselineOutcome.Warning,
                $"No baseline at {context.BaselinePath}. Wrote {context.ReceivedPath}; check the values, then run " +
                $"this to accept them:\n  {regenerate}");
        }

        var missing = new List<string>();
        var rows = Compare(existing, values, context.Architecture, missing);

        if (rows.Count > 0)
        {
            WriteReceived(context, updatedText);
            var message = new StringBuilder()
                .Append($"Baseline mismatch ({context.Architecture}) in {context.BaselinePath}\n\n")
                .Append(FormatTable(rows))
                .Append($"\nWrote {context.ReceivedPath}. If the change is intended, regenerate with:\n  {regenerate}");
            return new BaselineVerdict(BaselineOutcome.Failed, message.ToString());
        }

        if (missing.Count > 0)
        {
            WriteReceived(context, updatedText);
            return new BaselineVerdict(
                BaselineOutcome.Warning,
                $"Baseline {context.BaselinePath} has no {context.Architecture} value for {string.Join(", ", missing)}. " +
                $"Wrote {context.ReceivedPath}; check the values, then run this to accept them:\n  {regenerate}");
        }

        DeleteReceived(context);
        return new BaselineVerdict(BaselineOutcome.Passed, "");
    }

    internal static string RegenerateCommand(BaselineContext context, string? guard)
    {
        return $"{UpdateVariable}={guard ?? "1"} dotnet test {context.ProjectPath} " +
               $"--filter \"FullyQualifiedName~{context.TestFilter}\"";
    }

    /// <summary>
    ///     Builds the file update mode would write: current values go into the current architecture's slot for
    ///     digests, and for floats and text whenever the entry already carries per-architecture overrides; otherwise
    ///     they replace <see cref="BaselineDocument.DefaultSlot" />. Other architectures' slots are kept, and entries
    ///     the test no longer produces are dropped.
    /// </summary>
    internal static BaselineDocument BuildUpdated(
        BaselineDocument? existing,
        BaselineValues values,
        string architecture,
        string regenerate)
    {
        var document = new BaselineDocument(regenerate);
        foreach (var entry in values.Entries)
        {
            var slots = new OrderedDictionary<string, BaselineValue>(StringComparer.Ordinal);
            if (existing is not null
                && existing.Entries.TryGetValue(entry.Key, out var previous)
                && previous.Kind == entry.Kind)
            {
                foreach ((string slot, var value) in previous.Slots)
                    slots[slot] = value;
            }

            if (entry.Kind == BaselineEntryKind.Digest)
            {
                slots.Remove(BaselineDocument.DefaultSlot);
                slots[architecture] = entry.Value;
            }
            else if (slots.Keys.Any(slot => slot != BaselineDocument.DefaultSlot))
            {
                slots[architecture] = entry.Value;
            }
            else
            {
                slots[BaselineDocument.DefaultSlot] = entry.Value;
            }

            string? tolerance = entry.Kind is BaselineEntryKind.Float or BaselineEntryKind.Floats
                ? entry.Tolerance.ToString()
                : null;
            document.Entries[entry.Key] = new BaselineStoredEntry(entry.Kind, tolerance, SortSlots(slots));
        }

        return document;
    }

    private static OrderedDictionary<string, BaselineValue> SortSlots(OrderedDictionary<string, BaselineValue> slots)
    {
        var sorted = new OrderedDictionary<string, BaselineValue>(StringComparer.Ordinal);
        if (slots.TryGetValue(BaselineDocument.DefaultSlot, out var defaultValue))
            sorted[BaselineDocument.DefaultSlot] = defaultValue;

        foreach (string slot in slots.Keys.Where(slot => slot != BaselineDocument.DefaultSlot).Order(StringComparer.Ordinal))
            sorted[slot] = slots[slot];

        return sorted;
    }

    private static bool IsContinuousIntegration(BaselineContext context)
    {
        string? ci = context.GetEnvironmentVariable("CI")?.Trim();
        return ci == "1" || string.Equals(ci, "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Decides whether this test's baseline is selected by the update variable. The value is a comma list: <c>1</c>,
    ///     <c>true</c>, or <c>all</c> selects every unguarded baseline, and any other item selects unguarded baselines
    ///     whose test full name contains it. A guarded baseline is selected only by an item equal to its guard.
    /// </summary>
    private static bool Selects(string update, BaselineContext context, string? guard)
    {
        string[] items = update.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (guard is not null)
            return items.Any(item => item.Equals(guard, StringComparison.OrdinalIgnoreCase));

        return items.Any(item =>
            item == "1"
            || item.Equals("true", StringComparison.OrdinalIgnoreCase)
            || item.Equals("all", StringComparison.OrdinalIgnoreCase)
            || context.TestFullName.Contains(item, StringComparison.Ordinal));
    }

    private static List<Row> Compare(
        BaselineDocument existing,
        BaselineValues values,
        string architecture,
        List<string> missing)
    {
        var rows = new List<Row>();
        var produced = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in values.Entries)
        {
            produced.Add(entry.Key);
            if (!existing.Entries.TryGetValue(entry.Key, out var stored))
            {
                missing.Add(entry.Key);
                continue;
            }

            if (stored.Kind != entry.Kind)
            {
                rows.Add(new Row(
                    entry.Key,
                    BaselineDocument.KindName(stored.Kind),
                    BaselineDocument.KindName(entry.Kind),
                    "",
                    "",
                    "kind changed"));
                continue;
            }

            if (!stored.Slots.TryGetValue(architecture, out var expected)
                && (entry.Kind == BaselineEntryKind.Digest
                    || !stored.Slots.TryGetValue(BaselineDocument.DefaultSlot, out expected)))
            {
                missing.Add(entry.Key);
                continue;
            }

            switch (entry.Kind)
            {
                case BaselineEntryKind.Float:
                case BaselineEntryKind.Floats:
                    CompareFloats(entry, expected.Floats, rows);
                    break;
                case BaselineEntryKind.Digest:
                    if (expected.Digest != entry.Value.Digest)
                    {
                        rows.Add(new Row(
                            entry.Key,
                            BaselineDocument.FormatDigest(expected.Digest),
                            BaselineDocument.FormatDigest(entry.Value.Digest),
                            "",
                            "",
                            "exact"));
                    }

                    break;
                default:
                    if (!string.Equals(expected.Text, entry.Value.Text, StringComparison.Ordinal))
                        rows.Add(new Row(entry.Key, Quote(expected.Text), Quote(entry.Value.Text), "", "", "exact"));
                    break;
            }
        }

        foreach (string key in existing.Entries.Keys.Where(key => !produced.Contains(key)))
            rows.Add(new Row(key, "(in file)", "(not produced)", "", "", "stale entry"));

        return rows;
    }

    private static void CompareFloats(BaselineEntry entry, float[] expected, List<Row> rows)
    {
        float[] actual = entry.Value.Floats;
        string tolerance = entry.Tolerance.ToString();

        if (expected.Length != actual.Length)
        {
            rows.Add(new Row(
                entry.Key,
                $"length {expected.Length}",
                $"length {actual.Length}",
                "",
                "",
                tolerance));
            return;
        }

        int failures = 0;
        for (int index = 0; index < expected.Length; index++)
        {
            if (entry.Tolerance.Accepts(expected[index], actual[index]))
                continue;

            failures++;
            if (failures > MaxRowsPerKey)
                continue;

            double delta = Math.Abs((double)expected[index] - actual[index]);
            long? ulps = BaselineTolerance.UlpDistance(expected[index], actual[index]);
            rows.Add(new Row(
                entry.Kind == BaselineEntryKind.Floats ? $"{entry.Key}[{index}]" : entry.Key,
                BaselineDocument.FormatFloat(expected[index]),
                BaselineDocument.FormatFloat(actual[index]),
                delta.ToString("G6", CultureInfo.InvariantCulture),
                ulps?.ToString(CultureInfo.InvariantCulture) ?? "n/a",
                tolerance));
        }

        if (failures > MaxRowsPerKey)
            rows.Add(new Row($"{entry.Key}[...]", $"{failures - MaxRowsPerKey} more", "", "", "", tolerance));
    }

    private static string Quote(string text)
    {
        const int limit = 60;
        return text.Length <= limit ? $"\"{text}\"" : $"\"{text[..limit]}...\" ({text.Length} chars)";
    }

    private static string FormatTable(List<Row> rows)
    {
        string[] header = ["key", "expected", "actual", "|delta|", "ulps", "tolerance"];
        var cells = new List<string[]> { header };
        cells.AddRange(rows.Select(row =>
            new[] { row.Key, row.Expected, row.Actual, row.Delta, row.Ulps, row.Tolerance }));

        int[] widths = Enumerable.Range(0, header.Length).Select(column => cells.Max(line => line[column].Length))
            .ToArray();

        var table = new StringBuilder();
        foreach (string[] line in cells)
        {
            for (int column = 0; column < line.Length; column++)
            {
                table.Append(column == line.Length - 1 ? line[column] : line[column].PadRight(widths[column] + 2));
            }

            table.Append('\n');
        }

        return table.ToString();
    }

    private static void WriteReceived(BaselineContext context, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(context.ReceivedPath)!);
        File.WriteAllText(context.ReceivedPath, text);
    }

    private static void DeleteReceived(BaselineContext context)
    {
        if (File.Exists(context.ReceivedPath))
            File.Delete(context.ReceivedPath);
    }

    private sealed record Row(string Key, string Expected, string Actual, string Delta, string Ulps, string Tolerance);
}
