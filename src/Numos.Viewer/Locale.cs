using System.Globalization;
using Linguini.Bundle;
using Linguini.Bundle.Builder;
using Linguini.Shared.Types.Bundle;

namespace Numos.Viewer;

public static class Locale
{
    private static FluentBundle _bundle = default!;

    public static void LoadCulture(string culture = "en")
    {
        string ftlContent = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "locale",
            $"{culture}.ftl"));
        
        _bundle = LinguiniBuilder.Builder()
            .CultureInfo(new CultureInfo(culture))
            .AddResource(ftlContent)
            .UncheckedBuild();
    }

    public static string GetString(string key)
    {
        return _bundle.GetAttrMessage(key) ?? throw new ArgumentException($"Locale key '{key}' returned a null value.");
    }

    public static string GetString(string key, params (string, object?)[] args)
    {
        var cast = new (string, IFluentType)[args.Length];
        for (int i = 0; i < args.Length; i++)
        {
            cast[i] = new ValueTuple<string, IFluentType>(args[i].Item1, args[i].Item2.FluentFromObject());
        }
        
        return _bundle.GetAttrMessage(key, cast) ?? throw new ArgumentException($"Locale key '{key}' returned a null value.");
    }

    private static IFluentType FluentFromObject(this object? obj)
    {
        return obj switch
        {
            bool or Enum => (FluentString)obj.ToString()!.ToLowerInvariant(),
            string str => (FluentString)str,
            byte num => (FluentNumber)num,
            sbyte num => (FluentNumber)num,
            short num => (FluentNumber)num,
            ushort num => (FluentNumber)num,
            int num => (FluentNumber)num,
            uint num => (FluentNumber)num,
            long num => (FluentNumber)num,
            ulong num => (FluentNumber)num,
            double dbl => (FluentNumber)dbl,
            float dbl => (FluentNumber)dbl,
            null => (FluentString)string.Empty,
            _ => (FluentString)obj.ToString()!,
        };
    }
}
