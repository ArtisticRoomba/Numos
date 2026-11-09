using System.Numerics;
using ImGuiNET;
using Numos.API;
using Numos.CoreSim;
using Numos.Maths;
using Numos.SimDrawer;
using Numos.Viewer.Rendering;
using Numos.Viewer.Rendering.Viewport;
using Numos.Viewer.Ui;
using Numos.Viewer.Workspace;
using Numos.Workbench;
using Raylib_cs;

namespace Numos.Viewer;

public partial class SimulationViewer
{
    /// <summary>
    ///     Matches the surfaces to the frame's simulations and resolves the active one.
    /// </summary>
    private void ReconcileSimulationSurfaces()
    {
        var simulations = _frame.Simulations;
        var liveIds = new HashSet<AtmosSimulationId>();
        foreach (var simulation in simulations)
            liveIds.Add(simulation.Id);

        if (_document.Topology.PortalFirst is { } firstPortal && !liveIds.Contains(firstPortal.Simulation))
            _document.Topology.PortalFirst = null;

        if (_document.Topology.PortalSecond is { } secondPortal && !liveIds.Contains(secondPortal.Simulation))
            _document.Topology.PortalSecond = null;

        for (int index = _document.Surfaces.Count - 1; index >= 0; index--)
        {
            var removed = _document.Surfaces[index];
            if (liveIds.Contains(removed.Id))
                continue;

            removed.Dispose();
            _document.Surfaces.RemoveAt(index);
        }

        bool added = false;
        foreach (var simulation in simulations)
        {
            var retained = FindSurface(simulation.Id);
            if (retained != null)
            {
                retained.Frame = simulation;
                continue;
            }

            var surface = new SimulationSurface(simulation, CreateSurfaceBuilder());
            if (_windowInitialized)
            {
                surface.Viewport = new SimulationViewport(
                    TextureFilter.Bilinear,
                    new Color(0.04f, 0.04f, 0.05f, 1f));
            }

            _document.Surfaces.Add(surface);
            added = true;
        }

        if (added)
            _document.Surfaces.Sort(static (left, right) => left.Id.CompareTo(right.Id));

        if (_document.PendingActiveSimulationId is { } pending && liveIds.Contains(pending))
        {
            _document.PendingActiveSimulationId = null;
            SetActiveSimulation(pending);
        }
        else if (_document.ActiveSimulationId is not { } active || !liveIds.Contains(active))
        {
            // The session repairs its own selection after a removal or a timeline move, so follow it first.
            var fallback = _frame.ActiveSimulationId is { } sessionActive && liveIds.Contains(sessionActive)
                ? sessionActive
                : simulations.Count > 0
                    ? simulations[0].Id
                    : (AtmosSimulationId?)null;

            SetActiveSimulation(fallback);
        }

        _simulation = GetActiveSurface()?.Frame;
    }

    private SimulationSurface? FindSurface(AtmosSimulationId id)
    {
        foreach (var surface in _document.Surfaces)
        {
            if (surface.Id == id)
                return surface;
        }

        return null;
    }

    private SimulationSurface? GetActiveSurface()
    {
        return _document.ActiveSimulationId is { } id ? FindSurface(id) : null;
    }

    private SimulationFrameBuilder? CreateSurfaceBuilder()
    {
        return _document.PresentationConfig == null || _document.FrameBuilder == null
            ? null
            : new SimulationFrameBuilder(_document.PresentationConfig, _document.FrameBuilder.Visualizations);
    }

    private void SetActiveSimulation(AtmosSimulationId? id)
    {
        var surface = id is { } value ? FindSurface(value) : null;
        if (surface == null)
        {
            SaveActiveSurfaceState();
            _document.ActiveSimulationId = null;
            _simulation = null;
            _viewport = null;
            _drawData = null;
            return;
        }

        if (_document.ActiveSimulationId == id)
            return;

        SaveActiveSurfaceState();
        _document.ActiveSimulationId = id;
        ResetActiveSurfaceCaches();
        _simulation = surface.Frame;
        _viewport = surface.Viewport;
        _camera3D = surface.Camera;
        _frameSceneOnNextPresentation = surface.DrawData == null;

        // Only the session's defaults read its active simulation, such as an export without a simulation id.
        if (_frame.ActiveSimulationId != id)
        {
            var target = id!.Value;
            _host.Post(session =>
            {
                if (session.TryGetSimulation(target, out _))
                    session.SetActiveSimulation(target);
            });
        }
    }

    private void SaveActiveSurfaceState()
    {
        // The frame can already list another active simulation by now, so key off the one being presented.
        var surface = GetActiveSurface();
        if (surface != null)
            surface.Camera = _camera3D;
    }

    /// <summary>
    ///     Drops the render caches and the pointer state of the surface on screen, before another surface or project
    ///     takes its place.
    /// </summary>
    private void ResetActiveSurfaceCaches()
    {
        _viewport = null;
        _simulation = null;
        _liveChunkHandles = [];
        _liveChunkPositions.Clear();
        _drawData = null;
        _sliceDrawData = null;
        _sliceSourceFrame = null;
        _sliceFrameChunk = null;
        _sliceCameraFit = true;
        _sliceRenderer?.ReleaseTexture();
        _hovered3DCell = null;
        _hoveredSliceCell = null;
        _selectedCell = null;
        _selectedCells.Clear();
        _paintedCells.Clear();
        _lastPaintedCell = null;
        _voxelDragStart = null;
        _voxelDragAnchor = null;
        _voxelDragViewport = 0;
        _highlights.Clear();
        _focusedChunk = null;
        _cameraInitialized = false;
        _frameSceneOnNextPresentation = false;
    }

    /// <summary>
    ///     Collects finished background builds and starts a build for every surface whose inputs changed.
    /// </summary>
    /// <remarks>
    ///     A surface has at most one build in flight, because its builder must not run two builds at once. A frame
    ///     that arrives during a build waits for the next build, which reads the newest frame.
    /// </remarks>
    private void UpdateSurfaceBuilds()
    {
        foreach (var surface in _document.Surfaces)
            CollectSurfaceBuild(surface);

        if (_document.FrameBuilder == null)
            return;

        // Right after a visualization change the frame still holds the old fields. Building from it would map
        // fields the snapshots lack, so the surfaces keep their draw data until the host captures the new ones.
        var drawFields = GetDrawSnapshotFields();
        if ((_frame.SnapshotFields & drawFields) != drawFields)
            return;

        var visualization = _document.FrameBuilder.Visualizations.GetRequired(_currentVisualizationId);
        foreach (var surface in _document.Surfaces)
        {
            if (surface.PendingBuild != null || surface.Builder == null)
                continue;

            var frame = surface.Frame;
            var inputs = new DrawBuildInputs(
                surface.Builder,
                frame.Snapshots,
                frame.ChunkHandles,
                frame.SnapshotTickCount,
                _frame.TimelineRevision,
                visualization.Id,
                visualization.MappingRevision,
                drawFields,
                GetLegendResolution(),
                _legendRangeRevision,
                _legendAutomaticBounds ? _legendAutomaticRangeOffset : 0f,
                _legendAutomaticBounds ? null : new VisualizationRange(_legendMinimum, _legendMaximum));

            if (inputs != surface.BuiltInputs)
                StartSurfaceBuild(surface, inputs);
        }
    }

    private static void StartSurfaceBuild(SimulationSurface surface, DrawBuildInputs inputs)
    {
        // The task captures only immutable values. The previous draw data is immutable too, and the UI thread keeps
        // drawing it while the build runs.
        var previous = surface.DrawData;
        bool forceRemap = inputs.RequiresRemap(surface.BuiltInputs);
        surface.PendingInputs = inputs;
        surface.PendingBuild = Task.Run(() => inputs.Builder.BuildSimulation(
            inputs.OrderSnapshots(),
            inputs.VisualizationId,
            inputs.SourceVersion,
            previous,
            forceRemap: forceRemap,
            resolution: inputs.Resolution,
            automaticRangeOffset: inputs.AutomaticRangeOffset,
            rangeOverride: inputs.RangeOverride));
    }

    private void CollectSurfaceBuild(SimulationSurface surface)
    {
        if (surface.PendingBuild is not { IsCompleted: true } task)
            return;

        var inputs = surface.PendingInputs!;
        surface.PendingBuild = null;
        surface.PendingInputs = null;
        RequestNextFrame();

        // A configuration change replaced the builder while this build ran, so its result may use the old one.
        if (!ReferenceEquals(inputs.Builder, surface.Builder))
            return;

        if (task.IsCompletedSuccessfully)
        {
            surface.DrawData = task.Result;
            surface.BuiltInputs = inputs;
            return;
        }

        // Keep the failed inputs, so the same failure does not rebuild and log every frame.
        surface.BuiltInputs = inputs;
        if (task.Exception is { } exception)
            WriteException(Locale.GetString("draw-data-build-failed"), exception.GetBaseException());
    }

    private bool HasPendingSurfaceBuild()
    {
        foreach (var surface in _document.Surfaces)
        {
            if (surface.PendingBuild != null)
                return true;
        }

        return false;
    }

    private void RenderSimulationViewports()
    {
        if (!_show3DViewport)
            return;

        AtmosSimulationId? requestedActive = null;
        foreach (var surface in _document.Surfaces)
        {
            if (surface.Viewport == null)
                continue;

            var savedSimulation = _simulation;
            var savedDrawData = _drawData;
            var savedViewport = _viewport;
            var savedCamera = _camera3D;
            var savedMeshes = _meshCache;
            _simulation = surface.Frame;
            _drawData = surface.DrawData;
            _meshCache = surface.MeshCache;
            _viewport = surface.Viewport;

            // UpdateCamera moves _camera3D before the draw, so the active surface draws with _camera3D.
            // Its surface.Camera is a copy that this loop refreshes after the draw.
            bool active = surface.Id == _document.ActiveSimulationId;
            if (!active)
                _camera3D = surface.Camera;

            string name = GetSimulationName(surface.Id);
            string title = active
                ? Locale.GetString("viewport-3d-title-active", ("name", name))
                : Locale.GetString("viewport-3d-title", ("name", name));

            surface.Viewport.Draw(
                WorkspacePanels.ViewportWindowName(title, surface.Id),
                RenderSimulationScene,
                new Vector2(320 + surface.Id.Index * 26, 40 + surface.Id.Index * 26),
                new Vector2(660, 510),
                () =>
                {
                    if (ImGui.IsItemClicked(ImGuiMouseButton.Left) || ImGui.IsItemClicked(ImGuiMouseButton.Middle))
                        requestedActive = surface.Id;

                    if (active)
                    {
                        Update3DPicking();
                        Render3DVoxelTooltip();
                        RenderVoxelContextMenu();
                    }
                },
                _centralDockNodeId);

            surface.Camera = _camera3D;
            _simulation = savedSimulation;
            _drawData = savedDrawData;
            _viewport = savedViewport;
            if (!active)
                _camera3D = savedCamera;

            _meshCache = savedMeshes;
        }

        // Clicking another simulation's viewport selects that simulation, which also makes it active. While a portal
        // endpoint pick is armed, the click only activates the simulation, so Properties keeps the portal form.
        if (requestedActive is { } clicked && clicked != _document.ActiveSimulationId)
        {
            SetActiveSimulation(clicked);
            if (!_document.PortalCapture.IsArmed)
                SelectTreeNode(TreeNodeId.ForSimulation(clicked));
        }
    }

    private void RequestRemoveSimulation(AtmosSimulationId id)
    {
        _document.SimulationPendingRemoval = id;
        _document.RequestRemoveSimulation = true;
        _document.RemoveSimulationModalOpen = true;
    }

    private void DrawSimulationFeedback()
    {
        if (!string.IsNullOrWhiteSpace(_document.SimulationFeedback))
        {
            ImGui.TextColored(
                _document.SimulationFeedbackIsError ? ViewerTheme.Error : ViewerTheme.Running,
                _document.SimulationFeedback);
        }
    }

    /// <summary>
    ///     Draws the chunk dimensions and the button of the add-simulation form.
    /// </summary>
    /// <param name="inspecting">Whether editing is blocked.</param>
    /// <param name="focus">Whether to scroll to the form and focus its first field.</param>
    private void RenderNewSimulationForm(bool inspecting, bool focus = false)
    {
        ImGui.SeparatorText(Locale.GetString("common-new-simulation"));
        bool dimensionsChanged = false;
        ImGui.BeginDisabled(inspecting);
        FocusFormHere(focus);
        ImGui.SetNextItemWidth(NumericInputWidth);
        dimensionsChanged |= ImGui.InputInt(Locale.GetString("chunk-width-label"), ref _document.NewSimulation.Width);
        ImGui.SetNextItemWidth(NumericInputWidth);
        dimensionsChanged |= ImGui.InputInt(Locale.GetString("chunk-height-label"), ref _document.NewSimulation.Height);
        ImGui.SetNextItemWidth(NumericInputWidth);
        dimensionsChanged |= ImGui.InputInt(Locale.GetString("chunk-depth-label"), ref _document.NewSimulation.Depth);
        if (dimensionsChanged)
            _document.SimulationFeedback = null;

        if (ImGui.Button(Locale.GetString("add-simulation-button")))
            AddSimulation(_document.NewSimulation.Width, _document.NewSimulation.Height, _document.NewSimulation.Depth);

        ImGui.EndDisabled();
        ImGuiExtensions.QuestionTooltip(Locale.GetString("add-simulation-tooltip"));
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
        ImGuiExtensions.OpenPopupWhenRequested(popupId, ref _document.RequestRemoveSimulation);
        ImGui.SetNextWindowSize(new Vector2(440, 0), ImGuiCond.Appearing);
        using var modal = ImGuiExtensions.BeginPopupModal(popupId, ref _document.RemoveSimulationModalOpen);
        if (!modal.IsVisible)
            return;

        if (_document.SimulationPendingRemoval is not { } id || GetSimulationFrame(id) is not { } simulation)
        {
            ImGui.TextWrapped(Locale.GetString("simulation-no-longer-exists"));
            if (ImGui.Button(Locale.GetString("common-close"), new Vector2(120, 0)))
            {
                _document.RemoveSimulationModalOpen = false;
                _document.SimulationPendingRemoval = null;
                ImGui.CloseCurrentPopup();
            }

            ImGui.SetItemDefaultFocus();

            return;
        }

        string name = GetSimulationName(id);
        ImGui.TextWrapped(
            Locale.GetString("simulation-remove-confirmation", ("name", name), ("chunk-count", simulation.ChunkHandles.Count)) +
            " " +
            Locale.GetString("simulation-remove-immediate-warning"));

        ImGui.Spacing();
        ImGui.BeginDisabled(IsEditingBlocked);
        if (ImGui.Button(Locale.GetString("remove-simulation-button"), new Vector2(160, 0)))
        {
            RemoveSimulation(id, name);
            _document.SimulationPendingRemoval = null;
            _document.RemoveSimulationModalOpen = false;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button(Locale.GetString("common-cancel"), new Vector2(120, 0)))
        {
            _document.RemoveSimulationModalOpen = false;
            _document.SimulationPendingRemoval = null;
            ImGui.CloseCurrentPopup();
        }

        ImGui.SetItemDefaultFocus();
    }

    private void AddSimulation(int width, int height, int depth)
    {
        Send(
            session =>
            {
                var added = session.AddSimulation(width, height, depth);
                return (added.Id, Name: session.GetSimulationName(added.Id));
            },
            result =>
            {
                if (result.Exception is { } exception)
                {
                    SetSimulationFeedback(
                        Locale.GetString("simulation-create-failed-feedback", ("message", DescribeFailure(exception))),
                        true);

                    return;
                }

                _document.PendingActiveSimulationId = result.Value.Id;
                SelectTreeNode(TreeNodeId.ForSimulation(result.Value.Id), _host.Frame.Sequence);
                SetSimulationFeedback(Locale.GetString("simulation-created-feedback", ("name", result.Value.Name)), false);
            });
    }

    private void RemoveSimulation(AtmosSimulationId id, string name)
    {
        Send(
            session => session.RemoveSimulation(id),
            result =>
            {
                if (result.Exception is { } exception)
                {
                    SetSimulationFeedback(
                        Locale.GetString(
                            "simulation-remove-failed-feedback",
                            ("name", name),
                            ("message", DescribeFailure(exception))),
                        true);

                    return;
                }

                SetSimulationFeedback(Locale.GetString("simulation-removed-feedback", ("name", name)), false);
            });
    }

    /// <summary>
    ///     The display name from the frame, or the session's default name for a simulation the frame does not list.
    /// </summary>
    private string GetSimulationName(AtmosSimulationId id)
    {
        return GetSimulationFrame(id)?.Name ?? $"Simulation {id.Index + 1}";
    }

    private void SetSimulationFeedback(string message, bool isError)
    {
        _document.SimulationFeedback = message;
        _document.SimulationFeedbackIsError = isError;
    }
}
