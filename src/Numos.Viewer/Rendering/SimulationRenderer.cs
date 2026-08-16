using System.Numerics;
using Numos.SimDrawer;
using Raylib_cs;

namespace Numos.Viewer.Rendering;

public readonly record struct VoxelHighlight(VoxelAddress Address, ColorRgba Color);

/// <summary>
///     Raylib renderer for immutable simulation presentation frames.
/// </summary>
public static class SimulationRenderer
{
    private readonly static Vector3 VoxelSize = Vector3.One;
    private readonly static Vector3 HighlightSize = new(1.04f);

    public static void Draw(
        SimulationDrawData frame,
        ChunkIdentity? focusedChunk,
        IReadOnlyList<VoxelHighlight> highlights)
    {
        foreach (var chunk in frame.Chunks.Values)
        {
            if (!frame.HasCurrentVisualizationMapping(chunk) ||
                focusedChunk.HasValue && chunk.Identity != focusedChunk.Value)
            {
                continue;
            }

            DrawChunk(chunk);
        }

        DrawHighlights(frame, focusedChunk, highlights);
    }

    private static void DrawChunk(ChunkDrawData chunk)
    {
        var cells = chunk.Cells;
        for (var localIndex = 0; localIndex < cells.Length; localIndex++)
        {
            ref readonly var cell = ref cells[localIndex];
            if (!cell.IsVisible || cell.VisibleFaces == VoxelFaceMask.None)
                continue;

            int x = localIndex % chunk.Dimensions.X;
            int yz = localIndex / chunk.Dimensions.X;
            int y = yz % chunk.Dimensions.Y;
            int z = yz / chunk.Dimensions.Y;
            var center = new Vector3(
                chunk.ChunkPosition.X * chunk.Dimensions.X + x + 0.5f,
                chunk.ChunkPosition.Y * chunk.Dimensions.Y + y + 0.5f,
                chunk.ChunkPosition.Z * chunk.Dimensions.Z + z + 0.5f);

            Raylib.DrawCubeV(center, VoxelSize, ToRaylibColor(cell.Color));
        }
    }

    private static void DrawHighlights(
        SimulationDrawData frame,
        ChunkIdentity? focusedChunk,
        IReadOnlyList<VoxelHighlight> highlights)
    {
        foreach (var highlight in highlights)
        {
            if (!TryResolveVisibleHighlight(frame, highlight, focusedChunk, out var chunk))
                continue;

            var position = chunk.GetWorldCoordinates(highlight.Address.LocalIndex);
            var center = new Vector3(position.X + 0.5f, position.Y + 0.5f, position.Z + 0.5f);
            Raylib.DrawCubeWiresV(center, HighlightSize, ToRaylibColor(highlight.Color));
        }
    }

    private static bool TryResolveVisibleHighlight(
        SimulationDrawData frame,
        VoxelHighlight highlight,
        ChunkIdentity? focusedChunk,
        out ChunkDrawData chunk)
    {
        if (frame.Chunks.TryGetValue(highlight.Address.Chunk.Position, out var resolved) &&
            resolved.Identity == highlight.Address.Chunk &&
            frame.HasCurrentVisualizationMapping(resolved) &&
            (!focusedChunk.HasValue || resolved.Identity == focusedChunk.Value) &&
            highlight.Address.LocalIndex < resolved.CellCount)
        {
            chunk = resolved;
            return true;
        }

        chunk = null!;
        return false;
    }

    internal static Color ToRaylibColor(ColorRgba color)
    {
        return new Color(
            Math.Clamp(color.R, 0f, 1f),
            Math.Clamp(color.G, 0f, 1f),
            Math.Clamp(color.B, 0f, 1f),
            Math.Clamp(color.A, 0f, 1f));
    }
}