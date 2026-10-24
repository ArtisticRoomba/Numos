using System.Collections.ObjectModel;
using Numos.Maths;

namespace Numos.SimDrawer;

/// <summary>
///     Axis normal used when cutting a two-dimensional simulation slice.
/// </summary>
public enum SliceAxis
{
    /// <summary>
    ///     Selects the X axis.
    /// </summary>
    X,
    /// <summary>
    ///     Selects the Y axis.
    /// </summary>
    Y,
    /// <summary>
    ///     Selects the Z axis.
    /// </summary>
    Z
}

/// <summary>
///     Backend-independent, linear RGBA color.
/// </summary>
public readonly record struct ColorRgba(float R, float G, float B, float A = 1f)
{
    /// <summary>
    ///     Linearly interpolates between two colors.
    /// </summary>
    /// <param name="from">Starting color.</param>
    /// <param name="to">Ending color.</param>
    /// <param name="amount">Interpolation amount from zero to one.</param>
    /// <returns>The interpolated color.</returns>
    public static ColorRgba Lerp(ColorRgba from, ColorRgba to, float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        return new ColorRgba(
            from.R + (to.R - from.R) * amount,
            from.G + (to.G - from.G) * amount,
            from.B + (to.B - from.B) * amount,
            from.A + (to.A - from.A) * amount);
    }

    /// <summary>
    ///     Converts the color to four 8-bit channels. Each channel is clamped to [0, 1], scaled by 255, and truncated,
    ///     which matches how raylib builds a <c>Color</c> from floats.
    /// </summary>
    /// <returns>The red, green, blue, and alpha channels.</returns>
    public (byte R, byte G, byte B, byte A) ToRgba8()
    {
        return (ToByte(R), ToByte(G), ToByte(B), ToByte(A));
    }

    private static byte ToByte(float channel)
    {
        return (byte)(Math.Clamp(channel, 0f, 1f) * 255f);
    }
}

/// <summary>
///     Stable identity of one incarnation of a chunk.
/// </summary>
public readonly record struct ChunkIdentity(Int3 Position, long Generation);

/// <summary>
///     Stable identity of one voxel. Presentation values are resolved from the latest frame.
/// </summary>
public readonly record struct VoxelAddress(ChunkIdentity Chunk, ushort LocalIndex);

/// <summary>
///     Faces of a voxel that are exposed to empty or filtered space.
/// </summary>
[Flags]
public enum VoxelFaceMask : byte
{
    /// <summary>
    ///     Selects no faces.
    /// </summary>
    None = 0,
    /// <summary>
    ///     Selects the negative X face.
    /// </summary>
    NegativeX = 1 << 0,
    /// <summary>
    ///     Selects the positive X face.
    /// </summary>
    PositiveX = 1 << 1,
    /// <summary>
    ///     Selects the negative Y face.
    /// </summary>
    NegativeY = 1 << 2,
    /// <summary>
    ///     Selects the positive Y face.
    /// </summary>
    PositiveY = 1 << 3,
    /// <summary>
    ///     Selects the negative Z face.
    /// </summary>
    NegativeZ = 1 << 4,
    /// <summary>
    ///     Selects the positive Z face.
    /// </summary>
    PositiveZ = 1 << 5,
    /// <summary>
    ///     Selects every face.
    /// </summary>
    All = NegativeX | PositiveX | NegativeY | PositiveY | NegativeZ | PositiveZ
}

/// <summary>
///     Immutable presentation values for one voxel. It contains no API-specific mesh data.
/// </summary>
/// <param name="Temperature">Temperature in kelvins (K).</param>
/// <param name="Pressure">Pressure in pascals (Pa).</param>
/// <param name="TotalMoles">Total gas amount in moles (mol).</param>
/// <param name="IsVisible">Whether the voxel is visible.</param>
/// <param name="VisibleFaces">Faces exposed by the voxel.</param>
/// <param name="PrimaryGasId">Registry ID of the primary gas.</param>
/// <param name="RoomId">ID of the voxel's room.</param>
/// <param name="Color">Mapped display color.</param>
public readonly record struct VoxelDrawData(
    bool IsVisible,
    VoxelFaceMask VisibleFaces,
    float Temperature,
    float Pressure,
    float TotalMoles,
    int PrimaryGasId,
    int RoomId,
    ColorRgba Color);

/// <summary>
///     A box of global voxel coordinates.
/// </summary>
/// <param name="Minimum">Smallest coordinate on each axis, inclusive.</param>
/// <param name="MaximumExclusive">Largest coordinate on each axis plus one.</param>
public readonly record struct VoxelBounds(Int3 Minimum, Int3 MaximumExclusive)
{
    /// <summary>
    ///     Gets the number of voxels on each axis.
    /// </summary>
    public Int3 Size => new(
        MaximumExclusive.X - Minimum.X,
        MaximumExclusive.Y - Minimum.Y,
        MaximumExclusive.Z - Minimum.Z);

    /// <summary>
    ///     Gets the smallest coordinate along <paramref name="axis" />.
    /// </summary>
    /// <param name="axis">Axis to read.</param>
    public int GetMinimum(SliceAxis axis)
    {
        return axis switch
        {
            SliceAxis.X => Minimum.X,
            SliceAxis.Y => Minimum.Y,
            SliceAxis.Z => Minimum.Z,
            _ => throw new ArgumentOutOfRangeException(nameof(axis), axis, "Unknown slice axis.")
        };
    }

    /// <summary>
    ///     Gets the largest coordinate along <paramref name="axis" />, plus one.
    /// </summary>
    /// <param name="axis">Axis to read.</param>
    public int GetMaximumExclusive(SliceAxis axis)
    {
        return axis switch
        {
            SliceAxis.X => MaximumExclusive.X,
            SliceAxis.Y => MaximumExclusive.Y,
            SliceAxis.Z => MaximumExclusive.Z,
            _ => throw new ArgumentOutOfRangeException(nameof(axis), axis, "Unknown slice axis.")
        };
    }

    internal static VoxelBounds Enclosing(IEnumerable<ChunkDrawData> chunks)
    {
        bool any = false;
        int minX = 0, minY = 0, minZ = 0, maxX = 0, maxY = 0, maxZ = 0;
        foreach (var chunk in chunks)
        {
            int x = chunk.ChunkPosition.X * chunk.Dimensions.X;
            int y = chunk.ChunkPosition.Y * chunk.Dimensions.Y;
            int z = chunk.ChunkPosition.Z * chunk.Dimensions.Z;
            if (!any)
            {
                (minX, minY, minZ) = (x, y, z);
                (maxX, maxY, maxZ) = (x + chunk.Dimensions.X, y + chunk.Dimensions.Y, z + chunk.Dimensions.Z);
                any = true;
                continue;
            }

            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            minZ = Math.Min(minZ, z);
            maxX = Math.Max(maxX, x + chunk.Dimensions.X);
            maxY = Math.Max(maxY, y + chunk.Dimensions.Y);
            maxZ = Math.Max(maxZ, z + chunk.Dimensions.Z);
        }

        return new VoxelBounds(new Int3(minX, minY, minZ), new Int3(maxX, maxY, maxZ));
    }
}

/// <summary>
///     Immutable presentation data and invalidation keys for one chunk.
/// </summary>
public sealed class ChunkDrawData
{
    private readonly VoxelDrawData[] _cells;

    internal ChunkDrawData(
        ChunkIdentity identity,
        Int3 dimensions,
        long sourceRevision,
        string visualizationId,
        ulong visualizationMappingRevision,
        VoxelDrawData[] cells,
        ulong topologyVersion,
        ulong styleVersion,
        int visibleCellCount,
        int surfaceFaceCount)
    {
        Identity = identity;
        Dimensions = dimensions;
        SourceRevision = sourceRevision;
        VisualizationId = visualizationId;
        VisualizationMappingRevision = visualizationMappingRevision;
        _cells = cells;
        TopologyVersion = topologyVersion;
        StyleVersion = styleVersion;
        VisibleCellCount = visibleCellCount;
        SurfaceFaceCount = surfaceFaceCount;
    }

    /// <summary>
    ///     Gets the chunk identity.
    /// </summary>
    public ChunkIdentity Identity { get; }

    /// <summary>
    ///     Gets the chunk grid position.
    /// </summary>
    public Int3 ChunkPosition => Identity.Position;

    /// <summary>
    ///     Gets the chunk dimensions.
    /// </summary>
    public Int3 Dimensions { get; }

    /// <summary>
    ///     Chunk snapshot revision the cells were mapped from. The builder reuses this chunk only while the snapshot
    ///     revision is unchanged.
    /// </summary>
    public long SourceRevision { get; }

    /// <summary>
    ///     Visualization mapping that produced this chunk. The chunk stores it because a
    ///     focused frame can retain older hidden chunk mappings until focus clears.
    /// </summary>
    public string VisualizationId { get; }

    /// <summary>
    ///     Gets the visualization mapping revision.
    /// </summary>
    public ulong VisualizationMappingRevision { get; }

    /// <summary>
    ///     Changes when visible cells or exposed faces change.
    /// </summary>
    public ulong TopologyVersion { get; }

    /// <summary>
    ///     Changes when a visible cell's mapped color changes.
    /// </summary>
    public ulong StyleVersion { get; }

    /// <summary>
    ///     Gets the total number of cells.
    /// </summary>
    public int CellCount => _cells.Length;

    /// <summary>
    ///     Gets the number of visible cells.
    /// </summary>
    public int VisibleCellCount { get; }

    /// <summary>
    ///     Gets the number of exposed faces.
    /// </summary>
    public int SurfaceFaceCount { get; }

    /// <summary>
    ///     Gets the cells in local-index order.
    /// </summary>
    public ReadOnlySpan<VoxelDrawData> Cells => _cells;

    /// <summary>
    ///     Gets a cell by local index.
    /// </summary>
    /// <param name="localIndex">Local voxel index.</param>
    /// <returns>A read-only reference to the cell.</returns>
    public ref readonly VoxelDrawData GetCell(ushort localIndex)
    {
        if (localIndex >= _cells.Length)
            throw new ArgumentOutOfRangeException(nameof(localIndex));

        return ref _cells[localIndex];
    }

    /// <summary>
    ///     Attempts to get a cell by address.
    /// </summary>
    /// <param name="address">Address of the voxel.</param>
    /// <param name="cell">The resolved cell.</param>
    /// <returns>Whether the cell was found.</returns>
    public bool TryGetCell(VoxelAddress address, out VoxelDrawData cell)
    {
        if (address.Chunk != Identity || address.LocalIndex >= _cells.Length)
        {
            cell = default;
            return false;
        }

        cell = _cells[address.LocalIndex];
        return true;
    }

    /// <summary>
    ///     Gets the local index for local coordinates.
    /// </summary>
    /// <param name="x">Local X coordinate.</param>
    /// <param name="y">Local Y coordinate.</param>
    /// <param name="z">Local Z coordinate.</param>
    /// <returns>The local voxel index.</returns>
    public ushort GetLocalIndex(int x, int y, int z)
    {
        if (x < 0 || x >= Dimensions.X)
            throw new ArgumentOutOfRangeException(nameof(x));

        if (y < 0 || y >= Dimensions.Y)
            throw new ArgumentOutOfRangeException(nameof(y));

        if (z < 0 || z >= Dimensions.Z)
            throw new ArgumentOutOfRangeException(nameof(z));

        return checked((ushort)(x + Dimensions.X * (y + Dimensions.Y * z)));
    }

    /// <summary>
    ///     Gets local coordinates for a local index.
    /// </summary>
    /// <param name="localIndex">Local voxel index.</param>
    /// <returns>The local coordinates.</returns>
    public Int3 GetCoordinates(ushort localIndex)
    {
        if (localIndex >= _cells.Length)
            throw new ArgumentOutOfRangeException(nameof(localIndex));

        int x = localIndex % Dimensions.X;
        int yz = localIndex / Dimensions.X;
        int y = yz % Dimensions.Y;
        int z = yz / Dimensions.Y;
        return new Int3(x, y, z);
    }

    /// <summary>
    ///     Gets world coordinates for a local index.
    /// </summary>
    /// <param name="localIndex">Local voxel index.</param>
    /// <returns>The world coordinates.</returns>
    public Int3 GetWorldCoordinates(ushort localIndex)
    {
        var local = GetCoordinates(localIndex);
        return new Int3(
            ChunkPosition.X * Dimensions.X + local.X,
            ChunkPosition.Y * Dimensions.Y + local.Y,
            ChunkPosition.Z * Dimensions.Z + local.Z);
    }
}

/// <summary>
///     Immutable, versioned retained presentation frame. A focused frame can temporarily omit a
///     newly discovered hidden chunk until the presentation scope expands.
/// </summary>
public sealed class SimulationDrawData
{
    private readonly ReadOnlyDictionary<Int3, ChunkDrawData> _chunks;

    internal SimulationDrawData(
        IDictionary<Int3, ChunkDrawData> chunks,
        VisualizationDescriptor visualization,
        ulong visualizationMappingRevision,
        long sourceVersion,
        long frameVersion)
    {
        _chunks = new ReadOnlyDictionary<Int3, ChunkDrawData>(new Dictionary<Int3, ChunkDrawData>(chunks));
        Bounds = VoxelBounds.Enclosing(_chunks.Values);
        Visualization = visualization;
        VisualizationMappingRevision = visualizationMappingRevision;
        SourceVersion = sourceVersion;
        FrameVersion = frameVersion;
    }

    /// <summary>
    ///     Gets retained chunk presentation data by grid position.
    /// </summary>
    public IReadOnlyDictionary<Int3, ChunkDrawData> Chunks => _chunks;

    /// <summary>
    ///     Gets the global voxel box that encloses every chunk of the frame. A frame without chunks has an empty box.
    /// </summary>
    public VoxelBounds Bounds { get; }

    /// <summary>
    ///     Gets the visualization descriptor used for this frame.
    /// </summary>
    public VisualizationDescriptor Visualization { get; }

    /// <summary>
    ///     Settings revision used to map the source frame into colors and visibility.
    /// </summary>
    public ulong VisualizationMappingRevision { get; }

    /// <summary>
    ///     Caller-supplied version passed to <see cref="SimulationFrameBuilder.BuildSimulation" />.
    /// </summary>
    public long SourceVersion { get; }

    /// <summary>
    ///     Increases with every frame its builder produces.
    /// </summary>
    public long FrameVersion { get; }

    /// <summary>
    ///     Returns whether a retained chunk was mapped by the visualization described by this frame.
    ///     Focused frames can contain older hidden mappings until their scope expands.
    /// </summary>
    public bool HasCurrentVisualizationMapping(ChunkDrawData chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        return string.Equals(
                   chunk.VisualizationId,
                   Visualization.Id,
                   StringComparison.OrdinalIgnoreCase) &&
               chunk.VisualizationMappingRevision == VisualizationMappingRevision;
    }

    /// <summary>
    ///     Attempts to resolve presentation data for a voxel address.
    /// </summary>
    /// <param name="address">Address of the voxel.</param>
    /// <param name="cell">The resolved cell.</param>
    /// <returns>Whether the cell was found.</returns>
    public bool TryResolve(VoxelAddress address, out VoxelDrawData cell)
    {
        if (_chunks.TryGetValue(address.Chunk.Position, out var chunk))
            return chunk.TryGetCell(address, out cell);

        cell = default;
        return false;
    }
}


/// <summary>
///     One cell of a two-dimensional slice, with its presentation data resolved.
/// </summary>
/// <param name="U">Horizontal slice coordinate.</param>
/// <param name="V">Vertical slice coordinate.</param>
/// <param name="Address">Address of the source voxel.</param>
/// <param name="Voxel">Presentation data for the voxel.</param>
public readonly record struct SliceCellDrawData(
    int U,
    int V,
    VoxelAddress Address,
    VoxelDrawData Voxel);

/// <summary>
///     Defines the world-space bounds of a two-dimensional slice.
/// </summary>
/// <param name="Left">Left bound.</param>
/// <param name="Right">Right bound.</param>
/// <param name="Bottom">Bottom bound.</param>
/// <param name="Top">Top bound.</param>
public readonly record struct SliceBounds(float Left, float Right, float Bottom, float Top);

/// <summary>
///     The rectangle one chunk covers in slice coordinates.
/// </summary>
/// <param name="Chunk">Identity of the chunk.</param>
/// <param name="U">Slice coordinate of the left edge.</param>
/// <param name="V">Slice coordinate of the bottom edge.</param>
/// <param name="Width">Extent along U.</param>
/// <param name="Height">Extent along V.</param>
public readonly record struct SliceChunkRect(ChunkIdentity Chunk, int U, int V, int Width, int Height);

/// <summary>
///     Immutable plane through a frame, with a dense RGBA image and constant-time picking.
/// </summary>
/// <remarks>
///     <para>
///         The plane spans the bounds of the chunks it was built from. Cell (0, 0) is the corner with the smallest
///         global coordinates, and V grows upward. A cell that no chunk covers has no address and a transparent pixel.
///     </para>
///     <para>
///         The slice stores two index arrays and a chunk table instead of one object per cell. A plane over a million
///         cells therefore costs a few bytes per cell, and <see cref="TryGetCell" /> builds a
///         <see cref="SliceCellDrawData" /> only when asked.
///     </para>
/// </remarks>
public sealed class SimulationSliceDrawData
{
    private readonly int[] _cellChunk;
    private readonly ushort[] _cellLocal;
    private readonly ChunkDrawData[] _chunks;
    private readonly byte[] _pixels;
    private readonly SliceChunkRect[] _rects;

    internal SimulationSliceDrawData(
        SliceAxis axis,
        int sliceIndex,
        int originU,
        int originV,
        int width,
        int height,
        Int3 chunkDimensions,
        ChunkDrawData[] chunks,
        SliceChunkRect[] rects,
        int[] cellChunk,
        ushort[] cellLocal,
        byte[] pixels,
        int visibleCellCount,
        ulong layoutVersion,
        ulong renderVersion)
    {
        Axis = axis;
        SliceIndex = sliceIndex;
        OriginU = originU;
        OriginV = originV;
        Width = width;
        Height = height;
        ChunkDimensions = chunkDimensions;
        _chunks = chunks;
        _rects = rects;
        _cellChunk = cellChunk;
        _cellLocal = cellLocal;
        _pixels = pixels;
        VisibleCellCount = visibleCellCount;
        LayoutVersion = layoutVersion;
        RenderVersion = renderVersion;
    }

    /// <summary>
    ///     Gets the axis normal to the slice.
    /// </summary>
    public SliceAxis Axis { get; }

    /// <summary>
    ///     Gets the global voxel coordinate of the plane along <see cref="Axis" />.
    /// </summary>
    public int SliceIndex { get; }

    /// <summary>
    ///     Gets the global voxel coordinate that slice coordinate U = 0 maps to.
    /// </summary>
    public int OriginU { get; }

    /// <summary>
    ///     Gets the global voxel coordinate that slice coordinate V = 0 maps to.
    /// </summary>
    public int OriginV { get; }

    /// <summary>
    ///     Gets the slice width.
    /// </summary>
    public int Width { get; }

    /// <summary>
    ///     Gets the slice height.
    /// </summary>
    public int Height { get; }

    /// <summary>
    ///     Gets the dimensions shared by the chunks of this slice.
    /// </summary>
    public Int3 ChunkDimensions { get; }

    /// <summary>
    ///     Gets the number of cells that are visible and so have an opaque pixel.
    /// </summary>
    public int VisibleCellCount { get; }

    /// <summary>
    ///     Gets the render invalidation version. It changes exactly when <see cref="Pixels" /> can differ.
    /// </summary>
    public ulong RenderVersion { get; }

    internal ulong LayoutVersion { get; }

    /// <summary>
    ///     Gets the image as 8-bit RGBA, row by row from V = 0. Pixel (u, v) starts at <c>4 * (u + Width * v)</c>.
    /// </summary>
    /// <remarks>
    ///     A visible cell is opaque. Cells with no chunk, and cells filtered from rendering, are transparent black.
    /// </remarks>
    public ReadOnlySpan<byte> Pixels => _pixels;

    /// <summary>
    ///     Gets the rectangle each chunk covers in the slice, in chunk position order.
    /// </summary>
    public ReadOnlySpan<SliceChunkRect> ChunkRects => _rects;

    internal int[] CellChunkArray => _cellChunk;

    internal ushort[] CellLocalArray => _cellLocal;

    internal byte[] PixelArray => _pixels;

    /// <summary>
    ///     Enumerates the visible cells in row order.
    /// </summary>
    /// <remarks>
    ///     This builds one <see cref="SliceCellDrawData" /> per cell. Use it for small slices and tests. A renderer
    ///     reads <see cref="Pixels" /> instead.
    /// </remarks>
    public IEnumerable<SliceCellDrawData> EnumerateVisibleCells()
    {
        for (int v = 0; v < Height; v++)
        {
            for (int u = 0; u < Width; u++)
            {
                if (_pixels[4 * (u + Width * v) + 3] != 0 && TryGetCell(u, v, out var cell))
                    yield return cell;
            }
        }
    }

    /// <summary>
    ///     Attempts to get a cell by slice coordinates, including cells filtered from rendering.
    /// </summary>
    /// <param name="u">Horizontal slice coordinate.</param>
    /// <param name="v">Vertical slice coordinate.</param>
    /// <param name="cell">The resolved cell.</param>
    /// <returns>Whether a chunk covers the coordinates.</returns>
    public bool TryGetCell(int u, int v, out SliceCellDrawData cell)
    {
        if (u < 0 || u >= Width || v < 0 || v >= Height)
        {
            cell = default;
            return false;
        }

        int index = u + Width * v;
        int slot = _cellChunk[index];
        if (slot < 0)
        {
            cell = default;
            return false;
        }

        var chunk = _chunks[slot];
        ushort local = _cellLocal[index];
        cell = new SliceCellDrawData(u, v, new VoxelAddress(chunk.Identity, local), chunk.GetCell(local));
        return true;
    }

    /// <summary>
    ///     Finds the cell that shows a voxel.
    /// </summary>
    /// <param name="address">Address of the voxel.</param>
    /// <param name="u">Horizontal slice coordinate of the voxel.</param>
    /// <param name="v">Vertical slice coordinate of the voxel.</param>
    /// <returns>
    ///     Whether the voxel lies in this plane. The chunk generation must also match the one the slice was built from.
    /// </returns>
    public bool TryLocate(VoxelAddress address, out int u, out int v)
    {
        u = 0;
        v = 0;
        var dimensions = ChunkDimensions;
        int count = dimensions.X * dimensions.Y * dimensions.Z;
        if (address.LocalIndex >= count)
            return false;

        int x = address.LocalIndex % dimensions.X;
        int yz = address.LocalIndex / dimensions.X;
        int y = yz % dimensions.Y;
        int z = yz / dimensions.Y;
        int globalX = address.Chunk.Position.X * dimensions.X + x;
        int globalY = address.Chunk.Position.Y * dimensions.Y + y;
        int globalZ = address.Chunk.Position.Z * dimensions.Z + z;
        int along = Axis switch
        {
            SliceAxis.X => globalX,
            SliceAxis.Y => globalY,
            _ => globalZ
        };

        if (along != SliceIndex)
            return false;

        (int projectedU, int projectedV) = Project(Axis, globalX, globalY, globalZ);
        int candidateU = projectedU - OriginU;
        int candidateV = projectedV - OriginV;
        if (candidateU < 0 || candidateU >= Width || candidateV < 0 || candidateV >= Height)
            return false;

        int index = candidateU + Width * candidateV;
        int slot = _cellChunk[index];
        if (slot < 0 || _chunks[slot].Identity != address.Chunk || _cellLocal[index] != address.LocalIndex)
            return false;

        u = candidateU;
        v = candidateV;
        return true;
    }

    /// <summary>
    ///     Gets the rectangle a chunk would cover in this slice, even when the plane does not cut the chunk.
    /// </summary>
    /// <param name="chunkPosition">Grid position of the chunk.</param>
    /// <returns>The left, bottom, width, and height of the footprint in slice coordinates.</returns>
    public (int U, int V, int Width, int Height) GetFootprint(Int3 chunkPosition)
    {
        var dimensions = ChunkDimensions;
        (int u, int v) = Project(
            Axis,
            chunkPosition.X * dimensions.X,
            chunkPosition.Y * dimensions.Y,
            chunkPosition.Z * dimensions.Z);

        (int width, int height) = Project(Axis, dimensions.X, dimensions.Y, dimensions.Z);
        return (u - OriginU, v - OriginV, width, height);
    }

    internal static (int U, int V) Project(SliceAxis axis, int x, int y, int z)
    {
        return axis switch
        {
            SliceAxis.X => (z, y),
            SliceAxis.Y => (x, z),
            SliceAxis.Z => (x, y),
            _ => throw new ArgumentOutOfRangeException(nameof(axis), axis, "Unknown slice axis.")
        };
    }

    /// <summary>
    ///     Gets aspect-corrected view bounds for the slice.
    /// </summary>
    /// <param name="viewportAspectRatio">Viewport width divided by height.</param>
    /// <param name="margin">Margin around the slice.</param>
    /// <returns>The view bounds.</returns>
    public SliceBounds GetViewBounds(float viewportAspectRatio, float margin = 0.5f)
    {
        float left = -margin;
        float right = Width + margin;
        float bottom = -margin;
        float top = Height + margin;

        float width = Math.Max(right - left, 1f);
        float height = Math.Max(top - bottom, 1f);
        viewportAspectRatio = Math.Max(viewportAspectRatio, 0.01f);

        float boundsAspectRatio = width / height;
        if (boundsAspectRatio < viewportAspectRatio)
        {
            float extra = (height * viewportAspectRatio - width) * 0.5f;
            left -= extra;
            right += extra;
        }
        else if (boundsAspectRatio > viewportAspectRatio)
        {
            float extra = (width / viewportAspectRatio - height) * 0.5f;
            bottom -= extra;
            top += extra;
        }

        return new SliceBounds(left, right, bottom, top);
    }

    /// <summary>
    ///     Picks a cell from bottom-left-origin normalized viewport coordinates.
    /// </summary>
    /// <param name="normalizedX">Horizontal viewport position in [0, 1].</param>
    /// <param name="normalizedY">Vertical viewport position in [0, 1], growing upward.</param>
    /// <param name="viewportAspectRatio">
    ///     Viewport width divided by height, as passed to <see cref="GetViewBounds" />.
    /// </param>
    /// <param name="cell">The picked cell, including cells filtered from rendering.</param>
    /// <returns>Whether the position lands on a cell of the slice.</returns>
    /// <remarks>
    ///     Picking assumes the slice is shown with <see cref="GetViewBounds" /> and its default margin.
    /// </remarks>
    public bool TryPickNormalized(
        float normalizedX,
        float normalizedY,
        float viewportAspectRatio,
        out SliceCellDrawData cell)
    {
        if (normalizedX < 0f || normalizedX > 1f || normalizedY < 0f || normalizedY > 1f)
        {
            cell = default;
            return false;
        }

        var bounds = GetViewBounds(viewportAspectRatio);
        float u = bounds.Left + normalizedX * (bounds.Right - bounds.Left);
        float v = bounds.Bottom + normalizedY * (bounds.Top - bounds.Bottom);
        return TryGetCell((int)MathF.Floor(u), (int)MathF.Floor(v), out cell);
    }
}
