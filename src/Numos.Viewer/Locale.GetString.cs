using Linguini.Bundle;

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

        bundle.TryGetAttrMessage(key, EmptyCache, out _, out var result);
        return result ?? throw new ArgumentException(
                   $"Locale key '{key}' returned a null value.",
                   nameof(key));
    }
    
    public static string GetString<T1>(
        string key,
        (string, T1?) arg1)
    {
        var bundle = GetState().Bundle;
        
        var argumentCache = GetArgumentCache();
        argumentCache.Add(arg1.Item1, FluentFromObject(arg1.Item2));
        
        bundle.TryGetAttrMessage(key, argumentCache, out _, out var result);
        return result ?? throw new ArgumentException(
            $"Locale key '{key}' returned a null value.",
            nameof(key));
    }
    
    public static string GetString<T1, T2>(
        string key,
        (string, T1?) arg1,
        (string, T2?) arg2)
    {
        var bundle = GetState().Bundle;
        
        var argumentCache = GetArgumentCache();
        argumentCache.Add(arg1.Item1, FluentFromObject(arg1.Item2));
        argumentCache.Add(arg2.Item1, FluentFromObject(arg2.Item2));
        
        bundle.TryGetAttrMessage(key, argumentCache, out _, out var result);
        return result ?? throw new ArgumentException(
            $"Locale key '{key}' returned a null value.",
            nameof(key));
    }
    
    public static string GetString<T1, T2, T3>(
        string key,
        (string, T1?) arg1,
        (string, T2?) arg2,
        (string, T3?) arg3)
    {
        var bundle = GetState().Bundle;
        
        var argumentCache = GetArgumentCache();
        argumentCache.Add(arg1.Item1, FluentFromObject(arg1.Item2));
        argumentCache.Add(arg2.Item1, FluentFromObject(arg2.Item2));
        argumentCache.Add(arg3.Item1, FluentFromObject(arg3.Item2));
        
        bundle.TryGetAttrMessage(key, argumentCache, out _, out var result);
        return result ?? throw new ArgumentException(
            $"Locale key '{key}' returned a null value.",
            nameof(key));
    }
    
    public static string GetString(
        string key,
        params (string, object?)[] args)
    {
        var bundle = GetState().Bundle;
        var argumentCache = GetArgumentCache();
        foreach (var (name, obj) in args)
        {
            argumentCache.Add(name, FluentFromObject(obj));
        }
        bundle.TryGetAttrMessage(key, argumentCache, out _, out var result);
        return result ?? throw new ArgumentException(
            $"Locale key '{key}' returned a null value.",
            nameof(key));
    }
}
