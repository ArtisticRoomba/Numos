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
    private readonly TextureFilter _textureFilter;
    private readonly Color _clearColor;
    private RenderTexture2D _renderTexture;
    private bool _disposed;

    public SimulationViewport(TextureFilter textureFilter, Color clearColor)
    {
        _textureFilter = textureFilter;
        _clearColor = clearColor;
        _renderTexture = CreateRenderTexture(Width, Height);
    }

    public int Width { get; private set; } = 1;

    public int Height { get; private set; } = 1;

    public bool IsHovered { get; private set; }

    public Vector2 NormalizedMousePosition { get; private set; }

    public void Draw(string title, Action renderScene, Vector2 firstUsePosition, Vector2 firstUseSize)
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
}