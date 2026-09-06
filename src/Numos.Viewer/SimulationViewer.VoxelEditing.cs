using System.Numerics;
using ImGuiNET;
using Numos.API;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.SimDrawer;
using Numos.Viewer.Rendering.Viewport;
using Numos.Viewer.Ui;
using Raylib_cs;

namespace Numos.Viewer;

public partial class SimulationViewer
{
    private const string VoxelContextPopupId = "Voxel Actions##voxel-context";
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
        ImGui.Text("3D Voxel");
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
                if (!cell.IsVisible || cell.VisibleFaces == VoxelFaceMask.None)
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
        bool canEdit = _replayTimeline?.IsInspecting != true;
        if (viewport.IsHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right) && hovered.HasValue)
        {
            if (!_selectedCells.Contains(hovered.Value))
                SetSingleVoxelSelection(hovered.Value);

            ImGui.OpenPopup(VoxelContextPopupId);
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
                ViewerTheme.Selection.X,
                ViewerTheme.Selection.Y,
                ViewerTheme.Selection.Z,
                0.18f));

        uint border = ImGui.ColorConvertFloat4ToU32(ViewerTheme.Selection);
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

        SliceCellDrawData? start = null;
        SliceCellDrawData? end = null;
        foreach (var cell in _sliceDrawData.Cells)
        {
            if (cell.Address == _voxelDragAnchor.Value)
                start = cell;

            if (hovered.HasValue && cell.Address == hovered.Value)
                end = cell;
        }

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
        foreach (var cell in _sliceDrawData.Cells)
        {
            if (cell.U >= minimumU && cell.U <= maximumU && cell.V >= minimumV && cell.V <= maximumV)
                addresses.Add(cell.Address);
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
                if (!cell.IsVisible || cell.VisibleFaces == VoxelFaceMask.None)
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
        if (!ImGui.BeginPopup(VoxelContextPopupId))
            return;

        ImGui.TextDisabled($"{_selectedCells.Count} voxel{(_selectedCells.Count == 1 ? string.Empty : "s")} selected");
        if (_replayTimeline?.IsInspecting == true)
            ImGui.BeginDisabled();

        RenderContextInjectionMenu();

        if (ImGui.MenuItem("Clear Gas"))
            ApplyClearGas(_selectedCells);

        if (ImGui.MenuItem("Clear Cell"))
            ApplyClearCell(_selectedCells);

        if (ImGui.BeginMenu("Set Classification"))
        {
            if (ImGui.MenuItem("Unassigned (0)"))
                ApplyClassification(_selectedCells, VoxelClassification.RoomUnassigned);

            if (ImGui.MenuItem("Solid (-2)"))
                ApplyClassification(_selectedCells, VoxelClassification.RoomSolid);

            if (ImGui.MenuItem("Void (-1)"))
                ApplyClassification(_selectedCells, VoxelClassification.RoomVoid);

            if (ImGui.MenuItem($"Draft ({_voxelClassificationDraft})"))
                ApplyClassification(_selectedCells, _voxelClassificationDraft);

            ImGui.EndMenu();
        }

        if (ImGui.MenuItem($"Set Temperature to {_injectionTemperature:F1} K"))
            ApplyTemperature(_selectedCells, _injectionTemperature);

        if (_replayTimeline?.IsInspecting == true)
            ImGui.EndDisabled();

        ImGui.Separator();
        if (ImGui.MenuItem("Frame Voxel") &&
            _selectedCell.HasValue &&
            _drawData != null &&
            _drawData.Chunks.TryGetValue(_selectedCell.Value.Chunk.Position, out var chunk))
        {
            var coordinates = chunk.GetCoordinates(_selectedCell.Value.LocalIndex);
            MoveCameraToVoxel(chunk, coordinates.X, coordinates.Y, coordinates.Z);
        }

        ImGui.EndPopup();
    }

    private void RenderContextInjectionMenu()
    {
        if (!ImGui.BeginMenu("Inject"))
            return;

        if (_config == null || _config.GasRegistry.Count == 0)
        {
            ImGui.TextDisabled("No gases registered");
            ImGui.EndMenu();
            return;
        }

        for (int gasId = 0; gasId < _config.GasRegistry.Count; gasId++)
        {
            ImGui.PushID(gasId);
            if (ImGui.BeginMenu(FormatGas(gasId)))
            {
                ImGui.SetNextItemWidth(180f);
                ImGui.SliderFloat(
                    "Moles##context-injection",
                    ref _contextInjectionMoles,
                    0.01f,
                    100f,
                    "%.2f mol",
                    ImGuiSliderFlags.Logarithmic);

                ImGui.TextDisabled($"Temperature: {_injectionTemperature:F1} K");
                if (ImGui.Button("Inject##context-confirm", new Vector2(140f, 0f)))
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
            address => _simulation!.SetVoxelClassification(
                new AtmosChunkHandle(address.Chunk.Position),
                address.LocalIndex,
                new VoxelClassification(classification)),
            $"Set classification {classification}");
    }

    private void ApplyGasInjection(IEnumerable<VoxelAddress> addresses)
    {
        if (_config == null || _config.GasRegistry.Count == 0)
        {
            SetProjectMessage("Register a gas before injecting voxels.", true);
            return;
        }

        _injectionGasId = Math.Clamp(_injectionGasId, 0, _config.GasRegistry.Count - 1);
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
            address => _simulation!.AddGasToVoxel(
                new AtmosChunkHandle(address.Chunk.Position),
                address.LocalIndex,
                gasId,
                moles,
                temperature),
            $"Injected {moles:G4} mol {FormatGas(gasId)} into");
    }

    private void ApplyClearGas(IEnumerable<VoxelAddress> addresses)
    {
        ApplyVoxelMutation(
            addresses,
            address => _simulation!.GetVoxelGasMixture(
                new AtmosChunkHandle(address.Chunk.Position),
                address.LocalIndex).Clear(),
            "Cleared gas from");
    }

    private void ApplyClearCell(IEnumerable<VoxelAddress> addresses)
    {
        ApplyVoxelMutation(
            addresses,
            address =>
            {
                var handle = new AtmosChunkHandle(address.Chunk.Position);
                _simulation!.GetVoxelGasMixture(handle, address.LocalIndex).Clear();
                _simulation.SetVoxelClassification(
                    handle,
                    address.LocalIndex,
                    new VoxelClassification(VoxelClassification.RoomUnassigned));
            },
            "Cleared");
    }

    private void ApplyTemperature(IEnumerable<VoxelAddress> addresses, float temperature)
    {
        ApplyVoxelMutation(
            addresses,
            address => _simulation!.SetVoxelTemperature(
                new AtmosChunkHandle(address.Chunk.Position),
                address.LocalIndex,
                temperature),
            $"Set temperature to {temperature:F1} K for");
    }

    private void ApplyVoxelMutation(IEnumerable<VoxelAddress> addresses, Action<VoxelAddress> mutation, string action)
    {
        VoxelAddress[] targets = addresses.Distinct().ToArray();
        if (targets.Length == 0 || _simulation == null || _replayTimeline?.IsInspecting == true)
            return;

        try
        {
            foreach (var address in targets)
                mutation(address);

            SetProjectMessage($"{action} {targets.Length} voxel{(targets.Length == 1 ? string.Empty : "s")}.", false);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            SetProjectMessage($"Could not edit voxel selection: {exception.Message}", true);
        }
    }
}