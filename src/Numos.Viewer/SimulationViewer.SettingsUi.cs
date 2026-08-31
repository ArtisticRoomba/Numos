using System.Numerics;
using ImGuiNET;
using Numos.Viewer.Ui;
using Raylib_cs;

namespace Numos.Viewer;

public partial class SimulationViewer
{
    private const double ResolutionConfirmationDuration = 15.0;
    private int _previousResolutionHeight;
    private int _previousResolutionWidth;
    private bool _requestResolutionConfirmation;
    private double _resolutionChangedAt;
    private bool _resolutionConfirmationPending;

    private void RenderProgramSettingsPanel()
    {
        if (!_showProgramSettingsPanel)
            return;

        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(
            viewport.Pos + viewport.Size * 0.5f,
            ImGuiCond.Appearing,
            new Vector2(0.5f, 0.5f));

        using var window = ImGuiExtensions.BeginWindow(
            "Configure##program-settings",
            ref _showProgramSettingsPanel,
            new Vector2(460, 120),
            new Vector2(760, 430));

        if (!window.IsVisible)
            return;

        ImGui.BeginChild(
            "SettingsCategories##program-settings",
            new Vector2(180, 0),
            ImGuiChildFlags.Borders);

        ImGui.Selectable("Graphics", _programSettingsTab == 0);
        if (ImGui.IsItemClicked())
            _programSettingsTab = 0;

        ImGui.Selectable("Interface", _programSettingsTab == 1);
        if (ImGui.IsItemClicked())
            _programSettingsTab = 1;

        ImGui.EndChild();

        ImGui.SameLine();
        ImGui.BeginChild("SettingsContent##program-settings", Vector2.Zero);

        if (_programSettingsTab == 0)
            RenderGraphicsSettings();
        else
            RenderInterfaceSettings();

        ImGui.EndChild();
    }

    private void RenderGraphicsSettings()
    {
        ImGui.Text("Graphics");
        ImGui.TextDisabled("Display and rendering options.");
        ImGui.Separator();

        (int Width, int Height)[] resolutions = GetTargetResolutions();
        int currentWidth = Raylib.GetScreenWidth();
        int currentHeight = Raylib.GetScreenHeight();
        int selectedResolution = FindResolutionIndex(resolutions, currentWidth, currentHeight);
        string resolutionLabel = selectedResolution >= 0
            ? FormatResolution(resolutions[selectedResolution])
            : $"{currentWidth} x {currentHeight} (custom)";

        bool resolutionSelectorDisabled = _resolutionConfirmationPending;
        if (resolutionSelectorDisabled)
            ImGui.BeginDisabled();

        ImGui.SetNextItemWidth(220f);
        if (ImGui.BeginCombo("Target resolution", resolutionLabel))
        {
            for (int index = 0; index < resolutions.Length; index++)
            {
                bool selected = index == selectedResolution;
                if (ImGui.Selectable(FormatResolution(resolutions[index]), selected))
                    ApplyTargetResolution(resolutions[index].Width, resolutions[index].Height);

                if (selected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }

        if (resolutionSelectorDisabled)
            ImGui.EndDisabled();

        bool fullscreen = Raylib.IsWindowState(ConfigFlags.FullscreenMode);
        if (ImGui.Checkbox("Fullscreen", ref fullscreen))
            Raylib.ToggleFullscreen();

        ImGui.Separator();
        ImGui.Text("Frame pacing");
        if (ImGui.Checkbox("Uncap FPS", ref _uncappedFps))
            Raylib.SetTargetFPS(_uncappedFps ? 0 : _targetFps);

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "This will casually max out a CPU core while updating the UI if VSync is disabled.\n" +
                "Ill-advised to use, made accessible just for the love of the game.");
        }

        ImGui.TextDisabled("Uncapped mode lets the renderer run without a software frame limit.");

        if (_uncappedFps)
            ImGui.BeginDisabled();

        ImGui.SetNextItemWidth(220f);
        if (ImGui.SliderInt("Frame rate limit", ref _targetFps, 30, 240) && !_uncappedFps)
            Raylib.SetTargetFPS(_targetFps);

        if (_uncappedFps)
            ImGui.EndDisabled();

        bool vsyncEnabled = Raylib.IsWindowState(ConfigFlags.VSyncHint);
        if (ImGui.Checkbox("VSync", ref vsyncEnabled))
        {
            if (vsyncEnabled)
                Raylib.SetWindowState(ConfigFlags.VSyncHint);
            else
                Raylib.ClearWindowState(ConfigFlags.VSyncHint);
        }

        ImGui.TextDisabled("VSync synchronizes presentation to the display refresh rate.");
    }

    private void RenderInterfaceSettings()
    {
        ImGui.Text("Interface");
        ImGui.TextDisabled("Viewer display preferences.");
        ImGui.Separator();
        ImGui.Checkbox("Show FPS overlay", ref _showPerformanceOverlay);
        ImGui.TextDisabled("Displays the current render rate in the upper-left corner.");

        ImGui.Spacing();
        ImGui.Checkbox("Show viewport branding", ref _showViewportBranding);
        ImGui.TextDisabled("Displays the Numos logo and name in simulation views.");
        ImGui.TextDisabled("How it feels to LARP as an actual CFD tool.");
        ImGui.SetNextItemWidth(220f);
        ImGui.SliderFloat("Branding opacity", ref _viewportBrandingOpacityPercent, 0f, 100f, "%.0f%%");
        ImGui.SetNextItemWidth(220f);
        if (ImGui.BeginCombo("Corner", GetViewportBrandingCornerLabel(_viewportBrandingCorner)))
        {
            foreach (var corner in Enum.GetValues<ViewportBrandingCorner>())
            {
                bool isSelected = corner == _viewportBrandingCorner;
                if (ImGui.Selectable(GetViewportBrandingCornerLabel(corner), isSelected))
                    _viewportBrandingCorner = corner;

                if (isSelected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }

        ImGui.SetNextItemWidth(220f);
        ImGui.SliderFloat("Offset X", ref _viewportBrandingOffsetX, 0f, 500f, "%.0f px");
        ImGui.SetNextItemWidth(220f);
        ImGui.SliderFloat("Offset Y", ref _viewportBrandingOffsetY, 0f, 500f, "%.0f px");
        ImGui.SetNextItemWidth(220f);
        ImGui.SliderFloat("Logo size", ref _viewportBrandingSizePercent, 1f, 25f, "%.0f%%");

        ImGui.Separator();
        ImGui.Text("Window layout");
        if (ImGui.Button("Save Current Layout"))
            SaveCurrentLayout();

        ImGui.TextDisabled("Restores this arrangement the next time Numos starts.");

#if DEBUG || TOOLS
        ImGui.Spacing();
        if (ImGui.Button("Save as Default Preset"))
            SavePackagedDefaultLayout();

        ImGui.TextDisabled("Saves the default that future publishes use on first launch.");
#endif

        if (!string.IsNullOrEmpty(_layoutStatus))
            ImGui.TextDisabled(_layoutStatus);
    }

    private static (int Width, int Height)[] GetTargetResolutions()
    {
        return
        [
            (1280, 720),
            (1366, 768),
            (1600, 900),
            (1920, 1080),
            (2560, 1440),
            (3840, 2160)
        ];
    }

    private static int FindResolutionIndex((int Width, int Height)[] resolutions, int width, int height)
    {
        for (int index = 0; index < resolutions.Length; index++)
        {
            if (resolutions[index].Width == width && resolutions[index].Height == height)
                return index;
        }

        return -1;
    }

    private static string FormatResolution((int Width, int Height) resolution)
    {
        return $"{resolution.Width} x {resolution.Height}";
    }

    private void ApplyTargetResolution(int width, int height)
    {
        if (_resolutionConfirmationPending)
            return;

        _previousResolutionWidth = Raylib.GetScreenWidth();
        _previousResolutionHeight = Raylib.GetScreenHeight();
        Raylib.SetWindowSize(width, height);
        _resolutionChangedAt = ImGui.GetTime();
        _resolutionConfirmationPending = true;
        _requestResolutionConfirmation = true;
    }

    private void RenderResolutionConfirmationModal()
    {
        const string popupId = "Confirm Resolution Change";
        if (_requestResolutionConfirmation)
        {
            ImGui.OpenPopup(popupId);
            _requestResolutionConfirmation = false;
        }

        if (!_resolutionConfirmationPending)
            return;

        double elapsed = ImGui.GetTime() - _resolutionChangedAt;
        int secondsRemaining = Math.Max(
            0,
            (int)Math.Ceiling(ResolutionConfirmationDuration - elapsed));

        if (elapsed >= ResolutionConfirmationDuration)
        {
            RevertTargetResolution();
            ImGui.CloseCurrentPopup();
            return;
        }

        ImGui.SetNextWindowSize(new Vector2(420, 0), ImGuiCond.Appearing);
        bool modalOpen = true;
        using var modal = ImGuiExtensions.BeginPopupModal(popupId, ref modalOpen);
        if (!modal.IsVisible)
            return;

        ImGui.TextWrapped("Keep this resolution? The previous resolution will be restored automatically if you do not confirm.");
        ImGui.Spacing();
        ImGui.TextColored(
            new Vector4(1f, 0.8f, 0.25f, 1f),
            $"Reverting in {secondsRemaining} seconds.");

        ImGui.Spacing();

        if (ImGui.Button("Keep Resolution", new Vector2(150, 0)))
        {
            _resolutionConfirmationPending = false;
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button("Revert", new Vector2(120, 0)))
        {
            RevertTargetResolution();
            ImGui.CloseCurrentPopup();
        }

        if (!modalOpen && _resolutionConfirmationPending)
            RevertTargetResolution();
    }

    private void RevertTargetResolution()
    {
        Raylib.SetWindowSize(_previousResolutionWidth, _previousResolutionHeight);
        _resolutionConfirmationPending = false;
    }

    private void RenderPerformanceOverlay()
    {
        if (!_showPerformanceOverlay)
            return;

        ImGui.SetNextWindowPos(new Vector2(12, 36), ImGuiCond.Always);
        const ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoDecoration |
            ImGuiWindowFlags.NoInputs |
            ImGuiWindowFlags.AlwaysAutoResize |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoFocusOnAppearing |
            ImGuiWindowFlags.NoNav;

        ImGui.Begin("Performance##performance-overlay", flags);
        ImGui.TextDisabled($"FPS: {ImGui.GetIO().Framerate:F1}");
        ImGui.End();
    }
}