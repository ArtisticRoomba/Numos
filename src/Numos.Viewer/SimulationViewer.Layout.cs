using ImGuiNET;

namespace Numos.Viewer;

public partial class SimulationViewer
{
    private const string LayoutFileName = "imgui.ini";
    private const string PackageDefaultLayoutFileName = "imgui-default.ini";

    private string? _userLayoutPath;
    private string? _layoutStatus;

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
}