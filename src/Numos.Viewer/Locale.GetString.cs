using Linguini.Bundle;
using Linguini.Shared.Types.Bundle;

namespace Numos.Viewer;

public static partial class Locale
{
    /// <summary>
    /// Gets a raw fluent string without any arguments.
    /// </summary>
    /// <param name="key">The key of the string to get.</param>
    /// <returns>Localized fluent string.</returns>
    /// <exception cref="ArgumentException">Locale key doesn't exist.</exception>
    public static string GetString(string key)
    {
        var bundle = GetState().Bundle;

        return bundle.GetAttrMessage(key)
               ?? throw new ArgumentException(
                   $"Locale key '{key}' returned a null value.",
                   nameof(key));
    }
    
    public static string GetString(
        string key,
        params (string, object?)[] args)
    {
        var bundle = GetState().Bundle;
        var array = new (string, IFluentType)[args.Length];
        for (int i = 0; i < args.Length; i++)
        {
            array[i] = (args[i].Item1, FluentFromObject(args[i].Item2));
        }
        return bundle.GetAttrMessage(key, array)
                  ?? throw new ArgumentException(
                      $"Locale key '{key}' returned a null value.",
                      nameof(key));
    }
}
