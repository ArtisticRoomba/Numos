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

    private bool _requestOpenAboutModal;
    private bool _aboutModalOpen;

    private void RenderUi()
    {
        RenderMainDockspace();

        RenderMenuBar();
        RenderProgramSettingsPanel();
        RenderResolutionConfirmationModal();
        RenderPerformanceOverlay();

        if (_simulation == null)
        {
            RenderEmptyWorkspaceMessage();
            DrawCreateProjectModal();
            DrawCloseProjectModal();
            DrawAboutModal();
            return;
        }

        if (_show3DViewport)
        {
            _viewport?.Draw(
                "Simulation 3D##viewport",
                RenderSimulationScene,
                new Vector2(320, 40),
                new Vector2(660, 510));
        }

        if (_showSliceViewport)
        {
            _sliceViewport?.Draw(
                "Simulation Slice 2D##slice-viewport",
                RenderSimulationSliceScene,
                new Vector2(320, 560),
                new Vector2(660, 330));
            UpdateSlicePicking();
            RenderSliceCellTooltip();
        }
        else
        {
            _hoveredSliceCell = null;
            RebuildHighlights();
        }

        RenderSolutionPanel();
        RenderToolsPanel();
        RenderViewPanel();
        RenderConfigurationPanel();
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
        ImGui.DockSpace(dockspaceId, Vector2.Zero, ImGuiDockNodeFlags.PassthruCentralNode);

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
            "To get started, choose File > New Simulation.",
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

        ImGui.Text("© 2026 Numos contributors");

        ImGui.Spacing();

        ImGui.TextColored(
            new Vector4(0.2f, 0.6f, 1.0f, 1.0f),
            "License: MIT");

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
        ImGui.TextDisabled($"Source: {sourceReference}");
        ImGui.TextDisabled($"Commit: {shortCommit}");
        ImGui.TextDisabled($"Build: {configuration} / {targetFramework} / SDK {sdkVersion}");

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
        ImGui.TextDisabled("Simulation viewer and renderer, CoreSim maintainer");

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
        ImGui.TextUnformatted("Numos Simulation Viewer");
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
        var coreSimVersion = $"CoreSim v{CoreSimBuildInfo.PackageVersion}";
        var viewerVersion = $"Viewer v{ViewerBuildInfo.PackageVersion}";
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
                if (_simulation != null && ImGui.MenuItem("Close Simulation"))
                    RequestCloseProject();
                ImGui.Separator();
                if (ImGui.MenuItem("Exit", "Alt+F4"))
                    _requestExit = true;
                ImGui.EndMenu();
            }

            if (ImGui.BeginMenu("View"))
            {
                // The empty workspace does not render any of these panes. Keep their
                // entries in the menu for consistency, but prevent toggling them until
                // a simulation has been created.
                bool panesAvailable = _simulation != null && _config != null;
                if (!panesAvailable)
                    ImGui.BeginDisabled();

                ImGui.MenuItem("Solution", null, ref _showSolutionPanel);
                ImGui.MenuItem("Tools", null, ref _showToolsPanel);
                ImGui.MenuItem("View", null, ref _showViewPanel);
                ImGui.MenuItem("Configuration", null, ref _showConfigurationPanel);
                ImGui.MenuItem("3D Viewport", null, ref _show3DViewport);
                ImGui.MenuItem("2D Slice Viewport", null, ref _showSliceViewport);

                if (!panesAvailable)
                    ImGui.EndDisabled();

                ImGui.EndMenu();
            }

            if (ImGui.BeginMenu("Settings"))
            {
                if (ImGui.MenuItem("Configure..."))
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

            ImGui.EndMainMenuBar();
        }
    }

    private void RenderSolutionDiagnostics()
    {
        ImGui.Text($"FPS: {ImGui.GetIO().Framerate:F1}");
        ImGui.Text($"Simulation Ticks: {_simulation?.TickCount ?? 0}");

        if (ImGui.CollapsingHeader("Camera"))
        {
            ImGui.Text(
                $"Position: {_camera3D.Position.X:F1}, {_camera3D.Position.Y:F1}, {_camera3D.Position.Z:F1}");
            ImGui.Text($"Target: {_camera3D.Target.X:F1}, {_camera3D.Target.Y:F1}, {_camera3D.Target.Z:F1}");
            ImGui.Text($"Distance: {Vector3.Distance(_camera3D.Position, _camera3D.Target):F1}");
        }

        if (ImGui.CollapsingHeader("2D Slice"))
        {
            ImGui.Text($"Axis: {_currentSliceAxis}");
            ImGui.Text($"Slice Index: {_currentSliceIndex}");
            ImGui.Text($"Visible Cells: {(_sliceDrawData == null ? 0 : _sliceDrawData.Cells.Length)}");

            if (_hoveredSliceCell.HasValue)
                ImGui.Text($"Hovered Cell: {FormatCellCoordinates(_hoveredSliceCell.Value.Address)}");
            else
                ImGui.TextDisabled("Hovered Cell: none");
        }

        if (ImGui.CollapsingHeader("Selection"))
        {
            if (_selectedCell.HasValue)
            {
                DrawCellSelectionDetails(_selectedCell.Value);

                if (ImGui.Button("Clear Selection##selected-cell"))
                {
                    _selectedCell = null;
                    RebuildHighlights();
                }
            }
            else
            {
                ImGui.TextDisabled("No selected cell.");
            }
        }

        if (ImGui.CollapsingHeader("Chunks"))
        {
            ImGui.Text($"Presented Chunks: {_drawData?.Chunks.Count ?? 0}");
            if (_drawData != null)
            {
                foreach (var chunk in _drawData.Chunks.Values)
                {
                    ImGui.BulletText(
                        $"Chunk {chunk.ChunkPosition}: {chunk.VisibleCellCount} visible cells, {chunk.SurfaceFaceCount} faces");
                }
            }
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

        ImGui.Text("Visualization");

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

        ImGui.Separator();
        RenderRenderingStyleTable();

        ImGui.Separator();
        RenderChunkFocusControls();

        ImGui.Separator();
        ImGui.Text("2D Slice View");
        ImGui.Checkbox("Show Slice Viewport", ref _showSliceViewport);
        RenderSliceControls();
    }

    private void RenderRenderingStyleTable()
    {
        ImGui.Text("Rendering Style");

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
            "Chunk Outlines",
            "chunk-outlines",
            ref _show3DChunkOutlines,
            ref _show2DChunkOutlines);
        RenderRenderingStyleRow(
            "Voxel Outlines",
            "voxel-outlines",
            ref _show3DVoxelOutlines,
            ref _show2DVoxelOutlines);
        RenderRenderingStyleRow(
            "Transparent Voxels",
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
        ImGui.Text(label);
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

        if (ImGui.CollapsingHeader("AtmosConfig", ImGuiTreeNodeFlags.DefaultOpen))
        {
            if (ImGui.Button("Reset to Defaults##config-reset"))
                ResetConfigurationValues();
            ImGui.Separator();
            float globalTemp = _config.GlobalTemperature;
            if (ConfigSlider("Global Temperature", "config-global-temperature", ref globalTemp, 0f, 1000f,
                    "Reference ambient temperature."))
                _config.GlobalTemperature = globalTemp;
            float defaultTemperatureFallback = _config.DefaultTemperatureFallback;
            if (ConfigSlider("Default Temperature Fallback", "config-default-temperature-fallback",
                    ref defaultTemperatureFallback, 0f, 1000f,
                    "Default fallback temperature to set when a voxel has 0 or an uninitialized temperature."))
                _config.DefaultTemperatureFallback = defaultTemperatureFallback;
            float voxelVolume = _config.VoxelVolume;
            if (ConfigSlider("Voxel Volume (m³)", "config-voxel-volume", ref voxelVolume, 0.001f, 100f,
                    "Physical volume represented by each voxel. Pressure uses P = nRT/V."))
                _config.VoxelVolume = voxelVolume;
            float saturationReferencePressure = _config.SaturationReferencePressure;
            if (ConfigSlider("Saturation Reference Pressure (Pa)", "config-saturation-reference-pressure",
                    ref saturationReferencePressure, 100f, 200_000f,
                    "Pressure at which each gas's configured boiling point applies."))
                _config.SaturationReferencePressure = saturationReferencePressure;
            float defaultMolarHeatCapacityAtConstantVolume = _config.DefaultMolarHeatCapacityAtConstantVolume;
            if (ConfigSlider("Default Molar Cv", "config-default-molar-cv",
                    ref defaultMolarHeatCapacityAtConstantVolume, 0.01f, 10_000f,
                    "Fallback molar heat capacity at constant volume in J/(mol·K)."))
                _config.DefaultMolarHeatCapacityAtConstantVolume = defaultMolarHeatCapacityAtConstantVolume;
            float defaultDiffusionCoefficient = _config.DefaultDiffusionCoefficient;
            if (ConfigSlider("Default Diffusion Coefficient", "config-default-diffusion-coefficient",
                    ref defaultDiffusionCoefficient, 0f, 1f,
                    "Fallback fraction of the species mole imbalance mixed per tick."))
                _config.DefaultDiffusionCoefficient = defaultDiffusionCoefficient;
            float spaceTemperature = _config.SpaceTemperature;
            if (ConfigSlider("Space Temperature", "config-space-temperature", ref spaceTemperature, 0f, 100f,
                    "Default temperature of space."))
                _config.SpaceTemperature = spaceTemperature;
            float bulkFlowCoefficient = _config.BulkFlowCoefficient;
            if (ConfigSlider("Bulk Flow Coefficient", "config-bulk-flow-coefficient", ref bulkFlowCoefficient, 0f, 1f,
                    "Fraction of pressure delta converted to flow per tick."))
                _config.BulkFlowCoefficient = bulkFlowCoefficient;
            float bulkFlowDamping = _config.BulkFlowDamping;
            if (ConfigSlider("Bulk Flow Damping", "config-bulk-flow-damping", ref bulkFlowDamping, 0f, 1f,
                    "Multiplier applied to the bulk-flow coefficient during large pressure deltas."))
                _config.BulkFlowDamping = bulkFlowDamping;
            float lowPressureDeltaThreshold = _config.LowPressureDeltaThreshold;
            if (ConfigSlider("Low-Pressure Delta Threshold (Pa)", "config-low-pressure-delta-threshold",
                    ref lowPressureDeltaThreshold, 0f, 100f,
                    "Below this pressure delta, flow uses the maximum pressure-transfer fraction directly."))
                _config.LowPressureDeltaThreshold = lowPressureDeltaThreshold;
            float minimumPressureTransfer = _config.MinimumPressureTransfer;
            if (ConfigSlider("Minimum Pressure Transfer (Pa/tick)", "config-minimum-pressure-transfer",
                    ref minimumPressureTransfer, 0f, 10f,
                    "Candidate pressure transfers below this magnitude are discarded."))
                _config.MinimumPressureTransfer = minimumPressureTransfer;
            float vacuumThreshold = _config.VacuumThreshold;
            if (ConfigSlider("Vacuum Threshold", "config-vacuum-threshold", ref vacuumThreshold, 0f, 100f,
                    "Below this pressure, voxel contents are zeroed out."))
                _config.VacuumThreshold = vacuumThreshold;
            int sleepThreshold = _config.SleepThreshold;
            if (ConfigSlider("Sleep Threshold", "config-sleep-threshold", ref sleepThreshold, 1, 1000,
                    "Consecutive ticks below Sleep Epsilon before a chunk goes to sleep."))
                _config.SleepThreshold = sleepThreshold;
            float sleepEpsilon = _config.SleepEpsilon;
            if (ConfigSlider("Sleep Epsilon", "config-sleep-epsilon", ref sleepEpsilon, 0f, 100f,
                    "Maximum pressure delta considered at rest."))
                _config.SleepEpsilon = sleepEpsilon;
            float thermalConductance = _config.ThermalConductance;
            if (ConfigSlider("Thermal Conductance", "config-thermal-conductance", ref thermalConductance, 0f, 1f,
                    "Per-face energy conductance in J/K per thermodynamics tick."))
                _config.ThermalConductance = thermalConductance;
            float condensationRateFactor = _config.CondensationRateFactor;
            if (ConfigSlider("Condensation Rate Factor", "config-condensation-rate-factor", ref condensationRateFactor,
                    0f,
                    1f,
                    "Rate multiplier for phase-change condensation."))
                _config.CondensationRateFactor = condensationRateFactor;
            float maxPressureTransferFraction = _config.MaxPressureTransferFractionPerNeighbor;
            if (ConfigSlider("Max Pressure Transfer / Neighbor", "config-max-pressure-transfer-fraction",
                    ref maxPressureTransferFraction, 0f, 1f,
                    "Maximum source-pressure fraction requested as bulk flow to one neighbor per tick."))
                _config.MaxPressureTransferFractionPerNeighbor = maxPressureTransferFraction;
            float accumulatorWakeThreshold = _config.AccumulatorWakeThreshold;
            if (ConfigSlider("Accumulator Wake Threshold", "config-accumulator-wake-threshold",
                    ref accumulatorWakeThreshold, 0f, 100f,
                    "Minimum accumulated flow or pressure activity required to wake a sleeping chunk."))
                _config.AccumulatorWakeThreshold = accumulatorWakeThreshold;
            int accumulatorMaxAliveTicks = _config.AccumulatorMaxAliveTicks;
            if (ConfigSlider("Accumulator Max Alive Ticks", "config-accumulator-max-alive-ticks",
                    ref accumulatorMaxAliveTicks, 1, 1000,
                    "Maximum number of ticks that an accumulated activity value remains alive."))
                _config.AccumulatorMaxAliveTicks = accumulatorMaxAliveTicks;
        }

        ImGui.Spacing();
        if (ImGui.CollapsingHeader("Gas Registry", ImGuiTreeNodeFlags.DefaultOpen))
            RenderProjectGasControls();
    }

    private void ResetConfigurationValues()
    {
        if (_config == null)
            return;

        var defaults = new AtmosConfig();
        _config.GlobalTemperature = defaults.GlobalTemperature;
        _config.DefaultTemperatureFallback = defaults.DefaultTemperatureFallback;
        _config.DefaultMolarHeatCapacityAtConstantVolume = defaults.DefaultMolarHeatCapacityAtConstantVolume;
        _config.VoxelVolume = defaults.VoxelVolume;
        _config.SaturationReferencePressure = defaults.SaturationReferencePressure;
        _config.DefaultDiffusionCoefficient = defaults.DefaultDiffusionCoefficient;
        _config.SpaceTemperature = defaults.SpaceTemperature;
        _config.BulkFlowCoefficient = defaults.BulkFlowCoefficient;
        _config.BulkFlowDamping = defaults.BulkFlowDamping;
        _config.LowPressureDeltaThreshold = defaults.LowPressureDeltaThreshold;
        _config.MinimumPressureTransfer = defaults.MinimumPressureTransfer;
        _config.VacuumThreshold = defaults.VacuumThreshold;
        _config.SleepThreshold = defaults.SleepThreshold;
        _config.SleepEpsilon = defaults.SleepEpsilon;
        _config.ThermalConductance = defaults.ThermalConductance;
        _config.CondensationRateFactor = defaults.CondensationRateFactor;
        _config.MaxPressureTransferFractionPerNeighbor = defaults.MaxPressureTransferFractionPerNeighbor;
        _config.AccumulatorWakeThreshold = defaults.AccumulatorWakeThreshold;
        _config.AccumulatorMaxAliveTicks = defaults.AccumulatorMaxAliveTicks;
    }

    private static bool ConfigSlider(string label, string id, ref float value, float min, float max, string tooltip)
    {
        ImGui.SetNextItemWidth(100f);
        bool changed = ImGui.SliderFloat($"{label} (?)##{id}", ref value, min, max);
        SetConfigTooltip(tooltip);
        return changed;
    }

    private static bool ConfigSlider(string label, string id, ref int value, int min, int max, string tooltip)
    {
        ImGui.SetNextItemWidth(100f);
        bool changed = ImGui.SliderInt($"{label} (?)##{id}", ref value, min, max);
        SetConfigTooltip(tooltip);
        return changed;
    }

    private static void SetConfigTooltip(string tooltip)
    {
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);
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

        if (ImGui.CollapsingHeader("Chunks", ImGuiTreeNodeFlags.DefaultOpen))
            RenderProjectChunkControls();
        if (ImGui.CollapsingHeader("Voxel Selection", ImGuiTreeNodeFlags.DefaultOpen))
            RenderVoxelTools();
        if (ImGui.CollapsingHeader("Inject Gas"))
            RenderProjectInjectionControls();
    }

    private void RenderVoxelTools()
    {
        ImGui.Text("Chunk operations");
        if (_liveChunkHandles.Count == 0)
        {
            ImGui.TextDisabled("Add a chunk before editing classifications.");
        }
        else
        {
            if (!_toolChunkPosition.HasValue || !_liveChunkPositions.Contains(_toolChunkPosition.Value))
                _toolChunkPosition = _liveChunkHandles[0].Position;

            string chunkLabel = FormatChunkPosition(_toolChunkPosition.Value);
            if (ImGui.BeginCombo("Chunk##tool-classification", chunkLabel))
            {
                foreach (var handle in _liveChunkHandles)
                {
                    bool chunkSelected = handle.Position == _toolChunkPosition.Value;
                    if (ImGui.Selectable(FormatChunkPosition(handle.Position), chunkSelected))
                        _toolChunkPosition = handle.Position;
                    if (chunkSelected)
                        ImGui.SetItemDefaultFocus();
                }

                ImGui.EndCombo();
            }

            ImGui.SetNextItemWidth(NumericInputWidth);
            ImGui.InputInt("Fill VoxelClassification", ref _toolClassificationDraft);
            if (ImGui.Button("Fill Selected Chunk"))
            {
                try
                {
                    _simulation!.SetChunkClassification(
                        new AtmosChunkHandle(_toolChunkPosition.Value),
                        new VoxelClassification(_toolClassificationDraft));
                    SetProjectMessage(
                        $"Filled chunk {FormatChunkPosition(_toolChunkPosition.Value)} with classification " +
                        $"{_toolClassificationDraft}.", false);
                }
                catch (Exception exception) when (exception is ArgumentOutOfRangeException or KeyNotFoundException)
                {
                    SetProjectMessage(exception.Message, true);
                }
            }
        }

        ImGui.Separator();
        if (!_selectedCell.HasValue)
        {
            ImGui.TextDisabled("Select a voxel in the 3D or 2D view.");
            return;
        }

        var selected = _selectedCell.Value;
        DrawCellSelectionDetails(selected);

        if (ImGui.Button("Clear Selection"))
        {
            _selectedCell = null;
            RebuildHighlights();
            return;
        }

        ImGui.Separator();
        ImGui.SetNextItemWidth(NumericInputWidth);
        if (ImGui.InputInt("VoxelClassification", ref _voxelClassificationDraft))
        {
            try
            {
                _simulation!.SetVoxelClassification(
                    new AtmosChunkHandle(selected.Chunk.Position),
                    selected.LocalIndex,
                    new VoxelClassification(_voxelClassificationDraft));
                SetProjectMessage($"Changed voxel classification to {_voxelClassificationDraft}.", false);
            }
            catch (Exception exception) when (exception is ArgumentOutOfRangeException or KeyNotFoundException)
            {
                SetProjectMessage(exception.Message, true);
            }
        }

        ImGui.TextDisabled("0 = unassigned, -2 = solid, -1 = void \nPositive values are room IDs.");
    }

    private void RenderSliceControls()
    {
        if (_drawData == null || _drawData.Chunks.Count == 0)
        {
            ImGui.TextDisabled("No chunks available.");
            return;
        }

        if (!_selectedSliceChunkPosition.HasValue ||
            !_drawData.Chunks.ContainsKey(_selectedSliceChunkPosition.Value))
            _selectedSliceChunkPosition = _drawData.Chunks.Keys.First();

        string selectedChunkLabel = FormatChunkPosition(_selectedSliceChunkPosition.Value);
        if (_focusedChunk.HasValue)
        {
            _selectedSliceChunkPosition = _focusedChunk.Value.Position;
            selectedChunkLabel = FormatChunkPosition(_selectedSliceChunkPosition.Value);
            ImGui.TextDisabled($"Chunk: {selectedChunkLabel} (focused)");
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

        var axis = (int)_currentSliceAxis;
        if (ImGui.Combo("Axis##slice-axis", ref axis, new[]
            {
                "X (YZ plane)",
                "Y (XZ plane)",
                "Z (XY plane)"
            }, 3))
            _currentSliceAxis = (SliceAxis)axis;

        var selectedChunk = _drawData.Chunks[_selectedSliceChunkPosition.Value];
        int maxSliceIndex = Math.Max(
            SimulationFrameBuilder.GetSliceAxisLength(selectedChunk.Dimensions, _currentSliceAxis) - 1,
            0);
        _currentSliceIndex = Math.Clamp(_currentSliceIndex, 0, maxSliceIndex);

        ImGui.SliderInt("Slice##slice-index", ref _currentSliceIndex, 0, maxSliceIndex);

        ImGui.TextDisabled($"Viewing {_currentSliceAxis}={_currentSliceIndex} on chunk {selectedChunkLabel}");
    }

    private void RenderVisualizationLegend()
    {
        if (_drawData == null)
            return;

        var legend = _drawData.Visualization.Legend;
        ImGui.TextDisabled(string.IsNullOrWhiteSpace(legend.Units)
            ? legend.Title
            : $"{legend.Title} ({legend.Units})");

        if (legend.Kind == VisualizationLegendKind.Gradient && legend.Entries.Count > 0)
        {
            RenderLegendBoundsControls(legend.Range);

            ImGui.Checkbox("Resolution##legend-resolution-enabled", ref _legendResolutionEnabled);
            if (_legendResolutionEnabled)
            {
                ImGui.SameLine();
                ImGui.SetNextItemWidth(-1f);
                ImGui.SliderInt("##legend-resolution", ref _legendResolution, 1, 256);
            }
            else
                ImGui.SameLine();

            ImGui.TextDisabled(_legendResolutionEnabled ? $"{_legendResolution} levels" : "Off (coarse)");
            RenderGradientLegend(legend);
            return;
        }

        for (var index = 0; index < legend.Entries.Count; index++)
        {
            var entry = legend.Entries[index];
            var color = entry.Color;
            ImGui.ColorButton($"##legend-{index}", new Vector4(color.R, color.G, color.B, color.A),
                ImGuiColorEditFlags.NoTooltip, new Vector2(16, 16));
            ImGui.SameLine();
            ImGui.TextUnformatted(entry.Label);
        }
    }

    private void RenderLegendBoundsControls(VisualizationRange currentRange)
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
            float offset = _legendAutomaticRangeOffset;
            if (ImGui.InputFloat("± Offset##legend-range-offset", ref offset))
            {
                _legendAutomaticRangeOffset = Math.Max(offset, 0f);
                _legendRangeRevision++;
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Expands the automatic minimum and maximum by this amount.");

            return;
        }

        ImGui.SetNextItemWidth(NumericInputWidth);
        float minimum = _legendMinimum;
        if (ImGui.InputFloat("Min##legend-minimum", ref minimum))
        {
            _legendMinimum = Math.Min(minimum, _legendMaximum);
            _legendRangeRevision++;
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(NumericInputWidth);
        float maximum = _legendMaximum;
        if (ImGui.InputFloat("Max##legend-maximum", ref maximum))
        {
            _legendMaximum = Math.Max(maximum, _legendMinimum);
            _legendRangeRevision++;
        }

        if (_legendMaximum <= _legendMinimum)
        {
            _legendMaximum = _legendMinimum + 1f;
            _legendRangeRevision++;
        }
    }

    private static void RenderGradientLegend(VisualizationLegend legend)
    {
        const int segmentCount = 256;
        const float barHeight = 18f;
        const float labelHeight = 18f;
        float width = Math.Max(ImGui.GetContentRegionAvail().X, 160f);
        var topLeft = ImGui.GetCursorScreenPos();
        var bottomRight = topLeft + new Vector2(width, barHeight);
        var drawList = ImGui.GetWindowDrawList();
        for (var segment = 0; segment < segmentCount; segment++)
        {
            float start = segment / (float)segmentCount;
            float end = (segment + 1) / (float)segmentCount;
            var color = InterpolateLegendColor(legend, QuantizeLegendPosition(start, legend.Range.Resolution));
            drawList.AddRectFilled(new Vector2(topLeft.X + width * start, topLeft.Y),
                new Vector2(topLeft.X + width * end + 1f, bottomRight.Y),
                ImGui.GetColorU32(new Vector4(color.R, color.G, color.B, color.A)));
        }

        ImGui.Dummy(new Vector2(width, barHeight + labelHeight));
        uint textColor = ImGui.GetColorU32(ImGuiCol.Text);
        foreach (var entry in legend.Entries)
        {
            if (!entry.Value.HasValue)
                continue;
            float position = NormalizeLegendValue(entry.Value.Value, legend);
            float textWidth = ImGui.CalcTextSize(entry.Label).X;
            float x = Math.Clamp(topLeft.X + width * position - textWidth * position, topLeft.X,
                bottomRight.X - textWidth);
            drawList.AddText(new Vector2(x, bottomRight.Y + 1f), textColor, entry.Label);
        }
    }

    private static ColorRgba InterpolateLegendColor(VisualizationLegend legend, float position)
    {
        var entries = legend.Entries;
        if (entries.Count == 1)
            return entries[0].Color;
        var lower = 0;
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
        return _legendResolutionEnabled ? _legendResolution : 8;
    }

    private void RenderChunkFocusControls()
    {
        ImGui.Text("3D Focus");
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

        if (ImGui.Button("Frame current view"))
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
            if (ImGui.Button("Reset focus"))
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
        ImGui.Separator();
        ImGui.TextDisabled("Left-click to select");
        ImGui.EndTooltip();
    }

    private void DrawCellSelectionDetails(VoxelAddress address, int? sliceU = null, int? sliceV = null)
    {
        ImGui.Text($"Chunk: {FormatChunkPosition(address.Chunk.Position)}");
        ImGui.Text($"Cell: {FormatCellCoordinates(address)}");
        if (sliceU.HasValue && sliceV.HasValue)
            ImGui.Text($"Slice UV: {sliceU.Value}, {sliceV.Value}");

        if (_drawData == null || !_drawData.TryResolve(address, out _))
        {
            ImGui.TextDisabled("Cell is not present in the latest frame.");
            return;
        }

        if (!TryGetVoxelDetails(address, out var details))
        {
            ImGui.TextDisabled("Details are unavailable for this presented revision.");
            return;
        }

        ImGui.Text($"Temperature: {details.Temperature:F2} K");
        ImGui.Text($"Pressure: {details.Pressure:F2} Pa");
        GetGasSummary(details.Gases, out float totalMoles, out int primaryGasId);
        ImGui.Text($"Total Moles: {totalMoles:F2}");
        ImGui.Text($"Primary Gas: {FormatGas(primaryGasId)}");
        ImGui.Text($"Room: {details.RoomId}");
    }

    private static void GetGasSummary(
        IReadOnlyList<VoxelGasSnapshot> gases,
        out float totalMoles,
        out int primaryGasId)
    {
        totalMoles = 0f;
        primaryGasId = -1;
        var maximumMoles = 0f;
        foreach (var gas in gases)
        {
            float moles = gas.Moles;
            if (float.IsFinite(moles) && moles > 0f)
                totalMoles += moles;
            if (moles > maximumMoles ||
                moles == maximumMoles && moles > 0f && (primaryGasId < 0 || gas.GasId < primaryGasId))
            {
                maximumMoles = moles;
                primaryGasId = gas.GasId;
            }
        }
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
            if (_simulation != null)
            {
                AtmosChunkSnapshot? chunk = _snapshotCache.Count > 0 ? _snapshotCache.Values.First() : null;
                if (chunk.HasValue)
                {
                    var snapshot = chunk.Value;
                    ImGui.Text(
                        $"Chunk Position: {snapshot.GridPosition.X}x{snapshot.GridPosition.Y}x{snapshot.GridPosition.Z}");
                    ImGui.Text(
                        $"Dimensions: {snapshot.Dimensions.X}x{snapshot.Dimensions.Y}x{snapshot.Dimensions.Z}");
                    ImGui.Text($"Total Voxels: {snapshot.VoxelRoomMap.Length}");
                    ImGui.Text($"Active Voxels: {snapshot.ActiveAirCount}");
                    ImGui.Text($"Active Gases: {snapshot.ActiveGasCount}");
                    ImGui.Text($"Awake: {snapshot.IsAwake}");
                    ImGui.Text($"Sleep Timer: {snapshot.SleepTimer}");

                    if (snapshot.TotalPressure is { Length: > 0 })
                    {
                        float maxPressure = snapshot.TotalPressure.Max();
                        float avgPressure = snapshot.TotalPressure.Where(p => p > 0).DefaultIfEmpty(0).Average();
                        ImGui.Text($"Max Pressure: {maxPressure:F2} Pa");
                        ImGui.Text($"Avg Pressure: {avgPressure:F2} Pa");
                    }

                    if (snapshot.Temperature is { Length: > 0 })
                    {
                        float maxTemp = snapshot.Temperature.Max();
                        float minTemp = snapshot.Temperature.Where(t => t > 0).DefaultIfEmpty(0).Min();
                        ImGui.Text($"Min Temp: {minTemp:F2}K");
                        ImGui.Text($"Max Temp: {maxTemp:F2}K");
                    }
                }
            }
        }
    }
}
