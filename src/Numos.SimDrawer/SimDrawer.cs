using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.Maths;
using Silk.NET.Maths;

namespace Numos.SimDrawer;

/// <summary>
///     Converts a provided simstate into drawable primitives that can be passed to relevant APIs.
/// </summary>
/// <remarks>
///     In essence, a future plan of mine is to have the simulator be available to play around with
///     in a little computer on the SS14 client, so doing this right now is a "why not".
/// </remarks>
public class SimDrawer
{
    private const float VoxelSize = 1.0f;

    public SimDrawer(AtmosConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
    }

    /// <summary>
    ///     Generate drawable data for a chunk using the specified visualization mode.
    /// </summary>
    public ChunkDrawData DrawChunk(AtmosChunkSnapshot snapshot,
        VisualizationMode mode = VisualizationMode.Temperature)
    {
        var drawData = new ChunkDrawData
        {
            ChunkPosition = snapshot.GridPosition,
            CurrentMode = mode
        };

        if (!snapshot.IsSnapshotValid)
            return drawData;

        // Generate voxel data for all voxels
        for (ushort i = 0; i < snapshot.VoxelRoomMap.Length; i++)
        {
            (int x, int y, int z) = GetXyz(snapshot.Dimensions, i);
            int roomId = snapshot.VoxelRoomMap[i];

            // Skip void and solid voxels
            if (roomId == VoxelClassification.RoomVoid || roomId == VoxelClassification.RoomSolid)
                continue;

            var voxelData = CreateVoxelDrawData(snapshot, i, x, y, z, roomId, mode);
            drawData.Voxels.Add(voxelData);
        }

        // Generate cube vertices for visualization
        GenerateCubeGeometry(drawData, snapshot.Dimensions.X, snapshot.Dimensions.Y, snapshot.Dimensions.Z);

        return drawData;
    }

    /// <summary>
    ///     Generate two-dimensional wireframe drawable data for one chunk slice.
    ///     The slice index is local to the chunk and is clamped to the chosen axis size.
    /// </summary>
    public ChunkSliceDrawData DrawChunkSlice(
        AtmosChunkSnapshot snapshot,
        SliceAxis axis,
        int sliceIndex,
        VisualizationMode mode = VisualizationMode.Temperature)
    {
        int clampedSliceIndex = ClampSliceIndex(snapshot.Dimensions, axis, sliceIndex);
        (int sliceWidth, int sliceHeight) = GetSliceDimensions(snapshot.Dimensions, axis);

        var drawData = new ChunkSliceDrawData
        {
            ChunkPosition = snapshot.GridPosition,
            Axis = axis,
            SliceIndex = clampedSliceIndex,
            Width = sliceWidth,
            Height = sliceHeight,
            CurrentMode = mode
        };

        if (!snapshot.IsSnapshotValid)
            return drawData;

        for (ushort i = 0; i < snapshot.VoxelRoomMap.Length; i++)
        {
            (int x, int y, int z) = GetXyz(snapshot.Dimensions, i);
            if (GetAxisCoordinate(axis, x, y, z) != clampedSliceIndex)
                continue;

            int roomId = snapshot.VoxelRoomMap[i];
            if (roomId == VoxelClassification.RoomVoid || roomId == VoxelClassification.RoomSolid)
                continue;

            var voxelData = CreateVoxelDrawData(snapshot, i, x, y, z, roomId, mode);
            (int u, int v) = MapSliceCoordinates(snapshot, axis, x, y, z);

            drawData.Cells.Add(new SliceCellDrawData
            {
                X = voxelData.X,
                Y = voxelData.Y,
                Z = voxelData.Z,
                U = u,
                V = v,
                Temperature = voxelData.Temperature,
                Pressure = voxelData.Pressure,
                TotalMoles = voxelData.TotalMoles,
                PrimaryGasId = voxelData.PrimaryGasId,
                RoomId = voxelData.RoomId,
                Color = voxelData.Color
            });
        }

        GenerateSliceWireframeGeometry(drawData);

        return drawData;
    }

    /// <summary>
    ///     Generate drawable data for the entire simulation.
    /// </summary>
    public SimulationDrawData DrawSimulation(IEnumerable<AtmosChunkSnapshot> chunks,
        VisualizationMode mode = VisualizationMode.Temperature)
    {
        var drawData = new SimulationDrawData
        {
            CurrentMode = mode,
            UpdateTimestamp = DateTimeOffset.UtcNow.Ticks
        };

        foreach (var chunk in chunks)
        {
            var chunkDrawData = DrawChunk(chunk, mode);
            if (chunkDrawData.Voxels.Count > 0)
            {
                drawData.Chunks[chunk.GridPosition] = chunkDrawData;
            }
        }

        return drawData;
    }

    /// <summary>
    ///     Generate two-dimensional wireframe slice drawable data for the simulation.
    ///     If a chunk position is supplied, only that chunk is sliced.
    /// </summary>
    public SimulationSliceDrawData DrawSimulationSlice(
        IEnumerable<AtmosChunkSnapshot> chunks,
        SliceAxis axis,
        int sliceIndex,
        VisualizationMode mode = VisualizationMode.Temperature,
        Int3? chunkPosition = null)
    {
        var drawData = new SimulationSliceDrawData
        {
            Axis = axis,
            SliceIndex = sliceIndex,
            CurrentMode = mode,
            UpdateTimestamp = DateTimeOffset.UtcNow.Ticks
        };

        foreach (var chunk in chunks)
        {
            if (chunkPosition.HasValue && chunk.GridPosition != chunkPosition.Value)
                continue;

            var chunkDrawData = DrawChunkSlice(chunk, axis, sliceIndex, mode);
            if (chunkDrawData.Cells.Count > 0)
            {
                drawData.Chunks[chunk.GridPosition] = chunkDrawData;
                drawData.SliceIndex = chunkDrawData.SliceIndex;
            }
        }

        return drawData;
    }

    /// <summary>
    ///     Change visualization mode for existing draw data.
    /// </summary>
    public void UpdateVisualizationMode(SimulationDrawData drawData, VisualizationMode newMode)
    {
        drawData.CurrentMode = newMode;
        foreach (var chunk in drawData.Chunks.Values)
        {
            chunk.CurrentMode = newMode;
            foreach (var voxel in chunk.Voxels)
            {
                // Recompute colors based on new mode
                // Note: We need the original snapshot to do this properly
                // This is a simplified version
            }
        }
    }

    private VoxelDrawData CreateVoxelDrawData(
        AtmosChunkSnapshot snapshot,
        ushort voxelIndex,
        int x,
        int y,
        int z,
        int roomId,
        VisualizationMode mode)
    {
        var voxelData = new VoxelDrawData
        {
            X = x,
            Y = y,
            Z = z,
            Temperature = snapshot.Temperature[voxelIndex],
            Pressure = snapshot.TotalPressure[voxelIndex],
            RoomId = roomId,
            Color = ComputeVoxelColor(snapshot, voxelIndex, mode)
        };

        // Calculate total moles and primary gas
        var totalMoles = 0f;
        int primaryGasId = -1;
        var maxGasMoles = 0f;

        for (var g = 0; g < snapshot.Gases.Length; g++)
        {
            totalMoles += snapshot.Gases[g].Moles[voxelIndex];
            if (snapshot.Gases[g].Moles[voxelIndex] > maxGasMoles)
            {
                maxGasMoles = snapshot.Gases[g].Moles[voxelIndex];
                primaryGasId = snapshot.Gases[g].GasId;
            }
        }

        voxelData.TotalMoles = totalMoles;
        voxelData.PrimaryGasId = primaryGasId;

        return voxelData;
    }

    private Vector3D<float> ComputeVoxelColor(AtmosChunkSnapshot snapshot, ushort voxelIndex,
        VisualizationMode mode)
    {
        return mode switch
        {
            VisualizationMode.Temperature => ColorFromTemperature(snapshot.Temperature[voxelIndex]),
            VisualizationMode.Pressure => ColorFromPressure(snapshot.TotalPressure[voxelIndex]),
            VisualizationMode.GasComposition => ColorFromGasComposition(snapshot, voxelIndex),
            VisualizationMode.ActiveOnly => ColorFromActive(snapshot, voxelIndex),
            _ => new Vector3D<float>(0.5f, 0.5f, 0.5f)
        };
    }

    private Vector3D<float> ColorFromTemperature(float temperature)
    {
        // Blue (cold) -> Yellow -> Red (hot)
        // Scale: 0K-373K (0-100°C)
        float normalized = Math.Clamp(temperature / 373f, 0f, 1f);

        if (normalized < 0.5f)
        {
            // Blue to Yellow
            float t = normalized * 2f;
            return new Vector3D<float>(t, t, 1f - t);
        }
        else
        {
            // Yellow to Red
            float t = (normalized - 0.5f) * 2f;
            return new Vector3D<float>(1f, 1f - t, 0f);
        }
    }

    private Vector3D<float> ColorFromPressure(float pressure)
    {
        // Black (vacuum) -> Gray -> White (high pressure)
        float normalized = Math.Clamp(pressure / 300f, 0f, 1f);
        return new Vector3D<float>(normalized, normalized, normalized);
    }

    private Vector3D<float> ColorFromGasComposition(AtmosChunkSnapshot snapshot, ushort voxelIndex)
    {
        if (snapshot.Gases.Length == 0)
            return new Vector3D<float>(0.5f, 0.5f, 0.5f);

        // Find dominant gas
        var dominantGasId = 0;
        var maxMoles = 0f;

        for (var g = 0; g < snapshot.Gases.Length; g++)
        {
            if (snapshot.Gases[g].Moles[voxelIndex] > maxMoles)
            {
                maxMoles = snapshot.Gases[g].Moles[voxelIndex];
                dominantGasId = g;
            }
        }

        // Assign colors based on gas ID (simple hashing)
        return dominantGasId switch
        {
            0 => new Vector3D<float>(0.2f, 0.8f, 0.2f), // Gas 0 - Green
            1 => new Vector3D<float>(0.8f, 0.2f, 0.2f), // Gas 1 - Red
            2 => new Vector3D<float>(0.2f, 0.2f, 0.8f), // Gas 2 - Blue
            3 => new Vector3D<float>(0.8f, 0.8f, 0.2f), // Gas 3 - Yellow
            4 => new Vector3D<float>(0.8f, 0.2f, 0.8f), // Gas 4 - Magenta
            5 => new Vector3D<float>(0.2f, 0.8f, 0.8f), // Gas 5 - Cyan
            _ => new Vector3D<float>(0.5f, 0.5f, 0.5f) // Default - Gray
        };
    }

    private Vector3D<float> ColorFromActive(AtmosChunkSnapshot snapshot, ushort voxelIndex)
    {
        float pressure = snapshot.TotalPressure[voxelIndex];
        return pressure > 0.1f
            ? new Vector3D<float>(0.3f, 0.7f, 1f)
            : new Vector3D<float>(0.1f, 0.1f, 0.1f);
    }

    private void GenerateCubeGeometry(ChunkDrawData drawData, int chunkWidth, int chunkHeight, int chunkDepth)
    {
        uint indexOffset = 0;

        foreach (var voxel in drawData.Voxels)
        {
            var worldPos = new Vector3D<float>(
                (drawData.ChunkPosition.X * chunkWidth + voxel.X) * VoxelSize,
                (drawData.ChunkPosition.Y * chunkHeight + voxel.Y) * VoxelSize,
                (drawData.ChunkPosition.Z * chunkDepth + voxel.Z) * VoxelSize
            );

            AddCubeVertices(drawData, worldPos, voxel.Color, ref indexOffset);
        }
    }

    private void AddCubeVertices(ChunkDrawData drawData, Vector3D<float> position,
        Vector3D<float> color, ref uint indexOffset)
    {
        // Define cube vertices (8 corners)
        var vertices = new[]
        {
            // Front face
            new Vertex(position.X, position.Y, position.Z, color.X, color.Y, color.Z),
            new Vertex(position.X + VoxelSize, position.Y, position.Z, color.X, color.Y, color.Z),
            new Vertex(position.X + VoxelSize, position.Y + VoxelSize, position.Z, color.X, color.Y, color.Z),
            new Vertex(position.X, position.Y + VoxelSize, position.Z, color.X, color.Y, color.Z),

            // Back face
            new Vertex(position.X, position.Y, position.Z + VoxelSize, color.X, color.Y, color.Z),
            new Vertex(position.X + VoxelSize, position.Y, position.Z + VoxelSize, color.X, color.Y, color.Z),
            new Vertex(position.X + VoxelSize, position.Y + VoxelSize, position.Z + VoxelSize, color.X, color.Y,
                color.Z),
            new Vertex(position.X, position.Y + VoxelSize, position.Z + VoxelSize, color.X, color.Y, color.Z)
        };

        // Add vertices to the draw data
        foreach (var vertex in vertices)
            drawData.Vertices.Add(vertex);

        // Define cube indices (6 faces, 2 triangles per face)
        var indices = new[]
        {
            // Front
            indexOffset, indexOffset + 1, indexOffset + 2,
            indexOffset, indexOffset + 2, indexOffset + 3,

            // Back
            indexOffset + 4, indexOffset + 6, indexOffset + 5,
            indexOffset + 4, indexOffset + 7, indexOffset + 6,

            // Left
            indexOffset + 4, indexOffset + 0, indexOffset + 3,
            indexOffset + 4, indexOffset + 3, indexOffset + 7,

            // Right
            indexOffset + 1, indexOffset + 5, indexOffset + 6,
            indexOffset + 1, indexOffset + 6, indexOffset + 2,

            // Top
            indexOffset + 3, indexOffset + 2, indexOffset + 6,
            indexOffset + 3, indexOffset + 6, indexOffset + 7,

            // Bottom
            indexOffset + 4, indexOffset + 5, indexOffset + 1,
            indexOffset + 4, indexOffset + 1, indexOffset + 0
        };

        foreach (uint index in indices)
            drawData.Indices.Add(index);

        indexOffset += 8;
    }

    private void GenerateSliceWireframeGeometry(ChunkSliceDrawData drawData)
    {
        uint indexOffset = 0;

        foreach (var cell in drawData.Cells)
        {
            var position = new Vector3D<float>(cell.U * VoxelSize, cell.V * VoxelSize, 0f);
            AddSliceCellWireframeVertices(drawData, position, cell.Color, ref indexOffset);
        }
    }

    private void AddSliceCellWireframeVertices(
        ChunkSliceDrawData drawData,
        Vector3D<float> position,
        Vector3D<float> color,
        ref uint indexOffset)
    {
        var vertices = new[]
        {
            new Vertex(position.X, position.Y, position.Z, color.X, color.Y, color.Z),
            new Vertex(position.X + VoxelSize, position.Y, position.Z, color.X, color.Y, color.Z),
            new Vertex(position.X + VoxelSize, position.Y + VoxelSize, position.Z, color.X, color.Y, color.Z),
            new Vertex(position.X, position.Y + VoxelSize, position.Z, color.X, color.Y, color.Z)
        };

        foreach (var vertex in vertices)
            drawData.Vertices.Add(vertex);

        var indices = new[]
        {
            indexOffset, indexOffset + 1,
            indexOffset + 1, indexOffset + 2,
            indexOffset + 2, indexOffset + 3,
            indexOffset + 3, indexOffset
        };

        foreach (uint index in indices)
            drawData.Indices.Add(index);

        indexOffset += 4;
    }

    private static int ClampSliceIndex(Int3 dimensions, SliceAxis axis, int sliceIndex)
    {
        int maxIndex = Math.Max(GetSliceAxisLength(dimensions, axis) - 1, 0);
        return Math.Clamp(sliceIndex, 0, maxIndex);
    }

    public static int GetSliceAxisLength(Int3 dimensions, SliceAxis axis)
    {
        return axis switch
        {
            SliceAxis.X => dimensions.X,
            SliceAxis.Y => dimensions.Y,
            SliceAxis.Z => dimensions.Z,
            _ => dimensions.Z
        };
    }

    private static (int width, int height) GetSliceDimensions(Int3 dimensions, SliceAxis axis)
    {
        return axis switch
        {
            SliceAxis.X => (dimensions.Z, dimensions.Y),
            SliceAxis.Y => (dimensions.X, dimensions.Z),
            SliceAxis.Z => (dimensions.X, dimensions.Y),
            _ => (dimensions.X, dimensions.Y)
        };
    }

    private static int GetAxisCoordinate(SliceAxis axis, int x, int y, int z)
    {
        return axis switch
        {
            SliceAxis.X => x,
            SliceAxis.Y => y,
            SliceAxis.Z => z,
            _ => z
        };
    }

    private static (int u, int v) MapSliceCoordinates(AtmosChunkSnapshot chunk, SliceAxis axis, int x, int y, int z)
    {
        return axis switch
        {
            SliceAxis.X => (chunk.GridPosition.Z * chunk.Dimensions.Z + z,
                chunk.GridPosition.Y * chunk.Dimensions.Y + y),
            SliceAxis.Y => (chunk.GridPosition.X * chunk.Dimensions.X + x,
                chunk.GridPosition.Z * chunk.Dimensions.Z + z),
            SliceAxis.Z => (chunk.GridPosition.X * chunk.Dimensions.X + x,
                chunk.GridPosition.Y * chunk.Dimensions.Y + y),
            _ => (chunk.GridPosition.X * chunk.Dimensions.X + x, chunk.GridPosition.Y * chunk.Dimensions.Y + y)
        };
    }

    private static (int x, int y, int z) GetXyz(Int3 dimensions, ushort index)
    {
        int x = index % dimensions.X;
        int yz = index / dimensions.X;
        int y = yz % dimensions.Y;
        int z = yz / dimensions.Y;
        return (x, y, z);
    }
}