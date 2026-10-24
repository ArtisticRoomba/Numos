using System.Numerics;
using Numos.SimDrawer;
using Raylib_cs;

namespace Numos.Viewer.Rendering;

/// <summary>
///     Wireframe outline drawn around one voxel on top of the visualization colours.
/// </summary>
/// <param name="Address">Voxel to outline. Ignored when its chunk generation no longer matches the frame.</param>
/// <param name="Color">Outline colour.</param>
public readonly record struct VoxelHighlight(VoxelAddress Address, ColorRgba Color);

/// <summary>
///     Raylib renderer for immutable simulation presentation frames.
/// </summary>
public static class SimulationRenderer
{
    /// <summary>
    ///     Most voxels that get an outline when no chunk is focused. A wireframe cube is twelve immediate-mode lines,
    ///     so a large scene would spend its whole frame on them.
    /// </summary>
    public const int MaxOutlinedVoxels = 20_000;

    private readonly static Vector3 VoxelSize = Vector3.One;
    private readonly static Vector3 HighlightSize = new(1.04f);

    /// <summary>
    ///     Draws the visible voxels of <paramref name="frame" /> from cached meshes, then the requested overlays.
    ///     Must be called inside a raylib 3D mode.
    /// </summary>
    /// <param name="frame">
    ///     Frame to draw. Chunks whose colours were built for a different visualization mapping are skipped.
    /// </param>
    /// <param name="focusedChunk">When set, only this chunk and its highlights are drawn.</param>
    /// <param name="highlights">
    ///     Voxel outlines to draw; entries that do not resolve to a drawn chunk are ignored.
    /// </param>
    /// <param name="meshes">Mesh cache of the simulation that owns <paramref name="frame" />.</param>
    /// <param name="options">Outline and transparency treatments.</param>
    internal static void Draw(
        SimulationDrawData frame,
        ChunkIdentity? focusedChunk,
        IReadOnlyList<VoxelHighlight> highlights,
        ChunkMeshCache meshes,
        Render3DStyleOptions options = default)
    {
        ArgumentNullException.ThrowIfNull(meshes);

        // Meshes go first. Their draw call is not batched, and the overlays below are, so this order keeps depth
        // testing and blending consistent.
        meshes.Draw(frame, focusedChunk, options.TransparentVoxels);

        bool voxelOutlines = options.ShowVoxelOutlines && ShouldOutlineVoxels(frame, focusedChunk);
        foreach (var chunk in frame.Chunks.Values)
        {
            if (!IsDrawn(frame, chunk, focusedChunk))
                continue;

            if (voxelOutlines)
                DrawVoxelOutlines(chunk);

            if (options.ShowChunkOutlines)
                DrawChunkOutline(chunk);
        }

        DrawHighlights(frame, focusedChunk, highlights);
    }

    /// <summary>
    ///     Returns whether voxel outlines are affordable: always for a focused chunk, otherwise only for a scene of at
    ///     most <see cref="MaxOutlinedVoxels" /> drawn voxels.
    /// </summary>
    /// <param name="frame">Frame to draw.</param>
    /// <param name="focusedChunk">The focused chunk, or <see langword="null" />.</param>
    internal static bool ShouldOutlineVoxels(SimulationDrawData frame, ChunkIdentity? focusedChunk)
    {
        if (focusedChunk.HasValue)
            return true;

        long voxels = 0;
        foreach (var chunk in frame.Chunks.Values)
        {
            if (!IsDrawn(frame, chunk, focusedChunk))
                continue;

            voxels += chunk.VisibleCellCount;
            if (voxels > MaxOutlinedVoxels)
                return false;
        }

        return true;
    }

    private static bool IsDrawn(SimulationDrawData frame, ChunkDrawData chunk, ChunkIdentity? focusedChunk)
    {
        return frame.HasCurrentVisualizationMapping(chunk) &&
               (!focusedChunk.HasValue || chunk.Identity == focusedChunk.Value);
    }

    private static void DrawVoxelOutlines(ChunkDrawData chunk)
    {
        var color = new Color(0f, 0f, 0f, 0.55f);
        ReadOnlySpan<VoxelDrawData> cells = chunk.Cells;
        for (int localIndex = 0; localIndex < cells.Length; localIndex++)
        {
            ref readonly var cell = ref cells[localIndex];
            if (!cell.IsVisible || cell.VisibleFaces == VoxelFaceMask.None)
                continue;

            Raylib.DrawCubeWiresV(GetCellCenter(chunk, localIndex), VoxelSize, color);
        }
    }

    private static Vector3 GetCellCenter(ChunkDrawData chunk, int localIndex)
    {
        int x = localIndex % chunk.Dimensions.X;
        int yz = localIndex / chunk.Dimensions.X;
        int y = yz % chunk.Dimensions.Y;
        int z = yz / chunk.Dimensions.Y;
        return new Vector3(
            chunk.ChunkPosition.X * chunk.Dimensions.X + x + 0.5f,
            chunk.ChunkPosition.Y * chunk.Dimensions.Y + y + 0.5f,
            chunk.ChunkPosition.Z * chunk.Dimensions.Z + z + 0.5f);
    }

    private static void DrawChunkOutline(ChunkDrawData chunk)
    {
        var dimensions = chunk.Dimensions;
        var center = new Vector3(
            chunk.ChunkPosition.X * dimensions.X + dimensions.X * 0.5f,
            chunk.ChunkPosition.Y * dimensions.Y + dimensions.Y * 0.5f,
            chunk.ChunkPosition.Z * dimensions.Z + dimensions.Z * 0.5f);

        var size = new Vector3(dimensions.X, dimensions.Y, dimensions.Z);

        Raylib.DrawCubeWiresV(center, size, new Color(1f, 1f, 1f, 0.7f));
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