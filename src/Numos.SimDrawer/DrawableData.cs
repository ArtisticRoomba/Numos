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
    ///     Gets the source revision used to create this data.
    /// </summary>
    public long SourceRevision { get; }

    /// <summary>
    ///     Visualization mapping that produced this chunk. This is stored per chunk because a
    ///     focused frame may deliberately retain older, hidden chunk mappings until focus clears.
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
        _chunks = new ReadOnlyDictionary<Int3, ChunkDrawData>(
            new Dictionary<Int3, ChunkDrawData>(chunks));
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
    ///     Gets the visualization descriptor used for this frame.
    /// </summary>
    public VisualizationDescriptor Visualization { get; }

    /// <summary>
    ///     Settings revision used to map the source frame into colors and visibility.
    /// </summary>
    public ulong VisualizationMappingRevision { get; }

    /// <summary>
    ///     Gets the source simulation version.
    /// </summary>
    public long SourceVersion { get; }

    /// <summary>
    ///     Gets the retained frame version.
    /// </summary>
    public long FrameVersion { get; }

    /// <summary>
    ///     Returns whether a retained chunk was mapped by the visualization described by this frame.
    ///     Focused frames may intentionally contain older hidden mappings until their scope expands.
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
///     One visible cell in a focused two-dimensional slice.
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
///     Immutable dense-coordinate slice view with constant-time picking.
/// </summary>
public sealed class SimulationSliceDrawData
{
    private readonly SliceCellDrawData[] _cells;
    private readonly int[] _lookup;

    internal SimulationSliceDrawData(
        ChunkIdentity chunk,
        SliceAxis axis,
        int sliceIndex,
        int width,
        int height,
        SliceCellDrawData[] cells,
        int[] lookup,
        ulong renderVersion)
    {
        Chunk = chunk;
        Axis = axis;
        SliceIndex = sliceIndex;
        Width = width;
        Height = height;
        _cells = cells;
        _lookup = lookup;
        RenderVersion = renderVersion;
    }

    /// <summary>
    ///     Gets the sliced chunk identity.
    /// </summary>
    public ChunkIdentity Chunk { get; }

    /// <summary>
    ///     Gets the axis normal to the slice.
    /// </summary>
    public SliceAxis Axis { get; }

    /// <summary>
    ///     Gets the selected index along the slice axis.
    /// </summary>
    public int SliceIndex { get; }

    /// <summary>
    ///     Gets the slice width.
    /// </summary>
    public int Width { get; }

    /// <summary>
    ///     Gets the slice height.
    /// </summary>
    public int Height { get; }

    /// <summary>
    ///     Gets the render invalidation version.
    /// </summary>
    public ulong RenderVersion { get; }

    /// <summary>
    ///     Gets visible slice cells.
    /// </summary>
    public ReadOnlySpan<SliceCellDrawData> Cells => _cells;

    /// <summary>
    ///     Attempts to get a visible cell by slice coordinates.
    /// </summary>
    /// <param name="u">Horizontal slice coordinate.</param>
    /// <param name="v">Vertical slice coordinate.</param>
    /// <param name="cell">The resolved cell.</param>
    /// <returns>Whether a visible cell exists at the coordinates.</returns>
    public bool TryGetCell(int u, int v, out SliceCellDrawData cell)
    {
        if (u < 0 || u >= Width || v < 0 || v >= Height)
        {
            cell = default;
            return false;
        }

        int entry = _lookup[u + Width * v];
        if (entry == 0)
        {
            cell = default;
            return false;
        }

        cell = _cells[entry - 1];
        return true;
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
