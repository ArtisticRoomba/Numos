using System.Numerics;
using ImGuiNET;
using Numos.API;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.Maths;
using Numos.SimDrawer;
using Numos.Viewer.Ui;
using Raylib_cs;

namespace Numos.Viewer;

public partial class SimulationViewer
{
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

        RenderDebugPanel();
        RenderProjectPanel();
        RenderSettingsPanel();
        RenderSimInfoPanel();
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
                    ImGui.MenuItem("Project Panel", null, ref _showProjectPanel);
                ImGui.MenuItem("Debug Panel", null, ref _showDebugPanel);
                ImGui.MenuItem("Settings Panel", null, ref _showSettingsPanel);
                ImGui.MenuItem("Sim Info Panel", null, ref _showSimInfoPanel);
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

    private void RenderDebugPanel()
    {
        if (!_showDebugPanel)
            return;

        using var window = ImGuiExtensions.BeginWindow(
            "Debug Info##debug",
            ref _showDebugPanel,
            new Vector2(10, 40),
            new Vector2(300, 300));
        if (!window.IsVisible)
            return;

        ImGui.Text($"FPS: {ImGui.GetIO().Framerate:F1}");
        ImGui.Text($"Simulation Ticks: {_simulation?.TickCount ?? 0}");
        ImGui.Checkbox("Paused", ref _isPaused);

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

    private void RenderSettingsPanel()
    {
        if (!_showSettingsPanel)
            return;

        using var window = ImGuiExtensions.BeginWindow(
            "Settings##settings",
            ref _showSettingsPanel,
            new Vector2(320, 40),
            new Vector2(300, 400));
        if (!window.IsVisible)
            return;

        ImGui.Text("Simulation Settings");
        ImGui.Separator();

        if (_config != null)
        {
            float globalTemp = _config.GlobalTemperature;
            if (ImGui.SliderFloat("Global Temperature", ref globalTemp, 0f, 500f))
            {
                _config.GlobalTemperature = globalTemp;
            }

            float flowFriction = _config.FlowFriction;
            if (ImGui.SliderFloat("Flow Friction", ref flowFriction, 0f, 1f))
            {
                _config.FlowFriction = flowFriction;
            }

            float thermalConductivity = _config.ThermalConductivity;
            if (ImGui.SliderFloat("Thermal Conductivity", ref thermalConductivity, 0f, 0.2f))
            {
                _config.ThermalConductivity = thermalConductivity;
            }
        }

        ImGui.Separator();
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

        for (var index = 0; index < legend.Entries.Count; index++)
        {
            var entry = legend.Entries[index];
            var color = entry.Color;
            ImGui.ColorButton(
                $"##legend-{index}",
                new Vector4(color.R, color.G, color.B, color.A),
                ImGuiColorEditFlags.NoTooltip,
                new Vector2(16, 16));
            ImGui.SameLine();
            ImGui.TextUnformatted(entry.Label);
        }
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

    private void RenderSimInfoPanel()
    {
        if (!_showSimInfoPanel)
            return;

        using var window = ImGuiExtensions.BeginWindow(
            "Simulation Info##siminfo",
            ref _showSimInfoPanel,
            new Vector2(Raylib.GetScreenWidth() - 310, 40),
            new Vector2(300, 400));
        if (!window.IsVisible)
            return;

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