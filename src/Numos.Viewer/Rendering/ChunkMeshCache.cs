using System.Buffers;
using Numos.Maths;
using Numos.SimDrawer;
using Raylib_cs;

namespace Numos.Viewer.Rendering;

/// <summary>
///     Keeps one uploaded mesh per chunk of a simulation and draws them.
/// </summary>
/// <remarks>
///     <para>
///         The cache exists because a voxel scene is too large for immediate-mode drawing. A mesh is built from
///         <see cref="ChunkDrawData.TopologyVersion" />, which covers the visible voxels and their exposed faces.
///         When only <see cref="ChunkDrawData.StyleVersion" /> or the transparency changes, the cache rewrites the color
///         buffer and keeps the positions.
///     </para>
///     <para>
///         Each simulation needs its own cache because chunk positions repeat between simulations. Every member
///         must run on the thread that owns the GL context, including <see cref="Dispose" />.
///     </para>
/// </remarks>
internal sealed class ChunkMeshCache : IDisposable
{
    /// <summary>
    ///     Default number of vertices <see cref="Draw" /> builds in one call. Building 6 million vertices takes
    ///     tens of milliseconds, so a large scene appears over several frames instead of one long stall.
    /// </summary>
    public const int DefaultVertexBudget = 6_000_000;

    private const int ParallelThreshold = 4;
    private readonly List<Entry> _drawList = [];
    private readonly Dictionary<Int3, Entry> _entries = [];
    private readonly List<Work> _work = [];
    private readonly List<Int3> _evicted = [];
    private Material _material;
    private bool _hasMaterial;
    private bool _disposed;

    /// <summary>
    ///     Gets whether the last <see cref="Draw" /> left chunks unbuilt because of the vertex budget. The caller
    ///     should draw another frame.
    /// </summary>
    public bool HasPendingWork { get; private set; }

    /// <summary>
    ///     Gets the number of chunks that hold an uploaded mesh.
    /// </summary>
    public int MeshCount
    {
        get
        {
            int count = 0;
            foreach (var entry in _entries.Values)
            {
                if (entry.Mesh != null)
                    count++;
            }

            return count;
        }
    }

    /// <summary>
    ///     Brings the meshes up to date with <paramref name="frame" /> and draws them. Call it inside a raylib 3D
    ///     mode.
    /// </summary>
    /// <param name="frame">Frame to draw. Chunks that its visualization did not map are skipped.</param>
    /// <param name="focusedChunk">When set, only this chunk is built and drawn.</param>
    /// <param name="transparent">Whether every voxel is drawn at partial alpha.</param>
    /// <param name="vertexBudget">
    ///     Vertices to build before the call defers the rest. At least one chunk is built when any is stale.
    /// </param>
    /// <remarks>
    ///     <para>
    ///         A chunk that waits for the budget keeps drawing its older mesh, or nothing if it has none. Meshes of
    ///         chunks that left the frame are freed. Meshes of chunks hidden by focus stay, so unfocusing is cheap.
    ///     </para>
    ///     <para>
    ///         The arrays are filled on worker threads because chunks are independent. Every GL call stays on the
    ///         calling thread.
    ///     </para>
    /// </remarks>
    public void Draw(
        SimulationDrawData frame,
        ChunkIdentity? focusedChunk,
        bool transparent,
        int vertexBudget = DefaultVertexBudget)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_hasMaterial)
        {
            _material = Raylib.LoadMaterialDefault();
            _hasMaterial = true;
        }

        HasPendingWork = false;
        _drawList.Clear();
        _work.Clear();
        int planned = 0;
        foreach (var chunk in frame.Chunks.Values)
        {
            if (!frame.HasCurrentVisualizationMapping(chunk) ||
                focusedChunk.HasValue && chunk.Identity != focusedChunk.Value)
            {
                continue;
            }

            if (!_entries.TryGetValue(chunk.ChunkPosition, out var entry))
            {
                entry = new Entry();
                _entries.Add(chunk.ChunkPosition, entry);
            }

            _drawList.Add(entry);
            bool geometryStale = !entry.IsBuilt ||
                                 entry.Identity != chunk.Identity ||
                                 entry.TopologyVersion != chunk.TopologyVersion;

            if (geometryStale)
            {
                int vertices = ChunkMeshBuilder.CountVertices(chunk);
                if (planned > 0 && planned + vertices > vertexBudget)
                {
                    HasPendingWork = true;
                    continue;
                }

                planned += vertices;
                _work.Add(new Work(entry, chunk, vertices, rebuildGeometry: true));
            }
            else if (entry.StyleVersion != chunk.StyleVersion || entry.Transparent != transparent)
            {
                if (entry.Mesh == null)
                {
                    entry.StyleVersion = chunk.StyleVersion;
                    entry.Transparent = transparent;
                }
                else
                    _work.Add(new Work(entry, chunk, entry.Mesh.VertexCount, rebuildGeometry: false));
            }
        }

        FillArrays(transparent);
        foreach (var work in _work)
            Upload(work, transparent);

        foreach (var entry in _drawList)
            entry.Mesh?.Draw(_material);

        EvictMissing(frame);
    }

    /// <summary>
    ///     Frees every mesh and the material. Call it before the raylib window closes.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        foreach (var entry in _entries.Values)
            entry.Mesh?.Dispose();

        _entries.Clear();
        if (_hasMaterial)
        {
            Raylib.UnloadMaterial(_material);
            _hasMaterial = false;
        }
    }

    /// <summary>
    ///     Fills the position and color arrays of every work item. The items touch different entries, so they run in
    ///     parallel.
    /// </summary>
    private void FillArrays(bool transparent)
    {
        void Fill(Work work)
        {
            var entry = work.Entry;
            if (entry.Colors == null || entry.Colors.Length != work.Vertices * 4)
                entry.Colors = new byte[work.Vertices * 4];

            if (work.RebuildGeometry && work.Vertices > 0)
            {
                work.Positions = ArrayPool<float>.Shared.Rent(work.Vertices * 3);
                ChunkMeshBuilder.WritePositions(work.Chunk, work.Positions);
            }

            int written = ChunkMeshBuilder.WriteColors(work.Chunk, transparent, entry.Colors);
            work.ColorsMatch = written == work.Vertices;
        }

        if (_work.Count < ParallelThreshold)
        {
            foreach (var work in _work)
                Fill(work);

            return;
        }

        Parallel.ForEach(_work, Fill);
    }

    private static void Upload(Work work, bool transparent)
    {
        var entry = work.Entry;
        var chunk = work.Chunk;
        if (!work.RebuildGeometry && !work.ColorsMatch)
        {
            // The cells no longer give the vertex count of the uploaded mesh, so the topology hash missed a change.
            // Rebuild rather than draw a mesh with shifted colors.
            work = new Work(entry, chunk, ChunkMeshBuilder.CountVertices(chunk), rebuildGeometry: true);
            work.Positions = work.Vertices > 0 ? new float[work.Vertices * 3] : null;
            entry.Colors = new byte[work.Vertices * 4];
            if (work.Positions != null)
                ChunkMeshBuilder.WritePositions(chunk, work.Positions);

            ChunkMeshBuilder.WriteColors(chunk, transparent, entry.Colors);
        }

        if (work.RebuildGeometry)
        {
            entry.Mesh?.Dispose();
            entry.Mesh = null;
            if (work.Vertices > 0)
                entry.Mesh = GpuMesh.Upload(work.Positions!, entry.Colors!, work.Vertices);

            if (work.Positions != null)
                ArrayPool<float>.Shared.Return(work.Positions);

            entry.IsBuilt = true;
            entry.Identity = chunk.Identity;
            entry.TopologyVersion = chunk.TopologyVersion;
        }
        else
            entry.Mesh!.UpdateColors(entry.Colors!);

        entry.StyleVersion = chunk.StyleVersion;
        entry.Transparent = transparent;
    }

    private void EvictMissing(SimulationDrawData frame)
    {
        _evicted.Clear();
        foreach (var position in _entries.Keys)
        {
            if (!frame.Chunks.ContainsKey(position))
                _evicted.Add(position);
        }

        foreach (var position in _evicted)
        {
            _entries[position].Mesh?.Dispose();
            _entries.Remove(position);
        }
    }

    /// <summary>
    ///     Array work for one chunk. <see cref="Positions" /> and <see cref="ColorsMatch" /> are set by a worker.
    /// </summary>
    private sealed class Work(Entry entry, ChunkDrawData chunk, int vertices, bool rebuildGeometry)
    {
        public Entry Entry { get; } = entry;
        public ChunkDrawData Chunk { get; } = chunk;
        public int Vertices { get; } = vertices;
        public bool RebuildGeometry { get; } = rebuildGeometry;
        public float[]? Positions { get; set; }
        public bool ColorsMatch { get; set; }
    }

    private sealed class Entry
    {
        /// <summary>
        ///     Vertex colors as last uploaded. Keeping them lets a worker refill the array without an allocation.
        /// </summary>
        public byte[]? Colors;
        public bool IsBuilt;
        public ChunkIdentity Identity;
        public GpuMesh? Mesh;
        public ulong StyleVersion;
        public ulong TopologyVersion;
        public bool Transparent;
    }
}
