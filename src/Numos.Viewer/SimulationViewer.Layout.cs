using ImGuiNET;
using Numos.Viewer.Ui;
using Numos.Workbench;

namespace Numos.Viewer;

public partial class SimulationViewer
{
    private const string LayoutFileName = "imgui.ini";
    private const string PackageDefaultLayoutFileName = "imgui-default.ini";
#if DEBUG || TOOLS
    private string? _packagedDefaultLayoutPath;
#endif

    private string? _userLayoutPath;

    /// <summary>
    ///     Loads the per-user ImGui layout, first copying the packaged default into place when the user has no saved
    ///     layout yet.
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
            WriteMessage(
                WorkbenchLogLevel.Error,
                Locale.GetString("source-layout"), 
                Locale.GetString("message-layout-not-initialized"));
            return;
        }

        try
        {
            ImGui.SaveIniSettingsToDisk(_userLayoutPath);
            WriteMessage(WorkbenchLogLevel.Info,
                Locale.GetString("source-layout"), 
                Locale.GetString("message-layout-saved-success"));
        }
        catch (Exception ex)
        {
            WriteException(Locale.GetString("message-layout-exception"), ex);
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
            WriteMessage(WorkbenchLogLevel.Error, Locale.GetString("source-layout"), Locale.GetString("message-layout-not-initialized-packaged"));
            return;
        }

        try
        {
            ImGui.SaveIniSettingsToDisk(_packagedDefaultLayoutPath);
            WriteMessage(WorkbenchLogLevel.Info, Locale.GetString("source-layout"), Locale.GetString("message-layout-saved-success-packaged"));
        }
        catch (Exception ex)
        {
            WriteException(Locale.GetString("message-layout-exception"), ex);
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