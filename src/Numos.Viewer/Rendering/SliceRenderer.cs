using System.Numerics;
using Numos.SimDrawer;
using Raylib_cs;

namespace Numos.Viewer.Rendering;

/// <summary>
///     Defines voxel overlays for a rendered slice.
/// </summary>
/// <param name="Highlights">Voxel highlights to draw when their addresses occur in this slice.</param>
public readonly record struct SliceRenderOptions(
    IReadOnlyList<VoxelHighlight>? Highlights = null);

/// <summary>
///     Draws a simulation slice with raylib's two-dimensional camera. The cells come from one texture.
/// </summary>
/// <remarks>
///     <para>
///         A plane can hold millions of cells, so the renderer never draws one rectangle per cell. It uploads
///         <see cref="SimulationSliceDrawData.Pixels" /> into one point-filtered texture when
///         <see cref="SimulationSliceDrawData.RenderVersion" /> changes, and it draws that texture as a single quad.
///         Outlines and highlights cover only the part of the plane in view.
///     </para>
///     <para>
///         The renderer owns a GPU texture. It must be used on the GL thread and disposed before the window closes.
///         The texture is limited to <see cref="MaxTextureSize" /> cells per side.
///     </para>
/// </remarks>
public sealed class SliceRenderer : IDisposable
{
    /// <summary>
    ///     Largest plane width or height the renderer uploads. Common GPUs accept at least this size.
    /// </summary>
    public const int MaxTextureSize = 16384;

    /// <summary>
    ///     Cell size in screen pixels from which cell outlines appear. Smaller cells would turn into a dark mesh.
    /// </summary>
    public const float MinOutlinePixels = 8f;

    /// <summary>
    ///     Most cells that get an outline in one frame.
    /// </summary>
    public const int MaxOutlinedCells = 40_000;

    private const byte TransparentAlpha = 89;
    private const float HighlightThickness = 0.06f;
    private readonly static Color CellBorder = new(0f, 0f, 0f, 0.5f);

    private bool _disposed;
    private bool _hasTexture;
    private Texture2D _texture;
    private ulong _uploadedVersion;

    /// <summary>
    ///     Gets the number of texture uploads so far. Tests and the performance overlay use it to see whether a frame
    ///     reused the texture.
    /// </summary>
    public int UploadCount { get; private set; }

    /// <summary>
    ///     Draws <paramref name="slice" /> in one-unit cells under <paramref name="camera" />, with V flipped so the
    ///     slice reads with its origin at the bottom left.
    /// </summary>
    /// <param name="slice">Slice to draw.</param>
    /// <param name="camera">2D camera for the slice. This method begins and ends its 2D mode.</param>
    /// <param name="viewportSize">Size in pixels of the render target, which decides the part in view.</param>
    /// <param name="options">Voxel highlights to outline.</param>
    /// <param name="style">Outline and transparency treatments.</param>
    /// <exception cref="ArgumentNullException"><paramref name="slice" /> is <see langword="null" />.</exception>
    public void Draw(
        SimulationSliceDrawData slice,
        Camera2D camera,
        Vector2 viewportSize,
        SliceRenderOptions options = default,
        Render2DStyleOptions style = default)
    {
        ArgumentNullException.ThrowIfNull(slice);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (slice.Width <= 0 || slice.Height <= 0)
            return;

        bool textureReady = EnsureTexture(slice);
        var view = GetVisibleCells(slice, camera, viewportSize);

        Raylib.BeginMode2D(camera);
        try
        {
            if (textureReady)
                DrawTexture(slice, style);

            if (style.ShowChunkOutlines)
                DrawChunkOutlines(slice, camera.Zoom, view);

            if (style.ShowVoxelOutlines)
                DrawVoxelOutlines(slice, camera.Zoom, view);

            if (options.Highlights != null)
            {
                foreach (var highlight in options.Highlights)
                {
                    if (!slice.TryLocate(highlight.Address, out int u, out int v))
                        continue;

                    float thickness = Math.Max(HighlightThickness, 2f / camera.Zoom);
                    Raylib.DrawRectangleLinesEx(
                        GetCellRectangle(slice, u, v),
                        thickness,
                        SimulationRenderer.ToRaylibColor(highlight.Color));
                }
            }
        }
        finally
        {
            Raylib.EndMode2D();
        }
    }

    /// <summary>
    ///     Frees the texture. The renderer cannot draw afterwards.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        ReleaseTexture();
    }

    /// <summary>
    ///     Frees the texture and keeps the renderer usable. The next draw uploads again.
    /// </summary>
    public void ReleaseTexture()
    {
        if (_hasTexture)
        {
            Raylib.UnloadTexture(_texture);
            _texture = default;
            _hasTexture = false;
        }

        _uploadedVersion = 0;
    }

    /// <summary>
    ///     Gets the cells in view, clipped to the plane.
    /// </summary>
    /// <param name="slice">Slice that is drawn.</param>
    /// <param name="camera">Camera that draws it.</param>
    /// <param name="viewportSize">Size in pixels of the render target.</param>
    /// <returns>The first cell and the number of cells on each axis. The range can be empty.</returns>
    internal static (int U, int V, int Columns, int Rows) GetVisibleCells(
        SimulationSliceDrawData slice,
        Camera2D camera,
        Vector2 viewportSize)
    {
        var topLeft = Raylib.GetScreenToWorld2D(Vector2.Zero, camera);
        var bottomRight = Raylib.GetScreenToWorld2D(viewportSize, camera);
        int firstU = Math.Clamp((int)MathF.Floor(topLeft.X), 0, slice.Width);
        int lastU = Math.Clamp((int)MathF.Ceiling(bottomRight.X), 0, slice.Width);

        // World Y is flipped against V: world Y = Height - V.
        int firstV = Math.Clamp((int)MathF.Floor(slice.Height - bottomRight.Y), 0, slice.Height);
        int lastV = Math.Clamp((int)MathF.Ceiling(slice.Height - topLeft.Y), 0, slice.Height);
        return (firstU, firstV, Math.Max(lastU - firstU, 0), Math.Max(lastV - firstV, 0));
    }

    private bool EnsureTexture(SimulationSliceDrawData slice)
    {
        if (slice.Width > MaxTextureSize || slice.Height > MaxTextureSize)
            return false;

        if (_hasTexture && (_texture.Width != slice.Width || _texture.Height != slice.Height))
            ReleaseTexture();

        if (!_hasTexture)
        {
            var image = Raylib.GenImageColor(slice.Width, slice.Height, Color.Blank);
            _texture = Raylib.LoadTextureFromImage(image);
            Raylib.UnloadImage(image);
            if (!Raylib.IsTextureValid(_texture))
            {
                _texture = default;
                return false;
            }

            Raylib.SetTextureFilter(_texture, TextureFilter.Point);
            Raylib.SetTextureWrap(_texture, TextureWrap.Clamp);
            _hasTexture = true;
            _uploadedVersion = 0;
        }

        if (_uploadedVersion != slice.RenderVersion)
        {
            Raylib.UpdateTexture(_texture, slice.Pixels);
            _uploadedVersion = slice.RenderVersion;
            UploadCount++;
        }

        return true;
    }

    private void DrawTexture(SimulationSliceDrawData slice, Render2DStyleOptions style)
    {
        // A negative source height flips the image, so row V = 0 lands at the bottom of the screen.
        var tint = style.TransparentVoxels ? new Color(255, 255, 255, (int)TransparentAlpha) : Color.White;
        Raylib.DrawTexturePro(
            _texture,
            new Rectangle(0f, 0f, slice.Width, -slice.Height),
            new Rectangle(0f, 0f, slice.Width, slice.Height),
            Vector2.Zero,
            0f,
            tint);
    }

    private static void DrawChunkOutlines(
        SimulationSliceDrawData slice,
        float zoom,
        (int U, int V, int Columns, int Rows) view)
    {
        float thickness = Math.Max(0.08f, 1.5f / zoom);
        foreach (var rect in slice.ChunkRects)
        {
            if (rect.U >= view.U + view.Columns ||
                rect.U + rect.Width <= view.U ||
                rect.V >= view.V + view.Rows ||
                rect.V + rect.Height <= view.V)
            {
                continue;
            }

            Raylib.DrawRectangleLinesEx(
                new Rectangle(rect.U, slice.Height - rect.V - rect.Height, rect.Width, rect.Height),
                thickness,
                Color.White);
        }
    }

    /// <summary>
    ///     Outlines each visible cell in view. It skips the work when cells are small or the view holds too many.
    /// </summary>
    private static void DrawVoxelOutlines(
        SimulationSliceDrawData slice,
        float zoom,
        (int U, int V, int Columns, int Rows) view)
    {
        if (zoom < MinOutlinePixels || (long)view.Columns * view.Rows > MaxOutlinedCells)
            return;

        float thickness = Math.Max(0.025f, 1f / zoom);
        ReadOnlySpan<byte> pixels = slice.Pixels;
        for (int v = view.V; v < view.V + view.Rows; v++)
        {
            for (int u = view.U; u < view.U + view.Columns; u++)
            {
                if (pixels[4 * (u + slice.Width * v) + 3] != 0)
                    Raylib.DrawRectangleLinesEx(GetCellRectangle(slice, u, v), thickness, CellBorder);
            }
        }
    }

    private static Rectangle GetCellRectangle(SimulationSliceDrawData slice, int u, int v)
    {
        // Raylib's 2D screen coordinates grow downward. Flip V so the slice keeps a
        // bottom-left origin when ImGui shows its render texture.
        return new Rectangle(u, slice.Height - v - 1, 1f, 1f);
    }
}
