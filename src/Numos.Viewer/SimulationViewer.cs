using System.Numerics;
using ImGuiNET;
using Numos.API;
using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.Maths;
using Numos.SimDrawer;
using Numos.Viewer.Rendering;
using Numos.Viewer.Rendering.Viewport;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ImGui;
using Silk.NET.Windowing;
using MouseButton = Silk.NET.Input.MouseButton;

namespace Numos.Viewer;

/// <summary>
///     A viewer application (god-class) for visualizing Numos using OpenGL and ImGui.
/// </summary>
public partial class SimulationViewer : IDisposable
{
    private IWindow? _window;
    private GL? _gl;
    private ImGuiController? _imguiController;
    private SimulationRenderer? _renderer;
    private SimulationRenderer? _sliceRenderer;
    private IInputContext? _input;

    private AtmosSimulation? _simulation;
    private AtmosConfig? _config;
    private readonly List<AtmosChunkHandle> _chunkHandles = [];
    private List<AtmosChunkSnapshot> _chunkSnapshots = [];
    private SimDrawer.SimDrawer? _simDrawer;
    private SimulationDrawData? _drawData;
    private SimulationSliceDrawData? _sliceDrawData;
    private CellSelection? _hoveredSliceCell;
    private CellSelection? _selectedCell;

    public CellSelection? SelectedCell => _selectedCell;

    private bool _isPaused;
    private float _timeAccumulator;
    private VisualizationMode _currentVisualizationMode = VisualizationMode.Temperature;

    private SimulationViewport? _viewport;
    private SimulationViewport? _sliceViewport;

    // Camera
    private Vector3D<float> _cameraPosition = new(24, 24, 24);
    private readonly Vector3D<float> _cameraTarget = new(8, 8, 8);
    private float _cameraDistance = 50f;
    private float _cameraYaw = 0.75f * (float)Math.PI;
    private float _cameraPitch = 0.4f * (float)Math.PI;

    // UI State
    private bool _showDebugPanel = true;
    private bool _showSettingsPanel;
    private bool _showSimInfoPanel = true;
    private bool _showSliceViewport = true;
    private SliceAxis _currentSliceAxis = SliceAxis.Z;
    private int _currentSliceIndex;
    private int _selectedSliceChunkIndex;

    public void Run()
    {
        InitializeSimulation();

        var options = WindowOptions.Default;
        options.Title = "Numos Atmospheric Simulation Viewer";

        options.Size = new Vector2D<int>(1400, 900);

        _window = Window.Create(options);
        _window.Load += OnWindowLoad;
        _window.Render += OnWindowRender;
        _window.Update += OnWindowUpdate;
        _window.Closing += OnWindowClosing;
        _window.FramebufferResize += OnFramebufferResize;

        _window.Run();
    }

    private void OnFramebufferResize(Vector2D<int> obj)
    {
        _gl?.Viewport(obj);
    }

    private void InitializeSimulation()
    {
        _config = new AtmosConfig();

        // Register some test gases
        _config.GasRegistry.Add(new GasProperties
        {
            Name = "Oxygen",
            SpecificHeatCapacity = 1000f,
            BoilingPoint = 90f,
            CondensationPoint = 85f,
            LatentHeatOfVaporization = 10000f,
            LiquidId = 0,
            DiffusionCoefficient = 0.1f
        });

        _config.GasRegistry.Add(new GasProperties
        {
            Name = "Nitrogen",
            SpecificHeatCapacity = 1040f,
            BoilingPoint = 77f,
            CondensationPoint = 73f,
            LatentHeatOfVaporization = 11500f,
            LiquidId = 1,
            DiffusionCoefficient = 0.08f
        });

        _simulation = new AtmosSimulation(_config, chunkDepth: 1);
        _simDrawer = new SimDrawer.SimDrawer(_config);

        // Create a test chunk
        var chunk = _simulation.CreateAndRegisterChunk(new Int3(0, 0, 0));
        _chunkHandles.Add(chunk);
        _simulation.SetChunkClassification(chunk, new VoxelClassification(1));

        // Add some gas to create visible data
        for (ushort i = 0; i < 16 * 16; i++)
            _simulation.AddGasToVoxel(chunk, i, 0, 100, 293.15f);
    }

    private void OnWindowLoad()
    {
        _gl = _window!.CreateOpenGL();
        _input = _window!.CreateInput();

        _imguiController = new ImGuiController(_gl, _window, _input);

        ImGui.GetIO().ConfigFlags |= ImGuiConfigFlags.DockingEnable;
        ImGui.GetIO().ConfigWindowsMoveFromTitleBarOnly = true;

        _renderer = new SimulationRenderer(_gl);
        _sliceRenderer = new SimulationRenderer(_gl);
        _viewport = new SimulationViewport(_gl);
        _sliceViewport = new SimulationViewport(_gl);

        _gl.ClearColor(0.1f, 0.1f, 0.1f, 1.0f);
        _gl.Enable(EnableCap.DepthTest);
    }

    private void OnWindowUpdate(double deltaTime)
    {
        if (_simulation != null && _config != null)
        {
            if (!_isPaused)
                _simulation.Update((float)deltaTime, _config);

            // Update draw data less frequently for performance, even while paused so UI slice controls update.
            _timeAccumulator += (float)deltaTime;
            if (_timeAccumulator >= 0.016f) // ~60 FPS cap for draw updates
            {
                UpdateDrawData();
                _timeAccumulator = 0f;
            }
        }

        UpdateCamera();
    }

    private void UpdateDrawData()
    {
        if (_simulation == null || _simDrawer == null)
            return;

        _chunkSnapshots = _chunkHandles.Select(_simulation.GetChunkSnapshot).ToList();

        _drawData = _simDrawer.DrawSimulation(_chunkSnapshots, _currentVisualizationMode);
        if (_drawData.Chunks.FirstOrDefault().Value is ChunkDrawData chunkData)
        {
            var vertices = chunkData.Vertices.ToArray();
            uint[] indices = chunkData.Indices.ToArray();
            if (vertices.Length > 0 && indices.Length > 0)
                _renderer?.UpdateGeometry(vertices, indices);
            else
                _renderer?.ClearGeometry();
        }
        else
        {
            _renderer?.ClearGeometry();
        }

        var selectedSliceChunk = GetSelectedSliceChunk(_chunkSnapshots);
        if (!selectedSliceChunk.HasValue)
        {
            _sliceDrawData = null;
            _sliceRenderer?.ClearGeometry();
            return;
        }

        int maxSliceIndex = Math.Max(
            SimDrawer.SimDrawer.GetSliceAxisLength(selectedSliceChunk.Value.Dimensions, _currentSliceAxis) - 1,
            0);
        _currentSliceIndex = Math.Clamp(_currentSliceIndex, 0, maxSliceIndex);

        _sliceDrawData = _simDrawer.DrawSimulationSlice(
            _chunkSnapshots,
            _currentSliceAxis,
            _currentSliceIndex,
            _currentVisualizationMode,
            selectedSliceChunk.Value.GridPosition);

        if (_sliceDrawData.Chunks.FirstOrDefault().Value is ChunkSliceDrawData sliceData)
        {
            var vertices = sliceData.Vertices.ToArray();
            uint[] indices = sliceData.Indices.ToArray();
            if (vertices.Length > 0 && indices.Length > 0)
                _sliceRenderer?.UpdateGeometry(vertices, indices, PrimitiveType.Lines);
            else
                _sliceRenderer?.ClearGeometry();
        }
        else
        {
            _sliceRenderer?.ClearGeometry();
        }
    }

    private AtmosChunkSnapshot? GetSelectedSliceChunk(IReadOnlyList<AtmosChunkSnapshot> chunks)
    {
        if (chunks.Count == 0)
            return null;

        _selectedSliceChunkIndex = Math.Clamp(_selectedSliceChunkIndex, 0, chunks.Count - 1);
        return chunks[_selectedSliceChunkIndex];
    }

    private void UpdateCamera()
    {
        if (_input == null || _input.Mice.Count == 0)
            return;

        var mouse = _input.Mice[0];

        var io = ImGui.GetIO();

        if (_viewport is not { IsHovered: true })
            return;

        // TODO unhardcode buttons
        if (mouse.IsButtonPressed(MouseButton.Left))
        {
            _cameraYaw -= io.MouseDelta.X * 0.01f;
            _cameraPitch -= io.MouseDelta.Y * 0.01f;
            _cameraPitch = Math.Clamp(_cameraPitch, 0.1f, (float)Math.PI - 0.1f);
        }

        // Scroll wheel zoom
        if (_input.Mice.Count > 0 && _input.Mice[0].ScrollWheels.Count > 0)
        {
            _cameraDistance = Math.Max(5f, _cameraDistance - _input.Mice[0].ScrollWheels[0].Y * 2f);
        }

        _cameraPosition.X =
            _cameraTarget.X + _cameraDistance * (float)Math.Sin(_cameraYaw) * (float)Math.Sin(_cameraPitch);
        _cameraPosition.Y = _cameraTarget.Y + _cameraDistance * (float)Math.Cos(_cameraPitch);
        _cameraPosition.Z =
            _cameraTarget.Z + _cameraDistance * (float)Math.Cos(_cameraYaw) * (float)Math.Sin(_cameraPitch);
    }

    private void OnWindowRender(double deltaTime)
    {
        if (_gl == null)
            return;

        _imguiController?.Update((float)deltaTime);

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _gl.Viewport(0, 0, (uint)_window!.FramebufferSize.X, (uint)_window.FramebufferSize.Y);
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        RenderUi();

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _gl.Viewport(0, 0, (uint)_window.FramebufferSize.X, (uint)_window.FramebufferSize.Y);

        _imguiController?.Render();
    }

    private void RenderSimulationScene(SimulationViewportRenderContext context)
    {
        if (_renderer == null)
            return;

        Vector3D<float> upVector = new(0, 1, 0);

        var viewMatrix = Matrix4X4.CreateLookAt(
            _cameraPosition,
            _cameraTarget,
            upVector);

        var projectionMatrix = Matrix4X4.CreatePerspectiveFieldOfView(
            (float)Math.PI / 4f,
            context.AspectRatio,
            0.1f,
            1000f);

        var modelMatrix = Matrix4X4<float>.Identity;

        _renderer.Render(projectionMatrix, viewMatrix, modelMatrix);
    }

    private void RenderSimulationSliceScene(SimulationViewportRenderContext context)
    {
        if (_sliceRenderer == null || _sliceDrawData == null)
            return;

        (float left, float right, float bottom, float top) = GetSliceBounds(_sliceDrawData, context.AspectRatio);

        var projectionMatrix = Matrix4X4.CreateOrthographicOffCenter(
            left,
            right,
            bottom,
            top,
            -1f,
            1f);

        var viewMatrix = Matrix4X4<float>.Identity;
        var modelMatrix = Matrix4X4<float>.Identity;

        _gl?.Disable(EnableCap.DepthTest);
        _gl?.Disable(EnableCap.CullFace);
        _sliceRenderer.Render(projectionMatrix, viewMatrix, modelMatrix);
        _gl?.Enable(EnableCap.DepthTest);
    }

    private void UpdateSlicePicking()
    {
        _hoveredSliceCell = null;

        if (_sliceViewport is not { IsHovered: true } sliceViewport || _sliceDrawData == null)
            return;

        float viewportAspectRatio = sliceViewport.Width / (float)Math.Max(sliceViewport.Height, 1);
        _hoveredSliceCell = PickSliceCell(
            _sliceDrawData,
            sliceViewport.NormalizedMousePosition,
            viewportAspectRatio);

        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            _selectedCell = _hoveredSliceCell;
    }

    private static CellSelection? PickSliceCell(
        SimulationSliceDrawData sliceDrawData,
        Vector2 normalizedMousePosition,
        float viewportAspectRatio)
    {
        (float left, float right, float bottom, float top) = GetSliceBounds(sliceDrawData, viewportAspectRatio);

        float sliceU = left + normalizedMousePosition.X * (right - left);
        float sliceV = bottom + normalizedMousePosition.Y * (top - bottom);

        foreach (var chunk in sliceDrawData.Chunks.Values)
        {
            foreach (var cell in chunk.Cells)
            {
                if (sliceU < cell.U || sliceU >= cell.U + VoxelDrawSize)
                    continue;

                if (sliceV < cell.V || sliceV >= cell.V + VoxelDrawSize)
                    continue;

                return CellSelection.FromSliceCell(chunk.ChunkPosition, cell);
            }
        }

        return null;
    }

    private static (float left, float right, float bottom, float top) GetSliceBounds(
        SimulationSliceDrawData sliceDrawData,
        float viewportAspectRatio)
    {
        var cells = sliceDrawData.Chunks.Values.SelectMany(chunk => chunk.Cells).ToList();
        if (cells.Count == 0)
            return (-1f, 17f, -1f, 17f);

        float left = cells.Min(cell => cell.U);
        float right = cells.Max(cell => cell.U) + VoxelDrawSize;
        float bottom = cells.Min(cell => cell.V);
        float top = cells.Max(cell => cell.V) + VoxelDrawSize;

        const float margin = 0.5f;
        left -= margin;
        right += margin;
        bottom -= margin;
        top += margin;

        float width = Math.Max(right - left, VoxelDrawSize);
        float height = Math.Max(top - bottom, VoxelDrawSize);
        viewportAspectRatio = Math.Max(viewportAspectRatio, 0.01f);

        float boundsAspectRatio = width / height;
        if (boundsAspectRatio < viewportAspectRatio)
        {
            float desiredWidth = height * viewportAspectRatio;
            float extra = (desiredWidth - width) * 0.5f;
            left -= extra;
            right += extra;
        }
        else if (boundsAspectRatio > viewportAspectRatio)
        {
            float desiredHeight = width / viewportAspectRatio;
            float extra = (desiredHeight - height) * 0.5f;
            bottom -= extra;
            top += extra;
        }

        return (left, right, bottom, top);
    }

    private const float VoxelDrawSize = 1.0f;
}