using System.Numerics;
using Numos.SimDrawer;

namespace Numos.Viewer.Rendering;

/// <summary>
///     Triangle arrays for the exposed faces of one chunk, ready to upload to the GPU.
/// </summary>
/// <param name="Positions">Three floats per vertex, in world coordinates.</param>
/// <param name="Colors">Four bytes per vertex, as red, green, blue, and alpha.</param>
/// <param name="VertexCount">Number of vertices. Every three vertices form one triangle.</param>
public sealed record ChunkMeshData(float[] Positions, byte[] Colors, int VertexCount);

/// <summary>
///     Turns the presentation cells of one chunk into non-indexed triangles. The builder makes no raylib calls.
/// </summary>
/// <remarks>
///     <para>
///         Only faces that <see cref="VoxelDrawData.VisibleFaces" /> marks as exposed produce triangles. A closed
///         duct therefore costs its surface, not six faces per voxel. Faces on a chunk boundary stay, because the
///         frame builder keeps them exposed.
///     </para>
///     <para>
///         The mesh has no indices because a 16-bit index buffer holds 65,535 vertices. A chunk of 65,535 voxels
///         can need over one million vertices, and raylib draws a non-indexed mesh with a 32-bit count.
///     </para>
///     <para>
///         Triangles wind counter-clockwise as seen from outside, which matches raylib's cube. Vertex order is
///         fixed by cell order, then face order (-X, +X, -Y, +Y, -Z, +Z). <see cref="WriteColors" /> uses the same
///         order, so a color-only update lines up with an uploaded mesh.
///     </para>
/// </remarks>
public static class ChunkMeshBuilder
{
    /// <summary>
    ///     Vertices one exposed face needs: two triangles.
    /// </summary>
    public const int VerticesPerFace = 6;

    /// <summary>
    ///     Alpha byte of a transparent voxel. It equals raylib's conversion of an alpha of 0.35.
    /// </summary>
    public const byte TransparentAlpha = 89;

    private readonly static sbyte[][] FaceCorners =
    [
        // -X, +X, -Y, +Y, -Z, +Z. Each face lists its corners counter-clockwise from outside.
        [0, 0, 0, 0, 0, 1, 0, 1, 1, 0, 1, 0],
        [1, 0, 0, 1, 1, 0, 1, 1, 1, 1, 0, 1],
        [0, 0, 0, 1, 0, 0, 1, 0, 1, 0, 0, 1],
        [0, 1, 0, 0, 1, 1, 1, 1, 1, 1, 1, 0],
        [0, 0, 0, 0, 1, 0, 1, 1, 0, 1, 0, 0],
        [0, 0, 1, 1, 0, 1, 1, 1, 1, 0, 1, 1]
    ];

    private readonly static byte[] TriangleCorners = [0, 1, 2, 0, 2, 3];

    /// <summary>
    ///     Counts the vertices of the exposed faces of <paramref name="chunk" />.
    /// </summary>
    /// <param name="chunk">Chunk to measure.</param>
    /// <returns>Six vertices for every exposed face of every visible voxel.</returns>
    public static int CountVertices(ChunkDrawData chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        int faces = 0;
        foreach (ref readonly var cell in chunk.Cells)
        {
            if (cell.IsVisible)
                faces += BitOperations.PopCount((uint)cell.VisibleFaces);
        }

        return faces * VerticesPerFace;
    }

    /// <summary>
    ///     Builds the triangle arrays for <paramref name="chunk" />.
    /// </summary>
    /// <param name="chunk">Chunk to mesh.</param>
    /// <param name="transparent">Whether every vertex gets <see cref="TransparentAlpha" />.</param>
    /// <returns>The arrays. A chunk with no exposed face gives empty arrays.</returns>
    public static ChunkMeshData Build(ChunkDrawData chunk, bool transparent)
    {
        int vertexCount = CountVertices(chunk);
        var positions = new float[vertexCount * 3];
        var colors = new byte[vertexCount * 4];
        WritePositions(chunk, positions);
        WriteColors(chunk, transparent, colors);
        return new ChunkMeshData(positions, colors, vertexCount);
    }

    /// <summary>
    ///     Writes the vertex positions of <paramref name="chunk" /> in world coordinates.
    /// </summary>
    /// <param name="chunk">Chunk to mesh.</param>
    /// <param name="destination">Receives three floats per vertex. It must hold <c>CountVertices(chunk) * 3</c>.</param>
    /// <returns>The number of vertices written.</returns>
    /// <exception cref="ArgumentException"><paramref name="destination" /> is too short.</exception>
    public static int WritePositions(ChunkDrawData chunk, Span<float> destination)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        var dimensions = chunk.Dimensions;
        int originX = chunk.ChunkPosition.X * dimensions.X;
        int originY = chunk.ChunkPosition.Y * dimensions.Y;
        int originZ = chunk.ChunkPosition.Z * dimensions.Z;
        ReadOnlySpan<VoxelDrawData> cells = chunk.Cells;
        int written = 0;
        for (int index = 0; index < cells.Length; index++)
        {
            ref readonly var cell = ref cells[index];
            if (!cell.IsVisible || cell.VisibleFaces == VoxelFaceMask.None)
                continue;

            int x = index % dimensions.X;
            int yz = index / dimensions.X;
            int y = yz % dimensions.Y;
            int z = yz / dimensions.Y;
            float baseX = originX + x;
            float baseY = originY + y;
            float baseZ = originZ + z;
            for (int face = 0; face < FaceCorners.Length; face++)
            {
                if (((int)cell.VisibleFaces & 1 << face) == 0)
                    continue;

                sbyte[] corners = FaceCorners[face];
                foreach (byte corner in TriangleCorners)
                {
                    int offset = written * 3;
                    destination[offset] = baseX + corners[corner * 3];
                    destination[offset + 1] = baseY + corners[corner * 3 + 1];
                    destination[offset + 2] = baseZ + corners[corner * 3 + 2];
                    written++;
                }
            }
        }

        return written;
    }

    /// <summary>
    ///     Writes the vertex colors of <paramref name="chunk" /> in the order <see cref="WritePositions" /> uses.
    /// </summary>
    /// <param name="chunk">Chunk to color.</param>
    /// <param name="transparent">Whether every vertex gets <see cref="TransparentAlpha" />.</param>
    /// <param name="destination">Receives four bytes per vertex. It must hold <c>CountVertices(chunk) * 4</c>.</param>
    /// <returns>The number of vertices written.</returns>
    /// <exception cref="ArgumentException"><paramref name="destination" /> is too short.</exception>
    public static int WriteColors(ChunkDrawData chunk, bool transparent, Span<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        ReadOnlySpan<VoxelDrawData> cells = chunk.Cells;
        int written = 0;
        foreach (ref readonly var cell in cells)
        {
            if (!cell.IsVisible || cell.VisibleFaces == VoxelFaceMask.None)
                continue;

            int vertices = BitOperations.PopCount((uint)cell.VisibleFaces) * VerticesPerFace;
            (byte r, byte g, byte b, byte a) = cell.Color.ToRgba8();
            if (transparent)
                a = TransparentAlpha;

            for (int vertex = 0; vertex < vertices; vertex++)
            {
                int offset = written * 4;
                destination[offset] = r;
                destination[offset + 1] = g;
                destination[offset + 2] = b;
                destination[offset + 3] = a;
                written++;
            }
        }

        return written;
    }
}
