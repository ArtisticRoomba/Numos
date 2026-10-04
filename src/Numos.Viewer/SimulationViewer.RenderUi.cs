using System.Globalization;
using System.Numerics;
using ImGuiNET;
using Numos.API;
using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.Maths;
using Numos.SimDrawer;
using Numos.Viewer.Ui;
using Raylib_cs;
using rlImGui_cs;

namespace Numos.Viewer;

public partial class SimulationViewer
{
    private const float NumericInputWidth = 100f;
    private const float AboutTabContentWidth = 520f;
    private const float AboutTabContentHeight = 390f;
    private bool _aboutModalOpen;

    private bool _requestOpenAboutModal;

    private void RenderUi()
    {
        RenderMainDockspace();

        RenderMenuBar();
        RenderProgramSettingsPanel();
        RenderResolutionConfirmationModal();
        RenderPerformanceOverlay();
        DrawReplayFileModals();
        DrawWorldModals();
        RenderMessagesPanel();

        if (_world == null)
        {
            RenderEmptyWorkspaceMessage();
            DrawCreateProjectModal();
            DrawCloseProjectModal();
            DrawAboutModal();
            return;
        }

        RenderSimulationViewports();

        if (_showSliceViewport && _simulation != null)
        {
            _sliceViewport?.Draw(
                "Simulation Slice 2D##slice-viewport",
                RenderSimulationSliceScene,
                new Vector2(320, 560),
                new Vector2(660, 330),
                () =>
                {
                    UpdateSlicePicking();
                    RenderSliceCellTooltip();
                    RenderVoxelContextMenu();
                });
        }
        else
        {
            _hoveredSliceCell = null;
            RebuildHighlights();
        }

        // Each of these panels disables only its own editing controls while the Timeline inspects history, so
        // readouts stay legible.
        RenderSolutionPanel();
        RenderToolsPanel();
        RenderConfigurationPanel();
        RenderWorldPanel();
        RenderViewPanel();
        RenderTimelinePanel();
        DrawCreateProjectModal();
        DrawCloseProjectModal();
        DrawAboutModal();
    }

    private static void RenderMainDockspace()
    {
        var viewport = ImGui.GetMainViewport();

        ImGui.SetNextWindowPos(viewport.WorkPos);
        ImGui.SetNextWindowSize(viewport.WorkSize);
        ImGui.SetNextWindowViewport(viewport.ID);

        var windowFlags =
            ImGuiWindowFlags.NoDocking |
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoBringToFrontOnFocus |
            ImGuiWindowFlags.NoNavFocus |
            ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);

        ImGui.Begin("MainDockspace##main-dockspace-window", windowFlags);

        ImGui.PopStyleVar(3);

        uint dockspaceId = ImGui.GetID("MainDockspace##main-dockspace-id");
        ImGui.DockSpace(dockspaceId, Vector2.Zero, ImGuiDockNodeFlags.None);

        ImGui.End();
    }

    private void RenderEmptyWorkspaceMessage()
    {
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(
            viewport.WorkPos + viewport.WorkSize * 0.5f,
            ImGuiCond.Always,
            new Vector2(0.5f, 0.5f));

        const ImGuiWindowFlags windowFlags =
            ImGuiWindowFlags.AlwaysAutoResize |
            ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoDecoration |
            ImGuiWindowFlags.NoDocking |
            ImGuiWindowFlags.NoInputs |
            ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoSavedSettings;

        ImGui.Begin("Empty workspace##empty-workspace", windowFlags);

        DrawEmptyWorkspaceBranding();

        ImGui.Spacing();
        ImGui.Separator();

        ImGuiExtensions.TextCentered("An external viewer for Numos, an engine-agnostic,");
        ImGuiExtensions.TextCentered(" pseudo-realistic, voxel-based atmospherics simulation library.");

        ImGui.Separator();
        ImGui.Spacing();

        ImGuiExtensions.TextCentered("No simulation is currently loaded.", ImGui.TextDisabled);
        ImGuiExtensions.TextCentered(
            "Choose File > New Simulation to create a workspace.",
            ImGui.TextDisabled);

        ImGui.End();
    }

    private void DrawAboutModal()
    {
        const string popupId = "About Numos";

        ImGuiExtensions.OpenPopupWhenRequested(popupId, ref _requestOpenAboutModal);

        float aboutWindowWidth = AboutTabContentWidth + ImGui.GetStyle().WindowPadding.X * 2;
        ImGui.SetNextWindowSize(new Vector2(aboutWindowWidth, 0), ImGuiCond.Appearing);

        using var modal = ImGuiExtensions.BeginPopupModal(popupId, ref _aboutModalOpen);
        if (!modal.IsVisible)
            return;

        DrawAboutHeader();

        DrawAboutTabs();

        ImGui.Spacing();

        const float buttonWidth = 120f;
        float availableWidth = ImGui.GetContentRegionAvail().X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + availableWidth - buttonWidth);

        if (ImGui.Button("Close", new Vector2(buttonWidth, 0)))
        {
            _aboutModalOpen = false;
            ImGui.CloseCurrentPopup();
        }
    }

    private void DrawAboutTabs()
    {
        var tabAreaSize = new Vector2(AboutTabContentWidth, AboutTabContentHeight);

        if (ImGui.BeginTabBar("AboutTabs", ImGuiTabBarFlags.None))
        {
            if (ImGui.BeginTabItem("About"))
            {
                DrawAboutTab(tabAreaSize);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Authors"))
            {
                DrawAuthorsTab(tabAreaSize);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Thanks To"))
            {
                DrawThanksToTab(tabAreaSize);
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    private void DrawAboutTab(Vector2 size)
    {
        ImGui.BeginChild(
            "AboutTabContent",
            size,
            ImGuiChildFlags.Borders,
            ImGuiWindowFlags.None);

        ImGui.TextWrapped(
            "An external viewer for Numos, an engine-agnostic, pseudo-realistic, voxel-based atmospherics simulation library.");

        ImGui.Spacing();

        using (var legal = ImGuiExtensions.BeginDefinitionTable("about-legal"))
        {
            if (legal.IsVisible)
            {
                ImGuiExtensions.DefinitionRow("Copyright", "© 2026 Numos contributors");
                ImGuiExtensions.DefinitionRow("License", "MIT");
            }
        }

        ImGui.Spacing();
        ImGui.SeparatorText("Build provenance");
        DrawBuildProvenance(
            "Viewer",
            ViewerBuildInfo.PackageVersion,
            ViewerBuildInfo.GitBranch ?? ViewerBuildInfo.SourceReference,
            ViewerBuildInfo.GitCommit,
            ViewerBuildInfo.GitCommitShort,
            ViewerBuildInfo.BuildConfiguration,
            ViewerBuildInfo.TargetFramework,
            ViewerBuildInfo.SdkVersion,
            ViewerBuildInfo.RepositoryUrl,
            ViewerBuildInfo.CommitUrl);

        ImGui.Spacing();
        DrawBuildProvenance(
            "CoreSim",
            CoreSimBuildInfo.PackageVersion,
            CoreSimBuildInfo.GitBranch ?? CoreSimBuildInfo.SourceReference,
            CoreSimBuildInfo.GitCommit,
            CoreSimBuildInfo.GitCommitShort,
            CoreSimBuildInfo.BuildConfiguration,
            CoreSimBuildInfo.TargetFramework,
            CoreSimBuildInfo.SdkVersion,
            CoreSimBuildInfo.RepositoryUrl,
            CoreSimBuildInfo.CommitUrl);

        ImGui.EndChild();
    }

    private static void DrawBuildProvenance(
        string component,
        string version,
        string sourceReference,
        string commit,
        string shortCommit,
        string configuration,
        string targetFramework,
        string sdkVersion,
        string repositoryUrl,
        string? commitUrl)
    {
        ImGui.TextUnformatted($"{component} {version}");
        using (var table = ImGuiExtensions.BeginDefinitionTable($"provenance-{component}"))
        {
            if (table.IsVisible)
            {
                ImGuiExtensions.DefinitionRow("Source", sourceReference);
                ImGuiExtensions.DefinitionRow("Commit", shortCommit);
                ImGuiExtensions.DefinitionRow("Configuration", configuration);
                ImGuiExtensions.DefinitionRow("Framework", targetFramework);
                ImGuiExtensions.DefinitionRow("SDK", sdkVersion);
            }
        }

        bool hasRepository = IsKnownBuildValue(repositoryUrl);
        if (!hasRepository)
            ImGui.BeginDisabled();

        if (ImGui.SmallButton($"Repository##{component}"))
            Raylib.OpenURL(repositoryUrl);

        if (!hasRepository)
            ImGui.EndDisabled();

        ImGui.SameLine();
        bool hasCommitUrl = commitUrl != null;
        if (!hasCommitUrl)
            ImGui.BeginDisabled();

        if (ImGui.SmallButton($"Commit##{component}") && commitUrl != null)
            Raylib.OpenURL(commitUrl);

        if (!hasCommitUrl)
            ImGui.EndDisabled();

        ImGui.SameLine();
        bool hasCommit = IsKnownBuildValue(commit);
        if (!hasCommit)
            ImGui.BeginDisabled();

        if (ImGui.SmallButton($"Copy hash##{component}"))
            Raylib.SetClipboardText(commit);

        if (!hasCommit)
            ImGui.EndDisabled();
    }

    private static bool IsKnownBuildValue(string value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               !string.Equals(value, "unknown", StringComparison.Ordinal);
    }

    private void DrawAuthorsTab(Vector2 size)
    {
        ImGui.BeginChild(
            "AuthorsTabContent",
            size,
            ImGuiChildFlags.Borders,
            ImGuiWindowFlags.None);

        ImGui.Text("VeritableCalamity");
        ImGui.TextDisabled("Original author of CoreSim");

        ImGui.Separator();

        ImGui.Text("ArtisticRoomba");
        ImGui.TextDisabled("CoreSim maintainer, Viewer author");

        ImGui.EndChild();
    }

    private void DrawThanksToTab(Vector2 size)
    {
        ImGui.BeginChild(
            "ThanksToTabContent",
            size,
            ImGuiChildFlags.Borders,
            ImGuiWindowFlags.None);

        ImGui.Text("riccardi48");
        ImGui.TextDisabled("Numerical methods and physics guidance");

        ImGui.Separator();

        ImGui.Text("Ewanderer");
        ImGui.TextDisabled("GasReactions solver");

        ImGui.EndChild();
    }

    private void DrawAboutHeader()
    {
        float brandingHeight = ImGui.GetTextLineHeight() * 3 + ImGui.GetStyle().ItemSpacing.Y * 2;
        int logoSize = Math.Max(1, (int)MathF.Ceiling(brandingHeight));

        ImGui.BeginGroup();

        bool hasLogo = _viewportBranding.Id != 0;
        if (hasLogo)
        {
            rlImGui.ImageSize(_viewportBranding, logoSize, logoSize);

            ImGui.SameLine(0, ImGui.GetStyle().ItemSpacing.X);
        }

        ImGui.BeginGroup();
        ImGui.TextUnformatted("Numos.Viewer");
        ImGui.TextDisabled($"CoreSim v{CoreSimBuildInfo.PackageVersion}");
        ImGui.TextDisabled($"Viewer v{ViewerBuildInfo.PackageVersion}");
        ImGui.EndGroup();

        ImGui.EndGroup();
    }

    private void DrawEmptyWorkspaceBranding()
    {
        if (_viewportBranding.Id == 0)
            return;

        const string title = "Numos Simulation Viewer";
        string coreSimVersion = $"CoreSim v{CoreSimBuildInfo.PackageVersion}";
        string viewerVersion = $"Viewer v{ViewerBuildInfo.PackageVersion}";
        float textHeight = ImGui.GetTextLineHeight() * 3 + ImGui.GetStyle().ItemSpacing.Y * 2;
        int logoSize = Math.Max(1, (int)MathF.Ceiling(textHeight));
        float textWidth = Math.Max(
            ImGui.CalcTextSize(title).X,
            Math.Max(ImGui.CalcTextSize(coreSimVersion).X, ImGui.CalcTextSize(viewerVersion).X));

        float logoTextGap = ImGui.GetStyle().ItemSpacing.X * 2;
        float availableWidth = ImGui.GetContentRegionAvail().X;
        float left = ImGui.GetCursorPosX() + Math.Max(0f, (availableWidth - logoSize - logoTextGap - textWidth) * 0.5f);
        ImGui.SetCursorPosX(left);
        rlImGui.ImageSize(_viewportBranding, logoSize, logoSize);
        ImGui.SameLine(0, logoTextGap);

        ImGui.BeginGroup();
        ImGui.TextUnformatted(title);
        ImGui.TextDisabled(coreSimVersion);
        ImGui.TextDisabled(viewerVersion);
        ImGui.EndGroup();
    }

    private void RenderMenuBar()
    {
        if (ImGui.BeginMainMenuBar())
        {
            if (ImGui.BeginMenu("File"))
            {
                if (ImGui.MenuItem("New Simulation"))
                    RequestCreateProject();

                if (ImGui.MenuItem("Open Replay..."))
                    RequestOpenReplay();

                ImGui.BeginDisabled(_world == null);
                if (ImGui.MenuItem("Save Replay..."))
                    RequestSaveReplay();

                if (ImGui.MenuItem("Close Project"))
                    RequestCloseProject();

                ImGui.EndDisabled();

                ImGui.Separator();
                if (ImGui.MenuItem("Exit", "Alt+F4"))
                    _requestExit = true;

                ImGui.EndMenu();
            }

            if (ImGui.BeginMenu("View"))
            {
                ImGui.MenuItem("Messages / Logs", null, ref _showMessagesPanel);
                ImGui.Separator();

                bool simulationAvailable = _simulation != null && _config != null;
                ImGui.BeginDisabled(!simulationAvailable);

                ImGui.MenuItem("Solution", null, ref _showSolutionPanel);
                ImGui.MenuItem("Tools", null, ref _showToolsPanel);
                ImGui.MenuItem("View", null, ref _showViewPanel);
                ImGui.EndDisabled();

                bool worldAvailable = _world != null && _config != null;
                ImGui.BeginDisabled(!worldAvailable);
                ImGui.MenuItem("Configuration", null, ref _showConfigurationPanel);
                ImGui.MenuItem("Timeline", null, ref _showTimelinePanel);
                ImGui.MenuItem("World & Topology", null, ref _showWorldPanel);
                ImGui.EndDisabled();

                ImGui.BeginDisabled(!simulationAvailable);
                ImGui.MenuItem("3D Viewport", null, ref _show3DViewport);
                ImGui.MenuItem("2D Slice Viewport", null, ref _showSliceViewport);
                ImGui.EndDisabled();

                ImGui.EndMenu();
            }

            if (ImGui.BeginMenu("Settings"))
            {
                if (ImGui.MenuItem("Configure"))
                    _showProgramSettingsPanel = true;

                ImGui.EndMenu();
            }

            if (ImGui.BeginMenu("Help"))
            {
                if (ImGui.MenuItem("About"))
                {
                    _requestOpenAboutModal = true;
                    _aboutModalOpen = true;
                }

                ImGui.EndMenu();
            }

            RenderUnseenMessagesIndicator();
            ImGui.EndMainMenuBar();
        }
    }

    private void RenderViewPanel()
    {
        if (!_showViewPanel)
            return;

        using var window = ImGuiExtensions.BeginWindow(
            "View##view",
            ref _showViewPanel,
            new Vector2(990, 40),
            new Vector2(400, 420));

        if (!window.IsVisible)
            return;

        ImGui.SeparatorText("Visualization");

        if (_frameBuilder != null)
        {
            var current = _frameBuilder.Visualizations.GetRequired(_currentVisualizationId);
            if (ImGui.BeginCombo("Mode##viz", current.DisplayName))
            {
                foreach (var method in _frameBuilder.Visualizations.Methods)
                {
                    bool selected = string.Equals(
                        method.Id,
                        _currentVisualizationId,
                        StringComparison.OrdinalIgnoreCase);

                    if (ImGui.Selectable(method.DisplayName, selected))
                        SetVisualization(method.Id);

                    if (selected)
                        ImGui.SetItemDefaultFocus();
                }

                ImGui.EndCombo();
            }
        }

        RenderVisualizationLegend();

        ImGui.SeparatorText("Rendering style");
        RenderRenderingStyleTable();

        ImGui.SeparatorText("3D focus");
        RenderChunkFocusControls();

        ImGui.SeparatorText("2D slice");
        ImGui.Checkbox("Show slice viewport", ref _showSliceViewport);
        RenderSliceControls();
    }

    private void RenderRenderingStyleTable()
    {
        if (!ImGui.BeginTable(
                "RenderingStyleTable##render-style",
                3,
                ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg))
        {
            return;
        }

        ImGui.TableSetupColumn("");
        ImGui.TableSetupColumn("3D", ImGuiTableColumnFlags.WidthFixed, 40f);
        ImGui.TableSetupColumn("2D", ImGuiTableColumnFlags.WidthFixed, 40f);
        ImGui.TableHeadersRow();

        RenderRenderingStyleRow(
            "Chunk outlines",
            "chunk-outlines",
            ref _show3DChunkOutlines,
            ref _show2DChunkOutlines);

        RenderRenderingStyleRow(
            "Voxel outlines",
            "voxel-outlines",
            ref _show3DVoxelOutlines,
            ref _show2DVoxelOutlines);

        RenderRenderingStyleRow(
            "Transparent voxels",
            "transparent-voxels",
            ref _transparent3DVoxels,
            ref _transparent2DVoxels);

        ImGui.EndTable();
    }

    private static void RenderRenderingStyleRow(
        string label,
        string id,
        ref bool show3D,
        ref bool show2D)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted(label);
        ImGui.TableSetColumnIndex(1);
        ImGui.Checkbox($"##{id}-3d", ref show3D);
        ImGui.TableSetColumnIndex(2);
        ImGui.Checkbox($"##{id}-2d", ref show2D);
    }

    private void RenderConfigurationPanel()
    {
        if (!_showConfigurationPanel || _config == null)
            return;

        using var window = ImGuiExtensions.BeginWindow(
            "Configuration##configuration",
            ref _showConfigurationPanel,
            new Vector2(990, 470),
            new Vector2(400, 420));

        if (!window.IsVisible)
            return;

        bool inspecting = DrawInspectionNotice();
        if (inspecting)
            ClearConfigurationDrafts();

        if (ImGui.CollapsingHeader("AtmosConfig", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.BeginDisabled(inspecting);
            if (ImGui.Button("Reset to Defaults...##config-reset"))
                RequestResetConfiguration();

            ImGui.EndDisabled();

            // Every apply is a recorded replay operation, so typed values wait for Enter or focus loss instead of
            // recording each keystroke.
            if (!inspecting)
                ImGui.TextDisabled("Values apply on Enter, when a field loses focus, or when a slider is released.");

            DrawFeedback(_configurationFeedback);
            ImGui.Separator();

            ConfigFloatInput(
                AtmosConfigFields.GlobalTemperature,
                "config-global-temperature",
                _config.GlobalTemperature,
                ConfigDomain.Positive,
                inspecting,
                static (config, value) => config.GlobalTemperature = value);

            ConfigFloatInput(
                AtmosConfigFields.DefaultTemperatureFallback,
                "config-default-temperature-fallback",
                _config.DefaultTemperatureFallback,
                ConfigDomain.Positive,
                inspecting,
                static (config, value) => config.DefaultTemperatureFallback = value);

            ConfigFloatInput(
                AtmosConfigFields.VoxelVolume,
                "config-voxel-volume",
                _config.VoxelVolume,
                ConfigDomain.Positive,
                inspecting,
                static (config, value) => config.VoxelVolume = value);

            ConfigFloatInput(
                AtmosConfigFields.SaturationReferencePressure,
                "config-saturation-reference-pressure",
                _config.SaturationReferencePressure,
                ConfigDomain.Positive,
                inspecting,
                static (config, value) => config.SaturationReferencePressure = value,
                QuantityFormat.KilopascalsPerPascal);

            ConfigFloatInput(
                AtmosConfigFields.DefaultMolarHeatCapacityAtConstantVolume,
                "config-default-molar-cv",
                _config.DefaultMolarHeatCapacityAtConstantVolume,
                ConfigDomain.Positive,
                inspecting,
                static (config, value) => config.DefaultMolarHeatCapacityAtConstantVolume = value);

            ConfigFloatSlider(
                AtmosConfigFields.DefaultDiffusionCoefficient,
                "config-default-diffusion-coefficient",
                _config.DefaultDiffusionCoefficient,
                0f,
                1f,
                ConfigDomain.UnitInterval,
                inspecting,
                static (config, value) => config.DefaultDiffusionCoefficient = value);

            ConfigFloatInput(
                AtmosConfigFields.SpaceTemperature,
                "config-space-temperature",
                _config.SpaceTemperature,
                ConfigDomain.Positive,
                inspecting,
                static (config, value) => config.SpaceTemperature = value);

            ConfigFloatSlider(
                AtmosConfigFields.BulkFlowCoefficient,
                "config-bulk-flow-coefficient",
                _config.BulkFlowCoefficient,
                0f,
                0.5f,
                ConfigDomain.UnitInterval,
                inspecting,
                static (config, value) => config.BulkFlowCoefficient = value);

            ConfigFloatInput(
                AtmosConfigFields.VacuumThreshold,
                "config-vacuum-threshold",
                _config.VacuumThreshold,
                ConfigDomain.NonNegative,
                inspecting,
                static (config, value) => config.VacuumThreshold = value,
                QuantityFormat.KilopascalsPerPascal);

            ConfigIntInput(
                AtmosConfigFields.SleepThreshold,
                "config-sleep-threshold",
                _config.SleepThreshold,
                inspecting,
                static (config, value) => config.SleepThreshold = value);

            ConfigFloatSlider(
                AtmosConfigFields.SleepEpsilon,
                "config-sleep-epsilon",
                _config.SleepEpsilon,
                0f,
                100f,
                ConfigDomain.NonNegative,
                inspecting,
                static (config, value) => config.SleepEpsilon = value);

            ConfigFloatInput(
                AtmosConfigFields.ThermalConductance,
                "config-thermal-conductance",
                _config.ThermalConductance,
                ConfigDomain.NonNegative,
                inspecting,
                static (config, value) => config.ThermalConductance = value);

            ConfigFloatSlider(
                AtmosConfigFields.CondensationRateFactor,
                "config-condensation-rate-factor",
                _config.CondensationRateFactor,
                0f,
                1f,
                ConfigDomain.UnitInterval,
                inspecting,
                static (config, value) => config.CondensationRateFactor = value);

            ConfigFloatSlider(
                AtmosConfigFields.MaxPressureTransferFractionPerNeighbor,
                "config-max-pressure-transfer-fraction",
                _config.MaxPressureTransferFractionPerNeighbor,
                0f,
                1f,
                ConfigDomain.UnitInterval,
                inspecting,
                static (config, value) => config.MaxPressureTransferFractionPerNeighbor = value);

            ConfigFloatInput(
                AtmosConfigFields.AccumulatorWakeThreshold,
                "config-accumulator-wake-threshold",
                _config.AccumulatorWakeThreshold,
                ConfigDomain.NonNegative,
                inspecting,
                static (config, value) => config.AccumulatorWakeThreshold = value,
                QuantityFormat.KilopascalsPerPascal);

            ConfigIntInput(
                AtmosConfigFields.AccumulatorMaxAliveTicks,
                "config-accumulator-max-alive-ticks",
                _config.AccumulatorMaxAliveTicks,
                inspecting,
                static (config, value) => config.AccumulatorMaxAliveTicks = value);
        }

        ImGui.Spacing();
        if (ImGui.CollapsingHeader("Gas Registry", ImGuiTreeNodeFlags.DefaultOpen))
            RenderProjectGasControls(inspecting);
    }

    private void ResetConfigurationValues()
    {
        if (_config == null)
            return;

        ClearConfigurationDrafts();
        var previous = new AtmosConfig();
        CopyScalarSettings(_config, previous);
        CopyScalarSettings(new AtmosConfig(), _config);

        // One SetAtmosConfig call, so the Timeline records the whole reset as a single operation.
        if (TryApplyConfiguration(out string? error))
        {
            _configurationFeedback = new FeedbackMessage("Reset every AtmosConfig setting to the library defaults.", false);
            return;
        }

        CopyScalarSettings(previous, _config);
        _configurationFeedback = new FeedbackMessage($"Could not reset the settings: {error}", true);
    }

    private static void DrawConfigFieldLabel(AtmosConfigFieldInfo field)
    {
        ImGui.TextUnformatted(field.LabelWithUnit);
        ImGuiExtensions.QuestionTooltip(field.Tooltip);
        ImGui.SetNextItemWidth(-1f);
    }

    /// <summary>
    ///     Draws a typed numeric field that holds its edit as a draft and applies it once the field is deactivated.
    /// </summary>
    /// <param name="field">Shared label, unit, and tooltip for the setting.</param>
    /// <param name="id">ImGui ID and draft key, unique within the Configuration panel.</param>
    /// <param name="current">The value currently in <see cref="_config" />.</param>
    /// <param name="domain">Values the snapshot keeps unchanged; anything else is rejected with a message.</param>
    /// <param name="readOnly">Shows <paramref name="current" /> without allowing edits.</param>
    /// <param name="assign">Writes an accepted value into the editable configuration.</param>
    /// <param name="displayScale">Factor from the stored value to the value shown in the field.</param>
    private void ConfigFloatInput(
        AtmosConfigFieldInfo field,
        string id,
        float current,
        ConfigDomain domain,
        bool readOnly,
        Action<AtmosConfig, float> assign,
        float displayScale = 1f)
    {
        DrawConfigFieldLabel(field);
        float shown = current * displayScale;
        if (readOnly)
        {
            ImGui.InputFloat($"##{id}", ref shown, 0f, 0f, "%g", ImGuiInputTextFlags.ReadOnly);
            return;
        }

        float value = _configFloatDrafts.GetValueOrDefault(id, shown);
        if (ImGui.InputFloat($"##{id}", ref value, 0f, 0f, "%g"))
            SetConfigDraft(_configFloatDrafts, id, value);

        if (ImGui.IsItemDeactivatedAfterEdit())
            CommitConfigFloat(field, id, current, domain, assign, displayScale);

        ImGuiExtensions.Feedback(_configFieldErrors.GetValueOrDefault(id), true);
    }

    /// <summary>
    ///     Draws a slider for a bounded setting. The value is held as a draft while dragging and applied on release.
    /// </summary>
    /// <remarks>
    ///     Ctrl+click typing can still enter a value outside the slider's range; <paramref name="domain" /> decides
    ///     whether that value is accepted.
    /// </remarks>
    private void ConfigFloatSlider(
        AtmosConfigFieldInfo field,
        string id,
        float current,
        float minimum,
        float maximum,
        ConfigDomain domain,
        bool readOnly,
        Action<AtmosConfig, float> assign)
    {
        DrawConfigFieldLabel(field);
        if (readOnly)
        {
            ImGui.InputFloat($"##{id}", ref current, 0f, 0f, "%.3f", ImGuiInputTextFlags.ReadOnly);
            return;
        }

        float value = _configFloatDrafts.GetValueOrDefault(id, current);
        if (ImGui.SliderFloat($"##{id}", ref value, minimum, maximum, "%.3f"))
            SetConfigDraft(_configFloatDrafts, id, value);

        if (ImGui.IsItemDeactivatedAfterEdit())
            CommitConfigFloat(field, id, current, domain, assign);

        ImGuiExtensions.Feedback(_configFieldErrors.GetValueOrDefault(id), true);
    }

    private void ConfigIntInput(
        AtmosConfigFieldInfo field,
        string id,
        int current,
        bool readOnly,
        Action<AtmosConfig, int> assign)
    {
        DrawConfigFieldLabel(field);
        if (readOnly)
        {
            ImGui.InputInt($"##{id}", ref current, 0, 0, ImGuiInputTextFlags.ReadOnly);
            return;
        }

        // No step buttons: they are separate items and would make "deactivated after edit" unreliable.
        int value = _configIntDrafts.GetValueOrDefault(id, current);
        if (ImGui.InputInt($"##{id}", ref value, 0, 0))
            SetConfigDraft(_configIntDrafts, id, value);

        if (ImGui.IsItemDeactivatedAfterEdit() && _configIntDrafts.Remove(id, out int draft))
        {
            if (draft < 0)
            {
                _configIntDrafts[id] = draft;
                _configFieldErrors[id] = $"{field.Label} must be 0 or more. The previous value is still applied.";
            }
            else if (draft != current)
            {
                assign(_config!, draft);
                if (!TryCommitConfigurationField(field, FormatConfigValue(draft, field.Unit)))
                    assign(_config!, current);
            }
        }

        ImGuiExtensions.Feedback(_configFieldErrors.GetValueOrDefault(id), true);
    }

    private void SetConfigDraft<T>(Dictionary<string, T> drafts, string id, T value)
    {
        drafts[id] = value;
        _configFieldErrors.Remove(id);
        _configurationFeedback = null;
    }

    private void CommitConfigFloat(
        AtmosConfigFieldInfo field,
        string id,
        float current,
        ConfigDomain domain,
        Action<AtmosConfig, float> assign,
        float displayScale = 1f)
    {
        if (!_configFloatDrafts.TryGetValue(id, out float draft))
            return;

        string? error = ValidateConfigValue(field, draft, domain);
        if (error != null)
        {
            // Keep the rejected draft on screen so it can be corrected rather than retyped.
            _configFieldErrors[id] = error;
            return;
        }

        _configFloatDrafts.Remove(id);
        if (draft.Equals(current * displayScale))
            return;

        assign(_config!, draft / displayScale);
        if (!TryCommitConfigurationField(field, FormatConfigValue(draft, field.Unit)))
            assign(_config!, current);
    }

    private bool TryCommitConfigurationField(AtmosConfigFieldInfo field, string formattedValue)
    {
        if (TryApplyConfiguration(out string? error))
        {
            _configurationFeedback = new FeedbackMessage($"{field.Label} set to {formattedValue}.", false);
            return true;
        }

        _configurationFeedback = new FeedbackMessage($"Could not apply {field.Label}: {error}", true);
        return false;
    }

    private static string? ValidateConfigValue(AtmosConfigFieldInfo field, float value, ConfigDomain domain)
    {
        string unit = field.Unit.Length == 0 ? string.Empty : " " + field.Unit;
        return domain switch
        {
            ConfigDomain.Positive when !float.IsFinite(value) || value <= 0f =>
                $"{field.Label} must be finite and above 0{unit}. The previous value is still applied.",
            ConfigDomain.NonNegative when !float.IsFinite(value) || value < 0f =>
                $"{field.Label} must be finite and at least 0{unit}. The previous value is still applied.",
            ConfigDomain.UnitInterval when !float.IsFinite(value) || value < 0f || value > 1f =>
                $"{field.Label} must be between 0 and 1. The previous value is still applied.",
            _ => null
        };
    }

    private static string FormatConfigValue(double value, string unit)
    {
        string number = value.ToString("G6", CultureInfo.InvariantCulture);
        return unit.Length == 0 ? number : $"{number} {unit}";
    }

    private void RenderToolsPanel()
    {
        if (!_showToolsPanel || _simulation == null || _config == null)
            return;

        using var window = ImGuiExtensions.BeginWindow(
            "Tools##tools",
            ref _showToolsPanel,
            new Vector2(10, 340),
            new Vector2(300, 550));

        if (!window.IsVisible)
            return;

        bool inspecting = DrawInspectionNotice();
        ImGui.TextDisabled($"{(inspecting ? "Viewing" : "Editing")} {GetSimulationName(_simulation.Id)}");

        if (ImGui.CollapsingHeader("Chunks", ImGuiTreeNodeFlags.DefaultOpen))
            RenderProjectChunkControls(inspecting);

        if (ImGui.CollapsingHeader("Voxel Tools", ImGuiTreeNodeFlags.DefaultOpen))
            RenderVoxelTools(inspecting);

        if (ImGui.CollapsingHeader("Inject Gas"))
            RenderProjectInjectionControls(inspecting);
    }

    private void RenderVoxelTools(bool inspecting)
    {
        ImGui.SeparatorText("Viewport tool");
        DrawVoxelToolButton("Select / Box", VoxelEditTool.Select);
        ImGui.SameLine();
        DrawVoxelToolButton("Paint Room", VoxelEditTool.PaintClassification);
        DrawVoxelToolButton("Paint Gas", VoxelEditTool.PaintGas);
        ImGui.SameLine();
        DrawVoxelToolButton("Erase Gas", VoxelEditTool.EraseGas);

        bool draftChanged = false;
        ImGui.BeginDisabled(inspecting);
        ImGui.SetNextItemWidth(NumericInputWidth);
        draftChanged |= ImGui.InputInt("Classification##voxel-edit", ref _voxelClassificationDraft);
        ImGui.EndDisabled();
        ImGui.TextDisabled(ClassificationFormatHelp);

        ImGui.BeginDisabled(inspecting);
        if (_config!.GasRegistry.Count > 0)
        {
            _injectionGasId = Math.Clamp(_injectionGasId, 0, _config.GasRegistry.Count - 1);
            if (ImGui.BeginCombo("Gas##voxel-edit", FormatGas(_injectionGasId)))
            {
                for (int gasId = 0; gasId < _config.GasRegistry.Count; gasId++)
                {
                    bool gasSelected = gasId == _injectionGasId;
                    if (ImGui.Selectable(FormatGas(gasId), gasSelected))
                    {
                        _injectionGasId = gasId;
                        draftChanged = true;
                    }
                }

                ImGui.EndCombo();
            }
        }

        ImGui.SetNextItemWidth(NumericInputWidth);
        draftChanged |= ImGui.InputFloat("Amount (mol)##voxel-edit", ref _injectionMoles);
        ImGui.SetNextItemWidth(NumericInputWidth);
        draftChanged |= ImGui.InputFloat("Temperature (K)##voxel-edit", ref _injectionTemperature);
        ImGui.EndDisabled();

        if (draftChanged)
            _voxelFeedback = null;

        ImGui.SeparatorText("Selection");
        if (!_selectedCell.HasValue)
        {
            ImGui.TextDisabled("No voxels selected.");
        }
        else
        {
            ImGui.Text($"{_selectedCells.Count} voxel{(_selectedCells.Count == 1 ? string.Empty : "s")} selected");
            DrawCellSelectionDetails(_selectedCell.Value);

            ImGui.BeginDisabled(inspecting);
            if (ImGui.Button("Set Classification"))
                ApplyClassification(_selectedCells, _voxelClassificationDraft);

            ImGui.SameLine();
            if (ImGui.Button("Inject Gas##selected-voxels"))
                ApplyGasInjection(_selectedCells);

            if (ImGui.Button("Clear Gas"))
                ApplyClearGas(_selectedCells);

            ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.Button("Clear Selection"))
                ClearVoxelSelection();
        }

        DrawFeedback(_voxelFeedback);
    }

    private void DrawVoxelToolButton(string label, VoxelEditTool tool)
    {
        bool active = _voxelEditTool == tool;
        if (ImGui.RadioButton(label, active))
            _voxelEditTool = tool;
    }

    private void RenderSliceControls()
    {
        if (_drawData == null || _drawData.Chunks.Count == 0)
        {
            ImGui.TextDisabled("No chunks available.");
            return;
        }

        if (_replayTimeline?.IsInspecting == true &&
            _selectedSliceChunkPosition.HasValue &&
            !_drawData.Chunks.ContainsKey(_selectedSliceChunkPosition.Value))
        {
            ImGui.TextDisabled(
                $"Selected chunk {FormatChunkPosition(_selectedSliceChunkPosition.Value)} does not exist at this timeline position.");

            return;
        }

        if (!_selectedSliceChunkPosition.HasValue ||
            !_drawData.Chunks.ContainsKey(_selectedSliceChunkPosition.Value))
            _selectedSliceChunkPosition = _drawData.Chunks.Keys.First();

        string selectedChunkLabel = FormatChunkPosition(_selectedSliceChunkPosition.Value);
        bool followsFocus = _focusedChunk.HasValue;
        if (_focusedChunk is { } focusedChunk)
        {
            _selectedSliceChunkPosition = focusedChunk.Position;
            selectedChunkLabel = FormatChunkPosition(_selectedSliceChunkPosition.Value);
        }
        else if (ImGui.BeginCombo("Chunk##slice-chunk", selectedChunkLabel))
        {
            foreach (var chunk in _drawData.Chunks.Values)
            {
                bool selected = chunk.ChunkPosition == _selectedSliceChunkPosition.Value;
                string label = FormatChunkPosition(chunk.ChunkPosition);

                if (ImGui.Selectable(label, selected))
                    _selectedSliceChunkPosition = chunk.ChunkPosition;

                if (selected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }

        int axis = (int)_currentSliceAxis;
        if (ImGui.Combo(
                "Axis##slice-axis",
                ref axis,
                new[]
                {
                    "X (YZ plane)",
                    "Y (XZ plane)",
                    "Z (XY plane)"
                },
                3))
            _currentSliceAxis = (SliceAxis)axis;

        var selectedChunk = _drawData.Chunks[_selectedSliceChunkPosition.Value];
        int maxSliceIndex = Math.Max(
            SimulationFrameBuilder.GetSliceAxisLength(selectedChunk.Dimensions, _currentSliceAxis) - 1,
            0);

        _currentSliceIndex = Math.Clamp(_currentSliceIndex, 0, maxSliceIndex);

        ImGui.SliderInt("Slice##slice-index", ref _currentSliceIndex, 0, maxSliceIndex);

        using var readout = ImGuiExtensions.BeginDefinitionTable("slice-readout");
        if (!readout.IsVisible)
            return;

        ImGuiExtensions.DefinitionRow("Showing", $"{_currentSliceAxis} = {_currentSliceIndex} of 0-{maxSliceIndex}");
        ImGuiExtensions.DefinitionRow(
            "Chunk",
            followsFocus ? $"{selectedChunkLabel}, from 3D focus" : selectedChunkLabel);
    }

    private void RenderVisualizationLegend()
    {
        if (_drawData == null)
            return;

        var legend = _drawData.Visualization.Legend;

        // Pressure legends arrive in pascals; the viewer shows and edits them in kilopascals.
        bool inKilopascals = legend.Units == "Pa";
        float scale = inKilopascals ? QuantityFormat.KilopascalsPerPascal : 1f;
        string units = inKilopascals ? "kPa" : legend.Units;
        ImGui.TextUnformatted(
            string.IsNullOrWhiteSpace(units)
                ? legend.Title
                : $"{legend.Title} ({units})");

        if (legend.Kind == VisualizationLegendKind.Gradient && legend.Entries.Count > 0)
        {
            RenderGradientLegend(legend, units, scale);

            using (var readout = ImGuiExtensions.BeginDefinitionTable("legend-readout"))
            {
                if (readout.IsVisible)
                {
                    var range = legend.Range;
                    ImGuiExtensions.DefinitionRow("Range", FormatLegendRangeMode(units, scale));
                    ImGuiExtensions.DefinitionRow("Minimum", QuantityFormat.Format(range.Minimum * scale, units));
                    ImGuiExtensions.DefinitionRow("Maximum", QuantityFormat.Format(range.Maximum * scale, units));
                }
            }

            RenderLegendBoundsControls(legend.Range, scale);

            ImGui.Checkbox("Custom resolution##legend-resolution-enabled", ref _legendResolutionEnabled);
            ImGui.SameLine();
            if (_legendResolutionEnabled)
            {
                ImGui.SetNextItemWidth(-1f);
                ImGui.SliderInt("##legend-resolution", ref _legendResolution, 1, 256, "%d levels");
            }
            else
            {
                ImGui.TextDisabled($"{DefaultLegendResolution} levels (default)");
            }

            return;
        }

        float swatchSize = ImGui.GetTextLineHeight();
        foreach (var entry in legend.Entries)
        {
            var color = entry.Color;
            ImGuiExtensions.ColorSwatch(new Vector4(color.R, color.G, color.B, color.A), swatchSize);
            ImGui.SameLine();
            ImGui.TextUnformatted(entry.Label);
        }
    }

    private string FormatLegendRangeMode(string units, float scale)
    {
        if (!_legendAutomaticBounds)
            return "Fixed";

        return _legendAutomaticRangeOffset > 0f
            ? $"Automatic ± {QuantityFormat.Format(_legendAutomaticRangeOffset * scale, units)}, recomputed each frame"
            : "Automatic, recomputed each frame";
    }

    private void RenderLegendBoundsControls(VisualizationRange currentRange, float scale)
    {
        bool automaticBounds = _legendAutomaticBounds;
        if (ImGui.Checkbox("Automatic bounds##legend-automatic-bounds", ref automaticBounds))
        {
            _legendAutomaticBounds = automaticBounds;
            if (!automaticBounds)
            {
                _legendMinimum = currentRange.Minimum;
                _legendMaximum = currentRange.Maximum;
            }

            _legendRangeRevision++;
        }

        if (_legendAutomaticBounds)
        {
            ImGui.SetNextItemWidth(NumericInputWidth);
            float offset = _legendAutomaticRangeOffset * scale;
            if (ImGui.InputFloat("± Offset##legend-range-offset", ref offset))
            {
                _legendAutomaticRangeOffset = Math.Max(offset / scale, 0f);
                _legendRangeRevision++;
            }

            ImGuiExtensions.QuestionTooltip("Expands the automatic minimum and maximum by this amount.");

            return;
        }

        ImGui.SetNextItemWidth(NumericInputWidth);
        float minimum = _legendMinimum * scale;
        if (ImGui.InputFloat("Min##legend-minimum", ref minimum))
        {
            _legendMinimum = Math.Min(minimum / scale, _legendMaximum);
            _legendRangeRevision++;
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(NumericInputWidth);
        float maximum = _legendMaximum * scale;
        if (ImGui.InputFloat("Max##legend-maximum", ref maximum))
        {
            _legendMaximum = Math.Max(maximum / scale, _legendMinimum);
            _legendRangeRevision++;
        }

        if (_legendMaximum <= _legendMinimum)
        {
            _legendMaximum = _legendMinimum + 1f;
            _legendRangeRevision++;
        }
    }

    private static void RenderGradientLegend(VisualizationLegend legend, string units, float scale)
    {
        const int segmentCount = 256;
        const float barHeight = 18f;
        const float labelHeight = 18f;
        float width = Math.Max(ImGui.GetContentRegionAvail().X, 160f);
        var topLeft = ImGui.GetCursorScreenPos();
        var bottomRight = topLeft + new Vector2(width, barHeight);
        var drawList = ImGui.GetWindowDrawList();
        for (int segment = 0; segment < segmentCount; segment++)
        {
            float start = segment / (float)segmentCount;
            float end = (segment + 1) / (float)segmentCount;
            var color = InterpolateLegendColor(legend, QuantizeLegendPosition(start, legend.Range.Resolution));
            drawList.AddRectFilled(
                new Vector2(topLeft.X + width * start, topLeft.Y),
                new Vector2(topLeft.X + width * end + 1f, bottomRight.Y),
                ImGui.GetColorU32(new Vector4(color.R, color.G, color.B, color.A)));
        }

        drawList.AddRect(topLeft, bottomRight, ImGui.ColorConvertFloat4ToU32(ViewerTheme.StructuralLine));
        ImGui.Dummy(new Vector2(width, barHeight + labelHeight));

        // The end labels always draw; interior labels are dropped when they would collide with a neighbour, which
        // happens once units are appended on a narrow panel.
        IReadOnlyList<VisualizationLegendEntry> entries = legend.Entries;
        int first = -1;
        int last = -1;
        for (int index = 0; index < entries.Count; index++)
        {
            if (!entries[index].Value.HasValue)
                continue;

            if (first < 0)
                first = index;

            last = index;
        }

        if (first < 0)
            return;

        const float labelGap = 8f;
        uint textColor = ImGui.GetColorU32(ImGuiCol.Text);
        float labelY = bottomRight.Y + 1f;

        string firstLabel = GetGradientLabel(entries[first], units, scale);
        float firstX = GetGradientLabelX(entries[first], firstLabel, legend, topLeft.X, bottomRight.X, width);
        float firstEnd = firstX + ImGui.CalcTextSize(firstLabel).X;
        drawList.AddText(new Vector2(firstX, labelY), textColor, firstLabel);
        if (last == first)
            return;

        string lastLabel = GetGradientLabel(entries[last], units, scale);
        float lastX = GetGradientLabelX(entries[last], lastLabel, legend, topLeft.X, bottomRight.X, width);
        if (lastX < firstEnd + labelGap)
            return;

        drawList.AddText(new Vector2(lastX, labelY), textColor, lastLabel);

        float previousEnd = firstEnd;
        for (int index = first + 1; index < last; index++)
        {
            if (!entries[index].Value.HasValue)
                continue;

            string label = GetGradientLabel(entries[index], units, scale);
            float x = GetGradientLabelX(entries[index], label, legend, topLeft.X, bottomRight.X, width);
            float end = x + ImGui.CalcTextSize(label).X;
            if (x < previousEnd + labelGap || end + labelGap > lastX)
                continue;

            drawList.AddText(new Vector2(x, labelY), textColor, label);
            previousEnd = end;
        }
    }

    private static string GetGradientLabel(VisualizationLegendEntry entry, string units, float scale)
    {
        if (scale != 1f && entry.Value.HasValue)
            return $"{(entry.Value.Value * scale).ToString("G6", CultureInfo.InvariantCulture)} {units}";

        // Built-in legends differ: temperature labels already carry "K", pressure labels are bare numbers.
        if (string.IsNullOrWhiteSpace(units) || entry.Label.EndsWith(units, StringComparison.Ordinal))
            return entry.Label;

        return $"{entry.Label} {units}";
    }

    private static float GetGradientLabelX(
        VisualizationLegendEntry entry,
        string label,
        VisualizationLegend legend,
        float left,
        float right,
        float width)
    {
        float position = NormalizeLegendValue(entry.Value ?? 0f, legend);
        float textWidth = ImGui.CalcTextSize(label).X;
        return Math.Clamp(left + width * position - textWidth * position, left, Math.Max(left, right - textWidth));
    }

    private static ColorRgba InterpolateLegendColor(VisualizationLegend legend, float position)
    {
        IReadOnlyList<VisualizationLegendEntry> entries = legend.Entries;
        if (entries.Count == 1)
            return entries[0].Color;

        int lower = 0;
        while (lower < entries.Count - 2 &&
               NormalizeLegendValue(entries[lower + 1].Value ?? 0f, legend) < position)
            lower++;

        float lowerPosition = NormalizeLegendValue(entries[lower].Value ?? 0f, legend);
        float upperPosition = NormalizeLegendValue(entries[lower + 1].Value ?? 1f, legend);
        float amount = upperPosition <= lowerPosition
            ? 0.5f
            : (position - lowerPosition) / (upperPosition - lowerPosition);

        return ColorRgba.Lerp(entries[lower].Color, entries[lower + 1].Color, amount);
    }

    private static float NormalizeLegendValue(float value, VisualizationLegend legend)
    {
        float minimum = legend.Entries[0].Value ?? 0f;
        float maximum = legend.Entries[^1].Value ?? minimum + 1f;
        return maximum <= minimum ? 0.5f : Math.Clamp((value - minimum) / (maximum - minimum), 0f, 1f);
    }

    private static float QuantizeLegendPosition(float position, int resolution)
    {
        resolution = Math.Max(resolution, 1);
        return resolution == 1 ? 0.5f : MathF.Round(position * (resolution - 1)) / (resolution - 1);
    }

    private int GetLegendResolution()
    {
        return _legendResolutionEnabled ? _legendResolution : DefaultLegendResolution;
    }

    private void RenderChunkFocusControls()
    {
        string currentLabel = _focusedChunk.HasValue
            ? FormatChunkPosition(_focusedChunk.Value.Position)
            : "All chunks";

        if (ImGui.BeginCombo("Focus##chunk-focus", currentLabel))
        {
            bool allSelected = !_focusedChunk.HasValue;
            if (ImGui.Selectable("All chunks", allSelected))
                SetFocusedChunk(null);

            if (allSelected)
                ImGui.SetItemDefaultFocus();

            if (_drawData != null)
            {
                foreach (var chunk in _drawData.Chunks.Values)
                {
                    bool selected = _focusedChunk == chunk.Identity;
                    if (ImGui.Selectable(FormatChunkPosition(chunk.ChunkPosition), selected))
                        SetFocusedChunk(chunk.Identity);

                    if (selected)
                        ImGui.SetItemDefaultFocus();
                }
            }

            ImGui.EndCombo();
        }

        if (ImGui.Button("Frame View"))
        {
            if (_focusedChunk.HasValue &&
                _drawData != null &&
                _drawData.Chunks.TryGetValue(_focusedChunk.Value.Position, out var focused))
                FocusCameraOnChunk(focused);
            else
                FocusCameraOnScene();
        }

        if (_focusedChunk.HasValue)
        {
            ImGui.SameLine();
            if (ImGui.Button("Show All Chunks"))
                SetFocusedChunk(null);
        }
    }

    private void RenderSliceCellTooltip()
    {
        if (_sliceViewport is not { IsHovered: true } || !_hoveredSliceCell.HasValue)
            return;

        ImGui.BeginTooltip();
        ImGui.Text("2D Slice Cell");
        ImGui.Separator();
        DrawCellSelectionDetails(_hoveredSliceCell.Value.Address, _hoveredSliceCell.Value.U, _hoveredSliceCell.Value.V);
        ImGui.EndTooltip();
    }

    private void DrawCellSelectionDetails(VoxelAddress address, int? sliceU = null, int? sliceV = null)
    {
        // The ID must not depend on the voxel: a new table ID restarts column auto-fit, and a hover tooltip that
        // moves between voxels every frame would keep showing a half-measured table. The Tools panel and the
        // tooltips are separate windows, so their table IDs already differ.
        ImGui.PushID("voxel-details");

        bool exists = _drawData != null && _drawData.TryResolve(address, out _);
        AtmosVoxelSnapshot details = default;
        bool hasDetails = exists && TryGetVoxelDetails(address, out details);
        var summary = hasDetails ? GetGasSummary(details.Gases) : default;

        using (var table = ImGuiExtensions.BeginDefinitionTable("voxel-details"))
        {
            if (table.IsVisible)
            {
                ImGuiExtensions.DefinitionRow("Chunk", FormatChunkPosition(address.Chunk.Position));
                ImGuiExtensions.DefinitionRow("Cell", FormatCellCoordinates(address));
                if (sliceU.HasValue && sliceV.HasValue)
                    ImGuiExtensions.DefinitionRow("Slice UV", $"({sliceU.Value}, {sliceV.Value})");

                if (hasDetails)
                {
                    ImGuiExtensions.DefinitionRow("Temperature", QuantityFormat.Temperature(details.Temperature));
                    ImGuiExtensions.DefinitionRow("Pressure", QuantityFormat.Pressure(details.Pressure));
                    ImGuiExtensions.DefinitionRow(
                        "Total amount",
                        summary.InvalidCount > 0
                            ? $"{QuantityFormat.Amount(summary.TotalMoles)}, excluding invalid amounts"
                            : QuantityFormat.Amount(summary.TotalMoles));

                    ImGuiExtensions.DefinitionRow("Primary gas", FormatGas(summary.PrimaryGasId));
                    ImGuiExtensions.DefinitionRow("Room", FormatRoomId(details.RoomId));
                }
            }
        }

        if (!exists)
            ImGui.TextDisabled("Selected voxel/chunk does not exist at this timeline position.");
        else if (!hasDetails)
            ImGui.TextDisabled("Details are unavailable for this presented revision.");
        else
            DrawGasMakeupBar(details.Gases, summary);

        ImGui.PopID();
    }

    private void DrawGasMakeupBar(
        IReadOnlyList<VoxelGasSnapshot> gases,
        GasSummary summary)
    {
        ImGui.TextUnformatted("Gas makeup");
        ImGui.PushID("gas-makeup");
        Vector2 size = new(Math.Max(ImGui.GetContentRegionAvail().X, 1f), ImGui.GetFrameHeight());
        var minimum = ImGui.GetCursorScreenPos();
        var maximum = minimum + size;
        ImGui.Dummy(size);
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(minimum, maximum, ImGui.ColorConvertFloat4ToU32(ViewerTheme.RecessedSurface));

        float totalMoles = summary.TotalMoles;
        float cursor = minimum.X;
        if (totalMoles > 0f)
        {
            foreach (var gas in gases)
            {
                if (!IsValidGasAmount(gas.Moles) || gas.Moles == 0f)
                    continue;

                float fraction = gas.Moles / totalMoles;
                float right = Math.Min(maximum.X, cursor + size.X * fraction);
                draw.AddRectFilled(
                    new Vector2(cursor, minimum.Y),
                    new Vector2(right, maximum.Y),
                    ImGui.ColorConvertFloat4ToU32(GetGasColor(gas.GasId)));

                cursor = right;
            }
        }

        draw.AddRect(minimum, maximum, ImGui.ColorConvertFloat4ToU32(ViewerTheme.StructuralLine));
        string overlay = totalMoles > 0f
            ? summary.GasCount == 1 ? "1 gas" : $"{summary.GasCount} gases"
            : summary.InvalidCount > 0
                ? "No valid amounts"
                : "Vacuum";

        var textSize = ImGui.CalcTextSize(overlay);
        draw.AddText(
            new Vector2(minimum.X + (size.X - textSize.X) * 0.5f, minimum.Y + (size.Y - textSize.Y) * 0.5f),
            ImGui.ColorConvertFloat4ToU32(ViewerTheme.PrimaryText),
            overlay);

        if (summary.GasCount + summary.InvalidCount > 0)
            DrawGasMakeupTable(gases, totalMoles);

        ImGui.PopID();
    }

    /// <summary>
    ///     Lists every gas present at the voxel so the composition reads without colour or hovering. Invalid amounts
    ///     (non-finite or negative) are listed and flagged rather than dropped, since they are left out of the bar.
    /// </summary>
    private void DrawGasMakeupTable(IReadOnlyList<VoxelGasSnapshot> gases, float totalMoles)
    {
        _gasMakeupRows.Clear();
        foreach (var gas in gases)
        {
            if (!IsValidGasAmount(gas.Moles) || gas.Moles != 0f)
                _gasMakeupRows.Add(gas);
        }

        _gasMakeupRows.Sort(static (left, right) => left.GasId.CompareTo(right.GasId));

        using var table = ImGuiExtensions.BeginTable("gas-makeup-table", 4, ImGuiExtensions.ReadoutTableFlags);
        if (!table.IsVisible)
            return;

        float swatchSize = ImGui.GetTextLineHeight();
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, swatchSize);
        ImGui.TableSetupColumn("Gas", ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn("Amount", ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn("Share", ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableHeadersRow();

        foreach (var gas in _gasMakeupRows)
        {
            bool valid = IsValidGasAmount(gas.Moles);
            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            ImGuiExtensions.ColorSwatch(GetGasColor(gas.GasId), swatchSize);

            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(FormatGas(gas.GasId));

            ImGui.TableSetColumnIndex(2);
            if (!valid)
                ImGui.PushStyleColor(ImGuiCol.Text, ViewerTheme.Error);

            ImGuiExtensions.TextRightAligned(QuantityFormat.Amount(gas.Moles));
            if (!valid)
                ImGui.PopStyleColor();

            ImGui.TableSetColumnIndex(3);
            if (valid && totalMoles > 0f)
                ImGuiExtensions.TextRightAligned(QuantityFormat.Percent(gas.Moles / totalMoles));
            else
                ImGui.TextDisabled("invalid, not in bar");
        }
    }

    private static Vector4 GetGasColor(int gasId)
    {
        return ViewerTheme.GasPalette[Math.Abs(gasId) % ViewerTheme.GasPalette.Length];
    }

    private static bool IsValidGasAmount(float moles)
    {
        return float.IsFinite(moles) && moles >= 0f;
    }

    private static string FormatRoomId(int roomId)
    {
        return roomId switch
        {
            VoxelClassification.RoomSolid => $"Solid ({roomId})",
            VoxelClassification.RoomVoid => $"Void ({roomId})",
            VoxelClassification.RoomUnassigned => $"Unassigned ({roomId})",
            _ => roomId.ToString(CultureInfo.InvariantCulture)
        };
    }

    private static GasSummary GetGasSummary(IReadOnlyList<VoxelGasSnapshot> gases)
    {
        float totalMoles = 0f;
        int primaryGasId = -1;
        int gasCount = 0;
        int invalidCount = 0;
        float maximumMoles = 0f;
        foreach (var gas in gases)
        {
            float moles = gas.Moles;
            if (!IsValidGasAmount(moles))
            {
                invalidCount++;
                continue;
            }

            if (moles > 0f)
            {
                totalMoles += moles;
                gasCount++;
            }

            if (moles > maximumMoles ||
                moles == maximumMoles && moles > 0f && (primaryGasId < 0 || gas.GasId < primaryGasId))
            {
                maximumMoles = moles;
                primaryGasId = gas.GasId;
            }
        }

        return new GasSummary(totalMoles, primaryGasId, gasCount, invalidCount);
    }

    private bool TryGetVoxelDetails(VoxelAddress address, out AtmosVoxelSnapshot snapshot)
    {
        if (!_snapshotCache.TryGetValue(address.Chunk.Position, out var presented) ||
            presented.Version.Generation != address.Chunk.Generation)
        {
            snapshot = default;
            return false;
        }

        var presentedVersion = presented.Version;
        if (_voxelDetailCache.TryGetValue(address, out var cached) &&
            cached.PresentedVersion == presentedVersion)
        {
            snapshot = cached.Snapshot;
            return cached.IsAvailable;
        }

        if (_simulation == null)
        {
            snapshot = default;
            return false;
        }

        try
        {
            bool available = _simulation.TryGetVoxelSnapshot(
                new AtmosChunkHandle(address.Chunk.Position),
                address.LocalIndex,
                presentedVersion,
                out snapshot);

            CacheVoxelDetail(address, presentedVersion, available, snapshot);
            return available;
        }
        catch (Exception exception) when (
            exception is KeyNotFoundException or ArgumentOutOfRangeException)
        {
            snapshot = default;
            CacheVoxelDetail(address, presentedVersion, false, snapshot);
            return false;
        }
    }

    private void CacheVoxelDetail(
        VoxelAddress address,
        AtmosChunkVersion presentedVersion,
        bool isAvailable,
        AtmosVoxelSnapshot snapshot)
    {
        if (_voxelDetailCache.Count >= 16 && !_voxelDetailCache.ContainsKey(address))
            _voxelDetailCache.Clear();

        _voxelDetailCache[address] = new VoxelDetailCacheEntry(presentedVersion, isAvailable, snapshot);
    }

    private string FormatCellCoordinates(VoxelAddress address)
    {
        if (_drawData != null &&
            _drawData.Chunks.TryGetValue(address.Chunk.Position, out var chunk) &&
            chunk.Identity == address.Chunk &&
            address.LocalIndex < chunk.CellCount)
        {
            var coordinates = chunk.GetCoordinates(address.LocalIndex);
            return $"({coordinates.X}, {coordinates.Y}, {coordinates.Z})";
        }

        return $"index {address.LocalIndex}";
    }

    private static string FormatChunkPosition(Int3 position)
    {
        return $"({position.X}, {position.Y}, {position.Z})";
    }

    private string FormatGas(int gasId)
    {
        if (gasId < 0)
            return "none";

        if (_config != null && gasId < _config.GasRegistry.Count)
            return $"{_config.GasRegistry[gasId].Name} ({gasId})";

        return $"Gas {gasId}";
    }

    private void RenderSolutionDetails()
    {
        if (ImGui.CollapsingHeader("Simulation State", ImGuiTreeNodeFlags.DefaultOpen))
        {
            if (_simulation == null)
                return;

            if (!TryGetDetailChunk(out var snapshot, out string source))
            {
                ImGui.TextDisabled(
                    _liveChunkHandles.Count == 0
                        ? "No chunks in this simulation."
                        : "Waiting for a chunk snapshot.");

                return;
            }

            using var table = ImGuiExtensions.BeginDefinitionTable("SimulationState##solution");
            if (!table.IsVisible)
                return;

            var dimensions = snapshot.Dimensions;
            ImGuiExtensions.DefinitionRow("Chunk", FormatChunkPosition(snapshot.GridPosition));
            ImGuiExtensions.DefinitionRow("Source", source);
            ImGuiExtensions.DefinitionRow("Dimensions", $"{dimensions.X} × {dimensions.Y} × {dimensions.Z} voxels");
            ImGuiExtensions.DefinitionRow("Total voxels", (dimensions.X * dimensions.Y * dimensions.Z).ToString());
            ImGuiExtensions.DefinitionRow("Active voxels", snapshot.ActiveAirCount.ToString());
            ImGuiExtensions.DefinitionRow("Active gases", snapshot.ActiveGasCount.ToString());
            ImGuiExtensions.DefinitionRow("State", snapshot.IsAwake ? "Awake" : "Sleeping");
            ImGuiExtensions.DefinitionRow("Sleep timer", QuantityFormat.Ticks(snapshot.SleepTimer));

            // Zero entries are solid or empty voxels, so the average and minimum skip them.
            if (snapshot.TotalPressure is { Length: > 0 } pressures)
            {
                float maximum = pressures[0];
                double sum = 0d;
                int count = 0;
                foreach (float pressure in pressures)
                {
                    if (pressure > maximum || float.IsNaN(maximum))
                        maximum = pressure;

                    if (pressure > 0f)
                    {
                        sum += pressure;
                        count++;
                    }
                }

                ImGuiExtensions.DefinitionRow("Max pressure", QuantityFormat.Pressure(maximum));
                ImGuiExtensions.DefinitionRow(
                    "Avg pressure (non-zero voxels)",
                    QuantityFormat.Pressure(count == 0 ? 0d : sum / count));
            }
            else
            {
                ImGuiExtensions.DefinitionRow(
                    "Pressure",
                    "Not captured for the current visualization",
                    ViewerTheme.SecondaryText);
            }

            if (snapshot.Temperature is { Length: > 0 } temperatures)
            {
                float maximum = temperatures[0];
                float minimum = 0f;
                bool hasPositive = false;
                foreach (float temperature in temperatures)
                {
                    if (temperature > maximum || float.IsNaN(maximum))
                        maximum = temperature;

                    if (temperature > 0f && (!hasPositive || temperature < minimum))
                    {
                        minimum = temperature;
                        hasPositive = true;
                    }
                }

                ImGuiExtensions.DefinitionRow(
                    "Min temperature (non-zero voxels)",
                    QuantityFormat.Temperature(minimum));

                ImGuiExtensions.DefinitionRow("Max temperature", QuantityFormat.Temperature(maximum));
            }
            else
            {
                ImGuiExtensions.DefinitionRow(
                    "Temperature",
                    "Not captured for the current visualization",
                    ViewerTheme.SecondaryText);
            }
        }
    }

    /// <summary>
    ///     Per-voxel gas totals shared by the details table, the makeup bar, and the makeup table.
    /// </summary>
    /// <param name="TotalMoles">Sum of the valid, positive amounts; this is what the makeup bar divides.</param>
    /// <param name="PrimaryGasId">Gas with the largest valid amount, or -1 when there is none.</param>
    /// <param name="GasCount">Gases with a valid, positive amount.</param>
    /// <param name="InvalidCount">Gases whose amount is NaN, infinite, or negative.</param>
    private readonly record struct GasSummary(float TotalMoles, int PrimaryGasId, int GasCount, int InvalidCount);
}