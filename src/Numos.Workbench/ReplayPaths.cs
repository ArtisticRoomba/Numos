namespace Numos.Workbench;

/// <summary>
///     File naming rules for `.numos` replay files.
/// </summary>
public static class ReplayPaths
{
    /// <summary>
    ///     The replay file extension, including the dot.
    /// </summary>
    public const string Extension = ".numos";

    /// <summary>
    ///     Returns the full path of <paramref name="path" />, appending <see cref="Extension" /> when it is missing.
    /// </summary>
    public static string Normalize(string path)
    {
        return Path.GetFullPath(
            string.Equals(Path.GetExtension(path), Extension, StringComparison.OrdinalIgnoreCase)
                ? path
                : path + Extension);
    }

    /// <summary>
    ///     Replaces characters that are invalid in file names, falling back to `simulation` for a blank name.
    /// </summary>
    public static string SanitizeName(string name)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(name) ? "simulation" : name;
    }
}
