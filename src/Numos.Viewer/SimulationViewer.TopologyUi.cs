using System.Numerics;
using ImGuiNET;
using Numos.API;
using Numos.Chunks;
using Numos.Maths;
using Numos.Viewer.Ui;
using Raylib_cs;

namespace Numos.Viewer;

public partial class SimulationViewer
{
    private int _dockFirstChunk;
    private int _dockFirstFace;
    private int _dockFirstSimulation;
    private bool _dockFlipU;
    private bool _dockFlipV;
    private int _dockRotation;
    private int _dockSecondChunk;
    private int _dockSecondFace;
    private int _dockSecondSimulation;
    private AtmosCellRef? _portalFirst;
    private AtmosCellRef? _portalSecond;
    private bool _removeTopologyModalOpen;
    private bool _requestRemoveTopology;
    private ExplicitLinkSetHandle? _selectedLinkSet;
    private string? _topologyFeedback;
    private bool _topologyFeedbackIsError;
    private bool _topologyGas = true;
    private ExplicitLinkSetHandle? _topologyPendingRemoval;
    private bool _topologyThermal = true;

    private void RenderTopologyControls()
    {
        if (_world == null)
            return;

        ImGui.SeparatorText("Topology");
        IReadOnlyList<AtmosWorldLinkSetSnapshot> linkSets = _world.GetLinkSets();
        if (_selectedLinkSet is { } selectedHandle && linkSets.All(set => set.Handle != selectedHandle))
            _selectedLinkSet = null;

        if (ImGui.BeginTable(
                "TopologySets##world",
                3,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("CONNECTION");
            ImGui.TableSetupColumn("LINKS", ImGuiTableColumnFlags.WidthFixed, 54f);
            ImGui.TableSetupColumn("STATE", ImGuiTableColumnFlags.WidthFixed, 118f);
            ImGui.TableHeadersRow();
            foreach (var set in linkSets)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                if (ImGui.Selectable(
                        $"{FormatLinkSetKind(set.Kind)} {set.Handle.Index + 1}##link-{set.Handle.Index}-{set.Handle.Generation}",
                        _selectedLinkSet == set.Handle,
                        ImGuiSelectableFlags.SpanAllColumns))
                {
                    _selectedLinkSet = set.Handle;
                    _topologyFeedback = null;
                }

                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted(set.Links.Count.ToString());
                ImGui.TableSetColumnIndex(2);
                ImGui.TextUnformatted(FormatLinkSetState(set.State));
            }

            ImGui.EndTable();
        }

        if (linkSets.Count == 0)
            ImGui.TextDisabled("No portals or docks connect this world.");

        var selected = _selectedLinkSet is { } selectedId
            ? linkSets.FirstOrDefault(set => set.Handle == selectedId)
            : null;

        if (selected != null)
        {
            ImGui.SeparatorText("Selected Connection");
            if (ImGui.BeginTable(
                    "SelectedTopologyStatus##world",
                    3,
                    ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchSame))
            {
                ImGui.TableNextColumn();
                ImGuiExtensions.StatusField("TYPE", FormatLinkSetKind(selected.Kind));
                ImGui.TableNextColumn();
                ImGuiExtensions.StatusField("LINKS", selected.Links.Count.ToString());
                ImGui.TableNextColumn();
                ImGuiExtensions.StatusField("STATE", FormatLinkSetState(selected.State));
                ImGui.EndTable();
            }

            if (ImGui.CollapsingHeader("Endpoints"))
            {
                if (ImGui.BeginTable(
                        "SelectedTopologyEndpoints##world",
                        2,
                        ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchSame))
                {
                    ImGui.TableSetupColumn("FIRST CELL");
                    ImGui.TableSetupColumn("SECOND CELL");
                    ImGui.TableHeadersRow();
                    foreach (var link in selected.Links.Take(24))
                    {
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(FormatCell(link.First));
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(FormatCell(link.Second));
                    }

                    ImGui.EndTable();
                }

                if (selected.Links.Count > 24)
                    ImGui.TextDisabled($"Showing 24 of {selected.Links.Count} links.");
            }

            ImGui.BeginDisabled(selected.State == AtmosWorldLinkSetState.PendingRemoval);
            if (ImGui.Button("Remove Connection..."))
            {
                _topologyPendingRemoval = selected.Handle;
                _requestRemoveTopology = true;
                _removeTopologyModalOpen = true;
            }

            ImGui.EndDisabled();
        }

        if (!string.IsNullOrWhiteSpace(_topologyFeedback))
        {
            ImGui.TextColored(
                _topologyFeedbackIsError ? ViewerTheme.Error : ViewerTheme.Running,
                _topologyFeedback);
        }

        ImGui.SeparatorText("New Connection");
        if (ImGui.Checkbox("Gas transport", ref _topologyGas))
            _topologyFeedback = null;

        ImGuiExtensions.QuestionTooltip("Allows pressure flow, gas diffusion, and the thermal energy carried by moving gas.");
        if (ImGui.Checkbox("Thermal conduction", ref _topologyThermal))
            _topologyFeedback = null;

        ImGuiExtensions.QuestionTooltip("Allows conductive heat transfer even when no gas crosses the connection.");

        if (!_topologyGas && !_topologyThermal)
            ImGui.TextColored(ViewerTheme.Caution, "Enable at least one transport mode to create a connection.");

        if (ImGui.CollapsingHeader("New Portal", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.TextWrapped("Activate a 3D viewport, select a voxel, then capture each endpoint.");
            bool canCapture = GetSelectedAtmosCell().HasValue;
            ImGui.BeginDisabled(!canCapture);
            if (ImGui.Button("Capture Endpoint 1"))
            {
                _portalFirst = GetSelectedAtmosCell();
                _topologyFeedback = null;
            }

            if (ImGui.Button("Capture Endpoint 2"))
            {
                _portalSecond = GetSelectedAtmosCell();
                _topologyFeedback = null;
            }

            ImGui.EndDisabled();
            if (!canCapture)
                ImGui.TextDisabled("Select a voxel in the active viewport to enable capture.");

            if (ImGui.BeginTable(
                    "PortalEndpoints##world",
                    2,
                    ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchSame))
            {
                ImGui.TableNextColumn();
                ImGuiExtensions.StatusField(
                    "ENDPOINT 1",
                    _portalFirst.HasValue ? FormatCell(_portalFirst.Value) : "Not captured");

                ImGui.TableNextColumn();
                ImGuiExtensions.StatusField(
                    "ENDPOINT 2",
                    _portalSecond.HasValue ? FormatCell(_portalSecond.Value) : "Not captured");

                ImGui.EndTable();
            }

            ImGui.BeginDisabled(!_portalFirst.HasValue || !_portalSecond.HasValue || GetTopologyFlags() == ExplicitLinkFlags.None);
            if (ImGui.Button("Create Portal"))
                CreatePortalFromCapturedEndpoints();

            ImGui.EndDisabled();
        }

        if (ImGui.CollapsingHeader("New Rectangular Face Dock"))
        {
            bool dockInputChanged = DrawDockEndpoint(
                "First",
                ref _dockFirstSimulation,
                ref _dockFirstChunk,
                ref _dockFirstFace);

            dockInputChanged |= DrawDockEndpoint(
                "Second",
                ref _dockSecondSimulation,
                ref _dockSecondChunk,
                ref _dockSecondFace);

            string[] rotations = ["0°", "90°", "180°", "270°"];
            dockInputChanged |= ImGui.Combo("Rotation", ref _dockRotation, rotations, rotations.Length);
            dockInputChanged |= ImGui.Checkbox("Flip U", ref _dockFlipU);
            ImGui.SameLine();
            dockInputChanged |= ImGui.Checkbox("Flip V", ref _dockFlipV);
            if (dockInputChanged)
                _topologyFeedback = null;

            ImGui.BeginDisabled(GetTopologyFlags() == ExplicitLinkFlags.None || _simulationSurfaces.Count == 0);
            if (ImGui.Button("Create Dock"))
            {
                try
                {
                    ExplicitLinkDefinition[] links = BuildDockLinks();
                    var dock = _world.CreateDock(links);
                    _selectedLinkSet = dock.Links;
                    SetTopologyFeedback(
                        $"Created dock with {links.Length} links. It will activate on the next world tick.",
                        false);
                }
                catch (Exception exception)
                {
                    SetTopologyFeedback($"Could not create the dock: {exception.Message}", true);
                    WriteException("Could not create dock", exception);
                }
            }

            ImGui.EndDisabled();
        }
    }

    private bool DrawDockEndpoint(string label, ref int simulationIndex, ref int chunkIndex, ref int face)
    {
        if (_simulationSurfaces.Count == 0)
        {
            ImGui.TextDisabled($"{label}: no simulation");
            return false;
        }

        bool changed = false;
        simulationIndex = Math.Clamp(simulationIndex, 0, _simulationSurfaces.Count - 1);
        string simulationLabel = _simulationNames.GetValueOrDefault(
            _simulationSurfaces[simulationIndex].Simulation.Id,
            $"Simulation {_simulationSurfaces[simulationIndex].Simulation.Id.Index + 1}");

        if (ImGui.BeginCombo($"{label} simulation", simulationLabel))
        {
            for (int index = 0; index < _simulationSurfaces.Count; index++)
            {
                string candidate = _simulationNames.GetValueOrDefault(
                    _simulationSurfaces[index].Simulation.Id,
                    $"Simulation {_simulationSurfaces[index].Simulation.Id.Index + 1}");

                if (ImGui.Selectable(candidate, index == simulationIndex))
                {
                    simulationIndex = index;
                    chunkIndex = 0;
                    changed = true;
                }
            }

            ImGui.EndCombo();
        }

        ChunkHandle[] chunks = _simulationSurfaces[simulationIndex].Simulation.GetChunkHandles().ToArray();
        chunkIndex = chunks.Length == 0 ? 0 : Math.Clamp(chunkIndex, 0, chunks.Length - 1);
        string chunkLabel = chunks.Length == 0 ? "No chunks" : FormatChunkPosition(chunks[chunkIndex].Position);
        if (ImGui.BeginCombo($"{label} chunk", chunkLabel))
        {
            for (int index = 0; index < chunks.Length; index++)
            {
                if (ImGui.Selectable(FormatChunkPosition(chunks[index].Position), index == chunkIndex))
                {
                    chunkIndex = index;
                    changed = true;
                }
            }

            ImGui.EndCombo();
        }

        string[] faces = ["-X", "+X", "-Y", "+Y", "-Z", "+Z"];
        changed |= ImGui.Combo($"{label} face", ref face, faces, faces.Length);
        return changed;
    }

    private void DrawRemoveTopologyModal()
    {
        const string popupId = "Remove Connection?##world";
        ImGuiExtensions.OpenPopupWhenRequested(popupId, ref _requestRemoveTopology);
        ImGui.SetNextWindowSize(new Vector2(440, 0), ImGuiCond.Appearing);
        using var modal = ImGuiExtensions.BeginPopupModal(popupId, ref _removeTopologyModalOpen);
        if (!modal.IsVisible)
            return;

        var selected = _world != null && _topologyPendingRemoval is { } handle
            ? _world.GetLinkSets().FirstOrDefault(set => set.Handle == handle)
            : null;

        if (selected == null)
        {
            ImGui.TextWrapped("The selected connection no longer exists.");
            if (ImGui.Button("Close", new Vector2(120, 0)))
            {
                _removeTopologyModalOpen = false;
                _topologyPendingRemoval = null;
                ImGui.CloseCurrentPopup();
            }

            ImGui.SetItemDefaultFocus();

            return;
        }

        ImGui.TextWrapped(
            $"Remove this {FormatLinkSetKind(selected.Kind).ToLowerInvariant()} and its {selected.Links.Count} " +
            $"link{(selected.Links.Count == 1 ? string.Empty : "s")}? It will stop transporting at the next world tick.");

        ImGui.Spacing();
        if (ImGui.Button("Remove Connection", new Vector2(160, 0)))
        {
            try
            {
                _world!.DestroyLinks(selected.Handle);
                _topologyPendingRemoval = null;
                _removeTopologyModalOpen = false;
                SetTopologyFeedback("Queued the connection for removal on the next world tick.", false);
                ImGui.CloseCurrentPopup();
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                SetTopologyFeedback($"Could not remove the connection: {exception.Message}", true);
                WriteException("Could not remove topology", exception);
                _removeTopologyModalOpen = false;
                ImGui.CloseCurrentPopup();
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(120, 0)))
        {
            _removeTopologyModalOpen = false;
            _topologyPendingRemoval = null;
            ImGui.CloseCurrentPopup();
        }

        ImGui.SetItemDefaultFocus();
    }

    private ExplicitLinkDefinition[] BuildDockLinks()
    {
        var firstSurface = _simulationSurfaces[Math.Clamp(_dockFirstSimulation, 0, _simulationSurfaces.Count - 1)];
        var secondSurface = _simulationSurfaces[Math.Clamp(_dockSecondSimulation, 0, _simulationSurfaces.Count - 1)];
        ChunkHandle[] firstChunks = firstSurface.Simulation.GetChunkHandles().ToArray();
        ChunkHandle[] secondChunks = secondSurface.Simulation.GetChunkHandles().ToArray();
        if (firstChunks.Length == 0 || secondChunks.Length == 0)
            throw new InvalidOperationException("Each dock endpoint needs a chunk.");

        var firstChunk = firstChunks[Math.Clamp(_dockFirstChunk, 0, firstChunks.Length - 1)];
        var secondChunk = secondChunks[Math.Clamp(_dockSecondChunk, 0, secondChunks.Length - 1)];
        var firstSize = GetFaceSize(firstSurface.Simulation.ChunkDimensions, (DockFace)_dockFirstFace);
        var secondSize = GetFaceSize(secondSurface.Simulation.ChunkDimensions, (DockFace)_dockSecondFace);
        bool quarterTurn = (_dockRotation & 1) != 0;
        if (firstSize.Width != (quarterTurn ? secondSize.Height : secondSize.Width) ||
            firstSize.Height != (quarterTurn ? secondSize.Width : secondSize.Height))
            throw new InvalidOperationException("The selected faces do not have matching dimensions after rotation.");

        var links = new ExplicitLinkDefinition[firstSize.Width * firstSize.Height];
        int destination = 0;
        for (int v = 0; v < firstSize.Height; v++)
        for (int u = 0; u < firstSize.Width; u++)
        {
            (int secondU, int secondV) = RotateFace(u, v, secondSize, _dockRotation);
            if (_dockFlipU) secondU = secondSize.Width - 1 - secondU;
            if (_dockFlipV) secondV = secondSize.Height - 1 - secondV;
            ushort firstIndex = GetFaceVoxelIndex(firstSurface.Simulation.ChunkDimensions, (DockFace)_dockFirstFace, u, v);
            ushort secondIndex = GetFaceVoxelIndex(
                secondSurface.Simulation.ChunkDimensions,
                (DockFace)_dockSecondFace,
                secondU,
                secondV);

            links[destination++] = new ExplicitLinkDefinition(
                new AtmosCellRef(firstSurface.Simulation.Id, firstChunk, firstIndex),
                new AtmosCellRef(secondSurface.Simulation.Id, secondChunk, secondIndex),
                GetTopologyFlags());
        }

        return links;
    }

    private AtmosCellRef? GetSelectedAtmosCell()
    {
        if (_simulation == null || !_selectedCell.HasValue)
            return null;

        return new AtmosCellRef(
            _simulation.Id,
            new ChunkHandle(_selectedCell.Value.Chunk.Position),
            _selectedCell.Value.LocalIndex);
    }

    private ExplicitLinkFlags GetTopologyFlags()
    {
        return (_topologyGas ? ExplicitLinkFlags.GasTransport : ExplicitLinkFlags.None) |
               (_topologyThermal ? ExplicitLinkFlags.ThermalTransport : ExplicitLinkFlags.None);
    }

    private void CreatePortalFromCapturedEndpoints()
    {
        if (_world == null || !_portalFirst.HasValue || !_portalSecond.HasValue)
            return;

        try
        {
            var portal = _world.CreatePortal(
                _portalFirst.Value,
                _portalSecond.Value,
                GetTopologyFlags());

            _selectedLinkSet = portal.Links;
            _portalFirst = null;
            _portalSecond = null;
            SetTopologyFeedback("Created portal. It will activate on the next world tick.", false);
        }
        catch (Exception exception)
        {
            SetTopologyFeedback($"Could not create the portal: {exception.Message}", true);
            WriteException("Could not create portal", exception);
        }
    }

    private void DrawTopologyOverlay()
    {
        if (_world == null || _simulation == null || _drawData == null)
            return;

        foreach (var set in _world.GetLinkSets())
        {
            var color = GetLinkColor(set.Handle, set.State);
            foreach (var link in set.Links)
            {
                Vector3 first = default;
                Vector3 second = default;
                bool firstHere = link.First.Simulation == _simulation.Id && TryGetCellCenter(link.First, out first);
                bool secondHere = link.Second.Simulation == _simulation.Id && TryGetCellCenter(link.Second, out second);
                if (firstHere) Raylib.DrawCubeWiresV(first, new Vector3(1.12f), color);
                if (secondHere) Raylib.DrawCubeWiresV(second, new Vector3(1.12f), color);
                if (firstHere && secondHere)
                    Raylib.DrawLine3D(first, second, color);
            }
        }
    }

    private bool TryGetCellCenter(AtmosCellRef cell, out Vector3 center)
    {
        if (_drawData!.Chunks.TryGetValue(cell.Chunk.Position, out var chunk) && cell.LocalVoxelIndex < chunk.CellCount)
        {
            var coordinates = chunk.GetWorldCoordinates(cell.LocalVoxelIndex);
            center = new Vector3(coordinates.X + 0.5f, coordinates.Y + 0.5f, coordinates.Z + 0.5f);
            return true;
        }

        center = default;
        return false;
    }

    private static Color GetLinkColor(ExplicitLinkSetHandle handle, AtmosWorldLinkSetState state)
    {
        var role = ViewerTheme.TopologyPalette[handle.Index % ViewerTheme.TopologyPalette.Length];
        float brightness = state == AtmosWorldLinkSetState.PendingRemoval ? 0.55f : 1f;
        var color = new Color(role.X * brightness, role.Y * brightness, role.Z * brightness, role.W);
        color.A = state == AtmosWorldLinkSetState.Active ? (byte)255 : (byte)170;
        return color;
    }

    private string FormatCell(AtmosCellRef cell)
    {
        return $"{GetSimulationName(cell.Simulation)}\n{FormatChunkPosition(cell.Chunk.Position)}, voxel {cell.LocalVoxelIndex}";
    }

    private static string FormatLinkSetKind(ExplicitLinkSetKind kind)
    {
        return kind switch
        {
            ExplicitLinkSetKind.Arbitrary => "Link Set",
            ExplicitLinkSetKind.Portal => "Portal",
            ExplicitLinkSetKind.Dock => "Dock",
            _ => kind.ToString()
        };
    }

    private static string FormatLinkSetState(AtmosWorldLinkSetState state)
    {
        return state switch
        {
            AtmosWorldLinkSetState.PendingActivation => "Pending activation",
            AtmosWorldLinkSetState.Active => "Active",
            AtmosWorldLinkSetState.PendingRemoval => "Pending removal",
            _ => state.ToString()
        };
    }

    private void SetTopologyFeedback(string message, bool isError)
    {
        _topologyFeedback = message;
        _topologyFeedbackIsError = isError;
    }

    private static FaceSize GetFaceSize(Int3 dimensions, DockFace face)
    {
        return face switch
        {
            DockFace.NegativeX or DockFace.PositiveX => new FaceSize(dimensions.Y, dimensions.Z),
            DockFace.NegativeY or DockFace.PositiveY => new FaceSize(dimensions.X, dimensions.Z),
            _ => new FaceSize(dimensions.X, dimensions.Y)
        };
    }

    private static (int U, int V) RotateFace(int u, int v, FaceSize destination, int rotation)
    {
        return (rotation & 3) switch
        {
            0 => (u, v),
            1 => (destination.Width - 1 - v, u),
            2 => (destination.Width - 1 - u, destination.Height - 1 - v),
            _ => (v, destination.Height - 1 - u)
        };
    }

    private static ushort GetFaceVoxelIndex(Int3 dimensions, DockFace face, int u, int v)
    {
        (int x, int y, int z) = face switch
        {
            DockFace.NegativeX => (0, u, v),
            DockFace.PositiveX => (dimensions.X - 1, u, v),
            DockFace.NegativeY => (u, 0, v),
            DockFace.PositiveY => (u, dimensions.Y - 1, v),
            DockFace.NegativeZ => (u, v, 0),
            _ => (u, v, dimensions.Z - 1)
        };

        return checked((ushort)(x + dimensions.X * (y + dimensions.Y * z)));
    }

    private readonly record struct FaceSize(int Width, int Height);

    private enum DockFace
    {
        NegativeX,
        PositiveX,
        NegativeY,
        PositiveY,
        NegativeZ,
        PositiveZ
    }
}