using System.Numerics;
using ImGuiNET;
using Raylib_cs;
using rlImGui_cs;

namespace Numos.Viewer.Rendering.Viewport;

/// <summary>
///     ImGui viewport backed by a raylib render texture.
/// </summary>
public sealed class SimulationViewport : IDisposable
{
    private readonly Color _clearColor;
    private readonly TextureFilter _textureFilter;
    private bool _disposed;
    private RenderTexture2D _renderTexture;

    /// <summary>
    ///     Creates the viewport with a 1x1 render texture; <see cref="Draw" /> resizes it to the window on first use.
    /// </summary>
    /// <param name="textureFilter">Filter applied when ImGui scales the texture onto the window.</param>
    /// <param name="clearColor">Background cleared before each scene render.</param>
    /// <exception cref="InvalidOperationException">raylib could not create the render texture.</exception>
    /// <remarks>
    ///     Requires an initialized raylib window, since the render texture is a GPU resource.
    /// </remarks>
    public SimulationViewport(TextureFilter textureFilter, Color clearColor)
    {
        _textureFilter = textureFilter;
        _clearColor = clearColor;
        _renderTexture = CreateRenderTexture(Width, Height);
    }

    /// <summary>
    ///     Render texture width in pixels, matching the window's content region as of the last <see cref="Draw" />.
    /// </summary>
    public int Width { get; private set; } = 1;

    /// <summary>
    ///     Render texture height in pixels, matching the window's content region as of the last <see cref="Draw" />.
    /// </summary>
    public int Height { get; private set; } = 1;

    /// <summary>
    ///     Whether the mouse was over the viewport image during the last <see cref="Draw" />. Cleared when the window
    ///     is collapsed or hidden.
    /// </summary>
    public bool IsHovered { get; private set; }

    internal Vector2 ImageMaximum { get; private set; }

    internal Vector2 ImageMinimum { get; private set; }

    /// <summary>
    ///     Mouse position over the image in [0, 1] on both axes, clamped to the image, with Y growing upward to match
    ///     the rendered scene. Updated only while the window is open.
    /// </summary>
    public Vector2 NormalizedMousePosition { get; private set; }

    /// <summary>
    ///     Unloads the render texture. Call it before the raylib window closes.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (_renderTexture.Id != 0)
        {
            Raylib.UnloadRenderTexture(_renderTexture);
            _renderTexture = default;
        }
    }

    /// <summary>
    ///     Draws the render texture in an ImGui window and handles interaction after the image item is available.
    /// </summary>
    /// <param name="title">ImGui window title and stable identifier.</param>
    /// <param name="renderScene">Scene callback rendered into the viewport texture.</param>
    /// <param name="firstUsePosition">Initial window position when no saved layout exists.</param>
    /// <param name="firstUseSize">Initial window size when no saved layout exists.</param>
    /// <param name="handleInteraction">Optional callback invoked while the image remains the current ImGui item.</param>
    public void Draw(
        string title,
        Action renderScene,
        Vector2 firstUsePosition,
        Vector2 firstUseSize,
        Action? handleInteraction = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(renderScene);

        ImGui.SetNextWindowPos(firstUsePosition, ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(firstUseSize, ImGuiCond.FirstUseEver);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        const ImGuiWindowFlags windowFlags =
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse;

        bool opened = ImGui.Begin(title, windowFlags);
        ImGui.PopStyleVar();

        if (!opened)
        {
            IsHovered = false;
            ImGui.End();
            return;
        }

        var available = ImGui.GetContentRegionAvail();
        int targetWidth = Math.Max((int)available.X, 1);
        int targetHeight = Math.Max((int)available.Y, 1);
        ResizeIfNeeded(targetWidth, targetHeight);

        RenderToTexture(renderScene);
        rlImGui.ImageRenderTexture(_renderTexture);
        UpdateMouseState();
        handleInteraction?.Invoke();

        ImGui.End();
    }

    private void RenderToTexture(Action renderScene)
    {
        Raylib.BeginTextureMode(_renderTexture);
        try
        {
            Raylib.ClearBackground(_clearColor);
            renderScene();
        }
        finally
        {
            Raylib.EndTextureMode();
        }
    }

    private void UpdateMouseState()
    {
        IsHovered = ImGui.IsItemHovered();
        var imageMin = ImGui.GetItemRectMin();
        var imageMax = ImGui.GetItemRectMax();
        ImageMinimum = imageMin;
        ImageMaximum = imageMax;
        var local = ImGui.GetMousePos() - imageMin;
        float imageWidth = Math.Max(imageMax.X - imageMin.X, 1f);
        float imageHeight = Math.Max(imageMax.Y - imageMin.Y, 1f);

        local.X = Math.Clamp(local.X, 0f, imageWidth);
        local.Y = Math.Clamp(local.Y, 0f, imageHeight);
        NormalizedMousePosition = new Vector2(
            local.X / imageWidth,
            1f - local.Y / imageHeight);
    }

    private void ResizeIfNeeded(int width, int height)
    {
        if (width == Width && height == Height)
            return;

        var replacement = CreateRenderTexture(width, height);
        Raylib.UnloadRenderTexture(_renderTexture);
        _renderTexture = replacement;
        Width = width;
        Height = height;
    }

    private RenderTexture2D CreateRenderTexture(int width, int height)
    {
        var texture = Raylib.LoadRenderTexture(width, height);
        if (!Raylib.IsRenderTextureValid(texture))
            throw new InvalidOperationException($"Could not create {width}x{height} simulation render texture.");

        Raylib.SetTextureFilter(texture.Texture, _textureFilter);
        return texture;
    }
}