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
            Locale.GetString("program-settings.configure") + "##program-settings",
            ref _showProgramSettingsPanel,
            new Vector2(460, 120),
            new Vector2(760, 430));

        if (!window.IsVisible)
            return;

        ImGui.BeginChild(
            "SettingsCategories##program-settings",
            new Vector2(180, 0),
            ImGuiChildFlags.Borders);

        if (ImGui.Selectable(Locale.GetString("settings.graphics"), _programSettingsTab == 0))
            _programSettingsTab = 0;

        if (ImGui.Selectable(Locale.GetString("settings.interface"), _programSettingsTab == 1))
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
        ImGui.SeparatorText(Locale.GetString("settings.graphics"));

        (int Width, int Height)[] resolutions = GetTargetResolutions();
        int currentWidth = Raylib.GetScreenWidth();
        int currentHeight = Raylib.GetScreenHeight();
        int selectedResolution = FindResolutionIndex(resolutions, currentWidth, currentHeight);
        string resolutionLabel = selectedResolution >= 0
            ? FormatResolution(resolutions[selectedResolution])
            : Locale.GetString("settings.custom-resolution", ("width", currentWidth), ("height", currentHeight));

        bool resolutionSelectorDisabled = _resolutionConfirmationPending;
        if (resolutionSelectorDisabled)
            ImGui.BeginDisabled();

        ImGui.SetNextItemWidth(220f);
        if (ImGui.BeginCombo(Locale.GetString("settings.target-resolution"), resolutionLabel))
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
        if (ImGui.Checkbox(Locale.GetString("settings.fullscreen"), ref fullscreen))
            Raylib.ToggleFullscreen();

        ImGui.SeparatorText(Locale.GetString("settings.frame-pacing"));

        ImGui.Checkbox(Locale.GetString("settings.uncap-fps"), ref _uncappedFps);

        ImGuiExtensions.QuestionTooltip(
            Locale.GetString("settings.uncap-fps-tooltip"));

        if (_uncappedFps)
            ImGui.BeginDisabled();

        ImGui.SetNextItemWidth(220f);
        ImGui.SliderInt(Locale.GetString("settings.frame-rate-limit"), ref _targetFps, 30, 240);

        if (_uncappedFps)
            ImGui.EndDisabled();

        ImGui.Checkbox(Locale.GetString("settings.limit-fps-unfocused"), ref _limitFpsWhenUnfocused);
        ImGuiExtensions.QuestionTooltip(
            Locale.GetString("settings.limit-fps-unfocused-tooltip"));

        if (!_limitFpsWhenUnfocused)
            ImGui.BeginDisabled();

        ImGui.SetNextItemWidth(220f);
        ImGui.SliderInt(Locale.GetString("settings.unfocused-frame-rate"), ref _unfocusedFps, 1, 60);

        if (!_limitFpsWhenUnfocused)
            ImGui.EndDisabled();

        bool vsyncEnabled = Raylib.IsWindowState(ConfigFlags.VSyncHint);
        if (ImGui.Checkbox(Locale.GetString("settings.vsync"), ref vsyncEnabled))
        {
            if (vsyncEnabled)
                Raylib.SetWindowState(ConfigFlags.VSyncHint);
            else
                Raylib.ClearWindowState(ConfigFlags.VSyncHint);
        }

        ImGuiExtensions.QuestionTooltip(Locale.GetString("settings.vsync-tooltip"));

        ImGui.Checkbox(Locale.GetString("settings.event-based-rendering"), ref _eventBasedRenderingEnabled);
        ImGuiExtensions.QuestionTooltip(
            Locale.GetString("settings.event-based-rendering-tooltip"));
    }

    private void RenderInterfaceSettings()
    {
        ImGui.SeparatorText(Locale.GetString("settings.interface"));
        ImGui.Checkbox(Locale.GetString("settings.show-fps-overlay"), ref _showPerformanceOverlay);
        ImGuiExtensions.QuestionTooltip(Locale.GetString("settings.show-fps-overlay-tooltip"));

        ImGui.Spacing();
        ImGui.Checkbox(Locale.GetString("settings.show-viewport-branding"), ref _showViewportBranding);
        ImGuiExtensions.QuestionTooltip(
            Locale.GetString("settings.show-viewport-branding-tooltip"));

        ImGui.SetNextItemWidth(220f);
        ImGui.SliderFloat(Locale.GetString("settings.branding-opacity"), ref _viewportBrandingOpacityPercent, 0f, 100f, "%.0f%%");
        ImGui.SetNextItemWidth(220f);
        if (ImGui.BeginCombo(Locale.GetString("settings.corner"), GetViewportBrandingCornerLabel(_viewportBrandingCorner)))
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
        ImGui.SliderFloat(Locale.GetString("settings.offset-x"), ref _viewportBrandingOffsetX, 0f, 500f, "%.0f px");
        ImGui.SetNextItemWidth(220f);
        ImGui.SliderFloat(Locale.GetString("settings.offset-y"), ref _viewportBrandingOffsetY, 0f, 500f, "%.0f px");
        ImGui.SetNextItemWidth(220f);
        ImGui.SliderFloat(Locale.GetString("settings.logo-size"), ref _viewportBrandingSizePercent, 1f, 25f, "%.0f%%");

        ImGui.SeparatorText(Locale.GetString("settings.window-layout"));
        if (ImGui.Button(Locale.GetString("settings.save-current-layout")))
            SaveCurrentLayout();

        ImGuiExtensions.QuestionTooltip(Locale.GetString("settings.save-current-layout-tooltip"));

#if DEBUG || TOOLS
        ImGui.Spacing();
        if (ImGui.Button(Locale.GetString("settings.save-default-preset")))
            SavePackagedDefaultLayout();

        ImGuiExtensions.QuestionTooltip(Locale.GetString("settings.save-default-preset-tooltip"));
#endif
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
        return Locale.GetString("settings.resolution-format", ("width", resolution.Width), ("height", resolution.Height));
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
        string popupId = Locale.GetString("resolution.confirm-title");
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

        ImGui.TextWrapped(Locale.GetString("resolution.confirm-message"));
        ImGui.Spacing();
        ImGui.TextColored(
            ViewerTheme.Caution,
            Locale.GetString("resolution.reverting-countdown", ("seconds", secondsRemaining)));

        ImGui.Spacing();

        if (ImGui.Button(Locale.GetString("resolution.keep"), new Vector2(150, 0)))
        {
            _resolutionConfirmationPending = false;
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        bool revert = ImGui.Button(Locale.GetString("resolution.revert"), new Vector2(120, 0));

        // Reverting is the safe choice if the new mode left the window unusable.
        ImGui.SetItemDefaultFocus();
        if (revert)
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
        ImGui.TextDisabled(Locale.GetString("performance.fps", ("fps", $"{ImGui.GetIO().Framerate:F1}")));
        ImGui.End();
    }
}