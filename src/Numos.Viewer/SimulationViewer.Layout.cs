using ImGuiNET;

namespace Numos.Viewer;

public partial class SimulationViewer
{
    private const string LayoutFileName = "imgui.ini";
    private const string PackageDefaultLayoutFileName = "imgui-default.ini";

    private string? _userLayoutPath;
    private string? _layoutStatus;
#if DEBUG || TOOLS
    private string? _packagedDefaultLayoutPath;
#endif

    /// <summary>
    ///     Inits ImGui layout persistence,
    ///     loads a default if no previously saved layout file is present.
    /// </summary>
    private void ConfigureLayoutPersistence()
    {
        string settingsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Numos");
        Directory.CreateDirectory(settingsDirectory);

        _userLayoutPath = Path.Combine(settingsDirectory, LayoutFileName);
        string packagedLayoutPath = Path.Combine(
            AppContext.BaseDirectory,
            "assets",
            PackageDefaultLayoutFileName);
#if DEBUG || TOOLS
        _packagedDefaultLayoutPath = FindPackagedDefaultLayoutSourcePath();
#endif

        if (!File.Exists(_userLayoutPath) && File.Exists(packagedLayoutPath))
            File.Copy(packagedLayoutPath, _userLayoutPath);

        if (File.Exists(_userLayoutPath))
            ImGui.LoadIniSettingsFromDisk(_userLayoutPath);
    }

    private void SaveCurrentLayout()
    {
        if (_userLayoutPath == null)
        {
            _layoutStatus = "The ImGui layout has not been initialized yet.";
            return;
        }

        try
        {
            ImGui.SaveIniSettingsToDisk(_userLayoutPath);
            _layoutStatus = "Saved. This layout will be restored at the next launch.";
        }
        catch (Exception ex)
        {
            // TODO error handling make this prettier
            _layoutStatus = $"Could not save the layout: {ex.Message}";
        }
    }

#if DEBUG || TOOLS
    /// <summary>
    ///     Saves the active arrangement as the source layout included with future publishes.
    ///     New users receive this layout only when they do not yet have a saved layout.
    /// </summary>
    private void SavePackagedDefaultLayout()
    {
        if (_packagedDefaultLayoutPath == null)
        {
            _layoutStatus = "The packaged default layout has not been initialized yet.";
            return;
        }

        try
        {
            ImGui.SaveIniSettingsToDisk(_packagedDefaultLayoutPath);
            _layoutStatus = "Saved as the packaged default for first-time launches.";
        }
        catch (Exception ex)
        {
            // TODO error handling make this prettier
            _layoutStatus = $"Could not save the packaged default layout: {ex.Message}";
        }
    }

    private static string? FindPackagedDefaultLayoutSourcePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            string projectFilePath = Path.Combine(directory.FullName, "Numos.Viewer.csproj");
            string layoutFilePath = Path.Combine(
                directory.FullName,
                "assets",
                PackageDefaultLayoutFileName);

            if (File.Exists(projectFilePath) && File.Exists(layoutFilePath))
                return layoutFilePath;

            directory = directory.Parent;
        }

        return null;
    }
#endif
}