using System.Numerics;
using ImGuiNET;
using Numos.API;
using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.Maths;
using Numos.SimDrawer;
using Numos.Viewer.Ui;

namespace Numos.Viewer;

public partial class SimulationViewer
{
    private const float NumericInputWidth = 100f;

    private bool _requestOpenAboutModal;
    private bool _aboutModalOpen;

    private void RenderUi()
    {
        RenderMainDockspace();

        RenderMenuBar();

        if (_simulation == null)
        {
            RenderEmptyWorkspaceMessage();
            DrawCreateProjectModal();
            DrawCloseProjectModal();
            DrawAboutModal();
            return;
        }

        _viewport?.Draw("Simulation 3D##viewport", RenderSimulationScene);

        if (_showSliceViewport)
        {
            _sliceViewport?.Draw("Simulation Slice 2D##slice-viewport", RenderSimulationSliceScene);
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

    private static void RenderEmptyWorkspaceMessage()
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

        ImGuiExtensions.TextCentered("Numos");
        ImGuiExtensions.TextCentered($"v{ViewerVersion}", ImGui.TextDisabled);

        ImGui.Separator();

        ImGuiExtensions.TextCentered("No simulation is currently loaded.");
        ImGuiExtensions.TextCentered(
            "To get started, choose File > New Simulation.",
            ImGui.TextDisabled);

        ImGui.End();
    }

    private void DrawAboutModal()
    {
        const string popupId = "About Numos";

        ImGuiExtensions.OpenPopupWhenRequested(popupId, ref _requestOpenAboutModal);

        ImGui.SetNextWindowSize(new Vector2(420, 0), ImGuiCond.Appearing);

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
        var tabAreaSize = new Vector2(480, 280);

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

        ImGui.EndChild();
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
        ImGui.BeginGroup();

        ImGui.Text("Numos Simulation Viewer");
        ImGui.TextDisabled($"Version {ViewerVersion}");

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
                if (_simulation != null)
                    ImGui.MenuItem("Solution", null, ref _showSolutionPanel);
                ImGui.MenuItem("Tools", null, ref _showToolsPanel);
                ImGui.MenuItem("View", null, ref _showViewPanel);
                ImGui.MenuItem("Configuration", null, ref _showConfigurationPanel);
                ImGui.MenuItem("2D Slice Viewport", null, ref _showSliceViewport);

                ImGui.EndMenu();
            }

            if (ImGui.BeginMenu("Visualization"))
            {
                if (_frameBuilder != null)
                {
                    foreach (var method in _frameBuilder.Visualizations.Methods)
                    {
                        bool selected = string.Equals(
                            method.Id,
                            _currentVisualizationId,
                            StringComparison.OrdinalIgnoreCase);
                        if (ImGui.MenuItem(method.DisplayName, null, selected))
                            SetVisualization(method.Id);
                    }
                }

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
            new Vector2(320, 40),
            new Vector2(300, 400));
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
        RenderChunkFocusControls();

        ImGui.Separator();
        ImGui.Text("2D Slice View");
        ImGui.Checkbox("Show Slice Viewport", ref _showSliceViewport);
        RenderSliceControls();
    }

    private void RenderConfigurationPanel()
    {
        if (!_showConfigurationPanel || _config == null)
            return;

        using var window = ImGuiExtensions.BeginWindow(
            "Configuration##configuration",
            ref _showConfigurationPanel,
            new Vector2(640, 40),
            new Vector2(380, 600));
        if (!window.IsVisible)
            return;

        ImGui.Text("AtmosConfig");
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
        float spaceTemperature = _config.SpaceTemperature;
        if (ConfigSlider("Space Temperature", "config-space-temperature", ref spaceTemperature, 0f, 100f,
                "Default temperature of space."))
            _config.SpaceTemperature = spaceTemperature;
        float flowFriction = _config.FlowFriction;
        if (ConfigSlider("Flow Friction", "config-flow-friction", ref flowFriction, 0f, 1f,
                "Fraction of pressure delta converted to flow per tick."))
            _config.FlowFriction = flowFriction;
        float dampingFactor = _config.DampingFactor;
        if (ConfigSlider("Damping Factor", "config-damping-factor", ref dampingFactor, 0f, 1f,
                "Multiplier applied to Flow Friction during large-delta advection. Used to reduce oscillation in the sim."))
            _config.DampingFactor = dampingFactor;
        float snapThreshold = _config.SnapThreshold;
        if (ConfigSlider("Snap Threshold", "config-snap-threshold", ref snapThreshold, 0f, 100f,
                "Below this pressure delta, flow uses the Cfl Flow Cap directly instead of Flow Friction multiplied by Damping Factor."))
            _config.SnapThreshold = snapThreshold;
        float minFlowCutoff = _config.MinFlowCutoff;
        if (ConfigSlider("Minimum Flow Cutoff", "config-min-flow-cutoff", ref minFlowCutoff, 0f, 10f,
                "Flows below this magnitude are discarded."))
            _config.MinFlowCutoff = minFlowCutoff;
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
        float thermalConductivity = _config.ThermalConductivity;
        if (ConfigSlider("Thermal Conductivity", "config-thermal-conductivity", ref thermalConductivity, 0f, 1f,
                "Fraction of temperature delta transferred per neighbor per tick."))
            _config.ThermalConductivity = thermalConductivity;
        float condensationRateFactor = _config.CondensationRateFactor;
        if (ConfigSlider("Condensation Rate Factor", "config-condensation-rate-factor", ref condensationRateFactor, 0f,
                1f,
                "Rate multiplier for phase-change condensation."))
            _config.CondensationRateFactor = condensationRateFactor;
        float cflFlowCap = _config.CflFlowCap;
        if (ConfigSlider("CFL Flow Cap", "config-cfl-flow-cap", ref cflFlowCap, 0f, 1f,
                "Rate multiplier for phase-change condensation."))
            _config.CflFlowCap = cflFlowCap;
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
        _config.SpaceTemperature = defaults.SpaceTemperature;
        _config.FlowFriction = defaults.FlowFriction;
        _config.DampingFactor = defaults.DampingFactor;
        _config.SnapThreshold = defaults.SnapThreshold;
        _config.MinFlowCutoff = defaults.MinFlowCutoff;
        _config.VacuumThreshold = defaults.VacuumThreshold;
        _config.SleepThreshold = defaults.SleepThreshold;
        _config.SleepEpsilon = defaults.SleepEpsilon;
        _config.ThermalConductivity = defaults.ThermalConductivity;
        _config.CondensationRateFactor = defaults.CondensationRateFactor;
        _config.CflFlowCap = defaults.CflFlowCap;
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
            new Vector2(390, 40),
            new Vector2(380, 560));
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
        ImGui.Text($"Chunk: {FormatChunkPosition(selected.Chunk.Position)}");
        ImGui.Text($"Voxel: {FormatCellCoordinates(selected)}");
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

        ImGui.TextDisabled("0 = unassigned, -2 = solid, -1 = void; positive values are room IDs.");
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
        ImGui.Text($"Pressure: {details.Pressure:F2}");
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
        if (ImGui.CollapsingHeader("Simulation State"))
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
                        ImGui.Text($"Max Pressure: {maxPressure:F2}");
                        ImGui.Text($"Avg Pressure: {avgPressure:F2}");
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