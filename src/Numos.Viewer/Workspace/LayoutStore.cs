namespace Numos.Viewer.Workspace;

/// <summary>
///     Reads and writes the saved ImGui window layout.
/// </summary>
/// <remarks>
///     The file name carries a version. A layout change that old files cannot describe gets a new name, so the old
///     file stays on disk untouched and the new default applies once. Version 1 was <c>imgui.ini</c>, which this
///     class never reads or writes.
/// </remarks>
internal sealed class LayoutStore
{
    /// <summary>
    ///     Name of the layout file in <see cref="DirectoryPath" />.
    /// </summary>
    public const string FileName = "imgui-layout-v2.ini";

    /// <summary>
    ///     Creates a store for a layout file in <paramref name="directoryPath" />. The directory is created on the first
    ///     save.
    /// </summary>
    public LayoutStore(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        DirectoryPath = directoryPath;
        FilePath = Path.Combine(directoryPath, FileName);
    }

    /// <summary>
    ///     The directory that holds the layout file.
    /// </summary>
    public string DirectoryPath { get; }

    /// <summary>
    ///     The full path of the layout file.
    /// </summary>
    public string FilePath { get; }

    /// <summary>
    ///     Creates the store for the current user, in <c>LocalApplicationData/Numos</c>.
    /// </summary>
    public static LayoutStore CreateForCurrentUser()
    {
        return new LayoutStore(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Numos"));
    }

    /// <summary>
    ///     Reads the saved layout.
    /// </summary>
    /// <returns>The ini text, or <see langword="null" /> when no layout has been saved.</returns>
    /// <exception cref="IOException">The file exists but cannot be read.</exception>
    public string? Load()
    {
        return File.Exists(FilePath) ? File.ReadAllText(FilePath) : null;
    }

    /// <summary>
    ///     Replaces the saved layout with <paramref name="ini" />.
    /// </summary>
    /// <remarks>
    ///     The text goes to a temporary file first, which then replaces the layout file in one move. A crash during
    ///     the write leaves the previous layout intact.
    /// </remarks>
    /// <exception cref="IOException">The directory or the file cannot be written.</exception>
    public void Save(string ini)
    {
        ArgumentNullException.ThrowIfNull(ini);
        Directory.CreateDirectory(DirectoryPath);

        string temporaryPath = FilePath + ".tmp";
        File.WriteAllText(temporaryPath, ini);
        File.Move(temporaryPath, FilePath, overwrite: true);
    }
}
