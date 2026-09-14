namespace Numos.Serialization.FileSystem;

/// <summary>
///     Loads and atomically saves Numos replay documents using filesystem paths.
/// </summary>
public static class NumosReplayFile
{
    /// <summary>
    ///     Loads a replay from a path.
    /// </summary>
    /// <param name="path">Path to the Numos replay container.</param>
    /// <param name="options">Optional allocation and payload limits for untrusted files.</param>
    /// <returns>The decoded metadata and detached replay archive.</returns>
    /// <exception cref="ArgumentException"><paramref name="path" /> is empty.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="InvalidDataException">The file is malformed, unsupported, or internally inconsistent.</exception>
    public static NumosReplayDocument Load(string path, NumosReplayReadOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return NumosReplaySerializer.Deserialize(stream, options);
    }

    /// <summary>
    ///     Saves a replay through a temporary file in the destination directory.
    /// </summary>
    /// <param name="path">Destination path.</param>
    /// <param name="document">Replay document to save.</param>
    /// <param name="overwrite">Whether an existing destination may be replaced.</param>
    /// <exception cref="ArgumentException"><paramref name="path" /> is empty.</exception>
    /// <exception cref="DirectoryNotFoundException">The destination directory does not exist.</exception>
    /// <exception cref="IOException">
    ///     The destination exists without <paramref name="overwrite" />, or the file cannot be
    ///     replaced.
    /// </exception>
    /// <exception cref="NotSupportedException">
    ///     The replay contains host-defined state that the portable format cannot
    ///     represent.
    /// </exception>
    public static void Save(string path, NumosReplayDocument document, bool overwrite = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(document);
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (directory == null || !Directory.Exists(directory))
            throw new DirectoryNotFoundException($"Directory '{directory}' does not exist.");

        if (!overwrite && File.Exists(fullPath)) throw new IOException($"File '{fullPath}' already exists.");

        string temporary = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                NumosReplaySerializer.Serialize(stream, document);
                stream.Flush(true);
            }

            File.Move(temporary, fullPath, overwrite);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}