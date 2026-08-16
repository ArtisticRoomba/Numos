using Numos.Maths;
using Silk.NET.Maths;

namespace Numos.SimDrawer;

/// <summary>
///     Represents a 3D vertex with position and color.
/// </summary>
public struct Vertex
{
    public Vector3D<float> Position;
    public Vector3D<float> Color;

    public Vertex(float x, float y, float z, float r, float g, float b)
    {
        Position = new Vector3D<float>(x, y, z);
        Color = new Vector3D<float>(r, g, b);
    }
}

/// <summary>
///     Visualization mode for the simulation data.
/// </summary>
public enum VisualizationMode
{
    /// <summary>Color based on temperature (blue = cold, red = hot)</summary>
    Temperature,

    /// <summary>Color based on pressure (black = vacuum, white = high pressure)</summary>
    Pressure,

    /// <summary>Color based on primary gas composition</summary>
    GasComposition,

    /// <summary>Show only voxels with air (active voxels)</summary>
    ActiveOnly
}

/// <summary>
///     Axis normal used when cutting a two-dimensional simulation slice.
/// </summary>
public enum SliceAxis
{
    /// <summary>Cut along X and display the Y/Z plane.</summary>
    X,

    /// <summary>Cut along Y and display the X/Z plane.</summary>
    Y,

    /// <summary>Cut along Z and display the X/Y plane.</summary>
    Z
}

// TODO on all code elements from below, numos or the drawer should probs provide a struct that
// contains gas data alongside coords so it can easily be passed around through the pipeline

/// <summary>
///     Represents drawable data for a single voxel.
/// </summary>
public struct VoxelDrawData
{
    public int X;
    public int Y;
    public int Z;
    public float Temperature;
    public float Pressure;
    public float TotalMoles;
    public int PrimaryGasId;
    public int RoomId;
    public Vector3D<float> Color;
}

/// <summary>
///     Represents drawable data for a single voxel in a two-dimensional slice.
/// </summary>
public struct SliceCellDrawData
{
    public int X;
    public int Y;
    public int Z;
    public int U;
    public int V;
    public float Temperature;
    public float Pressure;
    public float TotalMoles;
    public int PrimaryGasId;
    public int RoomId;
    public Vector3D<float> Color;
}

/// <summary>
///     Represents a simulation cell selected from a viewport.
/// </summary>
public readonly record struct CellSelection(
    Int3 ChunkPosition,
    int X,
    int Y,
    int Z,
    int U,
    int V,
    float Temperature,
    float Pressure,
    float TotalMoles,
    int PrimaryGasId,
    int RoomId,
    Vector3D<float> Color)
{
    public static CellSelection FromSliceCell(Int3 chunkPosition, SliceCellDrawData cell)
    {
        return new CellSelection(
            chunkPosition,
            cell.X,
            cell.Y,
            cell.Z,
            cell.U,
            cell.V,
            cell.Temperature,
            cell.Pressure,
            cell.TotalMoles,
            cell.PrimaryGasId,
            cell.RoomId,
            cell.Color);
    }
}

/// <summary>
///     Contains all drawable data for rendering a chunk.
/// </summary>
public class ChunkDrawData
{
    public Int3 ChunkPosition { get; set; }
    public List<VoxelDrawData> Voxels { get; set; } = new();
    public List<Vertex> Vertices { get; set; } = new();
    public List<uint> Indices { get; set; } = new();
    public VisualizationMode CurrentMode { get; set; } = VisualizationMode.Temperature;

    public void Clear()
    {
        Voxels.Clear();
        Vertices.Clear();
        Indices.Clear();
    }
}

/// <summary>
///     Contains all drawable wireframe data for rendering one chunk slice in two dimensions.
/// </summary>
public class ChunkSliceDrawData
{
    public Int3 ChunkPosition { get; set; }
    public SliceAxis Axis { get; set; } = SliceAxis.Z;
    public int SliceIndex { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public List<SliceCellDrawData> Cells { get; set; } = new();
    public List<Vertex> Vertices { get; set; } = new();
    public List<uint> Indices { get; set; } = new();
    public VisualizationMode CurrentMode { get; set; } = VisualizationMode.Temperature;

    public void Clear()
    {
        Cells.Clear();
        Vertices.Clear();
        Indices.Clear();
    }
}

/// <summary>
///     Contains drawable data for the entire simulation.
/// </summary>
public class SimulationDrawData
{
    public Dictionary<Int3, ChunkDrawData> Chunks { get; } = new();
    public VisualizationMode CurrentMode { get; set; } = VisualizationMode.Temperature;
    public long UpdateTimestamp { get; set; }

    public void Clear()
    {
        foreach (var chunk in Chunks.Values)
            chunk.Clear();
        Chunks.Clear();
    }

    public ChunkDrawData GetOrCreateChunk(Int3 position)
    {
        if (!Chunks.TryGetValue(position, out var chunk))
        {
            chunk = new ChunkDrawData { ChunkPosition = position };
            Chunks[position] = chunk;
        }

        return chunk;
    }
}

/// <summary>
///     Contains two-dimensional slice data for the entire simulation.
/// </summary>
public class SimulationSliceDrawData
{
    public Dictionary<Int3, ChunkSliceDrawData> Chunks { get; } = new();
    public SliceAxis Axis { get; set; } = SliceAxis.Z;
    public int SliceIndex { get; set; }
    public VisualizationMode CurrentMode { get; set; } = VisualizationMode.Temperature;
    public long UpdateTimestamp { get; set; }

    public void Clear()
    {
        foreach (var chunk in Chunks.Values)
            chunk.Clear();
        Chunks.Clear();
    }

    public ChunkSliceDrawData GetOrCreateChunk(Int3 position)
    {
        if (!Chunks.TryGetValue(position, out var chunk))
        {
            chunk = new ChunkSliceDrawData { ChunkPosition = position };
            Chunks[position] = chunk;
        }

        return chunk;
    }
}