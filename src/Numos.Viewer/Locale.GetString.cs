using System.Buffers;
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
    
    // TODO consider making a source gen for the code below

    /// <summary>
    /// Gets a raw fluent string without 1 argument.
    /// </summary>
    /// <param name="key">The key of the string to get.</param>
    /// <param name="arg1">First argument.</param>
    /// <returns>Localized fluent string.</returns>
    /// <exception cref="ArgumentException">Locale key doesn't exist.</exception>
    public static string GetString<T1>(
        string key,
        (string, T1?) arg1)
    {
        var bundle = GetState().Bundle;
        var array = ArrayPool<(string, IFluentType)>.Shared.Rent(1);
        string message;
        try
        {
            array[1] = (arg1.Item1, FluentFromObject(arg1));
            
            message = bundle.GetAttrMessage(key, array)
                      ?? throw new ArgumentException(
                          $"Locale key '{key}' returned a null value.",
                          nameof(key));
        }
        finally
        {
            array.AsSpan().Clear();
            ArrayPool<(string, IFluentType)>.Shared.Return(array);
        }
        return message;
    }
    
    public static string GetString(
        string key,
        params (string, object?)[] args)
    {
        var bundle = GetState().Bundle;
        var array = ArrayPool<(string, IFluentType)>.Shared.Rent(args.Length);
        string message;
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                array[i] = (args[i].Item1, FluentFromObject(args[i].Item2));
            }
            
            message = bundle.GetAttrMessage(key, array)
                      ?? throw new ArgumentException(
                          $"Locale key '{key}' returned a null value.",
                          nameof(key));
        }
        finally
        {
            array.AsSpan().Clear();
            ArrayPool<(string, IFluentType)>.Shared.Return(array);
        }
        return message;
    }
}
