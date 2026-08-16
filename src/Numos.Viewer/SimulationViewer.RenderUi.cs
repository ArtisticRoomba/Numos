using System.Numerics;
using ImGuiNET;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.Maths;
using Numos.SimDrawer;

namespace Numos.Viewer;

public partial class SimulationViewer
{
    private bool _requestOpenAboutModal;
    private bool _aboutModalOpen;

    private void RenderUi()
    {
        RenderMainDockspace();

        RenderMenuBar();

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
        }

        RenderDebugPanel();
        RenderSettingsPanel();
        RenderSimInfoPanel();
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

    private void DrawAboutModal()
    {
        const string popupId = "About Numos";

        if (_requestOpenAboutModal)
        {
            ImGui.OpenPopup(popupId);
            _requestOpenAboutModal = false;
        }

        ImGui.SetNextWindowSize(new Vector2(420, 0), ImGuiCond.Appearing);

        var flags =
            ImGuiWindowFlags.AlwaysAutoResize |
            ImGuiWindowFlags.NoSavedSettings;

        if (ImGui.BeginPopupModal(popupId, ref _aboutModalOpen, flags))
        {
            DrawAboutHeader();

            DrawAboutTabs();

            ImGui.Spacing();

            var buttonWidth = 120f;
            float availableWidth = ImGui.GetContentRegionAvail().X;
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + availableWidth - buttonWidth);

            if (ImGui.Button("Close", new Vector2(buttonWidth, 0)))
            {
                _aboutModalOpen = false;
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
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
            ImGuiChildFlags.Border,
            ImGuiWindowFlags.None);

        ImGui.TextWrapped(
            "An external viewer for Numos, an engine-agnostic, pseudo-realistic, ideal-gas, cell-based, atmospherics simulation.");
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
            ImGuiChildFlags.Border,
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
        ImGui.TextDisabled("Version 0.1.0-alpha-alpha-alpha");

        ImGui.EndGroup();
    }

    private void RenderMenuBar()
    {
        if (ImGui.BeginMainMenuBar())
        {
            if (ImGui.BeginMenu("File"))
            {
                if (ImGui.MenuItem("Exit", "Alt+F4"))
                    _window?.Close();
                ImGui.EndMenu();
            }

            if (ImGui.BeginMenu("View"))
            {
                ImGui.MenuItem("Debug Panel", null, ref _showDebugPanel);
                ImGui.MenuItem("Settings Panel", null, ref _showSettingsPanel);
                ImGui.MenuItem("Sim Info Panel", null, ref _showSimInfoPanel);
                ImGui.MenuItem("2D Slice Viewport", null, ref _showSliceViewport);

                ImGui.EndMenu();
            }

            if (ImGui.BeginMenu("Visualization"))
            {
                if (ImGui.MenuItem("Temperature", null, _currentVisualizationMode == VisualizationMode.Temperature))
                    _currentVisualizationMode = VisualizationMode.Temperature;
                if (ImGui.MenuItem("Pressure", null, _currentVisualizationMode == VisualizationMode.Pressure))
                    _currentVisualizationMode = VisualizationMode.Pressure;
                if (ImGui.MenuItem("Gas Composition", null,
                        _currentVisualizationMode == VisualizationMode.GasComposition))
                    _currentVisualizationMode = VisualizationMode.GasComposition;
                if (ImGui.MenuItem("Active Only", null, _currentVisualizationMode == VisualizationMode.ActiveOnly))
                    _currentVisualizationMode = VisualizationMode.ActiveOnly;
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

        ImGui.SetNextWindowPos(new Vector2(10, 40), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(300, 300), ImGuiCond.FirstUseEver);

        if (ImGui.Begin("Debug Info##debug", ref _showDebugPanel))
        {
            ImGui.Text($"FPS: {ImGui.GetIO().Framerate:F1}");
            ImGui.Text($"Simulation Ticks: {_simulation?.TickCount ?? 0}");
            ImGui.Checkbox("Paused", ref _isPaused);

            if (ImGui.CollapsingHeader("Camera"))
            {
                ImGui.Text($"Position: {_cameraPosition.X:F1}, {_cameraPosition.Y:F1}, {_cameraPosition.Z:F1}");
                ImGui.Text($"Distance: {_cameraDistance:F1}");
                ImGui.Text($"Yaw: {_cameraYaw:F2}, Pitch: {_cameraPitch:F2}");
            }

            if (ImGui.CollapsingHeader("2D Slice"))
            {
                ImGui.Text($"Axis: {_currentSliceAxis}");
                ImGui.Text($"Slice Index: {_currentSliceIndex}");
                ImGui.Text($"Visible Cells: {_sliceDrawData?.Chunks.Values.Sum(chunk => chunk.Cells.Count) ?? 0}");

                if (_hoveredSliceCell.HasValue)
                    ImGui.Text($"Hovered Cell: {FormatCellCoordinates(_hoveredSliceCell.Value)}");
                else
                    ImGui.TextDisabled("Hovered Cell: none");
            }

            if (ImGui.CollapsingHeader("Selection"))
            {
                if (_selectedCell.HasValue)
                {
                    DrawCellSelectionDetails(_selectedCell.Value);

                    if (ImGui.Button("Clear Selection##selected-cell"))
                        _selectedCell = null;
                }
                else
                {
                    ImGui.TextDisabled("No selected cell.");
                }
            }

            if (ImGui.CollapsingHeader("Chunks"))
            {
                ImGui.Text($"Active Chunks: {_chunkSnapshots.Count}");
                foreach (var chunk in _chunkSnapshots)
                {
                    ImGui.BulletText($"Chunk {chunk.GridPosition}: Active Voxels: {chunk.ActiveAirCount}");
                }
            }

            ImGui.End();
        }
    }

    private void RenderSettingsPanel()
    {
        if (!_showSettingsPanel)
            return;

        ImGui.SetNextWindowPos(new Vector2(320, 40), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(300, 400), ImGuiCond.FirstUseEver);

        if (ImGui.Begin("Settings##settings", ref _showSettingsPanel))
        {
            ImGui.Text("Simulation Settings");
            ImGui.Separator();

            if (_config != null)
            {
                float globalTemp = _config.GlobalTemperature;
                if (ImGui.SliderFloat("Global Temperature", ref globalTemp, 0f, 500f))
                    _config.GlobalTemperature = globalTemp;

                float flowFriction = _config.FlowFriction;
                if (ImGui.SliderFloat("Flow Friction", ref flowFriction, 0f, 1f))
                    _config.FlowFriction = flowFriction;

                float thermalConductivity = _config.ThermalConductivity;
                if (ImGui.SliderFloat("Thermal Conductivity", ref thermalConductivity, 0f, 0.2f))
                    _config.ThermalConductivity = thermalConductivity;
            }

            ImGui.Separator();
            ImGui.Text("Visualization");

            var mode = (int)_currentVisualizationMode;
            if (ImGui.Combo("Mode##viz", ref mode,
                    new[] { "Temperature", "Pressure", "Gas Composition", "Active Only" }, 4))
            {
                _currentVisualizationMode = (VisualizationMode)mode;
            }

            ImGui.Separator();
            ImGui.Text("2D Slice View");
            ImGui.Checkbox("Show Slice Viewport", ref _showSliceViewport);
            RenderSliceControls();

            ImGui.End();
        }
    }

    private void RenderSliceControls()
    {
        if (_chunkSnapshots.Count == 0)
        {
            ImGui.TextDisabled("No chunks available.");
            return;
        }

        var chunks = _chunkSnapshots;
        _selectedSliceChunkIndex = Math.Clamp(_selectedSliceChunkIndex, 0, chunks.Count - 1);

        string selectedChunkLabel = FormatChunkPosition(chunks[_selectedSliceChunkIndex].GridPosition);
        if (ImGui.BeginCombo("Chunk##slice-chunk", selectedChunkLabel))
        {
            for (var i = 0; i < chunks.Count; i++)
            {
                bool selected = i == _selectedSliceChunkIndex;
                string label = FormatChunkPosition(chunks[i].GridPosition);

                if (ImGui.Selectable(label, selected))
                    _selectedSliceChunkIndex = i;

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
        {
            _currentSliceAxis = (SliceAxis)axis;
        }

        var selectedChunk = chunks[_selectedSliceChunkIndex];
        int maxSliceIndex = Math.Max(
            SimDrawer.SimDrawer.GetSliceAxisLength(selectedChunk.Dimensions, _currentSliceAxis) - 1,
            0);
        _currentSliceIndex = Math.Clamp(_currentSliceIndex, 0, maxSliceIndex);

        ImGui.SliderInt("Slice##slice-index", ref _currentSliceIndex, 0, maxSliceIndex);

        ImGui.TextDisabled($"Viewing {_currentSliceAxis}={_currentSliceIndex} on chunk {selectedChunkLabel}");
    }

    private void RenderSliceCellTooltip()
    {
        if (_sliceViewport is not { IsHovered: true } || !_hoveredSliceCell.HasValue)
            return;

        ImGui.BeginTooltip();
        ImGui.Text("2D Slice Cell");
        ImGui.Separator();
        DrawCellSelectionDetails(_hoveredSliceCell.Value);
        ImGui.Separator();
        ImGui.TextDisabled("Left-click to select");
        ImGui.EndTooltip();
    }

    private static void DrawCellSelectionDetails(CellSelection cell)
    {
        ImGui.Text($"Chunk: {FormatChunkPosition(cell.ChunkPosition)}");
        ImGui.Text($"Cell: {FormatCellCoordinates(cell)}");
        ImGui.Text($"Slice UV: {cell.U}, {cell.V}");
        ImGui.Text($"Temperature: {cell.Temperature:F2} K");
        ImGui.Text($"Pressure: {cell.Pressure:F2}");
        ImGui.Text($"Total Moles: {cell.TotalMoles:F2}");
        ImGui.Text($"Primary Gas: {cell.PrimaryGasId}");
        ImGui.Text($"Room: {cell.RoomId}");
    }

    private static string FormatCellCoordinates(CellSelection cell)
    {
        return $"({cell.X}, {cell.Y}, {cell.Z})";
    }

    private static string FormatChunkPosition(Int3 position)
    {
        return $"({position.X}, {position.Y}, {position.Z})";
    }

    private void RenderSimInfoPanel()
    {
        if (!_showSimInfoPanel)
            return;

        ImGui.SetNextWindowPos(new Vector2(_window!.Size.X - 310, 40), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(300, 400), ImGuiCond.FirstUseEver);

        if (ImGui.Begin("Simulation Info##siminfo", ref _showSimInfoPanel))
        {
            if (ImGui.CollapsingHeader("Simulation State"))
            {
                if (_simulation != null)
                {
                    AtmosChunkSnapshot? chunk = _chunkSnapshots.Count > 0 ? _chunkSnapshots[0] : null;
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

            ImGui.End();
        }
    }
}