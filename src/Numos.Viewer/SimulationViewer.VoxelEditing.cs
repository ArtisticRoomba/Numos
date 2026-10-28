using System.Numerics;
using ImGuiNET;
using Numos.Workbench;
using Numos.API;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.SimDrawer;
using Numos.Viewer.Rendering.Viewport;
using Numos.Viewer.Ui;
using Raylib_cs;

namespace Numos.Viewer;

public partial class SimulationViewer
{
    private float _contextInjectionMoles = 1f;

    private void Update3DPicking()
    {
        _hovered3DCell = null;
        if (_viewport == null || _drawData == null)
        {
            RebuildHighlights();
            return;
        }

        if (_viewport.IsHovered)
            _hovered3DCell = Pick3DVoxel(_viewport);

        HandleVoxelPointer(_viewport, _hovered3DCell, 3);
        RebuildHighlights();
    }

    private void Render3DVoxelTooltip()
    {
        if (_viewport is not { IsHovered: true } ||
            !_hovered3DCell.HasValue ||
            ImGui.IsMouseDown(ImGuiMouseButton.Left) ||
            ImGui.IsMouseDown(ImGuiMouseButton.Middle))
        {
            return;
        }

        ImGui.BeginTooltip();
        ImGui.Text(Locale.GetString("3d-voxel"));
        ImGui.Separator();
        DrawCellSelectionDetails(_hovered3DCell.Value);
        ImGui.EndTooltip();
    }

    private VoxelAddress? Pick3DVoxel(SimulationViewport viewport)
    {
        var mouse = new Vector2(
            viewport.NormalizedMousePosition.X * viewport.Width,
            (1f - viewport.NormalizedMousePosition.Y) * viewport.Height);

        var ray = Raylib.GetScreenToWorldRayEx(mouse, _camera3D, viewport.Width, viewport.Height);
        VoxelAddress? nearest = null;
        float nearestDistance = float.PositiveInfinity;
        foreach (var chunk in _drawData!.Chunks.Values)
        {
            if (!IsChunkVisibleForPicking(chunk))
                continue;

            var chunkMinimum = new Vector3(
                chunk.ChunkPosition.X * chunk.Dimensions.X,
                chunk.ChunkPosition.Y * chunk.Dimensions.Y,
                chunk.ChunkPosition.Z * chunk.Dimensions.Z);

            var chunkMaximum = chunkMinimum +
                               new Vector3(
                                   chunk.Dimensions.X,
                                   chunk.Dimensions.Y,
                                   chunk.Dimensions.Z);

            if (!Raylib.GetRayCollisionBox(ray, new BoundingBox(chunkMinimum, chunkMaximum)).Hit)
                continue;

            ReadOnlySpan<VoxelDrawData> cells = chunk.Cells;
            for (int localIndex = 0; localIndex < cells.Length; localIndex++)
            {
                ref readonly var cell = ref cells[localIndex];
                if (!IsSelectableIn3D(cell))
                    continue;

                var world = chunk.GetWorldCoordinates((ushort)localIndex);
                var collision = Raylib.GetRayCollisionBox(
                    ray,
                    new BoundingBox(
                        new Vector3(world.X, world.Y, world.Z),
                        new Vector3(world.X + 1f, world.Y + 1f, world.Z + 1f)));

                if (collision.Hit && collision.Distance < nearestDistance)
                {
                    nearestDistance = collision.Distance;
                    nearest = new VoxelAddress(chunk.Identity, (ushort)localIndex);
                }
            }
        }

        return nearest;
    }

    private bool IsChunkVisibleForPicking(ChunkDrawData chunk)
    {
        return _drawData!.HasCurrentVisualizationMapping(chunk) &&
               (!_focusedChunk.HasValue || chunk.Identity == _focusedChunk.Value);
    }

    private void HandleVoxelPointer(SimulationViewport viewport, VoxelAddress? hovered, int viewportKind)
    {
        bool canEdit = _session.Timeline?.IsInspecting != true;
        if (viewport.IsHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right) && hovered.HasValue)
        {
            if (!_selectedCells.Contains(hovered.Value))
                SetSingleVoxelSelection(hovered.Value);

            ImGui.OpenPopup("Voxel Actions##voxel-context");
        }

        if (viewport.IsHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            _voxelDragStart = viewport.NormalizedMousePosition;
            _voxelDragAnchor = hovered;
            _voxelDragViewport = viewportKind;
            _paintedCells.Clear();
            _lastPaintedCell = null;
        }

        if (_voxelDragViewport == viewportKind && ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            if (_voxelEditTool != VoxelEditTool.Select && canEdit && hovered.HasValue)
                PaintTo(hovered.Value);

            DrawSelectionRectangle(viewport);
        }

        if (_voxelDragViewport != viewportKind || !ImGui.IsMouseReleased(ImGuiMouseButton.Left))
            return;

        if (_voxelEditTool == VoxelEditTool.Select)
        {
            if (viewportKind == 2)
                CompleteSliceSelection(hovered);
            else
                Complete3DSelection(viewport, hovered);
        }

        _voxelDragStart = null;
        _voxelDragAnchor = null;
        _voxelDragViewport = 0;
        _paintedCells.Clear();
        _lastPaintedCell = null;
    }

    private void DrawSelectionRectangle(SimulationViewport viewport)
    {
        if (_voxelEditTool != VoxelEditTool.Select || !_voxelDragStart.HasValue)
            return;

        var start = NormalizedToScreen(viewport, _voxelDragStart.Value);
        var end = NormalizedToScreen(viewport, viewport.NormalizedMousePosition);
        var minimum = Vector2.Min(start, end);
        var maximum = Vector2.Max(start, end);
        var draw = ImGui.GetForegroundDrawList();
        uint fill = ImGui.ColorConvertFloat4ToU32(
            new Vector4(
                ViewerTheme.SelectionHighlight.X,
                ViewerTheme.SelectionHighlight.Y,
                ViewerTheme.SelectionHighlight.Z,
                0.18f));

        uint border = ImGui.ColorConvertFloat4ToU32(ViewerTheme.SelectionHighlight);
        draw.AddRectFilled(minimum, maximum, fill);
        draw.AddRect(minimum, maximum, border);
    }

    private static Vector2 NormalizedToScreen(SimulationViewport viewport, Vector2 normalized)
    {
        return new Vector2(
            viewport.ImageMinimum.X + normalized.X * (viewport.ImageMaximum.X - viewport.ImageMinimum.X),
            viewport.ImageMinimum.Y + (1f - normalized.Y) * (viewport.ImageMaximum.Y - viewport.ImageMinimum.Y));
    }

    private void CompleteSliceSelection(VoxelAddress? hovered)
    {
        if (_sliceDrawData == null || !_voxelDragAnchor.HasValue)
        {
            CompleteClickSelection(hovered);
            return;
        }

        SliceCellDrawData? start = FindSliceCell(_voxelDragAnchor.Value);
        SliceCellDrawData? end = hovered.HasValue ? FindSliceCell(hovered.Value) : null;

        if (!start.HasValue || !end.HasValue)
        {
            CompleteClickSelection(hovered);
            return;
        }

        int minimumU = Math.Min(start.Value.U, end.Value.U);
        int maximumU = Math.Max(start.Value.U, end.Value.U);
        int minimumV = Math.Min(start.Value.V, end.Value.V);
        int maximumV = Math.Max(start.Value.V, end.Value.V);
        var addresses = new List<VoxelAddress>();
        for (int v = minimumV; v <= maximumV; v++)
        {
            for (int u = minimumU; u <= maximumU; u++)
            {
                if (_sliceDrawData.TryGetCell(u, v, out var cell))
                    addresses.Add(cell.Address);
            }
        }

        SetVoxelSelection(addresses, end.Value.Address, ImGui.GetIO().KeyCtrl);
    }

    private void Complete3DSelection(SimulationViewport viewport, VoxelAddress? hovered)
    {
        if (!_voxelDragStart.HasValue || _drawData == null)
        {
            CompleteClickSelection(hovered);
            return;
        }

        var start = new Vector2(
            _voxelDragStart.Value.X * viewport.Width,
            (1f - _voxelDragStart.Value.Y) * viewport.Height);

        var end = new Vector2(
            viewport.NormalizedMousePosition.X * viewport.Width,
            (1f - viewport.NormalizedMousePosition.Y) * viewport.Height);

        var minimum = Vector2.Min(start, end);
        var maximum = Vector2.Max(start, end);
        const float ClickTolerance = 3f;
        if (Vector2.DistanceSquared(start, end) <= ClickTolerance * ClickTolerance)
        {
            CompleteClickSelection(hovered ?? _voxelDragAnchor);
            return;
        }

        var addresses = new List<VoxelAddress>();
        foreach (var chunk in _drawData.Chunks.Values)
        {
            if (!IsChunkVisibleForPicking(chunk))
                continue;

            ReadOnlySpan<VoxelDrawData> cells = chunk.Cells;
            for (int localIndex = 0; localIndex < cells.Length; localIndex++)
            {
                ref readonly var cell = ref cells[localIndex];
                if (!IsSelectableIn3D(cell))
                    continue;

                var world = chunk.GetWorldCoordinates((ushort)localIndex);
                var center = new Vector3(world.X + 0.5f, world.Y + 0.5f, world.Z + 0.5f);
                var projected = Raylib.GetWorldToScreenEx(center, _camera3D, viewport.Width, viewport.Height);
                if (projected.X >= minimum.X &&
                    projected.X <= maximum.X &&
                    projected.Y >= minimum.Y &&
                    projected.Y <= maximum.Y)
                {
                    addresses.Add(new VoxelAddress(chunk.Identity, (ushort)localIndex));
                }
            }
        }

        SetVoxelSelection(addresses, hovered ?? addresses.FirstOrDefault(), ImGui.GetIO().KeyCtrl);
    }

    private SliceCellDrawData? FindSliceCell(VoxelAddress address)
    {
        if (_sliceDrawData != null &&
            _sliceDrawData.TryLocate(address, out int u, out int v) &&
            _sliceDrawData.TryGetCell(u, v, out var cell))
        {
            return cell;
        }

        return null;
    }

    private static bool IsSelectableIn3D(in VoxelDrawData cell)
    {
        return cell.IsVisible && cell.VisibleFaces != VoxelFaceMask.None ||
               cell.RoomId == VoxelClassification.RoomSolid ||
               cell.RoomId == VoxelClassification.RoomVoid;
    }

    private void CompleteClickSelection(VoxelAddress? address)
    {
        if (!address.HasValue)
        {
            if (!ImGui.GetIO().KeyCtrl)
                ClearVoxelSelection();

            return;
        }

        if (ImGui.GetIO().KeyCtrl)
        {
            if (!_selectedCells.Add(address.Value))
                _selectedCells.Remove(address.Value);

            _selectedCell = _selectedCells.Contains(address.Value)
                ? address
                : _selectedCells.Cast<VoxelAddress?>().FirstOrDefault();

            if (_selectedCell.HasValue)
                SyncVoxelDraft(_selectedCell.Value);

            RebuildHighlights();
            return;
        }

        SetSingleVoxelSelection(address.Value);
    }

    private void SetSingleVoxelSelection(VoxelAddress address)
    {
        _selectedCells.Clear();
        _selectedCells.Add(address);
        _selectedCell = address;
        SyncVoxelDraft(address);
        RebuildHighlights();
    }

    private void SetVoxelSelection(IEnumerable<VoxelAddress> addresses, VoxelAddress primary, bool additive)
    {
        if (!additive)
            _selectedCells.Clear();

        foreach (var address in addresses)
            _selectedCells.Add(address);

        _selectedCell = _selectedCells.Contains(primary)
            ? primary
            : _selectedCells.Cast<VoxelAddress?>().FirstOrDefault();

        if (_selectedCell.HasValue)
            SyncVoxelDraft(_selectedCell.Value);

        RebuildHighlights();
    }

    private void ClearVoxelSelection()
    {
        _selectedCell = null;
        _selectedCells.Clear();
        RebuildHighlights();
    }

    private void SyncVoxelDraft(VoxelAddress address)
    {
        if (TryGetVoxelDetails(address, out var details))
            _voxelClassificationDraft = details.RoomId;
    }

    private void ApplyPaintMutation(VoxelAddress address)
    {
        switch (_voxelEditTool)
        {
            case VoxelEditTool.PaintClassification:
                ApplyClassification([address], _voxelClassificationDraft);
                break;
            case VoxelEditTool.PaintGas:
                ApplyGasInjection([address]);
                break;
            case VoxelEditTool.EraseGas:
                ApplyClearGas([address]);
                break;
        }
    }

    private void PaintTo(VoxelAddress address)
    {
        if (!_lastPaintedCell.HasValue ||
            _lastPaintedCell.Value.Chunk != address.Chunk ||
            _drawData == null ||
            !_drawData.Chunks.TryGetValue(address.Chunk.Position, out var chunk))
        {
            PaintVoxelOnce(address);
            _lastPaintedCell = address;
            return;
        }

        var from = chunk.GetCoordinates(_lastPaintedCell.Value.LocalIndex);
        var to = chunk.GetCoordinates(address.LocalIndex);
        int steps = Math.Max(Math.Abs(to.X - from.X), Math.Max(Math.Abs(to.Y - from.Y), Math.Abs(to.Z - from.Z)));
        for (int step = 0; step <= steps; step++)
        {
            float amount = steps == 0 ? 0f : step / (float)steps;
            int x = (int)MathF.Round(from.X + (to.X - from.X) * amount);
            int y = (int)MathF.Round(from.Y + (to.Y - from.Y) * amount);
            int z = (int)MathF.Round(from.Z + (to.Z - from.Z) * amount);
            PaintVoxelOnce(new VoxelAddress(chunk.Identity, chunk.GetLocalIndex(x, y, z)));
        }

        _lastPaintedCell = address;
    }

    private void PaintVoxelOnce(VoxelAddress address)
    {
        if (_paintedCells.Add(address))
            ApplyPaintMutation(address);
    }

    private void RenderVoxelContextMenu()
    {
        if (!ImGui.BeginPopup("Voxel Actions##voxel-context"))
            return;

        ImGui.TextDisabled(Locale.GetString("selected-voxel-count", ("count", _selectedCells.Count)));
        if (_session.Timeline?.IsInspecting == true)
            ImGui.BeginDisabled();

        RenderContextInjectionMenu();

        RenderContextPortalMenu();

        if (ImGui.MenuItem(Locale.GetString("clear-gas")))
            ApplyClearGas(_selectedCells);

        if (ImGui.MenuItem(Locale.GetString("clear-cell")))
            ApplyClearCell(_selectedCells);

        if (ImGui.BeginMenu(Locale.GetString("set-classification")))
        {
            if (ImGui.MenuItem(Locale.GetString("unassigned-classification")))
                ApplyClassification(_selectedCells, VoxelClassification.RoomUnassigned);

            if (ImGui.MenuItem(Locale.GetString("solid-classification")))
                ApplyClassification(_selectedCells, VoxelClassification.RoomSolid);

            if (ImGui.MenuItem(Locale.GetString("void-classification")))
                ApplyClassification(_selectedCells, VoxelClassification.RoomVoid);

            if (ImGui.MenuItem(Locale.GetString("draft-classification", ("classification", _voxelClassificationDraft))))
                ApplyClassification(_selectedCells, _voxelClassificationDraft);

            ImGui.EndMenu();
        }

        if (ImGui.MenuItem(Locale.GetString("set-temperature", ("temperature", _injectionTemperature.ToString("F1")))))
            ApplyTemperature(_selectedCells, _injectionTemperature);

        if (_session.Timeline?.IsInspecting == true)
            ImGui.EndDisabled();

        ImGui.Separator();
        if (ImGui.MenuItem(Locale.GetString("frame-voxel")) &&
            _selectedCell.HasValue &&
            _drawData != null &&
            _drawData.Chunks.TryGetValue(_selectedCell.Value.Chunk.Position, out var chunk))
        {
            var coordinates = chunk.GetCoordinates(_selectedCell.Value.LocalIndex);
            MoveCameraToVoxel(chunk, coordinates.X, coordinates.Y, coordinates.Z);
        }

        ImGui.EndPopup();
    }

    private void RenderContextPortalMenu()
    {
        AtmosCellRef? selected = GetSelectedAtmosCell();
        if (!selected.HasValue || !ImGui.BeginMenu(Locale.GetString("portal")))
            return;

        if (ImGui.MenuItem(Locale.GetString("start-portal-here")))
        {
            _portalFirst = selected;
            _portalSecond = null;
            _topologyFeedback = null;
        }

        bool canCreate = _portalFirst.HasValue && GetTopologyFlags() != ExplicitLinkFlags.None;
        ImGui.BeginDisabled(!canCreate);
        if (ImGui.MenuItem(Locale.GetString("create-portal-to-here")))
        {
            _portalSecond = selected;
            CreatePortalFromCapturedEndpoints();
        }

        ImGui.EndDisabled();

        ImGui.Separator();
        if (_portalFirst.HasValue)
            ImGui.TextDisabled(Locale.GetString("portal-first-endpoint", ("cell", FormatCell(_portalFirst.Value).Replace('\n', ' '))));
        else
            ImGui.TextDisabled(Locale.GetString("portal-first-endpoint-instruction"));

        if (GetTopologyFlags() == ExplicitLinkFlags.None)
            ImGui.TextDisabled(Locale.GetString("enable-transport-mode-in-world-topology"));

        ImGui.EndMenu();
    }

    private void RenderContextInjectionMenu()
    {
        if (!ImGui.BeginMenu(Locale.GetString("inject")))
            return;

        if (_session.Config == null || _session.Config.GasRegistry.Count == 0)
        {
            ImGui.TextDisabled(Locale.GetString("no-gases-registered"));
            ImGui.EndMenu();
            return;
        }

        for (int gasId = 0; gasId < _session.Config.GasRegistry.Count; gasId++)
        {
            ImGui.PushID(gasId);
            if (ImGui.BeginMenu(FormatGas(gasId)))
            {
                ImGui.SetNextItemWidth(180f);
                ImGui.SliderFloat(
                    Locale.GetString("moles-context-injection"),
                    ref _contextInjectionMoles,
                    0.01f,
                    100f,
                    "%.2f mol",
                    ImGuiSliderFlags.Logarithmic);

                ImGui.TextDisabled(Locale.GetString("temperature-kelvin", ("temperature", _injectionTemperature.ToString("F1"))));
                if (ImGui.Button(Locale.GetString("inject-context-confirm"), new Vector2(140f, 0f)))
                {
                    ApplyGasInjection(
                        _selectedCells,
                        gasId,
                        _contextInjectionMoles,
                        _injectionTemperature);

                    ImGui.CloseCurrentPopup();
                }

                ImGui.EndMenu();
            }

            ImGui.PopID();
        }

        ImGui.EndMenu();
    }

    private void ApplyClassification(IEnumerable<VoxelAddress> addresses, int classification)
    {
        ApplyVoxelMutation(
            addresses,
            Locale.GetString("set-classification-action", ("classification", classification)),
            cells => _session.SetVoxelClassification(cells, classification));
    }

    private void ApplyGasInjection(IEnumerable<VoxelAddress> addresses)
    {
        if (_session.Config == null || _session.Config.GasRegistry.Count == 0)
        {
            _voxelFeedback = Report(Locale.GetString("register-gas-before-injecting-voxels"), true);
            return;
        }

        _injectionGasId = Math.Clamp(_injectionGasId, 0, _session.Config.GasRegistry.Count - 1);
        ApplyGasInjection(addresses, _injectionGasId, _injectionMoles, _injectionTemperature);
    }

    private void ApplyGasInjection(
        IEnumerable<VoxelAddress> addresses,
        int gasId,
        float moles,
        float temperature)
    {
        ApplyVoxelMutation(
            addresses,
            Locale.GetString("injected-gas-into", ("moles", moles.ToString("G4")), ("gas", FormatGas(gasId))),
            cells => _session.AddVoxelGas(cells, gasId, moles, temperature));
    }

    private void ApplyClearGas(IEnumerable<VoxelAddress> addresses)
    {
        ApplyVoxelMutation(addresses, Locale.GetString("cleared-gas-from"), cells => _session.ClearVoxelGas(cells));
    }

    private void ApplyClearCell(IEnumerable<VoxelAddress> addresses)
    {
        ApplyVoxelMutation(addresses, Locale.GetString("cleared-cells"), cells => _session.ClearVoxelCells(cells));
    }

    private void ApplyTemperature(IEnumerable<VoxelAddress> addresses, float temperature)
    {
        ApplyVoxelMutation(
            addresses,
            Locale.GetString("set-temperature-for", ("temperature", temperature.ToString("F1"))),
            cells => _session.SetVoxelTemperature(cells, temperature));
    }

    private void ApplyVoxelMutation(
        IEnumerable<VoxelAddress> addresses,
        string action,
        Func<IReadOnlyCollection<VoxelCell>, int> mutation)
    {
        VoxelCell[] targets = addresses.Select(VoxelCell.From).Distinct().ToArray();
        if (targets.Length == 0 || _simulation == null || _session.IsInspecting)
            return;

        try
        {
            int edited = mutation(targets);
            _voxelFeedback = new FeedbackMessage(
                Locale.GetString("voxel-mutation-feedback", ("action", action), ("edited", edited)),
                false);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            // The session already logged the failure.
        }
    }
}
