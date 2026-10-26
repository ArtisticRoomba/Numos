namespace Numos.Serialization.FileSystem;

/// <summary>
///     Writes a file through a temporary sibling and a rename, so a crash mid-write never leaves a half-written file at
///     the destination.
/// </summary>
internal static class AtomicFile
{
    public static void Write(string path, bool overwrite, Action<Stream> write)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
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
                write(stream);
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
