using System.Collections.Concurrent;
using System.Globalization;
using Linguini.Bundle;
using Linguini.Bundle.Builder;
using Linguini.Shared.Types.Bundle;

namespace Numos.Viewer;

public static partial class Locale
{
    public static readonly string LocaleDirectory =
        Path.Combine(AppContext.BaseDirectory, "locale");

    private static readonly ConcurrentDictionary<string, Lazy<LocaleState>>
        BundleCache = new(StringComparer.OrdinalIgnoreCase);

    private static LocaleState? _current;

    private sealed record LocaleState(
        CultureInfo Culture,
        FluentBundle Bundle);

    public static CultureInfo CurrentCulture => GetState().Culture;

    /// <summary>
    /// Switches the active localization culture.
    /// </summary>
    public static void SetCulture(string culture = "en")
    {
        if (string.IsNullOrWhiteSpace(culture))
        {
            throw new ArgumentException(
                "A culture must be specified.", nameof(culture));
        }

        var cultureInfo = CultureInfo.GetCultureInfo(culture);

        var lazyState = BundleCache.GetOrAdd(
            cultureInfo.Name,
            _ => new Lazy<LocaleState>(
                () => BuildState(cultureInfo),
                LazyThreadSafetyMode.ExecutionAndPublication));
        
        var state = lazyState.Value;
        Volatile.Write(ref _current, state);
    }

    private static LocaleState BuildState(CultureInfo culture)
    {
        string directory = Path.Combine(
            LocaleDirectory, culture.Name);

        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(
                $"No localization directory exists for culture " +
                $"'{culture.Name}': '{directory}'.");
        }

        var files = Directory.GetFiles(
            directory, "*.ftl", SearchOption.TopDirectoryOnly);

        if (files.Length == 0)
        {
            throw new FileNotFoundException(
                $"No .ftl files were found for culture " +
                $"'{culture.Name}' in '{directory}'.");
        }
        
        Array.Sort(files, StringComparer.Ordinal);

        var bundle = LinguiniBuilder.Builder()
            .CultureInfo(culture)
            .AddFiles(files)
            .UseConcurrent()
            .Build();
        
        // TODO error handling

        return new LocaleState(culture, bundle.Item1);
    }

    private static LocaleState GetState() =>
        Volatile.Read(ref _current)
        ?? throw new InvalidOperationException(
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
}
