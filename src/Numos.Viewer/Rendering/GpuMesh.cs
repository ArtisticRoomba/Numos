using System.Numerics;
using Raylib_cs;

namespace Numos.Viewer.Rendering;

/// <summary>
///     A non-indexed raylib mesh with positions and vertex colors, kept on the GPU.
/// </summary>
/// <remarks>
///     <para>
///         This is the only file in the viewer that uses unsafe code. Raylib-cs exposes <see cref="Mesh" /> with raw
///         pointers, and <c>UploadMesh</c> reads the CPU arrays through them.
///     </para>
///     <para>
///         The CPU pointers are cleared after the upload, so <c>UnloadMesh</c> frees only GPU objects. The
///         mesh does not keep a CPU copy. Every member must run on the thread that owns the GL context.
///     </para>
/// </remarks>
internal sealed unsafe class GpuMesh : IDisposable
{
    private const int ColorBufferIndex = 3;
    private Mesh _mesh;
    private bool _disposed;

    private GpuMesh(Mesh mesh)
    {
        _mesh = mesh;
    }

    /// <summary>
    ///     Gets the number of vertices.
    /// </summary>
    public int VertexCount => _mesh.VertexCount;

    /// <summary>
    ///     Uploads positions and colors into new vertex buffers marked as dynamic.
    /// </summary>
    /// <param name="positions">Three floats per vertex. It must hold at least <paramref name="vertexCount" /> vertices.</param>
    /// <param name="colors">Four bytes per vertex. It must hold at least <paramref name="vertexCount" /> vertices.</param>
    /// <param name="vertexCount">Number of vertices, a positive multiple of three.</param>
    /// <exception cref="ArgumentOutOfRangeException">The count or an array length is not valid.</exception>
    public static GpuMesh Upload(ReadOnlySpan<float> positions, ReadOnlySpan<byte> colors, int vertexCount)
    {
        if (vertexCount <= 0 || vertexCount % 3 != 0)
            throw new ArgumentOutOfRangeException(nameof(vertexCount));

        if (positions.Length < vertexCount * 3)
            throw new ArgumentOutOfRangeException(nameof(positions));

        if (colors.Length < vertexCount * 4)
            throw new ArgumentOutOfRangeException(nameof(colors));

        var mesh = new Mesh(vertexCount, vertexCount / 3);
        fixed (float* positionPointer = positions)
        fixed (byte* colorPointer = colors)
        {
            mesh.Vertices = positionPointer;
            mesh.Colors = colorPointer;
            Raylib.UploadMesh(ref mesh, true);
            mesh.Vertices = null;
            mesh.Colors = null;
        }

        return new GpuMesh(mesh);
    }

    /// <summary>
    ///     Replaces every vertex color in place. The positions and the buffers stay.
    /// </summary>
    /// <param name="colors">Four bytes per vertex. It must hold at least <see cref="VertexCount" /> vertices.</param>
    public void UpdateColors(ReadOnlySpan<byte> colors)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (colors.Length < VertexCount * 4)
            throw new ArgumentOutOfRangeException(nameof(colors));

        Raylib.UpdateMeshBuffer(_mesh, ColorBufferIndex, colors[..(VertexCount * 4)], 0);
    }

    /// <summary>
    ///     Draws the mesh with an identity transform. Call it inside a raylib 3D mode.
    /// </summary>
    /// <param name="material">Material whose shader reads the vertex colors, such as the default material.</param>
    public void Draw(Material material)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Raylib.DrawMesh(_mesh, material, Matrix4x4.Identity);
    }

    /// <summary>
    ///     Frees the GPU buffers.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Raylib.UnloadMesh(_mesh);
        _mesh = default;
    }
}
