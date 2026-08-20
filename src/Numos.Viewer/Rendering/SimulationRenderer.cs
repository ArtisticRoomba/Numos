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
        IReadOnlyList<VoxelHighlight> highlights,
        Camera3D camera,
        Render3DStyleOptions options = default)
    {
        GetCameraPlane(camera, out Vector3 cameraRight, out Vector3 cameraUp);
        foreach (var chunk in frame.Chunks.Values)
        {
            if (!frame.HasCurrentVisualizationMapping(chunk) ||
                focusedChunk.HasValue && chunk.Identity != focusedChunk.Value)
            {
                continue;
            }

            DrawChunk(chunk, options, cameraRight, cameraUp);

            if (options.ShowChunkOutlines)
                DrawChunkOutline(chunk);
        }

        DrawHighlights(frame, focusedChunk, highlights);
    }

    private static void DrawChunk(
        ChunkDrawData chunk,
        Render3DStyleOptions options,
        Vector3 cameraRight,
        Vector3 cameraUp)
    {
        var cells = chunk.Cells;
        for (var localIndex = 0; localIndex < cells.Length; localIndex++)
        {
            ref readonly var cell = ref cells[localIndex];
            if ((!cell.IsVisible || cell.VisibleFaces == VoxelFaceMask.None) &&
                cell.StateMarker == VoxelStateMarker.None)
            {
                continue;
            }

            int x = localIndex % chunk.Dimensions.X;
            int yz = localIndex / chunk.Dimensions.X;
            int y = yz % chunk.Dimensions.Y;
            int z = yz / chunk.Dimensions.Y;
            var center = new Vector3(
                chunk.ChunkPosition.X * chunk.Dimensions.X + x + 0.5f,
                chunk.ChunkPosition.Y * chunk.Dimensions.Y + y + 0.5f,
                chunk.ChunkPosition.Z * chunk.Dimensions.Z + z + 0.5f);

            if (!cell.IsVisible || cell.VisibleFaces == VoxelFaceMask.None)
            {
                DrawFloatingStateMarker(
                    center,
                    cell.StateMarker,
                    ToRaylibColor(cell.StateMarkerColor),
                    cameraRight,
                    cameraUp);
                continue;
            }

            var color = ToRaylibColor(cell.Color);
            if (options.TransparentVoxels)
                color.A = 89;

            Raylib.DrawCubeV(center, VoxelSize, color);

            if (options.ShowVoxelOutlines)
                Raylib.DrawCubeWiresV(center, VoxelSize, new Color(0f, 0f, 0f, 0.55f));

            DrawStateMarker(
                center,
                cell.VisibleFaces,
                cell.StateMarker,
                ToRaylibColor(cell.StateMarkerColor));
        }
    }

    private static void DrawFloatingStateMarker(
        Vector3 center,
        VoxelStateMarker marker,
        Color color,
        Vector3 cameraRight,
        Vector3 cameraUp)
    {
        if (marker == VoxelStateMarker.None)
            return;

        const float radius = 0.27f;
        Raylib.DrawLine3D(
            center - cameraRight * radius - cameraUp * radius,
            center + cameraRight * radius + cameraUp * radius,
            color);
        if (marker == VoxelStateMarker.Sleeping)
        {
            Raylib.DrawLine3D(
                center - cameraRight * radius + cameraUp * radius,
                center + cameraRight * radius - cameraUp * radius,
                color);
        }
    }

    private static void GetCameraPlane(Camera3D camera, out Vector3 right, out Vector3 up)
    {
        Vector3 forward = camera.Target - camera.Position;
        forward = forward.LengthSquared() > 0.000001f
            ? Vector3.Normalize(forward)
            : -Vector3.UnitZ;
        right = Vector3.Cross(forward, camera.Up);
        if (right.LengthSquared() <= 0.000001f)
            right = Vector3.Cross(forward, Vector3.UnitX);
        if (right.LengthSquared() <= 0.000001f)
            right = Vector3.UnitX;
        else
            right = Vector3.Normalize(right);
        up = Vector3.Normalize(Vector3.Cross(right, forward));
    }

    private static void DrawStateMarker(
        Vector3 center,
        VoxelFaceMask visibleFaces,
        VoxelStateMarker marker,
        Color color)
    {
        if (marker == VoxelStateMarker.None)
            return;

        DrawMarkerOnFace(center, visibleFaces, VoxelFaceMask.NegativeX, Vector3.UnitY, Vector3.UnitZ, color, marker);
        DrawMarkerOnFace(center, visibleFaces, VoxelFaceMask.PositiveX, Vector3.UnitY, Vector3.UnitZ, color, marker);
        DrawMarkerOnFace(center, visibleFaces, VoxelFaceMask.NegativeY, Vector3.UnitX, Vector3.UnitZ, color, marker);
        DrawMarkerOnFace(center, visibleFaces, VoxelFaceMask.PositiveY, Vector3.UnitX, Vector3.UnitZ, color, marker);
        DrawMarkerOnFace(center, visibleFaces, VoxelFaceMask.NegativeZ, Vector3.UnitX, Vector3.UnitY, color, marker);
        DrawMarkerOnFace(center, visibleFaces, VoxelFaceMask.PositiveZ, Vector3.UnitX, Vector3.UnitY, color, marker);
    }

    private static void DrawMarkerOnFace(
        Vector3 center,
        VoxelFaceMask visibleFaces,
        VoxelFaceMask face,
        Vector3 horizontal,
        Vector3 vertical,
        Color color,
        VoxelStateMarker marker)
    {
        if ((visibleFaces & face) == 0)
            return;

        Vector3 normal = face switch
        {
            VoxelFaceMask.NegativeX => -Vector3.UnitX,
            VoxelFaceMask.PositiveX => Vector3.UnitX,
            VoxelFaceMask.NegativeY => -Vector3.UnitY,
            VoxelFaceMask.PositiveY => Vector3.UnitY,
            VoxelFaceMask.NegativeZ => -Vector3.UnitZ,
            VoxelFaceMask.PositiveZ => Vector3.UnitZ,
            _ => Vector3.Zero
        };
        Vector3 faceCenter = center + normal * 0.501f;
        const float radius = 0.27f;
        Raylib.DrawLine3D(
            faceCenter - horizontal * radius - vertical * radius,
            faceCenter + horizontal * radius + vertical * radius,
            color);

        if (marker == VoxelStateMarker.Sleeping)
        {
            Raylib.DrawLine3D(
                faceCenter - horizontal * radius + vertical * radius,
                faceCenter + horizontal * radius - vertical * radius,
                color);
        }
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
