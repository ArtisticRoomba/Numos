using System.Numerics;
using ImGuiNET;
using Numos.Workbench;
using Numos.API;
using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.Maths;
using Numos.SimDrawer;
using Numos.Viewer.Rendering;
using Numos.Viewer.Rendering.Viewport;
using Numos.Viewer.Ui;
using Raylib_cs;

namespace Numos.Viewer;

public partial class SimulationViewer
{
    private readonly List<SimulationSurface> _simulationSurfaces = [];
    private long _knownSimulationRevision = -1;
    private int _newSimulationDepth = 1;
    private int _newSimulationHeight = AtmosChunkConstants.DefaultHeight;
    private int _newSimulationWidth = AtmosChunkConstants.DefaultWidth;
    private bool _removeSimulationModalOpen;
    private bool _requestRemoveSimulation;
    private bool _showWorldPanel = true;
    private string? _simulationFeedback;
    private bool _simulationFeedbackIsError;
    private AtmosSimulationId? _simulationPendingRemoval;

    private void ReconcileSimulationSurfaces()
    {
        if (_session.World == null || !_session.World.TryGetSimulations(_knownSimulationRevision, out long revision, out AtmosSimulation[] simulations))
            return;

        _knownSimulationRevision = revision;
        HashSet<AtmosSimulationId> liveIds = simulations.Select(static simulation => simulation.Id).ToHashSet();
        if (_portalFirst is { } firstPortal && !liveIds.Contains(firstPortal.Simulation))
            _portalFirst = null;

        if (_portalSecond is { } secondPortal && !liveIds.Contains(secondPortal.Simulation))
            _portalSecond = null;

        foreach (var removed in _simulationSurfaces.Where(surface => !liveIds.Contains(surface.Simulation.Id)).ToArray())
        {
            removed.Dispose();
            _simulationSurfaces.Remove(removed);
        }

        foreach (var simulation in simulations)
        {
            var retained = _simulationSurfaces.FirstOrDefault(surface => surface.Simulation.Id == simulation.Id);
            if (retained != null)
            {
                retained.Simulation = simulation;
                continue;
            }

            var surface = new SimulationSurface(simulation);
            if (_windowInitialized)
            {
                surface.Viewport = new SimulationViewport(
                    TextureFilter.Bilinear,
                    new Color(0.04f, 0.04f, 0.05f, 1f));
            }

            _simulationSurfaces.Add(surface);
        }

        _simulationSurfaces.Sort(static (left, right) => left.Simulation.Id.CompareTo(right.Simulation.Id));
        _session.ReconcileSimulations();
        SetActiveSimulation(_session.ActiveSimulationId);
    }

    private void SetActiveSimulation(AtmosSimulationId? id)
    {
        if (_session.World == null || id == null || !_session.World.TryGetSimulation(id.Value, out var simulation))
        {
            if (_session.World != null)
                _session.SetActiveSimulation(null);

            _simulation = null;
            _viewport = null;
            return;
        }

        if (_session.ActiveSimulationId == id && ReferenceEquals(_simulation, simulation))
            return;

        SaveActiveSurfaceState();
        var resolved = simulation!;
        _session.SetActiveSimulation(id);
        _simulation = resolved;
        var surface = _simulationSurfaces.First(candidate => candidate.Simulation.Id == id.Value);
        _viewport = surface.Viewport;
        _camera3D = surface.Camera;
        ResetActivePresentationState();
        _frameSceneOnNextPresentation = surface.DrawData == null;
    }

    private void SaveActiveSurfaceState()
    {
        // The session can already point at another simulation by now, so key off the one being presented.
        if (_simulation == null)
            return;

        var surface =
            _simulationSurfaces.FirstOrDefault(candidate => candidate.Simulation.Id == _simulation.Id);

        if (surface == null)
            return;

        surface.Camera = _camera3D;
        if (_drawData != null)
            surface.DrawData = _drawData;
    }

    private void ResetActivePresentationState()
    {
        _liveChunkHandles.Clear();
        _liveChunkPositions.Clear();
        _chunkCollectionRevision = -1;
        _snapshotCache.Clear();
        _snapshotRequests.Clear();
        _orderedSnapshots.Clear();
        _staleSnapshotKeys.Clear();
        _voxelDetailCache.Clear();
        _drawData = null;
        _sliceDrawData = null;
        _sliceSourceFrame = null;
        _sliceFrameChunk = null;
        _sliceCameraFit = true;
        _hovered3DCell = null;
        _hoveredSliceCell = null;
        _selectedCell = null;
        _selectedCells.Clear();
        _highlights.Clear();
        _focusedChunk = null;
        _cameraInitialized = false;
    }

    private void RefreshSimulationSurfaces()
    {
        if (_frameBuilder == null)
            return;

        SaveActiveSurfaceState();
        foreach (var surface in _simulationSurfaces)
        {
            if (surface.Simulation.Id == _session.ActiveSimulationId && _drawData != null)
            {
                surface.DrawData = _drawData;
                continue;
            }

            RefreshSimulationSurface(surface);
        }
    }

    private void RefreshSimulationSurface(SimulationSurface surface)
    {
        var fields = _frameBuilder!.GetRequiredSnapshotFields(_currentVisualizationId);
        if (surface.Simulation.TryGetChunkHandles(surface.ChunkRevision, out long revision, out AtmosChunkHandle[] handles))
        {
            surface.ChunkRevision = revision;
            surface.Handles = handles;
        }

        var requests = new AtmosChunkSnapshotRequest[surface.Handles.Length];
        for (int index = 0; index < surface.Handles.Length; index++)
        {
            var handle = surface.Handles[index];
            bool usable = surface.Snapshots.TryGetValue(handle.Position, out var cached) && cached.HasFields(fields);
            requests[index] = new AtmosChunkSnapshotRequest(handle.Position, usable ? cached.Version : default, fields);
        }

        var batch = surface.Simulation.GetChangedChunkSnapshots(requests);
        foreach (var snapshot in batch.ChangedChunks)
            surface.Snapshots[snapshot.GridPosition] = snapshot;

        HashSet<Int3> live = surface.Handles.Select(static handle => handle.Position).ToHashSet();
        foreach (var stale in surface.Snapshots.Keys.Where(position => !live.Contains(position)).ToArray())
            surface.Snapshots.Remove(stale);

        AtmosChunkSnapshot[] ordered = surface.Handles
            .Select(handle => surface.Snapshots.GetValueOrDefault(handle.Position))
            .Where(static snapshot => snapshot.Dimensions.X > 0)
            .ToArray();

        surface.DrawData = _frameBuilder.BuildSimulation(
            ordered,
            _currentVisualizationId,
            batch.TickCount,
            surface.DrawData,
            forceRemap: true,
            resolution: GetLegendResolution(),
            automaticRangeOffset: _legendAutomaticBounds ? _legendAutomaticRangeOffset : 0f,
            rangeOverride: _legendAutomaticBounds ? null : new VisualizationRange(_legendMinimum, _legendMaximum));
    }

    private void RenderSimulationViewports()
    {
        if (!_show3DViewport)
            return;

        AtmosSimulationId? requestedActive = null;
        foreach (var surface in _simulationSurfaces)
        {
            if (surface.Viewport == null)
                continue;

            var savedSimulation = _simulation;
            var savedDrawData = _drawData;
            var savedViewport = _viewport;
            var savedCamera = _camera3D;
            var savedMeshes = _meshCache;
            _simulation = surface.Simulation;
            _drawData = surface.DrawData;
            _meshCache = surface.MeshCache;
            _viewport = surface.Viewport;
            _camera3D = surface.Camera;
            string name = GetSimulationName(surface.Simulation.Id);
            string activeLabel = surface.Simulation.Id == _session.ActiveSimulationId ? " (Active)" : string.Empty;
            surface.Viewport.Draw(
                $"{name} 3D{activeLabel}##viewport-{surface.Simulation.Id.Index}-{surface.Simulation.Id.Generation}",
                RenderSimulationScene,
                new Vector2(320 + surface.Simulation.Id.Index * 26, 40 + surface.Simulation.Id.Index * 26),
                new Vector2(660, 510),
                () =>
                {
                    if (ImGui.IsItemClicked(ImGuiMouseButton.Left) || ImGui.IsItemClicked(ImGuiMouseButton.Middle))
                        requestedActive = surface.Simulation.Id;

                    if (surface.Simulation.Id == _session.ActiveSimulationId)
                    {
                        Update3DPicking();
                        Render3DVoxelTooltip();
                        RenderVoxelContextMenu();
                    }
                });

            surface.Camera = _camera3D;
            _simulation = savedSimulation;
            _drawData = savedDrawData;
            _viewport = savedViewport;
            _camera3D = savedCamera;
            _meshCache = savedMeshes;
        }

        if (requestedActive.HasValue)
            SetActiveSimulation(requestedActive);
    }

    private void RenderWorldPanel()
    {
        if (!_showWorldPanel || _session.World == null)
            return;

        using var window = ImGuiExtensions.BeginWindow(
            "World & Topology##world",
            ref _showWorldPanel,
            new Vector2(580, 80),
            new Vector2(430, 620));

        if (!window.IsVisible)
            return;

        bool inspecting = DrawInspectionNotice();
        if (ImGui.BeginTable(
                "WorldStatus##world",
                3,
                ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableNextColumn();
            ImGuiExtensions.StatusField("SIMULATIONS", _session.World.Simulations.Count.ToString());
            ImGui.TableNextColumn();
            ImGuiExtensions.StatusField("ACTIVE LINKS", _session.World.ActiveLinkCount.ToString());
            ImGui.TableNextColumn();
            ImGuiExtensions.StatusField("TOPOLOGY VERSION", _session.World.TopologyVersion.ToString());
            ImGui.EndTable();
        }

        ImGui.SeparatorText("Simulations");
        if (ImGui.BeginTable(
                "Simulations##world",
                3,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("SIMULATION");
            ImGui.TableSetupColumn("CHUNKS", ImGuiTableColumnFlags.WidthFixed, 64f);
            ImGui.TableSetupColumn("CHUNK SIZE", ImGuiTableColumnFlags.WidthFixed, 100f);
            ImGui.TableHeadersRow();
            foreach (var surface in _simulationSurfaces)
            {
                var id = surface.Simulation.Id;
                var dimensions = surface.Simulation.ChunkDimensions;
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                if (ImGui.Selectable(
                        $"{GetSimulationName(id)}##sim-{id.Index}-{id.Generation}",
                        id == _session.ActiveSimulationId,
                        ImGuiSelectableFlags.SpanAllColumns))
                {
                    SetActiveSimulation(id);
                    _simulationFeedback = null;
                }

                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted(surface.Simulation.ChunkCount.ToString());
                ImGui.TableSetColumnIndex(2);
                ImGui.TextUnformatted($"{dimensions.X} x {dimensions.Y} x {dimensions.Z}");
            }

            ImGui.EndTable();
        }

        if (_simulationSurfaces.Count == 0)
            ImGui.TextDisabled("This world has no simulations.");

        ImGui.BeginDisabled(_session.ActiveSimulationId == null || inspecting);
        if (ImGui.Button("Remove Simulation..."))
        {
            _simulationPendingRemoval = _session.ActiveSimulationId;
            _requestRemoveSimulation = true;
            _removeSimulationModalOpen = true;
        }

        ImGui.EndDisabled();
        ImGuiExtensions.QuestionTooltip("Removes the selected simulation, its chunks, and every link connected to it.");

        if (!string.IsNullOrWhiteSpace(_simulationFeedback))
        {
            ImGui.TextColored(
                _simulationFeedbackIsError ? ViewerTheme.Error : ViewerTheme.Running,
                _simulationFeedback);
        }

        ImGui.SeparatorText("New Simulation");
        bool dimensionsChanged = false;
        ImGui.BeginDisabled(inspecting);
        ImGui.SetNextItemWidth(NumericInputWidth);
        dimensionsChanged |= ImGui.InputInt("Chunk width", ref _newSimulationWidth);
        ImGui.SetNextItemWidth(NumericInputWidth);
        dimensionsChanged |= ImGui.InputInt("Chunk height", ref _newSimulationHeight);
        ImGui.SetNextItemWidth(NumericInputWidth);
        dimensionsChanged |= ImGui.InputInt("Chunk depth", ref _newSimulationDepth);
        if (dimensionsChanged)
            _simulationFeedback = null;

        if (ImGui.Button("Add Simulation"))
        {
            try
            {
                var added = _session.AddSimulation(_newSimulationWidth, _newSimulationHeight, _newSimulationDepth);
                ReconcileSimulationSurfaces();
                SetActiveSimulation(added.Id);
                SetSimulationFeedback($"Created {GetSimulationName(added.Id)}.", false);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                SetSimulationFeedback($"Could not create the simulation: {exception.Message}", true);
            }
        }

        ImGui.EndDisabled();
        ImGuiExtensions.QuestionTooltip("Creates another simulation in this world using the shared atmosphere configuration.");

        RenderTopologyControls(inspecting);
    }

    private void DrawWorldModals()
    {
        DrawRemoveSimulationModal();
        DrawRemoveTopologyModal();
        DrawRemoveChunkModal();
        DrawResetConfigurationModal();
    }

    private void DrawRemoveSimulationModal()
    {
        const string popupId = "Remove Simulation?##world";
        ImGuiExtensions.OpenPopupWhenRequested(popupId, ref _requestRemoveSimulation);
        ImGui.SetNextWindowSize(new Vector2(440, 0), ImGuiCond.Appearing);
        using var modal = ImGuiExtensions.BeginPopupModal(popupId, ref _removeSimulationModalOpen);
        if (!modal.IsVisible)
            return;

        if (_session.World == null ||
            _simulationPendingRemoval is not { } id ||
            !_session.World.TryGetSimulation(id, out var simulation))
        {
            ImGui.TextWrapped("The selected simulation no longer exists.");
            if (ImGui.Button("Close", new Vector2(120, 0)))
            {
                _removeSimulationModalOpen = false;
                _simulationPendingRemoval = null;
                ImGui.CloseCurrentPopup();
            }

            ImGui.SetItemDefaultFocus();

            return;
        }

        string name = GetSimulationName(id);
        ImGui.TextWrapped(
            $"Remove {name}, its {simulation!.ChunkCount} chunks, and every portal or dock connected to it? " +
            "This changes the in-memory world immediately.");

        ImGui.Spacing();
        if (ImGui.Button("Remove Simulation", new Vector2(160, 0)))
        {
            try
            {
                _session.RemoveSimulation(id);
                _simulationPendingRemoval = null;
                _removeSimulationModalOpen = false;
                ReconcileSimulationSurfaces();
                SetSimulationFeedback($"Removed {name}.", false);
                ImGui.CloseCurrentPopup();
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                SetSimulationFeedback($"Could not remove {name}: {exception.Message}", true);
                _removeSimulationModalOpen = false;
                ImGui.CloseCurrentPopup();
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(120, 0)))
        {
            _removeSimulationModalOpen = false;
            _simulationPendingRemoval = null;
            ImGui.CloseCurrentPopup();
        }

        ImGui.SetItemDefaultFocus();
    }

    private string GetSimulationName(AtmosSimulationId id)
    {
        return _session.GetSimulationName(id);
    }

    private void SetSimulationFeedback(string message, bool isError)
    {
        _simulationFeedback = message;
        _simulationFeedbackIsError = isError;
    }

    private sealed class SimulationSurface : IDisposable
    {
        internal SimulationSurface(AtmosSimulation simulation)
        {
            Simulation = simulation;
        }

        internal AtmosSimulation Simulation { get; set; }
        internal SimulationViewport? Viewport { get; set; }
        internal Camera3D Camera { get; set; } = new()
        {
            Position = new Vector3(24, 24, 24),
            Target = new Vector3(8, 8, 8),
            Up = Vector3.UnitZ,
            FovY = 45f,
            Projection = CameraProjection.Perspective
        };
        internal long ChunkRevision { get; set; } = -1;
        internal AtmosChunkHandle[] Handles { get; set; } = [];
        internal Dictionary<Int3, AtmosChunkSnapshot> Snapshots { get; } = [];
        internal SimulationDrawData? DrawData { get; set; }

        /// <summary>
        ///     GPU meshes for this simulation's chunks. Chunk positions repeat between simulations, so each surface
        ///     owns a cache.
        /// </summary>
        internal ChunkMeshCache MeshCache { get; } = new();

        public void Dispose()
        {
            MeshCache.Dispose();
            Viewport?.Dispose();
            Viewport = null;
        }
    }
}