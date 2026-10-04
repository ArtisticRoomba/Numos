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
///     Draws a simulation slice with raylib's two-dimensional camera and shape API.
/// </summary>
public static class SliceRenderer
{
    private readonly static Color CellBorder = new(0f, 0f, 0f, 0.5f);

    /// <summary>
    ///     Draws <paramref name="slice" /> in one-unit cells under <paramref name="camera" />, with V flipped so the
    ///     slice reads with its origin at the bottom left.
    /// </summary>
    /// <param name="slice">Slice to draw.</param>
    /// <param name="camera">2D camera for the slice; this method begins and ends its 2D mode.</param>
    /// <param name="options">Voxel highlights to outline.</param>
    /// <param name="style">Outline and transparency treatments.</param>
    /// <exception cref="ArgumentNullException"><paramref name="slice" /> is <see langword="null" />.</exception>
    public static void Draw(
        SimulationSliceDrawData slice,
        Camera2D camera,
        SliceRenderOptions options = default,
        Render2DStyleOptions style = default)
    {
        ArgumentNullException.ThrowIfNull(slice);

        Raylib.BeginMode2D(camera);
        try
        {
            foreach (var cell in slice.Cells)
            {
                var rectangle = GetCellRectangle(slice, cell.U, cell.V);
                var color = SimulationRenderer.ToRaylibColor(cell.Voxel.Color);
                if (style.TransparentVoxels)
                    color.A = 89;

                Raylib.DrawRectangleRec(rectangle, color);
                if (style.ShowVoxelOutlines)
                    Raylib.DrawRectangleLinesEx(rectangle, 0.025f, CellBorder);
            }

            if (style.ShowChunkOutlines)
                Raylib.DrawRectangleLinesEx(new Rectangle(0f, 0f, slice.Width, slice.Height), 0.08f, Color.White);

            if (options.Highlights != null)
            {
                for (int v = 0; v < slice.Height; v++)
                {
                    for (int u = 0; u < slice.Width; u++)
                    {
                        if (!slice.TryGetCell(u, v, out var cell))
                            continue;

                        foreach (var highlight in options.Highlights)
                        {
                            if (highlight.Address == cell.Address)
                            {
                                DrawHighlight(
                                    slice,
                                    cell.U,
                                    cell.V,
                                    0.06f,
                                    SimulationRenderer.ToRaylibColor(highlight.Color));
                            }
                        }
                    }
                }
            }
        }
        finally
        {
            Raylib.EndMode2D();
        }
    }

    private static void DrawHighlight(
        SimulationSliceDrawData slice,
        int u,
        int v,
        float thickness,
        Color color)
    {
        if (u < 0 || u >= slice.Width || v < 0 || v >= slice.Height)
            return;

        Raylib.DrawRectangleLinesEx(GetCellRectangle(slice, u, v), thickness, color);
    }

    private static Rectangle GetCellRectangle(SimulationSliceDrawData slice, int u, int v)
    {
        // Raylib's 2D screen coordinates grow downward. Flip V so the slice remains a
        // conventional bottom-left-origin plot when its render texture is shown in ImGui.
        return new Rectangle(u, slice.Height - v - 1, 1f, 1f);
    }
}