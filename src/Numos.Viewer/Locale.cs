using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Globalization;
using Linguini.Bundle;
using Linguini.Bundle.Builder;
using Linguini.Bundle.Errors;
using Linguini.Shared.Types.Bundle;
using Linguini.Syntax.Parser;

namespace Numos.Viewer;

public static partial class Locale
{
    public static readonly string LocaleDirectory =
        Path.Combine(AppContext.BaseDirectory, "locale");

    private static readonly ConcurrentDictionary<string, Lazy<LocaleState>>
        BundleCache = new(StringComparer.OrdinalIgnoreCase);
    
    private static readonly FrozenDictionary<string, IFluentType> EmptyCache =
        FrozenDictionary.ToFrozenDictionary<string, IFluentType>([]);
    
    private static readonly Dictionary<string, IFluentType> ArgumentCache = new();

    private static LocaleState? _current;

    private readonly static Lazy<IReadOnlyList<string>> AvailableCulturesLazy =
        new(FindAvailableCultures, LazyThreadSafetyMode.ExecutionAndPublication);

    public static CultureInfo CurrentCulture => GetState().Culture;

    /// <summary>
    ///     Gets the names of the cultures that have a directory with at least one <c>.ftl</c> file under
    ///     <see cref="LocaleDirectory" />.
    /// </summary>
    /// <remarks>
    ///     The list is sorted by ordinal comparison and computed once. It is empty when the locale directory is missing.
    /// </remarks>
    public static IReadOnlyList<string> AvailableCultures => AvailableCulturesLazy.Value;

    /// <summary>
    /// Switches the active localization culture.
    /// </summary>
    public static void SetCulture(string culture = "en")
    {
        if (string.IsNullOrWhiteSpace(culture))
        {
            throw new ArgumentException("A culture must be specified.", nameof(culture));
        }

        var cultureInfo = CultureInfo.GetCultureInfo(culture);

        Volatile.Write(ref _current, GetOrLoadState(cultureInfo));
    }

    private static LocaleState GetOrLoadState(CultureInfo cultureInfo)
    {
        var lazyState = BundleCache.GetOrAdd(
            cultureInfo.Name,
            _ => new Lazy<LocaleState>(
                () => BuildState(cultureInfo),
                LazyThreadSafetyMode.ExecutionAndPublication));

        return lazyState.Value;
    }

    private static IReadOnlyList<string> FindAvailableCultures()
    {
        if (!Directory.Exists(LocaleDirectory))
            return [];

        var cultures = new List<string>();
        foreach (string directory in Directory.GetDirectories(LocaleDirectory))
        {
            if (Directory.EnumerateFiles(directory, "*.ftl", SearchOption.TopDirectoryOnly).Any())
                cultures.Add(Path.GetFileName(directory));
        }

        cultures.Sort(StringComparer.Ordinal);
        return cultures;
    }

    /// <summary>
    ///     Gets the name of a culture in its own language, taken from its <c>locale-name</c> message.
    /// </summary>
    /// <param name="culture">The culture directory name, such as <c>ru</c>.</param>
    /// <returns>The <c>locale-name</c> text, or <paramref name="culture" /> when the message is missing.</returns>
    /// <remarks>
    ///     This loads the culture bundle into the cache. It does not change <see cref="CurrentCulture" />.
    /// </remarks>
    public static string GetCultureDisplayName(string culture)
    {
        var state = GetOrLoadState(CultureInfo.GetCultureInfo(culture));
        string? name = state.Bundle.GetAttrMessage("locale-name");
        return string.IsNullOrEmpty(name) ? culture : name;
    }

    private static LocaleState BuildState(CultureInfo culture)
    {
        string directory = Path.Combine(LocaleDirectory, culture.Name);

        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(
                $"No localization directory exists for culture " +
                $"'{culture.Name}': '{directory}'.");
        }

        string[] files = Directory.GetFiles(directory, "*.ftl", SearchOption.TopDirectoryOnly);

        if (files.Length == 0)
        {
            throw new FileNotFoundException(
                $"No .ftl files were found for culture " +
                $"'{culture.Name}' in '{directory}'.");
        }

        Array.Sort(files, StringComparer.Ordinal);

        var parseErrors = new List<string>();
        foreach (string file in files)
        {
            foreach (var error in LinguiniParser.FromFile(file).Parse().Errors)
            {
                parseErrors.Add($"{Path.GetFileName(file)}:{error.Row}: {error.Message}");
            }
        }

        if (parseErrors.Count > 0)
        {
            throw new InvalidOperationException(
                $"Localization files for culture '{culture.Name}' " +
                $"in '{directory}' contain {parseErrors.Count} error(s):" +
                Environment.NewLine +
                string.Join(Environment.NewLine, parseErrors.Select(e => $"  {e}")));
        }

        (var bundle, List<FluentError>? errors) = LinguiniBuilder.Builder()
            .CultureInfo(culture)
            .AddFiles(files)
            .UseConcurrent()
            .Build();

        if (errors is { Count: > 0 })
        {
            throw new InvalidOperationException(
                $"Localization files for culture '{culture.Name}' " +
                $"in '{directory}' contain {errors.Count} error(s):" +
                Environment.NewLine +
                string.Join(
                    Environment.NewLine,
                    errors.Select(e => e.GetSpan() is { } span
                        ? $"  row {span.Row}: {e}"
                        : $"  {e}")));
        }

        return new LocaleState(culture, bundle);
    }

    private static LocaleState GetState() =>
        Volatile.Read(ref _current) ??
        throw new InvalidOperationException(
            "No localization culture has been set. " +
            "Call Locale.SetCulture() first.");

    private static IFluentType FluentFromObject<T>(T? obj)
    {
        return obj switch
        {
            bool b => (FluentString)(b ? "true" : "false"),
            Enum e => (FluentString)e.ToString().ToLowerInvariant(),
            string str => (FluentString)str,

            byte n => (FluentNumber)n,
            sbyte n => (FluentNumber)n,
            short n => (FluentNumber)n,
            ushort n => (FluentNumber)n,
            int n => (FluentNumber)n,
            uint n => (FluentNumber)n,
            long n => (FluentNumber)n,
            ulong n => (FluentNumber)n,
            double n => (FluentNumber)n,
            float n => (FluentNumber)n,

            null => (FluentString)string.Empty,
            _ => (FluentString)obj.ToString()!
        };
    }

    private sealed record LocaleState(
        CultureInfo Culture,
        FluentBundle Bundle);
}