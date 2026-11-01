using Linguini.Bundle;

namespace Numos.Viewer;

internal static partial class Locale
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
    
    /// <summary>
    ///     Gets a localized string with one argument.
    /// </summary>
    /// <param name="key">The message key.</param>
    /// <typeparam name="T1">The type of the value of argument 1.</typeparam>
    /// <param name="arg1">The name and value of argument 1.</param>
    /// <returns>The localized string.</returns>
    /// <exception cref="ArgumentException">The message formats to a null value.</exception>
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
    
    /// <summary>
    ///     Gets a localized string with two arguments.
    /// </summary>
    /// <param name="key">The message key.</param>
    /// <typeparam name="T1">The type of the value of argument 1.</typeparam>
    /// <typeparam name="T2">The type of the value of argument 2.</typeparam>
    /// <param name="arg1">The name and value of argument 1.</param>
    /// <param name="arg2">The name and value of argument 2.</param>
    /// <returns>The localized string.</returns>
    /// <exception cref="ArgumentException">The message formats to a null value.</exception>
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
    
    /// <summary>
    ///     Gets a localized string with three arguments.
    /// </summary>
    /// <param name="key">The message key.</param>
    /// <typeparam name="T1">The type of the value of argument 1.</typeparam>
    /// <typeparam name="T2">The type of the value of argument 2.</typeparam>
    /// <typeparam name="T3">The type of the value of argument 3.</typeparam>
    /// <param name="arg1">The name and value of argument 1.</param>
    /// <param name="arg2">The name and value of argument 2.</param>
    /// <param name="arg3">The name and value of argument 3.</param>
    /// <returns>The localized string.</returns>
    /// <exception cref="ArgumentException">The message formats to a null value.</exception>
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
    
    /// <summary>
    ///     Gets a localized string with any number of named arguments.
    /// </summary>
    /// <param name="key">The message key.</param>
    /// <param name="args">The argument names and values.</param>
    /// <returns>The localized string.</returns>
    /// <remarks>
    ///     This overload allocates the <paramref name="args" /> array. Prefer the fixed-arity overloads for up to three
    ///     arguments.
    /// </remarks>
    /// <exception cref="ArgumentException">The message formats to a null value.</exception>
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
