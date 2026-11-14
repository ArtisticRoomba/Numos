using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Numos.Tests.Baselines;

/// <summary>
///     A stored entry: its kind, the tolerance it was last written with, and one value per slot. A slot is either
///     <see cref="BaselineDocument.DefaultSlot" /> or an architecture name such as <c>x64</c>.
/// </summary>
internal sealed record BaselineStoredEntry(
    BaselineEntryKind Kind,
    string? Tolerance,
    OrderedDictionary<string, BaselineValue> Slots);

/// <summary>
///     The JSON form of one baseline file.
/// </summary>
/// <remarks>
///     The writer is hand-rolled over <see cref="Utf8JsonWriter" /> so key order, float formatting, and line endings
///     stay fixed regardless of the machine that regenerates the file. Floats use the shortest round-trip form, and
///     non-finite floats are written as the strings <c>NaN</c>, <c>Infinity</c>, and <c>-Infinity</c>.
/// </remarks>
internal sealed class BaselineDocument
{
    internal const string DefaultSlot = "default";

    private const string RegenerateProperty = "regenerate";
    private const string EntriesProperty = "entries";
    private const string KindProperty = "kind";
    private const string ToleranceProperty = "tolerance";

    internal BaselineDocument(string? regenerate)
    {
        Regenerate = regenerate;
    }

    internal string? Regenerate { get; }

    internal OrderedDictionary<string, BaselineStoredEntry> Entries { get; } = new(StringComparer.Ordinal);

    /// <exception cref="FormatException">The text is not a baseline document.</exception>
    internal static BaselineDocument Parse(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new FormatException(exception.Message, exception);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new FormatException("The root is not an object.");

            string? regenerate = root.TryGetProperty(RegenerateProperty, out var regenerateElement)
                ? regenerateElement.GetString()
                : null;
            var result = new BaselineDocument(regenerate);

            if (!root.TryGetProperty(EntriesProperty, out var entries))
                return result;

            if (entries.ValueKind != JsonValueKind.Object)
                throw new FormatException($"'{EntriesProperty}' is not an object.");

            foreach (var property in entries.EnumerateObject())
                result.Entries[property.Name] = ParseEntry(property.Name, property.Value);

            return result;
        }
    }

    internal string Write()
    {
        var buffer = new MemoryStream();
        var options = new JsonWriterOptions
        {
            Indented = true,
            NewLine = "\n",
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        using (var writer = new Utf8JsonWriter(buffer, options))
        {
            writer.WriteStartObject();
            if (Regenerate is not null)
                writer.WriteString(RegenerateProperty, Regenerate);

            writer.WriteStartObject(EntriesProperty);
            foreach ((string key, var entry) in Entries)
            {
                writer.WriteStartObject(key);
                writer.WriteString(KindProperty, KindName(entry.Kind));
                if (entry.Tolerance is not null)
                    writer.WriteString(ToleranceProperty, entry.Tolerance);

                foreach ((string slot, var value) in entry.Slots)
                {
                    writer.WritePropertyName(slot);
                    WriteValue(writer, entry.Kind, value);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }

    internal static string KindName(BaselineEntryKind kind)
    {
        return kind switch
        {
            BaselineEntryKind.Float => "float",
            BaselineEntryKind.Floats => "floats",
            BaselineEntryKind.Digest => "digest",
            _ => "text"
        };
    }

    internal static string FormatFloat(float value)
    {
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    internal static string FormatDigest(ulong digest)
    {
        return "0x" + digest.ToString("X16", CultureInfo.InvariantCulture);
    }

    private static BaselineStoredEntry ParseEntry(string key, JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new FormatException($"Entry '{key}' is not an object.");

        if (!element.TryGetProperty(KindProperty, out var kindElement))
            throw new FormatException($"Entry '{key}' has no '{KindProperty}'.");

        var kind = kindElement.GetString() switch
        {
            "float" => BaselineEntryKind.Float,
            "floats" => BaselineEntryKind.Floats,
            "digest" => BaselineEntryKind.Digest,
            "text" => BaselineEntryKind.Text,
            var other => throw new FormatException($"Entry '{key}' has unknown kind '{other}'.")
        };

        string? tolerance = null;
        var slots = new OrderedDictionary<string, BaselineValue>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            switch (property.Name)
            {
                case KindProperty:
                    break;
                case ToleranceProperty:
                    tolerance = property.Value.GetString();
                    break;
                default:
                    slots[property.Name] = ParseValue(key, kind, property.Value);
                    break;
            }
        }

        return new BaselineStoredEntry(kind, tolerance, slots);
    }

    private static BaselineValue ParseValue(string key, BaselineEntryKind kind, JsonElement element)
    {
        switch (kind)
        {
            case BaselineEntryKind.Float:
                return BaselineValue.OfFloats([ParseFloat(key, element)]);
            case BaselineEntryKind.Floats:
                if (element.ValueKind != JsonValueKind.Array)
                    throw new FormatException($"Entry '{key}' should hold an array.");

                return BaselineValue.OfFloats(element.EnumerateArray().Select(item => ParseFloat(key, item)).ToArray());
            case BaselineEntryKind.Digest:
                string? hex = element.ValueKind == JsonValueKind.String ? element.GetString() : null;
                if (hex is null
                    || !hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    || !ulong.TryParse(hex.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture,
                        out ulong digest))
                    throw new FormatException($"Entry '{key}' should hold a 0x-prefixed hex digest.");

                return BaselineValue.OfDigest(digest);
            default:
                if (element.ValueKind != JsonValueKind.String)
                    throw new FormatException($"Entry '{key}' should hold a string.");

                return BaselineValue.OfText(element.GetString()!);
        }
    }

    private static float ParseFloat(string key, JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetSingle(out float value))
            return value;

        if (element.ValueKind == JsonValueKind.String)
        {
            switch (element.GetString())
            {
                case "NaN":
                    return float.NaN;
                case "Infinity":
                    return float.PositiveInfinity;
                case "-Infinity":
                    return float.NegativeInfinity;
            }
        }

        throw new FormatException($"Entry '{key}' holds a value that is not a float: {element.GetRawText()}");
    }

    private static void WriteValue(Utf8JsonWriter writer, BaselineEntryKind kind, BaselineValue value)
    {
        switch (kind)
        {
            case BaselineEntryKind.Float:
                WriteFloat(writer, value.Floats[0]);
                break;
            case BaselineEntryKind.Floats:
                writer.WriteStartArray();
                foreach (float item in value.Floats)
                    WriteFloat(writer, item);
                writer.WriteEndArray();
                break;
            case BaselineEntryKind.Digest:
                writer.WriteStringValue(FormatDigest(value.Digest));
                break;
            default:
                writer.WriteStringValue(value.Text);
                break;
        }
    }

    private static void WriteFloat(Utf8JsonWriter writer, float value)
    {
        if (float.IsFinite(value))
            writer.WriteNumberValue(value);
        else
            writer.WriteStringValue(FormatFloat(value));
    }
}
