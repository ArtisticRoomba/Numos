using System.Numerics;
using ImGuiNET;
using Numos.Workbench;
using Numos.API;
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
    private float _topologyAreaFraction = 1f;
    private bool _removeTopologyModalOpen;
    private bool _requestRemoveTopology;
    private ExplicitLinkSetHandle? _selectedLinkSet;
    private string? _topologyFeedback;
    private bool _topologyFeedbackIsError;
    private bool _topologyGas = true;
    private ExplicitLinkSetHandle? _topologyPendingRemoval;
    private bool _topologyThermal = true;

    private void RenderTopologyControls(bool inspecting)
    {
        if (_session.World == null)
            return;

        ImGui.SeparatorText(Locale.GetString("topology"));
        IReadOnlyList<AtmosWorldLinkSetSnapshot> linkSets = _session.World.GetLinkSets();
        if (_selectedLinkSet is { } selectedHandle && linkSets.All(set => set.Handle != selectedHandle))
            _selectedLinkSet = null;

        if (ImGui.BeginTable(
                "TopologySets##world",
                3,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn(Locale.GetString("connection"));
            ImGui.TableSetupColumn(Locale.GetString("links"), ImGuiTableColumnFlags.WidthFixed, 54f);
            ImGui.TableSetupColumn(Locale.GetString("state"), ImGuiTableColumnFlags.WidthFixed, 118f);
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
            ImGui.TextDisabled(Locale.GetString("no-portals-or-docks-connect-this-world"));

        var selected = _selectedLinkSet is { } selectedId
            ? linkSets.FirstOrDefault(set => set.Handle == selectedId)
            : null;

        if (selected != null)
        {
            ImGui.SeparatorText(Locale.GetString("selected-connection"));
            if (ImGui.BeginTable(
                    "SelectedTopologyStatus##world",
                    4,
                    ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchSame))
            {
                ImGui.TableNextColumn();
                ImGuiExtensions.StatusField(Locale.GetString("type"), FormatLinkSetKind(selected.Kind));
                ImGui.TableNextColumn();
                ImGuiExtensions.StatusField(Locale.GetString("links"), selected.Links.Count.ToString());
                ImGui.TableNextColumn();
                ImGuiExtensions.StatusField(Locale.GetString("state"), FormatLinkSetState(selected.State));
                ImGui.TableNextColumn();
                ImGuiExtensions.StatusField(Locale.GetString("area"), FormatAreaFraction(selected.Links));
                ImGui.EndTable();
            }

            if (ImGui.CollapsingHeader(Locale.GetString("endpoints")))
            {
                if (ImGui.BeginTable(
                        "SelectedTopologyEndpoints##world",
                        2,
                        ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchSame))
                {
                    ImGui.TableSetupColumn(Locale.GetString("first-cell"));
                    ImGui.TableSetupColumn(Locale.GetString("second-cell"));
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
                    ImGui.TextDisabled(Locale.GetString("showing-links", ("count", 24), ("total", selected.Links.Count)));
            }

            ImGui.BeginDisabled(selected.State == AtmosWorldLinkSetState.PendingRemoval || inspecting);
            if (ImGui.Button(Locale.GetString("remove-connection-ellipsis")))
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

        ImGui.SeparatorText(Locale.GetString("new-connection"));
        ImGui.BeginDisabled(inspecting);
        if (ImGui.Checkbox(Locale.GetString("gas-transport"), ref _topologyGas))
            _topologyFeedback = null;

        ImGui.EndDisabled();
        ImGuiExtensions.QuestionTooltip(Locale.GetString("gas-transport-tooltip"));
        ImGui.BeginDisabled(inspecting);
        if (ImGui.Checkbox(Locale.GetString("thermal-conduction"), ref _topologyThermal))
            _topologyFeedback = null;

        ImGui.EndDisabled();
        ImGuiExtensions.QuestionTooltip(Locale.GetString("thermal-conduction-tooltip"));
        ImGui.BeginDisabled(inspecting);
        if (ImGui.SliderFloat(Locale.GetString("opening-area"), ref _topologyAreaFraction, 0f, 1f, "%.2f " + Locale.GetString("of-a-face")))
            _topologyFeedback = null;

        ImGui.EndDisabled();
        ImGuiExtensions.QuestionTooltip(Locale.GetString("opening-area-tooltip"));

        if (!_topologyGas && !_topologyThermal)
            ImGui.TextColored(ViewerTheme.Caution, Locale.GetString("enable-transport-mode"));

        if (ImGui.CollapsingHeader(Locale.GetString("new-portal"), ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.TextWrapped(Locale.GetString("capture-portal-endpoints-instructions"));
            bool canCapture = GetSelectedAtmosCell().HasValue;
            ImGui.BeginDisabled(!canCapture || inspecting);
            if (ImGui.Button(Locale.GetString("capture-endpoint", ("number", 1))))
            {
                _portalFirst = GetSelectedAtmosCell();
                _topologyFeedback = null;
            }

            if (ImGui.Button(Locale.GetString("capture-endpoint", ("number", 2))))
            {
                _portalSecond = GetSelectedAtmosCell();
                _topologyFeedback = null;
            }

            ImGui.EndDisabled();
            if (!canCapture)
                ImGui.TextDisabled(Locale.GetString("select-voxel-to-enable-capture"));

            if (ImGui.BeginTable(
                    "PortalEndpoints##world",
                    2,
                    ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchSame))
            {
                ImGui.TableNextColumn();
                ImGuiExtensions.StatusField(
                    Locale.GetString("endpoint-number", ("number", 1)),
                    _portalFirst.HasValue ? FormatCell(_portalFirst.Value) : Locale.GetString("not-captured"));

                ImGui.TableNextColumn();
                ImGuiExtensions.StatusField(
                    Locale.GetString("endpoint-number", ("number", 2)),
                    _portalSecond.HasValue ? FormatCell(_portalSecond.Value) : Locale.GetString("not-captured"));

                ImGui.EndTable();
            }

            ImGui.BeginDisabled(
                !_portalFirst.HasValue ||
                !_portalSecond.HasValue ||
                GetTopologyFlags() == ExplicitLinkFlags.None ||
                inspecting);

            if (ImGui.Button(Locale.GetString("create-portal")))
                CreatePortalFromCapturedEndpoints();

            ImGui.EndDisabled();
        }

        if (ImGui.CollapsingHeader(Locale.GetString("new-rectangular-face-dock")))
        {
            ImGui.BeginDisabled(inspecting);
            bool dockInputChanged = DrawDockEndpoint(
                Locale.GetString("first"),
                ref _dockFirstSimulation,
                ref _dockFirstChunk,
                ref _dockFirstFace);

            dockInputChanged |= DrawDockEndpoint(
                Locale.GetString("second"),
                ref _dockSecondSimulation,
                ref _dockSecondChunk,
                ref _dockSecondFace);

            string[] rotations = ["0°", "90°", "180°", "270°"];
            dockInputChanged |= ImGui.Combo(Locale.GetString("rotation"), ref _dockRotation, rotations, rotations.Length);
            dockInputChanged |= ImGui.Checkbox(Locale.GetString("flip-u"), ref _dockFlipU);
            ImGui.SameLine();
            dockInputChanged |= ImGui.Checkbox(Locale.GetString("flip-v"), ref _dockFlipV);
            if (dockInputChanged)
                _topologyFeedback = null;

            ImGui.EndDisabled();
            ImGui.BeginDisabled(
                GetTopologyFlags() == ExplicitLinkFlags.None ||
                _simulationSurfaces.Count == 0 ||
                inspecting);

            if (ImGui.Button(Locale.GetString("create-dock")))
            {
                try
                {
                    DockDefinition definition = BuildDockDefinition();
                    int linkCount = _session.BuildDockLinks(definition).Length;
                    var dock = _session.CreateDock(definition);
                    _selectedLinkSet = dock.Links;
                    SetTopologyFeedback(
                        Locale.GetString("created-dock-feedback", ("count", linkCount)),
                        false);
                }
                catch (Exception exception)
                {
                    SetTopologyFeedback(Locale.GetString("could-not-create-dock", ("message", exception.Message)), true);
                }
            }

            ImGui.EndDisabled();
        }
    }

    private bool DrawDockEndpoint(string label, ref int simulationIndex, ref int chunkIndex, ref int face)
    {
        if (_simulationSurfaces.Count == 0)
        {
            ImGui.TextDisabled(Locale.GetString("no-simulation-for-endpoint", ("label", label)));
            return false;
        }

        bool changed = false;
        simulationIndex = Math.Clamp(simulationIndex, 0, _simulationSurfaces.Count - 1);
        string simulationLabel = _session.GetSimulationName(_simulationSurfaces[simulationIndex].Simulation.Id);

        if (ImGui.BeginCombo(Locale.GetString("endpoint-simulation", ("label", label)), simulationLabel))
        {
            for (int index = 0; index < _simulationSurfaces.Count; index++)
            {
                string candidate = _session.GetSimulationName(_simulationSurfaces[index].Simulation.Id);

                if (ImGui.Selectable(candidate, index == simulationIndex))
                {
                    simulationIndex = index;
                    chunkIndex = 0;
                    changed = true;
                }
            }

            ImGui.EndCombo();
        }

        AtmosChunkHandle[] chunks = _simulationSurfaces[simulationIndex].Simulation.GetChunkHandles().ToArray();
        chunkIndex = chunks.Length == 0 ? 0 : Math.Clamp(chunkIndex, 0, chunks.Length - 1);
        string chunkLabel = chunks.Length == 0 ? Locale.GetString("no-chunks") : FormatChunkPosition(chunks[chunkIndex].Position);
        if (ImGui.BeginCombo(Locale.GetString("endpoint-chunk", ("label", label)), chunkLabel))
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
        changed |= ImGui.Combo(Locale.GetString("endpoint-face", ("label", label)), ref face, faces, faces.Length);
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

        var selected = _session.World != null && _topologyPendingRemoval is { } handle
            ? _session.World.GetLinkSets().FirstOrDefault(set => set.Handle == handle)
            : null;

        if (selected == null)
        {
            ImGui.TextWrapped(Locale.GetString("selected-connection-no-longer-exists"));
            if (ImGui.Button(Locale.GetString("close"), new Vector2(120, 0)))
            {
                _removeTopologyModalOpen = false;
                _topologyPendingRemoval = null;
                ImGui.CloseCurrentPopup();
            }

            ImGui.SetItemDefaultFocus();

            return;
        }

        ImGui.TextWrapped(
            Locale.GetString(
                "remove-connection-confirmation",
                ("kind", FormatLinkSetKind(selected.Kind).ToLowerInvariant()),
                ("count", selected.Links.Count),
                ("pluralSuffix", selected.Links.Count == 1 ? string.Empty : "s")));

        ImGui.Spacing();
        if (ImGui.Button(Locale.GetString("remove-connection"), new Vector2(160, 0)))
        {
            try
            {
                _session.DestroyLinkSet(selected.Handle);
                _topologyPendingRemoval = null;
                _removeTopologyModalOpen = false;
                SetTopologyFeedback(Locale.GetString("queued-connection-removal"), false);
                ImGui.CloseCurrentPopup();
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                SetTopologyFeedback(Locale.GetString("could-not-remove-connection", ("message", exception.Message)), true);
                _removeTopologyModalOpen = false;
                ImGui.CloseCurrentPopup();
            }
        }

        ImGui.SameLine();
        if (ImGui.Button(Locale.GetString("cancel"), new Vector2(120, 0)))
        {
            _removeTopologyModalOpen = false;
            _topologyPendingRemoval = null;
            ImGui.CloseCurrentPopup();
        }

        ImGui.SetItemDefaultFocus();
    }

    private DockDefinition BuildDockDefinition()
    {
        var firstSurface = _simulationSurfaces[Math.Clamp(_dockFirstSimulation, 0, _simulationSurfaces.Count - 1)];
        var secondSurface = _simulationSurfaces[Math.Clamp(_dockSecondSimulation, 0, _simulationSurfaces.Count - 1)];
        AtmosChunkHandle[] firstChunks = firstSurface.Simulation.GetChunkHandles().ToArray();
        AtmosChunkHandle[] secondChunks = secondSurface.Simulation.GetChunkHandles().ToArray();
        if (firstChunks.Length == 0 || secondChunks.Length == 0)
            throw new InvalidOperationException(Locale.GetString("each-dock-endpoint-needs-a-chunk"));

        var firstChunk = firstChunks[Math.Clamp(_dockFirstChunk, 0, firstChunks.Length - 1)];
        var secondChunk = secondChunks[Math.Clamp(_dockSecondChunk, 0, secondChunks.Length - 1)];
        return new DockDefinition(
            new DockEndpoint(firstSurface.Simulation.Id, firstChunk.Position, (DockFace)_dockFirstFace),
            new DockEndpoint(secondSurface.Simulation.Id, secondChunk.Position, (DockFace)_dockSecondFace),
            _dockRotation,
            _dockFlipU,
            _dockFlipV,
            GetTopologyFlags(),
            _topologyAreaFraction);
    }

    private AtmosCellRef? GetSelectedAtmosCell()
    {
        if (_simulation == null || !_selectedCell.HasValue)
            return null;

        return new AtmosCellRef(
            _simulation.Id,
            new AtmosChunkHandle(_selectedCell.Value.Chunk.Position),
            _selectedCell.Value.LocalIndex);
    }

    private ExplicitLinkFlags GetTopologyFlags()
    {
        return (_topologyGas ? ExplicitLinkFlags.GasTransport : ExplicitLinkFlags.None) |
               (_topologyThermal ? ExplicitLinkFlags.ThermalTransport : ExplicitLinkFlags.None);
    }

    private void CreatePortalFromCapturedEndpoints()
    {
        if (_session.World == null || !_portalFirst.HasValue || !_portalSecond.HasValue)
            return;

        try
        {
            var portal = _session.CreatePortal(
                _portalFirst.Value,
                _portalSecond.Value,
                GetTopologyFlags(),
                _topologyAreaFraction);

            _selectedLinkSet = portal.Links;
            _portalFirst = null;
            _portalSecond = null;
            SetTopologyFeedback(Locale.GetString("created-portal-feedback"), false);
        }
        catch (Exception exception)
        {
            SetTopologyFeedback(Locale.GetString("could-not-create-portal", ("message", exception.Message)), true);
        }
    }

    private void DrawTopologyOverlay()
    {
        if (_session.World == null || _simulation == null || _drawData == null)
            return;

        foreach (var set in _session.World.GetLinkSets())
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
        return $"{GetSimulationName(cell.Simulation)}\n{FormatChunkPosition(cell.Chunk.Position)}, " +
            Locale.GetString("voxel-number", ("index", cell.LocalVoxelIndex));
    }

    // A dock built in the Viewer gives every link the same fraction, but CreateLinks callers need not.
    private static string FormatAreaFraction(IReadOnlyList<ExplicitLinkDefinition> links)
    {
        if (links.Count == 0)
            return Locale.GetString("not-applicable");

        float? first = links[0].AreaFraction;
        for (int index = 1; index < links.Count; index++)
        {
            if (links[index].AreaFraction != first)
                return Locale.GetString("mixed");
        }

        return first.HasValue
            ? Locale.GetString("area-fraction-of-face", ("fraction", first.Value.ToString("0.00")))
            : Locale.GetString("config-default");
    }

    private static string FormatLinkSetKind(ExplicitLinkSetKind kind)
    {
        return kind switch
        {
            ExplicitLinkSetKind.Arbitrary => Locale.GetString("link-set"),
            ExplicitLinkSetKind.Portal => Locale.GetString("portal"),
            ExplicitLinkSetKind.Dock => Locale.GetString("dock"),
            _ => kind.ToString()
        };
    }

    private static string FormatLinkSetState(AtmosWorldLinkSetState state)
    {
        return state switch
        {
            AtmosWorldLinkSetState.PendingActivation => Locale.GetString("pending-activation"),
            AtmosWorldLinkSetState.Active => Locale.GetString("active"),
            AtmosWorldLinkSetState.PendingRemoval => Locale.GetString("pending-removal"),
            _ => state.ToString()
        };
    }

    private void SetTopologyFeedback(string message, bool isError)
    {
        _topologyFeedback = message;
        _topologyFeedbackIsError = isError;
    }
}
