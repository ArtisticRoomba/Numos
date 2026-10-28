using Numos.CoreSim;
using Numos.CoreSim.State;
using Numos.CoreSim.State.Advanced;

namespace Numos.Serialization.FileSystem;

/// <summary>
///     Loads and atomically saves single serializable values, such as solver configuration presets, using
///     filesystem paths.
/// </summary>
/// <remarks>The file format is <see cref="NumosStateSerializer" />'s.</remarks>
/// <example>
///     <code>
///     NumosStateFile.Save("presets/fire.numosstate", fireSettings);
///     FireSettings loaded = NumosStateFile.Load&lt;FireSettings&gt;("presets/fire.numosstate", world.Config.GasRegistry);
///     </code>
/// </example>
public static class NumosStateFile
{
    /// <summary>
    ///     Loads a value from a path.
    /// </summary>
    /// <param name="path">Path to the state file.</param>
    /// <param name="gases">Resolves gas references by name. Needed only when the value references gases.</param>
    /// <param name="options">Optional limits for untrusted files.</param>
    /// <typeparam name="T">A serializable type with a constant <c>Key</c>, such as a solver configuration.</typeparam>
    /// <returns>The value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException"><paramref name="path" /> is empty or whitespace.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="InvalidOperationException">
    ///     <typeparamref name="T" /> declares no key, or the value references a gas and <paramref name="gases" /> is
    ///     <see langword="null" />.
    /// </exception>
    /// <exception cref="InvalidDataException">
    ///     The file is malformed, holds a different type's key, or references a gas <paramref name="gases" /> lacks.
    /// </exception>
    public static T Load<T>(string path, IGasRegistry? gases = null, NumosReplayReadOptions? options = null)
        where T : INumosState<T>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        T value = NumosStateSerializer.Read<T>(stream, gases, options);
        if (stream.ReadByte() != -1) throw new InvalidDataException("The Numos state file has trailing data.");

        return value;
    }

    /// <summary>
    ///     Saves a value through a temporary file in the destination directory.
    /// </summary>
    /// <param name="path">Destination path.</param>
    /// <param name="value">The value to save.</param>
    /// <param name="overwrite">Whether an existing destination may be replaced.</param>
    /// <typeparam name="T">A serializable type with a constant <c>Key</c>, such as a solver configuration.</typeparam>
    /// <exception cref="ArgumentException"><paramref name="path" /> is empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException">
    ///     <paramref name="path" /> or <paramref name="value" /> is <see langword="null" />.
    /// </exception>
    /// <exception cref="DirectoryNotFoundException">The destination directory does not exist.</exception>
    /// <exception cref="IOException">The destination exists without overwrite permission or cannot be replaced.</exception>
    /// <exception cref="InvalidOperationException"><typeparamref name="T" /> declares no key.</exception>
    public static void Save<T>(string path, T value, bool overwrite = false) where T : INumosState<T>
    {
        ArgumentNullException.ThrowIfNull(value);
        AtomicFile.Write(path, overwrite, stream => NumosStateSerializer.Write(stream, value));
    }
}
