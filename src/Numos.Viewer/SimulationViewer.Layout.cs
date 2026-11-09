using System.Diagnostics;
using ImGuiNET;
using Numos.Viewer.Workspace;
using Numos.Workbench;

namespace Numos.Viewer;

public partial class SimulationViewer
{
    private readonly LayoutAutosave _layoutAutosave = new(TimeSpan.FromSeconds(2));
    private LayoutStore? _layoutStore;

    /// <summary>
    ///     Loads the per-user layout. Call it after <see cref="ImGuiIOPtr.IniFilename" /> is cleared and before the
    ///     first frame. Without a saved layout, the workspace builds the default one on the first frame.
    /// </summary>
    private void ConfigureLayoutPersistence()
    {
        _layoutStore = LayoutStore.CreateForCurrentUser();

        // ImGui counts its save delay in frame time, which stops while the viewer waits for events. The smallest
        // positive rate makes it report a change on the next frame, and LayoutAutosave applies the delay instead.
        ImGui.GetIO().IniSavingRate = float.Epsilon;
        try
        {
            if (_layoutStore.Load() is { } ini)
                ImGui.LoadIniSettingsFromMemory(ini);
        }
        catch (Exception ex)
        {
            WriteException(Locale.GetString("imgui-layout-load-error"), ex);
        }
    }

    private void SaveCurrentLayout()
    {
        if (_layoutStore == null)
        {
            WriteMessage(WorkbenchLogLevel.Error, "Layout", Locale.GetString("imgui-layout-not-initialized-error"));
            return;
        }

        try
        {
            WriteLayout(_layoutStore);
            WriteMessage(WorkbenchLogLevel.Info, "Layout", Locale.GetString("imgui-layout-saved"));
        }
        catch (Exception ex)
        {
            WriteException(Locale.GetString("imgui-layout-save-error"), ex);
        }
    }

    /// <summary>
    ///     Saves the layout a short time after ImGui reports a change, such as a window that moved or docked. Call it
    ///     after the frame ends and before the frame pacing is applied.
    /// </summary>
    /// <remarks>
    ///     ImGui sets <see cref="ImGuiIOPtr.WantSaveIniSettings" /> instead of writing a file because
    ///     <see cref="ImGuiIOPtr.IniFilename" /> is null. It does not clear the flag itself. While a save is pending,
    ///     this requests frames, so a viewer that waits for events still writes the file.
    /// </remarks>
    private void AutosaveLayout()
    {
        if (_layoutStore == null)
            return;

        var io = ImGui.GetIO();
        TimeSpan now = Stopwatch.GetElapsedTime(0);
        if (io.WantSaveIniSettings)
        {
            io.WantSaveIniSettings = false;
            _layoutAutosave.MarkChanged(now);
        }

        if (!_layoutAutosave.TryTakeDueSave(now))
        {
            if (_layoutAutosave.IsPending)
                RequestNextFrame();

            return;
        }

        try
        {
            WriteLayout(_layoutStore);
        }
        catch (Exception ex)
        {
            WriteException(Locale.GetString("imgui-layout-save-error"), ex);
        }
    }

    private static void WriteLayout(LayoutStore store)
    {
        store.Save(ImGui.SaveIniSettingsToMemory());
    }
}
