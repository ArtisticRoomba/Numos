using System.Numerics;
using ImGuiNET;
using Numos.API;
using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.Maths;
using Numos.SimDrawer;
using Numos.Viewer.Rendering;
using Numos.Viewer.Rendering.Viewport;
using Numos.Viewer.Ui;
using Raylib_cs;
using rlImGui_cs;

namespace Numos.Viewer;

/// <summary>
///     Interactive raylib/ImGui host for the simulation presentation layer.
/// </summary>
public partial class SimulationViewer : IDisposable
{
    private const float CameraMoveDuration = 0.45f;

    private readonly Action<VisualizationRegistry>? _configureVisualizations;
    private readonly List<VoxelHighlight> _highlights = [];
    private readonly List<AtmosChunkHandle> _liveChunkHandles = [];
    private readonly HashSet<Int3> _liveChunkPositions = [];
    private readonly List<AtmosChunkSnapshot> _orderedSnapshots = [];
    private readonly HashSet<VoxelAddress> _paintedCells = [];
    private readonly HashSet<VoxelAddress> _selectedCells = [];
    private readonly Dictionary<Int3, AtmosChunkSnapshot> _snapshotCache = new();
    private readonly List<AtmosChunkSnapshotRequest> _snapshotRequests = [];
    private readonly List<Int3> _staleSnapshotKeys = [];
    private readonly Dictionary<VoxelAddress, VoxelDetailCacheEntry> _voxelDetailCache = new();
    private ulong _appliedLegendRangeRevision = ulong.MaxValue;
    private Camera2D _camera2D = new()
    {
        Zoom = 1f
    };

    // Cameras
    private Camera3D _camera3D = new()
    {
        Position = new Vector3(24, 24, 24),
        Target = new Vector3(8, 8, 8),
        Up = Vector3.UnitZ,
        FovY = 45f,
        Projection = CameraProjection.Perspective
    };
    private bool _cameraInitialized;
    private float _cameraMoveElapsed = CameraMoveDuration;
    private Vector3 _cameraMoveEndPosition;
    private Vector3 _cameraMoveEndTarget;
    private Vector3 _cameraMoveStartPosition;
    private Vector3 _cameraMoveStartTarget;
    private long _chunkCollectionRevision = -1;
    private AtmosConfig? _config;
    private SliceAxis _currentSliceAxis = SliceAxis.Z;
    private int _currentSliceIndex;
    private string _currentVisualizationId = BuiltInVisualizationIds.Temperature;
    private SimulationDrawData? _drawData;
    private ChunkIdentity? _focusedChunk;
    private SimulationFrameBuilder? _frameBuilder;
    private bool _frameSceneOnNextPresentation;
    private VoxelAddress? _hovered3DCell;
    private SliceCellDrawData? _hoveredSliceCell;
    private bool _imguiInitialized;

    private bool _isPaused;
    private VoxelAddress? _lastPaintedCell;
    private bool _legendAutomaticBounds = true;
    private float _legendAutomaticRangeOffset;
    private float _legendMaximum = 1f;
    private float _legendMinimum;
    private ulong _legendRangeRevision;
    private int _legendResolution = 32;
    private bool _legendResolutionEnabled;
    private int _programSettingsTab;
    private string? _projectName;
    private bool _requestExit;
    private VoxelAddress? _selectedCell;
    private Int3? _selectedSliceChunkPosition;
    private bool _show2DChunkOutlines;
    private bool _show2DVoxelOutlines = true;
    private bool _show3DChunkOutlines;
    private bool _show3DViewport = true;
    private bool _show3DVoxelOutlines = true;
    private bool _showConfigurationPanel;
    private bool _showPerformanceOverlay;
    private bool _showProgramSettingsPanel;
    private bool _showSliceViewport = true;

    // UI state
    private bool _showSolutionPanel = true;
    private bool _showToolsPanel = true;
    private bool _showViewPanel = true;
    private bool _showViewportBranding = true;

    private AtmosSimulation? _simulation;
    private SimulationSliceDrawData? _sliceDrawData;
    private SliceProjectionKey? _sliceProjectionKey;
    private SimulationViewport? _sliceViewport;
    private int _snapshotSourceVersion;
    private int _targetFps = 144;
    private bool _transparent2DVoxels;
    private bool _transparent3DVoxels;
    private bool _uncappedFps;

    private SimulationViewport? _viewport;
    private Texture2D _viewportBranding;
    private ViewportBrandingCorner _viewportBrandingCorner = ViewportBrandingCorner.TopLeft;
    private float _viewportBrandingOffsetX = 10f;
    private float _viewportBrandingOffsetY = 10f;
    private float _viewportBrandingOpacityPercent = 80f;
    private float _viewportBrandingSizePercent = 6f;
    private VoxelAddress? _voxelDragAnchor;
    private Vector2? _voxelDragStart;
    private int _voxelDragViewport;
    private VoxelEditTool _voxelEditTool;
    private bool _windowInitialized;

    /// <summary>
    ///     Creates a viewer with an optional startup hook for registering application-specific
    ///     visualization methods before the UI and frame builder begin enumerating the registry.
    /// </summary>
    public SimulationViewer(Action<VisualizationRegistry>? configureVisualizations = null)
    {
        _configureVisualizations = configureVisualizations;
    }

    public VoxelAddress? SelectedCell => _selectedCell;

    public void Run()
    {
        Raylib.SetConfigFlags(
            ConfigFlags.ResizableWindow |
            ConfigFlags.Msaa4xHint |
            ConfigFlags.VSyncHint);

        Raylib.InitWindow(1400, 900, "Numos Simulation Viewer");
        _windowInitialized = true;

        string iconPath = Path.Combine(
            AppContext.BaseDirectory,
            "assets",
            "icon.png");

        if (File.Exists(iconPath))
        {
            var icon = Raylib.LoadImage(iconPath);
            Raylib.SetWindowIcon(icon);
            Raylib.UnloadImage(icon);

            _viewportBranding = Raylib.LoadTexture(iconPath);
            Raylib.SetTextureFilter(_viewportBranding, TextureFilter.Bilinear);
        }

        try
        {
            Raylib.SetTargetFPS(144);
            rlImGui.Setup(true, true);
            _imguiInitialized = true;
            ViewerTheme.Apply();
            ImGui.GetIO().ConfigWindowsMoveFromTitleBarOnly = true;
            ConfigureLayoutPersistence();

            _viewport = new SimulationViewport(
                TextureFilter.Bilinear,
                new Color(0.04f, 0.04f, 0.05f, 1f));

            _sliceViewport = new SimulationViewport(
                TextureFilter.Point,
                new Color(0.04f, 0.04f, 0.05f, 1f));

            while (!Raylib.WindowShouldClose() && !_requestExit)
            {
                float deltaTime = Raylib.GetFrameTime();
                Update(deltaTime);
                Draw(deltaTime);
            }
        }
        finally
        {
            DisposeGraphics();
        }
    }

    private void Update(float deltaTime)
    {
        if (_simulation != null && _config != null)
        {
            if (!_isPaused)
            {
                if (_replayTimeline?.IsInspecting == true)
                {
                    _replayElapsed += deltaTime;
                    if (_replayElapsed >= 1f / AtmosSimulation.SimulationRate)
                    {
                        _replayElapsed = 0f;
                        StepTimelineForward();
                    }
                }
                else
                {
                    _simulation.Update(deltaTime);
                }
            }

            _replayTimeline?.ObserveLiveState();

            RefreshPresentation();
        }

        UpdateCamera(deltaTime);
    }

    private void RefreshPresentation()
    {
        if (_simulation == null || _frameBuilder == null)
            return;

        bool snapshotsChanged = RefreshSnapshotCache();
        var visualization = _frameBuilder.Visualizations.GetRequired(_currentVisualizationId);
        bool visualizationChanged = _drawData == null ||
                                    !string.Equals(
                                        _drawData.Visualization.Id,
                                        visualization.Id,
                                        StringComparison.OrdinalIgnoreCase) ||
                                    _drawData.VisualizationMappingRevision != visualization.MappingRevision ||
                                    _drawData.Visualization.Range.Resolution != GetLegendResolution() ||
                                    _appliedLegendRangeRevision != _legendRangeRevision;

        if (!snapshotsChanged && !visualizationChanged)
        {
            RefreshSliceData();
            return;
        }

        _orderedSnapshots.Clear();
        foreach (var handle in _liveChunkHandles)
        {
            if (_snapshotCache.TryGetValue(handle.Position, out var snapshot))
                _orderedSnapshots.Add(snapshot);
        }

        _drawData = _frameBuilder.BuildSimulation(
            _orderedSnapshots,
            _currentVisualizationId,
            _snapshotSourceVersion,
            _drawData,
            forceRemap: visualizationChanged,
            resolution: GetLegendResolution(),
            automaticRangeOffset: _legendAutomaticBounds ? _legendAutomaticRangeOffset : 0f,
            rangeOverride: _legendAutomaticBounds
                ? null
                : new VisualizationRange(_legendMinimum, _legendMaximum));

        _appliedLegendRangeRevision = _legendRangeRevision;
        NormalizeInteractionState();
        RefreshSliceData();
        RebuildHighlights();

        if (_frameSceneOnNextPresentation)
        {
            FocusCameraOnScene();
            _frameSceneOnNextPresentation = false;
        }

        if (!_cameraInitialized && _drawData.Chunks.Count > 0)
        {
            FocusCameraOnScene();
            _cameraInitialized = true;
        }
    }

    private bool RefreshSnapshotCache()
    {
        if (_simulation == null)
            return false;

        bool changed = false;
        var requiredFields = _frameBuilder?.GetRequiredSnapshotFields(_currentVisualizationId) ?? AtmosChunkSnapshotFields.All;
        if (_simulation.TryGetChunkHandles(
                _chunkCollectionRevision,
                out long collectionRevision,
                out AtmosChunkHandle[] handles))
        {
            _chunkCollectionRevision = collectionRevision;
            _liveChunkHandles.Clear();
            _liveChunkHandles.AddRange(handles);
            _liveChunkPositions.Clear();
            foreach (var handle in handles)
                _liveChunkPositions.Add(handle.Position);

            changed = true;
        }

        if (_focusedChunk.HasValue &&
            !_liveChunkPositions.Contains(_focusedChunk.Value.Position) &&
            _replayTimeline?.IsInspecting != true &&
            !_refreshingReplay)
        {
            _focusedChunk = null;
            _frameSceneOnNextPresentation = true;
        }

        _snapshotRequests.Clear();
        foreach (var handle in _liveChunkHandles)
        {
            bool hasCachedSnapshot = _snapshotCache.TryGetValue(handle.Position, out var cached);
            bool cachedFieldsAreSufficient = hasCachedSnapshot && cached.HasFields(requiredFields);

            var knownVersion = cachedFieldsAreSufficient
                ? cached.Version
                : default;

            _snapshotRequests.Add(new AtmosChunkSnapshotRequest(handle.Position, knownVersion, requiredFields));
        }

        var batch = _simulation.GetChangedChunkSnapshots(_snapshotRequests);
        _snapshotSourceVersion = batch.TickCount;
        foreach (var snapshot in batch.ChangedChunks)
        {
            _snapshotCache[snapshot.GridPosition] = snapshot;
            changed = true;
        }

        _staleSnapshotKeys.Clear();
        foreach (var position in _snapshotCache.Keys)
        {
            if (!_liveChunkPositions.Contains(position))
                _staleSnapshotKeys.Add(position);
        }

        foreach (var position in _staleSnapshotKeys)
        {
            _snapshotCache.Remove(position);
            changed = true;
        }

        return changed;
    }

    private void RefreshSliceData()
    {
        if (_drawData == null || _frameBuilder == null || _drawData.Chunks.Count == 0)
        {
            _sliceDrawData = null;
            _sliceProjectionKey = null;
            return;
        }

        if (!_showSliceViewport)
            return;

        if (_focusedChunk.HasValue && _selectedSliceChunkPosition != _focusedChunk.Value.Position)
            _selectedSliceChunkPosition = _focusedChunk.Value.Position;

        if (_selectedSliceChunkPosition.HasValue &&
            !_drawData.Chunks.ContainsKey(_selectedSliceChunkPosition.Value) &&
            (_replayTimeline?.IsInspecting == true || _refreshingReplay))
        {
            _sliceDrawData = null;
            _sliceProjectionKey = null;
            return;
        }

        if (!_selectedSliceChunkPosition.HasValue ||
            !_drawData.Chunks.ContainsKey(_selectedSliceChunkPosition.Value))
            _selectedSliceChunkPosition = _focusedChunk?.Position ?? _drawData.Chunks.Keys.First();

        var chunk = _drawData.Chunks[_selectedSliceChunkPosition.Value];
        int maximumSlice = Math.Max(
            SimulationFrameBuilder.GetSliceAxisLength(chunk.Dimensions, _currentSliceAxis) - 1,
            0);

        _currentSliceIndex = Math.Clamp(_currentSliceIndex, 0, maximumSlice);

        var projectionKey = new SliceProjectionKey(
            chunk.Identity,
            chunk.TopologyVersion,
            chunk.StyleVersion,
            _currentSliceAxis,
            _currentSliceIndex);

        if (_sliceProjectionKey == projectionKey)
            return;

        _sliceDrawData = _frameBuilder.BuildChunkSlice(
            _drawData,
            chunk.Identity,
            _currentSliceAxis,
            _currentSliceIndex);

        _sliceProjectionKey = projectionKey;
        _hoveredSliceCell = null;
    }

    private void NormalizeInteractionState()
    {
        if (_drawData == null)
            return;

        if (_focusedChunk.HasValue)
        {
            if (_drawData.Chunks.TryGetValue(_focusedChunk.Value.Position, out var focused))
                _focusedChunk = focused.Identity;
            else if (_replayTimeline?.IsInspecting != true && !_refreshingReplay)
            {
                _focusedChunk = null;
                _frameSceneOnNextPresentation = true;
            }
        }

        if (_selectedCell.HasValue && !_drawData.TryResolve(_selectedCell.Value, out _))
        {
            if (_replayTimeline?.IsInspecting == true || _refreshingReplay)
            {
                var selected = _selectedCell.Value;
                if (_drawData.Chunks.TryGetValue(selected.Chunk.Position, out var chunk))
                    _selectedCell = new VoxelAddress(chunk.Identity, selected.LocalIndex);
            }
            else _selectedCell = null;
        }

        _selectedCells.RemoveWhere(address => !_drawData.TryResolve(address, out _));
        if (_selectedCell.HasValue)
            _selectedCells.Add(_selectedCell.Value);
        else
            _selectedCells.Clear();
    }

    private void SetVisualization(string visualizationId)
    {
        if (string.Equals(_currentVisualizationId, visualizationId, StringComparison.OrdinalIgnoreCase))
            return;

        _currentVisualizationId = visualizationId;
    }

    private void SetFocusedChunk(ChunkIdentity? chunk)
    {
        if (_focusedChunk == chunk)
            return;

        _focusedChunk = chunk;
        if (chunk.HasValue && _drawData != null && _drawData.Chunks.TryGetValue(chunk.Value.Position, out var data))
        {
            _selectedSliceChunkPosition = data.ChunkPosition;
            FocusCameraOnChunk(data);
        }
        else
        {
            FocusCameraOnScene();
        }
    }

    private void FocusCameraOnChunk(ChunkDrawData chunk)
    {
        var target = new Vector3(
            chunk.ChunkPosition.X * chunk.Dimensions.X + chunk.Dimensions.X * 0.5f,
            chunk.ChunkPosition.Y * chunk.Dimensions.Y + chunk.Dimensions.Y * 0.5f,
            chunk.ChunkPosition.Z * chunk.Dimensions.Z + chunk.Dimensions.Z * 0.5f);

        float distance = Math.Max(
            5f,
            Math.Max(chunk.Dimensions.X, Math.Max(chunk.Dimensions.Y, chunk.Dimensions.Z)) * 2.5f);

        FrameCamera(target, distance);
    }

    private void MoveCameraToChunk(ChunkDrawData chunk)
    {
        ShowAllChunks();
        FocusCameraOnChunk(chunk);
    }

    private void MoveCameraToVoxel(ChunkDrawData chunk, int x, int y, int z)
    {
        x = Math.Clamp(x, 0, chunk.Dimensions.X - 1);
        y = Math.Clamp(y, 0, chunk.Dimensions.Y - 1);
        z = Math.Clamp(z, 0, chunk.Dimensions.Z - 1);

        ShowAllChunks();
        var target = new Vector3(
            chunk.ChunkPosition.X * chunk.Dimensions.X + x + 0.5f,
            chunk.ChunkPosition.Y * chunk.Dimensions.Y + y + 0.5f,
            chunk.ChunkPosition.Z * chunk.Dimensions.Z + z + 0.5f);

        FrameCamera(target, Vector3.Distance(_camera3D.Position, _camera3D.Target));
    }

    private void ShowAllChunks()
    {
        _focusedChunk = null;
    }

    private void FocusCameraOnScene()
    {
        if (_drawData == null || _drawData.Chunks.Count == 0)
            return;

        float minX = float.PositiveInfinity;
        float minY = float.PositiveInfinity;
        float minZ = float.PositiveInfinity;
        float maxX = float.NegativeInfinity;
        float maxY = float.NegativeInfinity;
        float maxZ = float.NegativeInfinity;
        foreach (var chunk in _drawData.Chunks.Values)
        {
            float x = chunk.ChunkPosition.X * chunk.Dimensions.X;
            float y = chunk.ChunkPosition.Y * chunk.Dimensions.Y;
            float z = chunk.ChunkPosition.Z * chunk.Dimensions.Z;
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            minZ = Math.Min(minZ, z);
            maxX = Math.Max(maxX, x + chunk.Dimensions.X);
            maxY = Math.Max(maxY, y + chunk.Dimensions.Y);
            maxZ = Math.Max(maxZ, z + chunk.Dimensions.Z);
        }

        var target = new Vector3((minX + maxX) * 0.5f, (minY + maxY) * 0.5f, (minZ + maxZ) * 0.5f);
        float distance = Math.Max(5f, Math.Max(maxX - minX, Math.Max(maxY - minY, maxZ - minZ)) * 2.5f);
        FrameCamera(target, distance);
    }

    private void UpdateCamera(float deltaTime)
    {
        UpdateCameraMove(deltaTime);

        if (_viewport is not { IsHovered: true })
            return;

        if (Raylib.IsMouseButtonDown(MouseButton.Middle))
        {
            CancelCameraMove();
            var mouseDelta = Raylib.GetMouseDelta();
            Raylib.CameraYaw(ref _camera3D, -mouseDelta.X * 0.01f, true);
            Raylib.CameraPitch(ref _camera3D, -mouseDelta.Y * 0.01f, true, true, false);
        }

        float wheel = Raylib.GetMouseWheelMove();
        if (wheel != 0f)
        {
            CancelCameraMove();
            Raylib.CameraMoveToTarget(ref _camera3D, -wheel * 2f);
        }
    }

    private void FrameCamera(Vector3 target, float distance)
    {
        var direction = _camera3D.Position - _camera3D.Target;
        if (direction.LengthSquared() < 0.0001f)
            direction = Vector3.Normalize(new Vector3(1f, 0.8f, 1f));
        else
            direction = Vector3.Normalize(direction);

        _cameraMoveStartPosition = _camera3D.Position;
        _cameraMoveStartTarget = _camera3D.Target;
        _cameraMoveEndTarget = target;
        _cameraMoveEndPosition = target + direction * Math.Max(distance, 0.1f);
        _cameraMoveElapsed = 0f;
    }

    private void UpdateCameraMove(float deltaTime)
    {
        if (_cameraMoveElapsed >= CameraMoveDuration)
            return;

        _cameraMoveElapsed = Math.Min(_cameraMoveElapsed + Math.Max(deltaTime, 0f), CameraMoveDuration);
        float amount = _cameraMoveElapsed / CameraMoveDuration;
        amount = amount * amount * (3f - 2f * amount);
        _camera3D.Position = Vector3.Lerp(_cameraMoveStartPosition, _cameraMoveEndPosition, amount);
        _camera3D.Target = Vector3.Lerp(_cameraMoveStartTarget, _cameraMoveEndTarget, amount);
    }

    private void CancelCameraMove()
    {
        _cameraMoveElapsed = CameraMoveDuration;
    }

    private void Draw(float deltaTime)
    {
        Raylib.BeginDrawing();
        try
        {
            Raylib.ClearBackground(new Color(0.1f, 0.1f, 0.1f, 1f));
            rlImGui.Begin(deltaTime);
            RenderUi();
            rlImGui.End();
        }
        finally
        {
            Raylib.EndDrawing();
        }
    }

    private void RenderSimulationScene()
    {
        if (_drawData != null)
        {
            Raylib.BeginMode3D(_camera3D);
            try
            {
                SimulationRenderer.Draw(
                    _drawData,
                    _focusedChunk,
                    _highlights,
                    Get3DRenderStyleOptions());
            }
            finally
            {
                Raylib.EndMode3D();
            }
        }

        if (_viewport != null)
            NavigationGizmo.Draw3D(_camera3D, _viewport.Width, _viewport.Height);

        DrawViewportBranding(_viewport?.Width ?? 1, _viewport?.Height ?? 1);
    }

    private void RenderSimulationSliceScene()
    {
        if (_sliceDrawData != null && _sliceViewport != null)
        {
            const float margin = 0.5f;
            _camera2D.Offset = new Vector2(_sliceViewport.Width, _sliceViewport.Height) * 0.5f;
            _camera2D.Target = new Vector2(_sliceDrawData.Width * 0.5f, _sliceDrawData.Height * 0.5f);
            _camera2D.Rotation = 0f;
            _camera2D.Zoom = Math.Max(
                0.01f,
                Math.Min(
                    _sliceViewport.Width / (_sliceDrawData.Width + margin * 2f),
                    _sliceViewport.Height / (_sliceDrawData.Height + margin * 2f)));

            SliceRenderer.Draw(
                _sliceDrawData,
                _camera2D,
                GetSliceRenderOptions(),
                Get2DRenderStyleOptions());

            NavigationGizmo.Draw2D(
                _sliceDrawData.Axis,
                _sliceViewport.Width,
                _sliceViewport.Height);
        }

        DrawViewportBranding(_sliceViewport?.Width ?? 1, _sliceViewport?.Height ?? 1);
    }

    private void DrawViewportBranding(int viewportWidth, int viewportHeight)
    {
        if (!_showViewportBranding || _viewportBranding.Id == 0)
            return;

        const string label = "Numos";
        string coreSimVersionLabel = $"CoreSim v{CoreSimBuildInfo.PackageVersion}";
        string viewerVersionLabel = $"Viewer v{ViewerBuildInfo.PackageVersion}";

        float smallerViewportDimension = Math.Max(1f, Math.Min(viewportWidth, viewportHeight));
        int requestedLogoSize = Math.Max(
            1,
            (int)MathF.Round(smallerViewportDimension * _viewportBrandingSizePercent / 100f));

        int logoSize = Math.Min(requestedLogoSize, Math.Max(1, Math.Min(viewportHeight, viewportWidth / 4)));
        int textLineGap = Math.Max(1, (int)MathF.Round(logoSize * 0.08f));
        int textHeight = Math.Max(3, logoSize - textLineGap * 2);
        int titleTextSize = Math.Max(1, textHeight / 2);
        int coreSimVersionTextSize = Math.Max(1, (textHeight - titleTextSize) / 2);
        int viewerVersionTextSize = Math.Max(1, textHeight - titleTextSize - coreSimVersionTextSize);
        int logoTextGap = Math.Max(1, (int)MathF.Round(logoSize * 0.23f));
        int textColumnWidth = Math.Max(
            Raylib.MeasureText(label, titleTextSize),
            Math.Max(
                Raylib.MeasureText(coreSimVersionLabel, coreSimVersionTextSize),
                Raylib.MeasureText(viewerVersionLabel, viewerVersionTextSize)));

        int totalWidth = logoSize + logoTextGap + textColumnWidth;
        int offsetX = Math.Max(0, (int)MathF.Round(_viewportBrandingOffsetX));
        int offsetY = Math.Max(0, (int)MathF.Round(_viewportBrandingOffsetY));
        (int left, int top) = _viewportBrandingCorner switch
        {
            ViewportBrandingCorner.TopLeft => (offsetX, offsetY),
            ViewportBrandingCorner.TopRight => (viewportWidth - offsetX - totalWidth, offsetY),
            ViewportBrandingCorner.BottomLeft => (offsetX, viewportHeight - offsetY - logoSize),
            ViewportBrandingCorner.BottomRight =>
                (viewportWidth - offsetX - totalWidth, viewportHeight - offsetY - logoSize),
            _ => (offsetX, offsetY)
        };

        left = Math.Clamp(left, 0, Math.Max(0, viewportWidth - totalWidth));
        top = Math.Clamp(top, 0, Math.Max(0, viewportHeight - logoSize));
        var tint = new Color(1f, 1f, 1f, _viewportBrandingOpacityPercent / 100f);

        Raylib.DrawTexturePro(
            _viewportBranding,
            new Rectangle(0, 0, _viewportBranding.Width, _viewportBranding.Height),
            new Rectangle(left, top, logoSize, logoSize),
            Vector2.Zero,
            0f,
            tint);

        int textLeft = left + logoSize + logoTextGap;
        Raylib.DrawText(label, textLeft, top, titleTextSize, tint);
        Raylib.DrawText(
            coreSimVersionLabel,
            textLeft,
            top + titleTextSize + textLineGap,
            coreSimVersionTextSize,
            tint);

        Raylib.DrawText(
            viewerVersionLabel,
            textLeft,
            top + titleTextSize + textLineGap + coreSimVersionTextSize + textLineGap,
            viewerVersionTextSize,
            tint);
    }

    private static string GetViewportBrandingCornerLabel(ViewportBrandingCorner corner)
    {
        return corner switch
        {
            ViewportBrandingCorner.TopLeft => "Top left",
            ViewportBrandingCorner.TopRight => "Top right",
            ViewportBrandingCorner.BottomLeft => "Bottom left",
            ViewportBrandingCorner.BottomRight => "Bottom right",
            _ => throw new ArgumentOutOfRangeException(nameof(corner), corner, null)
        };
    }

    private void UpdateSlicePicking()
    {
        _hoveredSliceCell = null;
        if (_sliceViewport == null || _sliceDrawData == null)
        {
            RebuildHighlights();
            return;
        }

        var viewport = _sliceViewport;
        float aspectRatio = viewport.Width / (float)Math.Max(viewport.Height, 1);
        if (viewport.IsHovered &&
            _sliceDrawData.TryPickNormalized(
                viewport.NormalizedMousePosition.X,
                viewport.NormalizedMousePosition.Y,
                aspectRatio,
                out var cell))
        {
            _hoveredSliceCell = cell;
        }

        HandleVoxelPointer(viewport, _hoveredSliceCell?.Address, 2);

        RebuildHighlights();
    }

    private SliceRenderOptions GetSliceRenderOptions()
    {
        return new SliceRenderOptions(_highlights);
    }

    private Render3DStyleOptions Get3DRenderStyleOptions()
    {
        return new Render3DStyleOptions(
            _show3DChunkOutlines,
            _show3DVoxelOutlines,
            _transparent3DVoxels);
    }

    private Render2DStyleOptions Get2DRenderStyleOptions()
    {
        return new Render2DStyleOptions(
            _show2DChunkOutlines,
            _show2DVoxelOutlines,
            _transparent2DVoxels);
    }

    private void RebuildHighlights()
    {
        _highlights.Clear();
        foreach (var selected in _selectedCells)
        {
            var color = selected == _selectedCell
                ? new ColorRgba(1f, 0.82f, 0.15f)
                : new ColorRgba(0.22f, 0.68f, 0.92f);

            _highlights.Add(new VoxelHighlight(selected, color));
        }

        if (_hoveredSliceCell.HasValue && _hoveredSliceCell.Value.Address != _selectedCell)
            _highlights.Add(new VoxelHighlight(_hoveredSliceCell.Value.Address, new ColorRgba(1f, 1f, 1f)));

        if (_hovered3DCell.HasValue && !_selectedCells.Contains(_hovered3DCell.Value))
            _highlights.Add(new VoxelHighlight(_hovered3DCell.Value, new ColorRgba(1f, 1f, 1f)));
    }

    private enum VoxelEditTool
    {
        Select,
        PaintClassification,
        PaintGas,
        EraseGas
    }

    private enum ViewportBrandingCorner
    {
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight
    }

    private readonly record struct VoxelDetailCacheEntry(
        AtmosChunkVersion PresentedVersion,
        bool IsAvailable,
        AtmosVoxelSnapshot Snapshot);

    private readonly record struct SliceProjectionKey(
        ChunkIdentity Chunk,
        ulong TopologyVersion,
        ulong StyleVersion,
        SliceAxis Axis,
        int SliceIndex);
}