using System.Numerics;
using ImGuiNET;
using Silk.NET.OpenGL;

namespace Numos.Viewer.Rendering.Viewport;

/// <summary>
///     Represents a viewport for rendering simdata.
/// </summary>
/// <para>
///     Internally this renders the simulation into an offscreen framebuffer and then
///     presents the framebuffer's color texture to ImGui.
/// </para>
public sealed unsafe class SimulationViewport : IDisposable
{
    private readonly GL _gl;

    private uint _framebuffer;
    private uint _colorTexture;
    private uint _depthStencilRenderbuffer;

    private bool _disposed;

    public SimulationViewport(GL gl)
    {
        _gl = gl ?? throw new ArgumentNullException(nameof(gl));
        CreateFramebuffer(Width, Height);
    }

    public int Width { get; private set; } = 1;

    public int Height { get; private set; } = 1;

    public Vector2 Size => new(Width, Height);

    public bool IsHovered { get; private set; }

    public bool IsFocused { get; private set; }

    public Vector2 MousePositionInViewport { get; private set; }

    public Vector2 NormalizedMousePosition { get; private set; }

    /// <summary>
    ///     Draws the viewport as an ImGui window.
    ///     The provided callback is invoked while this viewport's framebuffer is bound.
    /// </summary>
    public void Draw(
        string title,
        Action<SimulationViewportRenderContext> renderScene,
        SimulationViewportDrawOptions? options = null)
    {
        ThrowIfDisposed();

        if (renderScene == null)
            throw new ArgumentNullException(nameof(renderScene));

        options ??= SimulationViewportDrawOptions.Default;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);

        var windowFlags =
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse;

        if (options.NoTitleBar)
            windowFlags |= ImGuiWindowFlags.NoTitleBar;

        bool opened = ImGui.Begin(title, windowFlags);
        ImGui.PopStyleVar();

        if (!opened)
        {
            ImGui.End();
            return;
        }

        IsFocused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);

        var available = ImGui.GetContentRegionAvail();

        int targetWidth = Math.Max((int)available.X, 1);
        int targetHeight = Math.Max((int)available.Y, 1);

        ResizeIfNeeded(targetWidth, targetHeight);

        RenderToFramebuffer(renderScene);

        DrawFramebufferImage(available);
        UpdateMouseState();

        ImGui.End();
    }

    private void RenderToFramebuffer(Action<SimulationViewportRenderContext> renderScene)
    {
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffer);
        _gl.Viewport(0, 0, (uint)Width, (uint)Height);

        _gl.Enable(EnableCap.DepthTest);
        _gl.Enable(EnableCap.CullFace);

        _gl.ClearColor(0.04f, 0.04f, 0.05f, 1.0f);
        _gl.Clear(
            ClearBufferMask.ColorBufferBit |
            ClearBufferMask.DepthBufferBit |
            ClearBufferMask.StencilBufferBit);

        var context = new SimulationViewportRenderContext(
            Width,
            Height,
            Width / (float)Height);

        renderScene(context);

        _gl.BindVertexArray(0);
        _gl.UseProgram(0);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    private void DrawFramebufferImage(Vector2 size)
    {
        ImGui.Image(
            (IntPtr)_colorTexture,
            size,
            new Vector2(0, 1),
            new Vector2(1, 0));
    }

    private void UpdateMouseState()
    {
        IsHovered = ImGui.IsItemHovered();

        var imageMin = ImGui.GetItemRectMin();
        var imageMax = ImGui.GetItemRectMax();
        var mouse = ImGui.GetMousePos();

        var local = mouse - imageMin;

        float imageWidth = Math.Max(imageMax.X - imageMin.X, 1f);
        float imageHeight = Math.Max(imageMax.Y - imageMin.Y, 1f);

        local.X = Math.Clamp(local.X, 0, imageWidth);
        local.Y = Math.Clamp(local.Y, 0, imageHeight);

        MousePositionInViewport = local;

        float normalizedX = local.X / imageWidth;

        // ImGui sees the image top-left first, but the OpenGL viewport is bottom-left.
        float normalizedY = 1.0f - local.Y / imageHeight;

        NormalizedMousePosition = new Vector2(normalizedX, normalizedY);
    }

    private void ResizeIfNeeded(int width, int height)
    {
        if (width == Width && height == Height)
            return;

        DestroyFramebuffer();

        Width = width;
        Height = height;

        CreateFramebuffer(width, height);
    }

    private void CreateFramebuffer(int width, int height)
    {
        _framebuffer = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffer);

        _colorTexture = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _colorTexture);

        _gl.TexImage2D(
            TextureTarget.Texture2D,
            0,
            InternalFormat.Rgba8,
            (uint)width,
            (uint)height,
            0,
            PixelFormat.Rgba,
            PixelType.UnsignedByte,
            null);

        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);

        _gl.FramebufferTexture2D(
            FramebufferTarget.Framebuffer,
            FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D,
            _colorTexture,
            0);

        _depthStencilRenderbuffer = _gl.GenRenderbuffer();
        _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _depthStencilRenderbuffer);

        _gl.RenderbufferStorage(
            RenderbufferTarget.Renderbuffer,
            InternalFormat.Depth24Stencil8,
            (uint)width,
            (uint)height);

        _gl.FramebufferRenderbuffer(
            FramebufferTarget.Framebuffer,
            FramebufferAttachment.DepthStencilAttachment,
            RenderbufferTarget.Renderbuffer,
            _depthStencilRenderbuffer);

        var framebufferStatus = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);

        if (framebufferStatus != GLEnum.FramebufferComplete)
        {
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            throw new InvalidOperationException(
                $"Simulation viewport framebuffer is incomplete: {framebufferStatus}");
        }

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
        _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, 0);
    }

    private void DestroyFramebuffer()
    {
        if (_depthStencilRenderbuffer != 0)
        {
            _gl.DeleteRenderbuffer(_depthStencilRenderbuffer);
            _depthStencilRenderbuffer = 0;
        }

        if (_colorTexture != 0)
        {
            _gl.DeleteTexture(_colorTexture);
            _colorTexture = 0;
        }

        if (_framebuffer != 0)
        {
            _gl.DeleteFramebuffer(_framebuffer);
            _framebuffer = 0;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        DestroyFramebuffer();

        _disposed = true;
        GC.SuppressFinalize(this);
    }
}