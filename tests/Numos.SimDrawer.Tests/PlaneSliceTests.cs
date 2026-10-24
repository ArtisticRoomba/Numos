using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.Maths;

namespace Numos.SimDrawer.Tests;

[TestFixture]
public sealed class PlaneSliceTests
{
    private static float Code(int x, int y, int z)
    {
        return 300f + x + 10 * y + 100 * z;
    }

    [Test]
    public void BuildPlaneSlice_FourChunks_StitchesCellsAcrossChunkSeams()
    {
        var dimensions = new Int3(2, 2, 1);
        var frame = BuildFrame(
            dimensions,
            new Int3(0, 0, 0),
            new Int3(1, 0, 0),
            new Int3(0, 1, 0),
            new Int3(1, 1, 0));

        var slice = new SimulationFrameBuilder(new AtmosConfig()).BuildPlaneSlice(frame, SliceAxis.Z, 0);

        Assert.Multiple(() =>
        {
            Assert.That(slice.Width, Is.EqualTo(4));
            Assert.That(slice.Height, Is.EqualTo(4));
            Assert.That(slice.VisibleCellCount, Is.EqualTo(16));
            Assert.That(slice.ChunkRects.Length, Is.EqualTo(4));
        });

        for (int v = 0; v < 4; v++)
        {
            for (int u = 0; u < 4; u++)
            {
                Assert.That(slice.TryGetCell(u, v, out var cell), Is.True, $"({u}, {v})");
                Assert.That(cell.Voxel.Temperature, Is.EqualTo(Code(u, v, 0)), $"({u}, {v})");
                Assert.That(cell.Address.Chunk.Position, Is.EqualTo(new Int3(u / 2, v / 2, 0)), $"({u}, {v})");
                Assert.That(slice.Pixels[4 * (u + 4 * v) + 3], Is.EqualTo(byte.MaxValue), $"({u}, {v})");
            }
        }
    }

    [Test]
    public void BuildPlaneSlice_MissingChunk_LeavesTransparentCellsThatCannotBePicked()
    {
        var dimensions = new Int3(2, 2, 1);
        var frame = BuildFrame(dimensions, new Int3(0, 0, 0), new Int3(1, 0, 0), new Int3(0, 1, 0));

        var slice = new SimulationFrameBuilder(new AtmosConfig()).BuildPlaneSlice(frame, SliceAxis.Z, 0);

        Assert.Multiple(() =>
        {
            Assert.That(slice.Width, Is.EqualTo(4));
            Assert.That(slice.Height, Is.EqualTo(4));
            Assert.That(slice.VisibleCellCount, Is.EqualTo(12));
        });

        for (int v = 2; v < 4; v++)
        {
            for (int u = 2; u < 4; u++)
            {
                int offset = 4 * (u + 4 * v);
                Assert.That(slice.TryGetCell(u, v, out _), Is.False, $"({u}, {v})");
                Assert.That(slice.Pixels.Slice(offset, 4).ToArray(), Is.EqualTo(new byte[4]), $"({u}, {v})");
            }
        }
    }

    [TestCase(SliceAxis.X, 1)]
    [TestCase(SliceAxis.X, 3)]
    [TestCase(SliceAxis.Y, 2)]
    [TestCase(SliceAxis.Y, 4)]
    [TestCase(SliceAxis.Z, 3)]
    [TestCase(SliceAxis.Z, 6)]
    public void BuildPlaneSlice_EachAxis_MapsCellsToTheDocumentedGlobalCoordinates(SliceAxis axis, int index)
    {
        // The two chunks sit on a diagonal, so each plane cuts exactly one of them and leaves the rest empty.
        var dimensions = new Int3(2, 3, 4);
        var frame = BuildFrame(dimensions, new Int3(0, 0, 0), new Int3(1, 1, 1));
        var slice = new SimulationFrameBuilder(new AtmosConfig()).BuildPlaneSlice(frame, axis, index);

        int present = 0;
        for (int v = 0; v < slice.Height; v++)
        {
            for (int u = 0; u < slice.Width; u++)
            {
                if (!slice.TryGetCell(u, v, out var cell))
                    continue;

                present++;
                (int x, int y, int z) = SimulationFrameBuilder.MapSliceToLocal(
                    axis,
                    index,
                    u + slice.OriginU,
                    v + slice.OriginV);

                Assert.That(cell.Voxel.Temperature, Is.EqualTo(Code(x, y, z)), $"{axis} ({u}, {v})");
            }
        }

        (int width, int height) = axis switch
        {
            SliceAxis.X => (dimensions.Z, dimensions.Y),
            SliceAxis.Y => (dimensions.X, dimensions.Z),
            _ => (dimensions.X, dimensions.Y)
        };

        Assert.Multiple(() =>
        {
            Assert.That(slice.SliceIndex, Is.EqualTo(index));
            Assert.That(present, Is.EqualTo(width * height));
        });
    }

    [Test]
    public void BuildPlaneSlice_NegativeChunkPositions_PlaceTheOriginAtTheSmallestCoordinate()
    {
        var dimensions = new Int3(3, 2, 1);
        var frame = BuildFrame(dimensions, new Int3(-2, -1, 0), new Int3(-1, -1, 0), new Int3(-2, 0, 0));
        var slice = new SimulationFrameBuilder(new AtmosConfig()).BuildPlaneSlice(frame, SliceAxis.Z, 0);

        Assert.Multiple(() =>
        {
            Assert.That(slice.OriginU, Is.EqualTo(-6));
            Assert.That(slice.OriginV, Is.EqualTo(-2));
            Assert.That(slice.Width, Is.EqualTo(6));
            Assert.That(slice.Height, Is.EqualTo(4));
        });

        Assert.That(slice.TryGetCell(0, 0, out var corner), Is.True);
        Assert.That(corner.Address.Chunk.Position, Is.EqualTo(new Int3(-2, -1, 0)));
        Assert.That(corner.Voxel.Temperature, Is.EqualTo(Code(-6, -2, 0)));
        Assert.That(slice.TryGetCell(5, 1, out var seam), Is.True);
        Assert.That(seam.Address.Chunk.Position, Is.EqualTo(new Int3(-1, -1, 0)));
        Assert.That(seam.Voxel.Temperature, Is.EqualTo(Code(-1, -1, 0)));
        Assert.That(slice.TryGetCell(5, 3, out _), Is.False);

        var footprint = slice.GetFootprint(new Int3(-1, -1, 0));
        Assert.That(footprint, Is.EqualTo((3, 0, 3, 2)));
    }

    [Test]
    public void TryLocate_RoundTripsEveryCellAndRejectsOtherPlanes()
    {
        var dimensions = new Int3(2, 3, 2);
        var frame = BuildFrame(dimensions, new Int3(0, 0, 0), new Int3(1, 0, 0), new Int3(0, 0, 1));
        var builder = new SimulationFrameBuilder(new AtmosConfig());
        foreach (var axis in new[] { SliceAxis.X, SliceAxis.Y, SliceAxis.Z })
        {
            var slice = builder.BuildPlaneSlice(frame, axis, 1);
            for (int v = 0; v < slice.Height; v++)
            {
                for (int u = 0; u < slice.Width; u++)
                {
                    if (!slice.TryGetCell(u, v, out var cell))
                        continue;

                    Assert.That(slice.TryLocate(cell.Address, out int foundU, out int foundV), Is.True);
                    Assert.That((foundU, foundV), Is.EqualTo((u, v)), $"{axis}");
                }
            }
        }

        var zSlice = builder.BuildPlaneSlice(frame, SliceAxis.Z, 0);
        var chunk = frame.Chunks[new Int3(0, 0, 0)];
        var abovePlane = new VoxelAddress(chunk.Identity, chunk.GetLocalIndex(0, 0, 1));
        var staleGeneration = new VoxelAddress(
            new ChunkIdentity(chunk.Identity.Position, chunk.Identity.Generation + 1),
            0);

        Assert.Multiple(() =>
        {
            Assert.That(zSlice.TryLocate(new VoxelAddress(chunk.Identity, 0), out _, out _), Is.True);
            Assert.That(zSlice.TryLocate(abovePlane, out _, out _), Is.False);
            Assert.That(zSlice.TryLocate(staleGeneration, out _, out _), Is.False);
        });
    }

    [Test]
    public void TryPickNormalized_OnAPlane_ReturnsTheCellUnderThePointer()
    {
        var dimensions = new Int3(2, 2, 1);
        var frame = BuildFrame(dimensions, new Int3(0, 0, 0), new Int3(1, 0, 0), new Int3(0, 1, 0));
        var slice = new SimulationFrameBuilder(new AtmosConfig()).BuildPlaneSlice(frame, SliceAxis.Z, 0);
        var bounds = slice.GetViewBounds(1f);
        const int targetU = 3;
        const int targetV = 1;
        float normalizedX = (targetU + 0.5f - bounds.Left) / (bounds.Right - bounds.Left);
        float normalizedY = (targetV + 0.5f - bounds.Bottom) / (bounds.Top - bounds.Bottom);

        bool found = slice.TryPickNormalized(normalizedX, normalizedY, 1f, out var cell);

        Assert.Multiple(() =>
        {
            Assert.That(found, Is.True);
            Assert.That(cell.Address.Chunk.Position, Is.EqualTo(new Int3(1, 0, 0)));
            Assert.That(frame.TryResolve(cell.Address, out var voxel), Is.True);
            Assert.That(voxel.Temperature, Is.EqualTo(Code(3, 1, 0)));
        });

        float emptyX = (3 + 0.5f - bounds.Left) / (bounds.Right - bounds.Left);
        float emptyY = (3 + 0.5f - bounds.Bottom) / (bounds.Top - bounds.Bottom);
        Assert.That(slice.TryPickNormalized(emptyX, emptyY, 1f, out _), Is.False);
    }

    [Test]
    public void BuildPlaneSlice_FilteredVoxel_IsTransparentButStillPickable()
    {
        var snapshot = CreateSnapshot(new Int3(0, 0, 0), new Int3(2, 1, 1));
        snapshot.VoxelRoomMap[1] = VoxelClassification.RoomSolid;
        var builder = new SimulationFrameBuilder(new AtmosConfig());
        var frame = builder.BuildSimulation([snapshot], BuiltInVisualizationIds.Temperature, 1);

        var slice = builder.BuildPlaneSlice(frame, SliceAxis.Z, 0);

        Assert.Multiple(() =>
        {
            Assert.That(slice.VisibleCellCount, Is.EqualTo(1));
            Assert.That(slice.Pixels[4 + 3], Is.Zero);
            Assert.That(slice.TryGetCell(1, 0, out var cell), Is.True);
            Assert.That(cell.Voxel.RoomId, Is.EqualTo(VoxelClassification.RoomSolid));
        });
    }

    [Test]
    public void BuildPlaneSlice_OutOfRangeIndex_ClampsIntoTheFrameBounds()
    {
        var frame = BuildFrame(new Int3(2, 2, 2), new Int3(0, 0, -1), new Int3(0, 0, 0));
        var builder = new SimulationFrameBuilder(new AtmosConfig());

        Assert.Multiple(() =>
        {
            Assert.That(frame.Bounds.GetMinimum(SliceAxis.Z), Is.EqualTo(-2));
            Assert.That(frame.Bounds.GetMaximumExclusive(SliceAxis.Z), Is.EqualTo(2));
            Assert.That(builder.BuildPlaneSlice(frame, SliceAxis.Z, -99).SliceIndex, Is.EqualTo(-2));
            Assert.That(builder.BuildPlaneSlice(frame, SliceAxis.Z, 99).SliceIndex, Is.EqualTo(1));
        });
    }

    [Test]
    public void BuildPlaneSlice_EmptyFrame_ProducesAnEmptySlice()
    {
        var builder = new SimulationFrameBuilder(new AtmosConfig());
        var frame = builder.BuildSimulation([], BuiltInVisualizationIds.Temperature, 1);

        var slice = builder.BuildPlaneSlice(frame, SliceAxis.Z, 0);

        Assert.Multiple(() =>
        {
            Assert.That(slice.Width, Is.Zero);
            Assert.That(slice.Height, Is.Zero);
            Assert.That(slice.TryGetCell(0, 0, out _), Is.False);
        });
    }

    [Test]
    public void BuildPlaneSlice_WithPrevious_SharesPixelsUntilAChunkChangesColor()
    {
        var builder = new SimulationFrameBuilder(new AtmosConfig());
        var snapshots = CreateSnapshots(new Int3(2, 2, 1), new Int3(0, 0, 0), new Int3(1, 0, 0));
        var frame = builder.BuildSimulation(snapshots, BuiltInVisualizationIds.Temperature, 1);
        var first = builder.BuildPlaneSlice(frame, SliceAxis.Z, 0);

        var unchanged = builder.BuildPlaneSlice(frame, SliceAxis.Z, 0, first);

        var changedSnapshots = CreateSnapshots(new Int3(2, 2, 1), new Int3(0, 0, 0), new Int3(1, 0, 0));
        changedSnapshots[1].Version = new AtmosChunkVersion(1, 2);
        for (int index = 0; index < changedSnapshots[1].Temperature.Length; index++)
            changedSnapshots[1].Temperature[index] += 50f;

        var changedFrame = builder.BuildSimulation(
            changedSnapshots,
            BuiltInVisualizationIds.Temperature,
            2,
            frame);

        var changed = builder.BuildPlaneSlice(changedFrame, SliceAxis.Z, 0, unchanged);
        var otherIndex = builder.BuildPlaneSlice(changedFrame, SliceAxis.Y, 0, changed);

        Assert.Multiple(() =>
        {
            Assert.That(unchanged.RenderVersion, Is.EqualTo(first.RenderVersion));
            Assert.That(SharesPixels(unchanged, first), Is.True);
            Assert.That(changed.RenderVersion, Is.Not.EqualTo(first.RenderVersion));
            Assert.That(SharesPixels(changed, first), Is.False);
            Assert.That(otherIndex.RenderVersion, Is.Not.EqualTo(changed.RenderVersion));
        });

        Assert.That(changed.TryGetCell(2, 0, out var cell), Is.True);
        Assert.That(cell.Voxel.Temperature, Is.EqualTo(Code(2, 0, 0) + 50f));
    }

    [Test]
    public void BuildChunkSlice_UsesLocalCoordinatesFromTheChunkCorner()
    {
        var frame = BuildFrame(new Int3(2, 2, 2), new Int3(-1, 0, 0), new Int3(0, 0, 0));
        var builder = new SimulationFrameBuilder(new AtmosConfig());
        var chunk = frame.Chunks[new Int3(-1, 0, 0)];

        var slice = builder.BuildChunkSlice(frame, chunk.Identity, SliceAxis.Z, 1);

        Assert.That(slice.TryGetCell(1, 0, out var cell), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(slice.Width, Is.EqualTo(2));
            Assert.That(slice.SliceIndex, Is.EqualTo(1));
            Assert.That(slice.OriginU, Is.EqualTo(-2));
            Assert.That(cell.Voxel.Temperature, Is.EqualTo(Code(-1, 0, 1)));
            Assert.That(slice.TryGetCell(2, 0, out _), Is.False);
        });
    }

    private static bool SharesPixels(SimulationSliceDrawData left, SimulationSliceDrawData right)
    {
        return Unsafe.AreSame(
            in MemoryMarshal.GetReference(left.Pixels),
            in MemoryMarshal.GetReference(right.Pixels));
    }

    private static SimulationDrawData BuildFrame(Int3 dimensions, params Int3[] positions)
    {
        var builder = new SimulationFrameBuilder(new AtmosConfig());
        return builder.BuildSimulation(
            CreateSnapshots(dimensions, positions),
            BuiltInVisualizationIds.Temperature,
            1);
    }

    private static AtmosChunkSnapshot[] CreateSnapshots(Int3 dimensions, params Int3[] positions)
    {
        return positions.Select(position => CreateSnapshot(position, dimensions)).ToArray();
    }

    private static AtmosChunkSnapshot CreateSnapshot(Int3 position, Int3 dimensions)
    {
        int count = dimensions.X * dimensions.Y * dimensions.Z;
        var temperature = new float[count];
        for (int index = 0; index < count; index++)
        {
            int x = index % dimensions.X;
            int y = index / dimensions.X % dimensions.Y;
            int z = index / (dimensions.X * dimensions.Y);
            temperature[index] = Code(
                position.X * dimensions.X + x,
                position.Y * dimensions.Y + y,
                position.Z * dimensions.Z + z);
        }

        return new AtmosChunkSnapshot
        {
            Version = new AtmosChunkVersion(1, 1),
            GridPosition = position,
            Dimensions = dimensions,
            TotalPressure = Enumerable.Repeat(100f, count).ToArray(),
            Temperature = temperature,
            VoxelRoomMap = Enumerable.Repeat(1, count).ToArray(),
            Gases = []
        };
    }
}
