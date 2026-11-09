using System.Numerics;
using ImGuiNET;
using Numos.Workbench;
using Numos.API;
using Numos.Maths;
using Numos.SimDrawer;
using Numos.Viewer.Rendering;
using Numos.Viewer.Rendering.Viewport;
using Numos.Viewer.Ui;
using Numos.Viewer.Workspace;
using Raylib_cs;

namespace Numos.Viewer;

public partial class SimulationViewer
{
    /// <summary>
    ///     Draws the status, endpoints, and remove button of one portal, dock, or link set.
    /// </summary>
    private void RenderSelectedLinkSet(AtmosWorldLinkSetSnapshot selected, bool inspecting)
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
            ImGuiExtensions.StatusField(Locale.GetString("common-state-heading"), FormatLinkSetState(selected.State));
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
            RequestRemoveLinkSet(selected.Handle);

        ImGui.EndDisabled();
    }

    private void RequestRemoveLinkSet(ExplicitLinkSetHandle handle)
    {
        _document.TopologyPendingRemoval = handle;
        _document.RequestRemoveTopology = true;
        _document.RemoveTopologyModalOpen = true;
    }

    private void DrawTopologyFeedback()
    {
        if (!string.IsNullOrWhiteSpace(_document.TopologyFeedback))
        {
            ImGui.TextColored(
                _document.TopologyFeedbackIsError ? ViewerTheme.Error : ViewerTheme.Running,
                _document.TopologyFeedback);
        }
    }

    /// <summary>
    ///     Draws the transport options shared by new connections, then the new portal and new dock forms.
    /// </summary>
    /// <param name="inspecting">Whether editing is blocked.</param>
    /// <param name="focus">
    ///     <see cref="PropertiesForm.NewPortal" /> or <see cref="PropertiesForm.NewDock" /> opens that form and scrolls
    ///     to it.
    /// </param>
    private void RenderNewConnectionForm(bool inspecting, PropertiesForm focus = PropertiesForm.None)
    {
        ImGui.SeparatorText(Locale.GetString("new-connection"));
        ImGui.BeginDisabled(inspecting);
        if (ImGui.Checkbox(Locale.GetString("gas-transport"), ref _document.Topology.Gas))
            _document.TopologyFeedback = null;

        ImGui.EndDisabled();
        ImGuiExtensions.QuestionTooltip(Locale.GetString("gas-transport-tooltip"));
        ImGui.BeginDisabled(inspecting);
        if (ImGui.Checkbox(Locale.GetString("thermal-conduction"), ref _document.Topology.Thermal))
            _document.TopologyFeedback = null;

        ImGui.EndDisabled();
        ImGuiExtensions.QuestionTooltip(Locale.GetString("thermal-conduction-tooltip"));
        ImGui.BeginDisabled(inspecting);
        if (ImGui.SliderFloat(Locale.GetString("opening-area"), ref _document.Topology.AreaFraction, 0f, 1f, "%.2f " + Locale.GetString("of-a-face")))
            _document.TopologyFeedback = null;

        ImGui.EndDisabled();
        ImGuiExtensions.QuestionTooltip(Locale.GetString("opening-area-tooltip"));

        if (!_document.Topology.Gas && !_document.Topology.Thermal)
            ImGui.TextColored(ViewerTheme.Caution, Locale.GetString("enable-transport-mode"));

        if (focus == PropertiesForm.NewPortal)
            ImGui.SetNextItemOpen(true);

        if (ImGui.CollapsingHeader(Locale.GetString("new-portal"), ImGuiTreeNodeFlags.DefaultOpen))
        {
            if (focus == PropertiesForm.NewPortal)
                ImGui.SetScrollHereY(0f);

            ImGui.TextWrapped(Locale.GetString("pick-portal-endpoints-instructions"));
            RenderPortalEndpointPicks(inspecting);

            ImGui.BeginDisabled(
                !_document.Topology.PortalFirst.HasValue ||
                !_document.Topology.PortalSecond.HasValue ||
                GetTopologyFlags() == ExplicitLinkFlags.None ||
                inspecting);

            if (ImGui.Button(Locale.GetString("create-portal")))
                CreatePortalFromCapturedEndpoints();

            ImGui.EndDisabled();
        }

        if (focus == PropertiesForm.NewDock)
            ImGui.SetNextItemOpen(true);

        if (ImGui.CollapsingHeader(Locale.GetString("new-rectangular-face-dock")))
        {
            ImGui.BeginDisabled(inspecting);
            FocusFormHere(focus == PropertiesForm.NewDock);
            bool dockInputChanged = DrawDockEndpoint(
                Locale.GetString("first"),
                ref _document.Topology.DockFirstSimulation,
                ref _document.Topology.DockFirstChunk,
                ref _document.Topology.DockFirstFace);

            dockInputChanged |= DrawDockEndpoint(
                Locale.GetString("second"),
                ref _document.Topology.DockSecondSimulation,
                ref _document.Topology.DockSecondChunk,
                ref _document.Topology.DockSecondFace);

            string[] rotations = ["0°", "90°", "180°", "270°"];
            dockInputChanged |= ImGui.Combo(Locale.GetString("rotation"), ref _document.Topology.DockRotation, rotations, rotations.Length);
            dockInputChanged |= ImGui.Checkbox(Locale.GetString("flip-u"), ref _document.Topology.DockFlipU);
            ImGui.SameLine();
            dockInputChanged |= ImGui.Checkbox(Locale.GetString("flip-v"), ref _document.Topology.DockFlipV);
            if (dockInputChanged)
                _document.TopologyFeedback = null;

            ImGui.EndDisabled();
            ImGui.BeginDisabled(
                GetTopologyFlags() == ExplicitLinkFlags.None ||
                _document.Surfaces.Count == 0 ||
                inspecting);

            if (ImGui.Button(Locale.GetString("create-dock")))
            {
                try
                {
                    CreateDock(BuildDockDefinition());
                }
                catch (InvalidOperationException exception)
                {
                    SetTopologyFeedback(Locale.GetString("could-not-create-dock", ("message", exception.Message)), true);
                }
            }

            ImGui.EndDisabled();
        }
    }

    /// <summary>
    ///     Draws both endpoints of the new portal form with a Pick button each, then the armed pick's instruction.
    /// </summary>
    private void RenderPortalEndpointPicks(bool inspecting)
    {
        using (var table = ImGuiExtensions.BeginTable(
                   "portal-endpoints",
                   2,
                   ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchSame))
        {
            if (table.IsVisible)
            {
                RenderPortalEndpointCell(PortalEndpoint.First, _document.Topology.PortalFirst, inspecting);
                RenderPortalEndpointCell(PortalEndpoint.Second, _document.Topology.PortalSecond, inspecting);
            }
        }

        var target = _document.PortalCapture.Target;
        if (target != PortalEndpoint.None)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, ViewerTheme.Caution);
            ImGui.TextWrapped(Locale.GetString("picking-portal-endpoint", ("number", GetEndpointNumber(target))));
            ImGui.PopStyleColor();
        }
    }

    private void RenderPortalEndpointCell(PortalEndpoint endpoint, AtmosCellRef? cell, bool inspecting)
    {
        var capture = _document.PortalCapture;
        int number = GetEndpointNumber(endpoint);
        bool armed = capture.Target == endpoint;

        ImGui.TableNextColumn();
        ImGui.PushID(number);
        ImGuiExtensions.StatusField(
            Locale.GetString("endpoint-number", ("number", number)),
            cell is { } value ? FormatCell(value) : Locale.GetString("not-captured"));

        ImGui.BeginDisabled(inspecting);
        if (RenderPresetButton(Locale.GetString(armed ? "cancel-portal-pick" : "pick-portal-endpoint") + "##pick", armed))
        {
            if (armed)
                capture.Cancel();
            else
                capture.Arm(endpoint);

            _document.TopologyFeedback = null;
        }

        ImGui.EndDisabled();
        ImGui.PopID();
    }

    private static int GetEndpointNumber(PortalEndpoint endpoint)
    {
        return endpoint == PortalEndpoint.Second ? 2 : 1;
    }

    /// <summary>
    ///     Handles a viewport's pointer while a portal endpoint pick is armed, in place of the voxel tools.
    /// </summary>
    /// <remarks>
    ///     A press and release on the same voxel fills the armed endpoint, so a camera drag that ends elsewhere picks
    ///     nothing. The click neither selects the voxel nor reaches a paint tool, so the selection stays on Topology.
    ///     Escape ends the pick.
    /// </remarks>
    private void HandlePortalEndpointPick(SimulationViewport viewport, VoxelAddress? hovered, int viewportKind)
    {
        var capture = _document.PortalCapture;
        if (ImGui.IsKeyPressed(ImGuiKey.Escape, false) && !ImGui.GetIO().WantTextInput)
        {
            capture.Cancel();
            _voxelDragAnchor = null;
            _voxelDragViewport = 0;
            RequestNextFrame();
            return;
        }

        if (viewport.IsHovered)
        {
            ImGuiExtensions.WrappedTooltip(
                Locale.GetString("picking-portal-endpoint", ("number", GetEndpointNumber(capture.Target))));
        }

        if (viewport.IsHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            _voxelDragAnchor = hovered;
            _voxelDragViewport = viewportKind;
        }

        if (_voxelDragViewport != viewportKind || !ImGui.IsMouseReleased(ImGuiMouseButton.Left))
            return;

        if (hovered is { } address && address == _voxelDragAnchor && _simulation != null)
        {
            capture.Accept(
                new AtmosCellRef(_simulation.Id, new AtmosChunkHandle(address.Chunk.Position), address.LocalIndex),
                _document.Topology);

            _document.TopologyFeedback = null;
            RequestNextFrame();
        }

        _voxelDragAnchor = null;
        _voxelDragViewport = 0;
    }

    private bool DrawDockEndpoint(string label, ref int simulationIndex, ref int chunkIndex, ref int face)
    {
        if (_document.Surfaces.Count == 0)
        {
            ImGui.TextDisabled(Locale.GetString("no-simulation-for-endpoint", ("label", label)));
            return false;
        }

        bool changed = false;
        simulationIndex = Math.Clamp(simulationIndex, 0, _document.Surfaces.Count - 1);
        string simulationLabel = GetSimulationName(_document.Surfaces[simulationIndex].Id);

        if (ImGui.BeginCombo(Locale.GetString("endpoint-simulation", ("label", label)), simulationLabel))
        {
            for (int index = 0; index < _document.Surfaces.Count; index++)
            {
                string candidate = GetSimulationName(_document.Surfaces[index].Id);

                if (ImGui.Selectable(candidate, index == simulationIndex))
                {
                    simulationIndex = index;
                    chunkIndex = 0;
                    changed = true;
                }
            }

            ImGui.EndCombo();
        }

        IReadOnlyList<AtmosChunkHandle> chunks = _document.Surfaces[simulationIndex].Frame.ChunkHandles;
        chunkIndex = chunks.Count == 0 ? 0 : Math.Clamp(chunkIndex, 0, chunks.Count - 1);
        string chunkLabel = chunks.Count == 0 ? Locale.GetString("topology-no-chunks") : FormatChunkPosition(chunks[chunkIndex].Position);
        if (ImGui.BeginCombo(Locale.GetString("endpoint-chunk", ("label", label)), chunkLabel))
        {
            for (int index = 0; index < chunks.Count; index++)
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
        ImGuiExtensions.OpenPopupWhenRequested(popupId, ref _document.RequestRemoveTopology);
        ImGui.SetNextWindowSize(new Vector2(440, 0), ImGuiCond.Appearing);
        using var modal = ImGuiExtensions.BeginPopupModal(popupId, ref _document.RemoveTopologyModalOpen);
        if (!modal.IsVisible)
            return;

        var selected = _document.TopologyPendingRemoval is { } handle
            ? _frame.LinkSets.FirstOrDefault(set => set.Handle == handle)
            : null;

        if (selected == null)
        {
            ImGui.TextWrapped(Locale.GetString("selected-connection-no-longer-exists"));
            if (ImGui.Button(Locale.GetString("common-close"), new Vector2(120, 0)))
            {
                _document.RemoveTopologyModalOpen = false;
                _document.TopologyPendingRemoval = null;
                ImGui.CloseCurrentPopup();
            }

            ImGui.SetItemDefaultFocus();

            return;
        }

        ImGui.TextWrapped(
            Locale.GetString(
                "remove-connection-confirmation",
                ("kind", FormatLinkSetKind(selected.Kind).ToLowerInvariant()),
                ("count", selected.Links.Count)));

        ImGui.Spacing();
        ImGui.BeginDisabled(IsEditingBlocked);
        if (ImGui.Button(Locale.GetString("remove-connection"), new Vector2(160, 0)))
        {
            var target = selected.Handle;
            Send(
                session =>
                {
                    session.DestroyLinkSet(target);
                    return true;
                },
                result => SetTopologyFeedback(
                    result.Exception is { } exception
                        ? Locale.GetString("could-not-remove-connection", ("message", DescribeFailure(exception)))
                        : Locale.GetString("queued-connection-removal"),
                    result.Exception != null));

            _document.TopologyPendingRemoval = null;
            _document.RemoveTopologyModalOpen = false;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button(Locale.GetString("common-cancel"), new Vector2(120, 0)))
        {
            _document.RemoveTopologyModalOpen = false;
            _document.TopologyPendingRemoval = null;
            ImGui.CloseCurrentPopup();
        }

        ImGui.SetItemDefaultFocus();
    }

    private DockDefinition BuildDockDefinition()
    {
        var firstSurface = _document.Surfaces[Math.Clamp(_document.Topology.DockFirstSimulation, 0, _document.Surfaces.Count - 1)];
        var secondSurface = _document.Surfaces[Math.Clamp(_document.Topology.DockSecondSimulation, 0, _document.Surfaces.Count - 1)];
        IReadOnlyList<AtmosChunkHandle> firstChunks = firstSurface.Frame.ChunkHandles;
        IReadOnlyList<AtmosChunkHandle> secondChunks = secondSurface.Frame.ChunkHandles;
        if (firstChunks.Count == 0 || secondChunks.Count == 0)
            throw new InvalidOperationException(Locale.GetString("each-dock-endpoint-needs-a-chunk"));

        var firstChunk = firstChunks[Math.Clamp(_document.Topology.DockFirstChunk, 0, firstChunks.Count - 1)];
        var secondChunk = secondChunks[Math.Clamp(_document.Topology.DockSecondChunk, 0, secondChunks.Count - 1)];
        return new DockDefinition(
            new DockEndpoint(firstSurface.Id, firstChunk.Position, (DockFace)_document.Topology.DockFirstFace),
            new DockEndpoint(secondSurface.Id, secondChunk.Position, (DockFace)_document.Topology.DockSecondFace),
            _document.Topology.DockRotation,
            _document.Topology.DockFlipU,
            _document.Topology.DockFlipV,
            GetTopologyFlags(),
            _document.Topology.AreaFraction);
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
        return (_document.Topology.Gas ? ExplicitLinkFlags.GasTransport : ExplicitLinkFlags.None) |
               (_document.Topology.Thermal ? ExplicitLinkFlags.ThermalTransport : ExplicitLinkFlags.None);
    }

    private void CreateDock(DockDefinition definition)
    {
        Send(
            session =>
            {
                int linkCount = session.BuildDockLinks(definition).Length;
                return (Links: session.CreateDock(definition).Links, Count: linkCount);
            },
            result =>
            {
                if (result.Exception is { } exception)
                {
                    SetTopologyFeedback(
                        Locale.GetString("could-not-create-dock", ("message", DescribeFailure(exception))),
                        true);

                    return;
                }

                SelectTreeNode(TreeNodeId.ForLinkSet(ExplicitLinkSetKind.Dock, result.Value.Links), _host.Frame.Sequence);
                SetTopologyFeedback(Locale.GetString("created-dock-feedback", ("count", result.Value.Count)), false);
            });
    }

    private void CreatePortalFromCapturedEndpoints()
    {
        if (!_frame.HasProject || _document.Topology.PortalFirst is not { } first || _document.Topology.PortalSecond is not { } second)
            return;

        var flags = GetTopologyFlags();
        float areaFraction = _document.Topology.AreaFraction;
        Send(
            session => session.CreatePortal(first, second, flags, areaFraction).Links,
            result =>
            {
                if (result.Exception is { } exception)
                {
                    SetTopologyFeedback(
                        Locale.GetString("could-not-create-portal", ("message", DescribeFailure(exception))),
                        true);

                    return;
                }

                SelectTreeNode(TreeNodeId.ForLinkSet(ExplicitLinkSetKind.Portal, result.Value), _host.Frame.Sequence);
                _document.Topology.PortalFirst = null;
                _document.Topology.PortalSecond = null;
                SetTopologyFeedback(Locale.GetString("created-portal-feedback"), false);
            });
    }

    private void DrawTopologyOverlay()
    {
        if (_simulation == null || _drawData == null)
            return;

        foreach (var set in _frame.LinkSets)
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

        // The new portal form's endpoints, so a pick shows where it landed before the portal exists.
        var draftColor = SimulationRenderer.ToRaylibColor(ViewerTheme.Caution);
        DrawDraftEndpoint(_document.Topology.PortalFirst, draftColor);
        DrawDraftEndpoint(_document.Topology.PortalSecond, draftColor);
    }

    private void DrawDraftEndpoint(AtmosCellRef? cell, Color color)
    {
        if (cell is { } value && value.Simulation == _simulation?.Id && TryGetCellCenter(value, out var center))
            Raylib.DrawCubeWiresV(center, new Vector3(1.2f), color);
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
        _document.TopologyFeedback = message;
        _document.TopologyFeedbackIsError = isError;
    }
}
