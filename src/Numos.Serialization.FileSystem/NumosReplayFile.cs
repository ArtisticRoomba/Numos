namespace Numos.Serialization.FileSystem;

/// <summary>
///     Loads and atomically saves Numos replay documents using filesystem paths.
/// </summary>
public static class NumosReplayFile
{
    /// <summary>
    ///     Loads a world replay from a path.
    /// </summary>
    /// <param name="path">Path to the Numos replay container.</param>
    /// <param name="options">Optional allocation and payload limits for untrusted files.</param>
    /// <returns>The decoded metadata and detached world replay archive.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException"><paramref name="path" /> is empty or whitespace.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="InvalidDataException">
    ///     The file is malformed, internally inconsistent, or holds unsupported content such as a single-simulation
    ///     replay written by an earlier Numos version.
    /// </exception>
    /// <exception cref="NotSupportedException">
    ///     The replay contains host-defined state, such as a solver configuration whose key isn't registered in
    ///     <see cref="NumosReplayReadOptions.SolverConfigurations" />.
    /// </exception>
    public static NumosWorldReplayDocument Load(string path, NumosReplayReadOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return NumosWorldReplaySerializer.Deserialize(stream, options);
    }

    /// <summary>
    ///     Saves a world replay through a temporary file in the destination directory.
    /// </summary>
    /// <param name="path">Destination path.</param>
    /// <param name="document">World replay document to save.</param>
    /// <param name="overwrite">Whether an existing destination may be replaced.</param>
    /// <exception cref="ArgumentException"><paramref name="path" /> is empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException">
    ///     <paramref name="path" /> or <paramref name="document" /> is <see langword="null" />.
    /// </exception>
    /// <exception cref="DirectoryNotFoundException">The destination directory does not exist.</exception>
    /// <exception cref="IOException">The destination exists without overwrite permission or cannot be replaced.</exception>
    /// <exception cref="NotSupportedException">
    ///     The replay contains host-defined state that the portable format cannot represent.
    /// </exception>
    public static void Save(string path, NumosWorldReplayDocument document, bool overwrite = false)
    {
        ArgumentNullException.ThrowIfNull(document);
        AtomicFile.Write(path, overwrite, stream => NumosWorldReplaySerializer.Serialize(stream, document));
    }
}