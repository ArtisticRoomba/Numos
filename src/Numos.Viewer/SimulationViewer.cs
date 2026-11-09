using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;
using Numos.API;
using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.Maths;
using Numos.SimDrawer;
using Numos.Viewer.Rendering;
using Numos.Viewer.Rendering.Viewport;
using Numos.Viewer.Ui;
using Numos.Viewer.Workspace;
using Numos.Workbench;
using Raylib_cs;
using rlImGui_cs;

namespace Numos.Viewer;

/// <summary>
///     Interactive raylib/ImGui host for the simulation presentation layer.
/// </summary>
public partial class SimulationViewer : IDisposable
{
    private const float CameraMoveDuration = 0.45f;
    private const float MaxSliceZoom = 256f;
    private const int DefaultLegendResolution = 8;

    /// <summary>
    ///     The smallest size raylib's <see cref="Raylib.DrawText(string, int, int, int, Color)" /> draws the default
    ///     font at. A smaller size draws at this one.
    /// </summary>
    private const int MinimumBrandingTextSize = 10;

    private readonly static TimeSpan ImGuiTransitionRenderDuration = TimeSpan.FromMilliseconds(250);
    private readonly static TimeSpan StartupRenderDuration = TimeSpan.FromMilliseconds(250);

    private readonly Action<VisualizationRegistry>? _configureVisualizations;
    private readonly List<VoxelGasSnapshot> _gasMakeupRows = [];
    private readonly List<VoxelHighlight> _highlights = [];
    private readonly HashSet<VoxelAddress> _paintedCells = [];
    private readonly HashSet<VoxelAddress> _selectedCells = [];
    private readonly HashSet<Int3> _liveChunkPositions = [];

    /// <summary>
    ///     Chunk handles of the active simulation, from the latest frame. <see cref="_liveChunkPositions" /> holds the
    ///     same positions for lookups.
    /// </summary>
    private IReadOnlyList<AtmosChunkHandle> _liveChunkHandles = [];
    private int _appliedTargetFps = -1;
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
    private long _continuousRenderingUntilTimestamp;
    private SliceAxis _currentSliceAxis = SliceAxis.Z;
    private int _currentSliceIndex;
    private string _currentVisualizationId = BuiltInVisualizationIds.Temperature;
    private SimulationDrawData? _drawData;
    private bool _eventBasedRenderingEnabled;
    private bool _eventWaitingEnabled;
    private ChunkIdentity? _focusedChunk;
    private bool _frameSceneOnNextPresentation;
    private bool _hadOpenImGuiPopup;
    private VoxelAddress? _hovered3DCell;
    private SliceCellDrawData? _hoveredSliceCell;
    private bool _imguiInitialized;

    private bool _isOrbiting;
    private VoxelAddress? _lastPaintedCell;
    private bool _legendAutomaticBounds = true;
    private float _legendAutomaticRangeOffset;
    private float _legendMaximum = 1f;
    private float _legendMinimum;
    private ulong _legendRangeRevision;
    private int _legendResolution = 32;
    private bool _legendResolutionEnabled;
    private bool _limitFpsWhenUnfocused = true;

    /// <summary>
    ///     Mesh cache of the surface being drawn. <see cref="RenderSimulationViewports" /> points it at each surface in
    ///     turn, like <see cref="_drawData" />.
    /// </summary>
    private ChunkMeshCache? _meshCache;

    // Read and written with Volatile, because a background task can request a frame.
    private bool _nextFrameRequested;
    private string? _pendingCulture;
    private int _programSettingsTab;
    private bool _requestExit;
    private VoxelAddress? _selectedCell;
    private bool _show2DChunkOutlines;
    private bool _show2DVelocityArrows;
    private bool _show2DVoxelOutlines = true;
    private bool _show3DChunkOutlines;
    private bool _show3DVelocityArrows;
    private bool _show3DViewport = true;
    private bool _show3DVoxelOutlines = true;
    private bool _showPerformanceOverlay;
    private bool _showProgramSettingsPanel;
    private bool _showSliceViewport = true;

    private bool _showViewportBranding = true;

    /// <summary>
    ///     The frame entry of the simulation being presented. <see cref="RenderSimulationViewports" /> points it at
    ///     each surface in turn, like <see cref="_drawData" />.
    /// </summary>
    private WorkbenchSimulationFrame? _simulation;
    private Vector2 _sliceCameraCenter;

    /// <summary>
    ///     Slice camera. The camera follows the plane while <see cref="_sliceCameraFit" /> is set, and it keeps the
    ///     centre and zoom below once a person pans or zooms. The centre is in camera coordinates, where Y grows
    ///     downward, so V maps to <c>Height - V</c>.
    /// </summary>
    private bool _sliceCameraFit = true;
    private (int Width, int Height, SliceAxis Axis) _sliceCameraPlane;
    private float _sliceCameraZoom = 1f;
    private SimulationSliceDrawData? _sliceDrawData;

    /// <summary>
    ///     Chunk that the slice camera should frame once a slice exists, set when the 3D view focuses a chunk.
    /// </summary>
    private Int3? _sliceFrameChunk;
    private bool _sliceIsPanning;
    private SliceRenderer? _sliceRenderer;
    private SliceAxis _sliceSourceAxis;

    /// <summary>
    ///     Frame, axis, and index that <see cref="_sliceDrawData" /> was built from. The plane is rebuilt only when
    ///     one of them changes, so a paused viewer does no slice work.
    /// </summary>
    private SimulationDrawData? _sliceSourceFrame;
    private int _sliceSourceIndex;
    private SimulationViewport? _sliceViewport;
    private int _targetFps = 144;
    private bool _transparent2DVoxels;
    private bool _transparent3DVoxels;
    private bool _uncappedFps;
    private int _unfocusedFps = 15;
    private float _velocityArrowScaleSeconds = VelocityArrows.DefaultScaleSeconds;

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
        Locale.SetCulture();

        // The document's drafts read localized defaults, so it comes after the culture.
        _document = new ProjectDocument();
        _projectNameDraft = Locale.GetString("common-untitled-simulation");

        _configureVisualizations = configureVisualizations;
        var session = new WorkbenchSession(
            _messages,
            new WorkbenchHostInfo("Numos.Viewer", ViewerBuildInfo.PackageVersion));

        _host = new WorkbenchHost(session);
        _frame = _host.Frame;
        _projectEpoch = _frame.ProjectEpoch;
        StartMessageCapture();
    }

    /// <summary>
    ///     Primary selected voxel, or <see langword="null" /> when nothing is selected.
    /// </summary>
    public VoxelAddress? SelectedCell => _selectedCell;

    /// <summary>
    ///     Opens the window and runs the frame loop on the calling thread until the window is closed or the viewer
    ///     requests exit.
    /// </summary>
    /// <remarks>
    ///     Graphics resources, including the window, are released when this returns. The simulation project stays
    ///     loaded until <see cref="Dispose" />.
    /// </remarks>
    public void Run()
    {
        Raylib.SetConfigFlags(
            ConfigFlags.ResizableWindow |
            ConfigFlags.Msaa4xHint |
            ConfigFlags.VSyncHint);

        Raylib.InitWindow(1400, 900, "Numos.Viewer");
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

            // Escape belongs to ImGui for closing popups and backing out of keyboard navigation; raylib's default
            // exit key would otherwise drop an unsaved in-memory project without asking.
            Raylib.SetExitKey(KeyboardKey.Null);
            rlImGui.Setup(true, true);
            _imguiInitialized = true;
            unsafe
            {
                // Without a file name ImGui never touches ./imgui.ini. The layout goes through LayoutStore instead.
                ImGui.GetIO().NativePtr->IniFilename = null;
            }

            ViewerTheme.Apply();
            var io = ImGui.GetIO();
            io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
            io.ConfigWindowsMoveFromTitleBarOnly = true;
            var font = LoadFonts(io);
            unsafe
            {
                io.NativePtr->FontDefault = font.NativePtr;
            }

            rlImGui.ReloadFonts();
            ConfigureLayoutPersistence();

            _sliceViewport = new SimulationViewport(
                TextureFilter.Point,
                new Color(0.04f, 0.04f, 0.05f, 1f));

            _sliceRenderer = new SliceRenderer();

            RequestRenderingFor(StartupRenderDuration);
            ApplyFramePacing();

            while (!Raylib.WindowShouldClose() && !_requestExit)
            {
                Volatile.Write(ref _nextFrameRequested, false);
                if (_pendingCulture != null)
                {
                    Locale.SetCulture(_pendingCulture);
                    _pendingCulture = null;
                    RequestNextFrame();
                }

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
        UpdateHost();
        UpdateReplayFileOperations();
        HandleReplayFileDrop();

        if (_frame.HasProject)
        {
            SyncPresentationConfig();
            ReconcileSimulationSurfaces();
            RefreshTreeSelection();
            ForgetStaleSteppedTick();
            UpdateSurfaceBuilds();
            RefreshPresentation();
        }

        UpdateCamera(deltaTime);
    }

    /// <summary>
    ///     Takes the active surface's newest draw data into the presentation. The draw data comes from a background
    ///     build, so this only swaps references and updates the selection, slice, and camera.
    /// </summary>
    private void RefreshPresentation()
    {
        var surface = GetActiveSurface();
        if (surface == null)
            return;

        if (!ReferenceEquals(_liveChunkHandles, surface.Frame.ChunkHandles))
        {
            _liveChunkHandles = surface.Frame.ChunkHandles;
            _liveChunkPositions.Clear();
            foreach (var handle in _liveChunkHandles)
                _liveChunkPositions.Add(handle.Position);
        }

        if (surface.DrawData == null || ReferenceEquals(surface.DrawData, _drawData))
        {
            RefreshSliceData();
            return;
        }

        // A timeline move can drop a chunk from the shown tick. The focus and selection stay then, so returning to
        // the head restores them.
        long timelineRevision = surface.BuiltInputs?.TimelineRevision ?? _document.PresentedTimelineRevision;
        bool timelineMoved = timelineRevision != _document.PresentedTimelineRevision;
        _document.PresentedTimelineRevision = timelineRevision;
        _drawData = surface.DrawData;
        NormalizeInteractionState(timelineMoved);
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

    private void RefreshSliceData()
    {
        if (_drawData == null || _document.FrameBuilder == null || _drawData.Chunks.Count == 0)
        {
            _sliceDrawData = null;
            _sliceSourceFrame = null;
            return;
        }

        if (!_showSliceViewport)
            return;

        _currentSliceIndex = ClampSliceIndex(_drawData, _currentSliceAxis, _currentSliceIndex);
        if (ReferenceEquals(_sliceSourceFrame, _drawData) &&
            _sliceSourceAxis == _currentSliceAxis &&
            _sliceSourceIndex == _currentSliceIndex)
        {
            return;
        }

        // Passing the old slice lets the builder keep its pixels and cell lookup when nothing in the plane changed.
        _sliceDrawData = _document.FrameBuilder.BuildPlaneSlice(
            _drawData,
            _currentSliceAxis,
            _currentSliceIndex,
            _sliceDrawData);

        _sliceSourceFrame = _drawData;
        _sliceSourceAxis = _currentSliceAxis;
        _sliceSourceIndex = _currentSliceIndex;
        _hoveredSliceCell = null;
    }

    private ImFontPtr LoadFonts(ImGuiIOPtr io)
    {
        string assets = Path.Combine(AppContext.BaseDirectory, "assets");
        string proggy = Path.Combine(assets, "ProggyClean.ttf");
        string cyrillic = Path.Combine(assets, "NumosFixedCyrillic-6x13.ttf");

        var main = AddFont(io, proggy, 13f, false, IntPtr.Zero, 0f);
        // The Cyrillic font is misc-fixed 6x13 padded to ProggyClean's 7px advance (eng/fonts/build_fixed_cyrillic.py).
        // At 13 px it lands on whole pixels like ProggyClean.
        AddFont(io, cyrillic, 13f, true, io.Fonts.GetGlyphRangesCyrillic(), 0f);
        RegisterIconRects(io, main, Path.Combine(assets, "icons.png"));
        return main;
    }

    /// <summary>
    ///     Registers one custom glyph rectangle per icon on <paramref name="font" />, builds the atlas, and copies the
    ///     sheet pixels into the rectangles. <c>rlImGui.ReloadFonts</c> only reads the built atlas, so it uploads the
    ///     copied pixels. A missing or too-small sheet leaves the atlas without icons.
    /// </summary>
    private unsafe void RegisterIconRects(ImGuiIOPtr io, ImFontPtr font, string sheetPath)
    {
        int width = Icons.Columns * Icons.CellSize;
        int height = (Icons.Count + Icons.Columns - 1) / Icons.Columns * Icons.CellSize;

        if (!File.Exists(sheetPath))
        {
            ReportIconSheetError(sheetPath, Locale.GetString("icon-sheet-missing"));
            return;
        }

        var sheet = Raylib.LoadImage(sheetPath);
        Color* colors = null;
        try
        {
            if (sheet.Width < width || sheet.Height < height)
            {
                ReportIconSheetError(
                    sheetPath,
                    Locale.GetString(
                        "icon-sheet-size",
                        ("width", width),
                        ("height", height),
                        ("actualWidth", sheet.Width),
                        ("actualHeight", sheet.Height)));
                return;
            }

            colors = Raylib.LoadImageColors(sheet);
            var rectIndices = new int[Icons.Count];
            for (int i = 0; i < Icons.Count; i++)
            {
                rectIndices[i] = io.Fonts.AddCustomRectFontGlyph(
                    font,
                    (ushort)(Icons.First + i),
                    Icons.CellSize,
                    Icons.CellSize,
                    Icons.Advance,
                    Icons.Offset);
            }

            // Building the atlas packs the rectangles. The RGBA32 copy that rlImGui uploads is cached after this call.
            io.Fonts.GetTexDataAsRGBA32(out byte* pixels, out int atlasWidth, out _, out _);
            for (int i = 0; i < Icons.Count; i++)
            {
                var rect = io.Fonts.GetCustomRectByIndex(rectIndices[i]);
                int cellX = i % Icons.Columns * Icons.CellSize;
                int cellY = i / Icons.Columns * Icons.CellSize;
                for (int y = 0; y < Icons.CellSize; y++)
                {
                    var row = (uint*)(pixels + ((rect.Y + y) * atlasWidth + rect.X) * 4);
                    for (int x = 0; x < Icons.CellSize; x++)
                    {
                        // Ink is opaque white and the background is transparent, so the alpha channel is the mask.
                        byte alpha = colors[(cellY + y) * sheet.Width + cellX + x].A;
                        row[x] = alpha == 0 ? 0x00FFFFFFu : 0xFFFFFFFFu;
                    }
                }
            }
        }
        finally
        {
            if (colors != null)
                Raylib.UnloadImageColors(colors);

            Raylib.UnloadImage(sheet);
        }
    }

    private void ReportIconSheetError(string path, string reason)
    {
        WriteMessage(
            WorkbenchLogLevel.Error,
            "Viewer",
            Locale.GetString("icon-sheet-error", ("path", path), ("reason", reason)));
    }

    private static unsafe ImFontPtr AddFont(
        ImGuiIOPtr io,
        string path,
        float size,
        bool merge,
        IntPtr glyphRanges,
        float glyphMinAdvanceX)
    {
        ImFontConfig* config = ImGuiNative.ImFontConfig_ImFontConfig();
        config->OversampleH = 1;
        config->OversampleV = 1;
        config->PixelSnapH = 1;
        config->MergeMode = merge ? (byte)1 : (byte)0;
        config->GlyphMinAdvanceX = glyphMinAdvanceX;
        try
        {
            return io.Fonts.AddFontFromFileTTF(path, size, new ImFontConfigPtr(config), glyphRanges);
        }
        finally
        {
            ImGuiNative.ImFontConfig_destroy(config);
        }
    }

    private static int ClampSliceIndex(SimulationDrawData frame, SliceAxis axis, int index)
    {
        int minimum = frame.Bounds.GetMinimum(axis);
        int maximum = Math.Max(frame.Bounds.GetMaximumExclusive(axis) - 1, minimum);
        return Math.Clamp(index, minimum, maximum);
    }

    /// <summary>
    ///     Points the slice at a chunk. The plane moves to the chunk if it does not cut it, and the slice camera frames
    ///     the chunk once the slice exists.
    /// </summary>
    private void FrameSliceOnChunk(ChunkDrawData chunk)
    {
        int length = SimulationFrameBuilder.GetSliceAxisLength(chunk.Dimensions, _currentSliceAxis);
        int first = _currentSliceAxis switch
                    {
                        SliceAxis.X => chunk.ChunkPosition.X,
                        SliceAxis.Y => chunk.ChunkPosition.Y,
                        _ => chunk.ChunkPosition.Z
                    } *
                    length;

        _currentSliceIndex = Math.Clamp(_currentSliceIndex, first, first + length - 1);
        _sliceFrameChunk = chunk.ChunkPosition;
    }

    private void NormalizeInteractionState(bool timelineMoved)
    {
        if (_drawData == null)
            return;

        if (_focusedChunk.HasValue)
        {
            if (_drawData.Chunks.TryGetValue(_focusedChunk.Value.Position, out var focused))
                _focusedChunk = focused.Identity;
            else if (!IsInspecting && !timelineMoved)
            {
                _focusedChunk = null;
                _frameSceneOnNextPresentation = true;
            }
        }

        if (_selectedCell.HasValue && !_drawData.TryResolve(_selectedCell.Value, out _))
        {
            if (IsInspecting || timelineMoved)
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
            FrameSliceOnChunk(data);
            FocusCameraOnChunk(data);
        }
        else
        {
            _sliceFrameChunk = null;
            _sliceCameraFit = true;
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

    private bool TryGetSceneBounds(out Vector3 minimum, out Vector3 maximum)
    {
        minimum = default;
        maximum = default;
        if (_drawData == null || _drawData.Chunks.Count == 0)
            return false;

        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        foreach (var chunk in _drawData.Chunks.Values)
        {
            var origin = new Vector3(
                chunk.ChunkPosition.X * chunk.Dimensions.X,
                chunk.ChunkPosition.Y * chunk.Dimensions.Y,
                chunk.ChunkPosition.Z * chunk.Dimensions.Z);
            min = Vector3.Min(min, origin);
            max = Vector3.Max(max, origin + new Vector3(chunk.Dimensions.X, chunk.Dimensions.Y, chunk.Dimensions.Z));
        }

        minimum = min;
        maximum = max;
        return true;
    }

    private void FocusCameraOnScene()
    {
        if (!TryGetSceneBounds(out var minimum, out var maximum))
            return;

        var target = (minimum + maximum) * 0.5f;
        var size = maximum - minimum;
        float distance = Math.Max(5f, Math.Max(size.X, Math.Max(size.Y, size.Z)) * 2.5f);
        FrameCamera(target, distance);
    }

    /// <summary>
    ///     Sets the Raylib clip planes for <see cref="_camera3D" /> and the current scene.
    /// </summary>
    /// <returns>The previous planes. Pass them to <see cref="RestoreClipPlanes" /> when the 3D work ends.</returns>
    /// <remarks>
    ///     <c>BeginMode3D</c>, <c>GetScreenToWorldRayEx</c>, and <c>GetWorldToScreenEx</c> read the planes when they
    ///     run, so call this before any of them.
    /// </remarks>
    private (double Near, double Far) ApplyClipPlanes()
    {
        var previous = (Rlgl.GetCullDistanceNear(), Rlgl.GetCullDistanceFar());
        float distanceToTarget = Vector3.Distance(_camera3D.Position, _camera3D.Target);
        float distanceToCenter = distanceToTarget;
        float radius = 0f;
        if (TryGetSceneBounds(out var minimum, out var maximum))
        {
            var center = (minimum + maximum) * 0.5f;
            distanceToCenter = Vector3.Distance(_camera3D.Position, center);
            radius = (maximum - minimum).Length() * 0.5f;
        }

        var (near, far) = CameraScale.GetClipPlanes(distanceToTarget, distanceToCenter, radius);
        Rlgl.SetClipPlanes(near, far);
        return previous;
    }

    private static void RestoreClipPlanes((double Near, double Far) previous)
    {
        Rlgl.SetClipPlanes(previous.Near, previous.Far);
    }

    private void UpdateCamera(float deltaTime)
    {
        UpdateCameraMove(deltaTime);

        bool hovered = _viewport is { IsHovered: true };
        bool middleDown = Raylib.IsMouseButtonDown(MouseButton.Middle);

        if (middleDown && (hovered || _isOrbiting))
        {
            _isOrbiting = true;
            CancelCameraMove();
            var mouseDelta = Raylib.GetMouseDelta();
            Raylib.CameraYaw(ref _camera3D, -mouseDelta.X * 0.01f, true);
            Raylib.CameraPitch(ref _camera3D, -mouseDelta.Y * 0.01f, true, true, false);
        }
        else
        {
            _isOrbiting = false;
        }

        if (!hovered)
            return;

        float wheel = Raylib.GetMouseWheelMove();
        if (wheel != 0f)
        {
            CancelCameraMove();
            float distance = Vector3.Distance(_camera3D.Position, _camera3D.Target);
            Raylib.CameraMoveToTarget(ref _camera3D, CameraScale.GetZoomedDistance(distance, wheel) - distance);
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
        RequestNextFrame();
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

        if (_cameraMoveElapsed < CameraMoveDuration)
            RequestNextFrame();
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
            UpdateImGuiFrameDemand();

            rlImGui.End();
            AutosaveLayout();
            ApplyFramePacing();
        }
        finally
        {
            Raylib.EndDrawing();
        }
    }

    private void ApplyFramePacing()
    {
        bool waitForEvents = _eventBasedRenderingEnabled && !RequiresContinuousFrames();
        if (waitForEvents != _eventWaitingEnabled)
        {
            if (waitForEvents)
                Raylib.EnableEventWaiting();
            else
                Raylib.DisableEventWaiting();

            _eventWaitingEnabled = waitForEvents;
        }

        int targetFps = !Raylib.IsWindowFocused() && _limitFpsWhenUnfocused
            ? _unfocusedFps
            : _uncappedFps
                ? 0
                : _targetFps;

        if (targetFps == _appliedTargetFps)
            return;

        Raylib.SetTargetFPS(targetFps);
        _appliedTargetFps = targetFps;
    }

    private bool RequiresContinuousFrames()
    {
        if (Volatile.Read(ref _nextFrameRequested) ||
            Stopwatch.GetTimestamp() < _continuousRenderingUntilTimestamp ||
            _resolutionConfirmationPending)
            return true;

        // The host cannot wake a window that waits for events, so the viewer keeps drawing while it expects a
        // command result, a new frame, or a background build.
        return IsAwaitingHost() || HasPendingSurfaceBuild();
    }

    /// <summary>
    ///     Requests one more rendered frame before the viewer returns to waiting for window events.
    /// </summary>
    /// <remarks>
    ///     Call this during every frame in which an animation or other time-dependent UI state remains active.
    ///     A single call schedules only the next frame, so the viewer becomes idle automatically when the caller
    ///     stops renewing the request.
    /// </remarks>
    private void RequestNextFrame()
    {
        Volatile.Write(ref _nextFrameRequested, true);
    }

    /// <summary>
    ///     Requests continuous rendering for at least the specified duration.
    /// </summary>
    /// <param name="duration">
    ///     The minimum time before the viewer may return to waiting for window events.
    /// </param>
    /// <remarks>
    ///     Use this for short transitions that need several cleanup frames after the triggering input has ended.
    ///     Animations with an explicit active state should call <see cref="RequestNextFrame" /> on every frame instead.
    /// </remarks>
    private void RequestRenderingFor(TimeSpan duration)
    {
        long durationTicks = (long)Math.Ceiling(duration.TotalSeconds * Stopwatch.Frequency);
        long requestedDeadline = Stopwatch.GetTimestamp() + durationTicks;
        _continuousRenderingUntilTimestamp = Math.Max(_continuousRenderingUntilTimestamp, requestedDeadline);
    }

    private void UpdateImGuiFrameDemand()
    {
        if (ImGui.IsAnyItemActive())
            RequestNextFrame();

        bool hasOpenPopup = ImGui.IsPopupOpen(string.Empty, ImGuiPopupFlags.AnyPopupId);
        bool pointerTransition =
            ImGui.IsMouseClicked(ImGuiMouseButton.Left) ||
            ImGui.IsMouseClicked(ImGuiMouseButton.Middle) ||
            ImGui.IsMouseClicked(ImGuiMouseButton.Right) ||
            ImGui.IsMouseReleased(ImGuiMouseButton.Left) ||
            ImGui.IsMouseReleased(ImGuiMouseButton.Middle) ||
            ImGui.IsMouseReleased(ImGuiMouseButton.Right);

        var io = ImGui.GetIO();
        if (pointerTransition ||
            io.MouseWheel != 0f ||
            io.MouseWheelH != 0f ||
            hasOpenPopup != _hadOpenImGuiPopup)
        {
            RequestRenderingFor(ImGuiTransitionRenderDuration);
        }

        _hadOpenImGuiPopup = hasOpenPopup;
    }

    private void RenderSimulationScene()
    {
        if (_drawData != null && _meshCache != null)
        {
            var previousPlanes = ApplyClipPlanes();
            Raylib.BeginMode3D(_camera3D);
            try
            {
                SimulationRenderer.Draw(
                    _drawData,
                    _focusedChunk,
                    _highlights,
                    _meshCache,
                    Get3DRenderStyleOptions());

                DrawTopologyOverlay();
            }
            finally
            {
                Raylib.EndMode3D();
                RestoreClipPlanes(previousPlanes);
            }

            // A large scene uploads over several frames, and the window may be waiting for events.
            if (_meshCache.HasPendingWork)
                RequestNextFrame();
        }

        if (_viewport != null)
            NavigationGizmo.Draw3D(_camera3D, _viewport.Width, _viewport.Height);

        DrawViewportBranding(_viewport?.Width ?? 1, _viewport?.Height ?? 1);
    }

    private void RenderSimulationSliceScene()
    {
        if (_sliceDrawData != null && _sliceViewport != null && _sliceRenderer != null)
        {
            var viewportSize = new Vector2(_sliceViewport.Width, _sliceViewport.Height);
            UpdateSliceCamera(_sliceDrawData, viewportSize);
            _sliceRenderer.Draw(
                _sliceDrawData,
                _camera2D,
                viewportSize,
                GetSliceRenderOptions(),
                Get2DRenderStyleOptions());

            NavigationGizmo.Draw2D(
                _sliceDrawData.Axis,
                _sliceViewport.Width,
                _sliceViewport.Height);
        }

        DrawViewportBranding(_sliceViewport?.Width ?? 1, _sliceViewport?.Height ?? 1);
    }

    /// <summary>
    ///     Sets <see cref="_camera2D" /> for this frame: it fits the plane, frames a requested chunk, or keeps the
    ///     centre and zoom a person chose.
    /// </summary>
    private void UpdateSliceCamera(SimulationSliceDrawData slice, Vector2 viewportSize)
    {
        const float margin = 0.5f;
        if (_sliceCameraPlane != (slice.Width, slice.Height, slice.Axis))
        {
            _sliceCameraPlane = (slice.Width, slice.Height, slice.Axis);
            _sliceCameraFit = true;
        }

        float fitZoom = Math.Max(
            0.001f,
            Math.Min(
                viewportSize.X / (slice.Width + margin * 2f),
                viewportSize.Y / (slice.Height + margin * 2f)));

        if (_sliceFrameChunk is { } framed)
        {
            _sliceFrameChunk = null;
            var (u, v, width, height) = slice.GetFootprint(framed);
            _sliceCameraCenter = new Vector2(u + width * 0.5f, slice.Height - v - height * 0.5f);
            _sliceCameraZoom = Math.Max(
                0.001f,
                Math.Min(
                    viewportSize.X / (width + margin * 2f),
                    viewportSize.Y / (height + margin * 2f)));

            _sliceCameraFit = false;
        }

        if (_sliceCameraFit)
        {
            _sliceCameraCenter = new Vector2(slice.Width * 0.5f, slice.Height * 0.5f);
            _sliceCameraZoom = fitZoom;
        }

        _sliceCameraZoom = Math.Clamp(_sliceCameraZoom, fitZoom * 0.25f, MaxSliceZoom);
        _camera2D.Offset = viewportSize * 0.5f;
        _camera2D.Target = _sliceCameraCenter;
        _camera2D.Rotation = 0f;
        _camera2D.Zoom = _sliceCameraZoom;
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
        int titleSplit = textHeight / 2;
        int coreSimSplit = (textHeight - titleSplit) / 2;

        // DrawText never draws the default font below its minimum size, so the lines are spaced by the size drawn.
        int titleTextSize = Math.Max(MinimumBrandingTextSize, titleSplit);
        int coreSimVersionTextSize = Math.Max(MinimumBrandingTextSize, coreSimSplit);
        int viewerVersionTextSize = Math.Max(MinimumBrandingTextSize, textHeight - titleSplit - coreSimSplit);
        int textBlockHeight = titleTextSize + coreSimVersionTextSize + viewerVersionTextSize + textLineGap * 2;
        int brandingHeight = Math.Max(logoSize, textBlockHeight);
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
            ViewportBrandingCorner.BottomLeft => (offsetX, viewportHeight - offsetY - brandingHeight),
            ViewportBrandingCorner.BottomRight =>
                (viewportWidth - offsetX - totalWidth, viewportHeight - offsetY - brandingHeight),
            _ => (offsetX, offsetY)
        };

        left = Math.Clamp(left, 0, Math.Max(0, viewportWidth - totalWidth));
        top = Math.Clamp(top, 0, Math.Max(0, viewportHeight - brandingHeight));
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
            ViewportBrandingCorner.TopLeft => Locale.GetString("side-top-left"),
            ViewportBrandingCorner.TopRight => Locale.GetString("side-top-right"),
            ViewportBrandingCorner.BottomLeft => Locale.GetString("side-bottom-left"),
            ViewportBrandingCorner.BottomRight => Locale.GetString("side-bottom-right"),
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
        HandleSliceCameraInput(viewport);
        if (viewport.IsHovered &&
            TryPickSliceCell(viewport, _sliceDrawData, out var cell))
        {
            _hoveredSliceCell = cell;
        }

        HandleVoxelPointer(viewport, _hoveredSliceCell?.Address, 2);

        RebuildHighlights();
    }

    /// <summary>
    ///     Finds the slice cell under the pointer with the camera the slice was drawn with.
    /// </summary>
    private bool TryPickSliceCell(
        SimulationViewport viewport,
        SimulationSliceDrawData slice,
        out SliceCellDrawData cell)
    {
        var screen = new Vector2(
            viewport.NormalizedMousePosition.X * viewport.Width,
            (1f - viewport.NormalizedMousePosition.Y) * viewport.Height);

        var world = Raylib.GetScreenToWorld2D(screen, _camera2D);
        int u = (int)MathF.Floor(world.X);
        int v = slice.Height - 1 - (int)MathF.Floor(world.Y);
        return slice.TryGetCell(u, v, out cell);
    }

    /// <summary>
    ///     Zooms the slice camera with the wheel around the pointer and pans it with the middle button. The left
    ///     button stays free for selection and painting.
    /// </summary>
    private void HandleSliceCameraInput(SimulationViewport viewport)
    {
        bool middleDown = Raylib.IsMouseButtonDown(MouseButton.Middle);
        if (middleDown && (viewport.IsHovered || _sliceIsPanning))
        {
            _sliceIsPanning = true;
            var delta = Raylib.GetMouseDelta();
            if (delta != Vector2.Zero)
            {
                _sliceCameraCenter -= delta / _sliceCameraZoom;
                _sliceCameraFit = false;
                RequestNextFrame();
            }
        }
        else
        {
            _sliceIsPanning = false;
        }

        float wheel = viewport.IsHovered ? Raylib.GetMouseWheelMove() : 0f;
        if (wheel == 0f)
            return;

        var screen = new Vector2(
            viewport.NormalizedMousePosition.X * viewport.Width,
            (1f - viewport.NormalizedMousePosition.Y) * viewport.Height);

        // The picking code reads _camera2D after this call, so the zoom is applied to a copy.
        var zoomed = _camera2D;
        var before = Raylib.GetScreenToWorld2D(screen, zoomed);
        _sliceCameraZoom = Math.Clamp(_sliceCameraZoom * MathF.Pow(1.2f, wheel), 0.001f, MaxSliceZoom);
        zoomed.Zoom = _sliceCameraZoom;
        var after = Raylib.GetScreenToWorld2D(screen, zoomed);

        // Keep the world point under the pointer fixed.
        _sliceCameraCenter += before - after;
        _sliceCameraFit = false;
        RequestNextFrame();
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
            _transparent3DVoxels,
            _show3DVelocityArrows,
            _velocityArrowScaleSeconds);
    }

    private Render2DStyleOptions Get2DRenderStyleOptions()
    {
        return new Render2DStyleOptions(
            _show2DChunkOutlines,
            _show2DVoxelOutlines,
            _transparent2DVoxels,
            _show2DVelocityArrows,
            _velocityArrowScaleSeconds);
    }

    /// <summary>
    ///     Fields the visualization needs, plus velocity whenever either viewport draws arrows: the arrows read
    ///     <see cref="VoxelDrawData.VelocityX" /> and friends, which are NaN unless the snapshot carried them.
    /// </summary>
    private AtmosChunkSnapshotFields GetDrawSnapshotFields()
    {
        var fields = _document.FrameBuilder?.GetRequiredSnapshotFields(_currentVisualizationId) ?? AtmosChunkSnapshotFields.All;
        if (_show3DVelocityArrows || _show2DVelocityArrows)
            fields |= AtmosChunkSnapshotFields.Velocity;

        return fields;
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

    private enum ViewportBrandingCorner
    {
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight
    }
}