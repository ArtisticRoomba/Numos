using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.Maths;

namespace Numos.SimDrawer.Tests;

[TestFixture]
public sealed class SliceProjectionTests
{
    [TestCase(SliceAxis.X, 1, 2, 1, 1, 1, 2)]
    [TestCase(SliceAxis.Y, 1, 1, 2, 1, 1, 2)]
    [TestCase(SliceAxis.Z, 2, 1, 1, 1, 1, 2)]
    public void BuildChunkSlice_AllAxes_MapsUvBackToExpectedLocalCoordinates(
        SliceAxis axis,
        int sliceIndex,
        int u,
        int v,
        int expectedX,
        int expectedY,
        int expectedZ)
    {
        var builder = new SimulationFrameBuilder(new AtmosConfig());
        var snapshot = CreateSnapshot(new Int3(2, -1, 3), new Int3(2, 3, 4));
        var frame = builder.BuildSimulation(
            [snapshot],
            BuiltInVisualizationIds.Temperature,
            1);

        var chunk = frame.Chunks[snapshot.GridPosition];

        var slice = builder.BuildChunkSlice(frame, chunk.Identity, axis, sliceIndex);
        bool found = slice.TryGetCell(u, v, out var cell);
        var coordinates = chunk.GetCoordinates(cell.Address.LocalIndex);

        Assert.Multiple(() =>
        {
            Assert.That(found, Is.True);
            Assert.That(coordinates, Is.EqualTo(new Int3(expectedX, expectedY, expectedZ)));
        });
    }

    [Test]
    public void TryPickNormalized_CellCenter_ReturnsCellInConstantCoordinateLookup()
    {
        var builder = new SimulationFrameBuilder(new AtmosConfig());
        var snapshot = CreateSnapshot(new Int3(0, 0, 0), new Int3(4, 3, 1));
        var frame = builder.BuildSimulation(
            [snapshot],
            BuiltInVisualizationIds.Temperature,
            1);

        var chunk = frame.Chunks[snapshot.GridPosition];
        var slice = builder.BuildChunkSlice(frame, chunk.Identity, SliceAxis.Z, 0);
        var bounds = slice.GetViewBounds(1f);
        const int targetU = 2;
        const int targetV = 1;
        float normalizedX = (targetU + 0.5f - bounds.Left) / (bounds.Right - bounds.Left);
        float normalizedY = (targetV + 0.5f - bounds.Bottom) / (bounds.Top - bounds.Bottom);

        bool found = slice.TryPickNormalized(normalizedX, normalizedY, 1f, out var cell);

        Assert.Multiple(() =>
        {
            Assert.That(found, Is.True);
            Assert.That(cell.U, Is.EqualTo(targetU));
            Assert.That(cell.V, Is.EqualTo(targetV));
            Assert.That(chunk.GetCoordinates(cell.Address.LocalIndex), Is.EqualTo(new Int3(2, 1, 0)));
        });
    }

    [Test]
    public void TryPickNormalized_LetterboxMargin_ReturnsFalse()
    {
        var builder = new SimulationFrameBuilder(new AtmosConfig());
        var snapshot = CreateSnapshot(new Int3(0, 0, 0), new Int3(2, 2, 1));
        var frame = builder.BuildSimulation(
            [snapshot],
            BuiltInVisualizationIds.Temperature,
            1);

        var chunk = frame.Chunks[snapshot.GridPosition];
        var slice = builder.BuildChunkSlice(frame, chunk.Identity, SliceAxis.Z, 0);

        Assert.That(slice.TryPickNormalized(0f, 0f, 1f, out _), Is.False);
    }

    [Test]
    public void SourceRevisionChanges_WithSameMappedColor_RebuildsSemanticSliceButKeepsRenderVersion()
    {
        var builder = new SimulationFrameBuilder(new AtmosConfig());
        var firstSnapshot = CreateSnapshot(new Int3(0, 0, 0), new Int3(1, 1, 1));
        firstSnapshot.Version = new AtmosChunkVersion(1, 1);
        firstSnapshot.Temperature[0] = 400f;
        var firstFrame = builder.BuildSimulation(
            [firstSnapshot],
            BuiltInVisualizationIds.Temperature,
            1);

        var firstSlice = builder.BuildChunkSlice(
            firstFrame,
            firstFrame.Chunks.Values.Single().Identity,
            SliceAxis.Z,
            0);

        var secondSnapshot = CreateSnapshot(new Int3(0, 0, 0), new Int3(1, 1, 1));
        secondSnapshot.Version = new AtmosChunkVersion(1, 2);
        secondSnapshot.Temperature[0] = 500f;
        var secondFrame = builder.BuildSimulation(
            [secondSnapshot],
            BuiltInVisualizationIds.Temperature,
            2,
            firstFrame);

        var secondSlice = builder.BuildChunkSlice(
            secondFrame,
            secondFrame.Chunks.Values.Single().Identity,
            SliceAxis.Z,
            0);

        Assert.Multiple(() =>
        {
            Assert.That(secondSlice.Cells[0].Voxel.Temperature, Is.EqualTo(500f));
            Assert.That(secondSlice.RenderVersion, Is.EqualTo(firstSlice.RenderVersion));
        });
    }

    private static AtmosChunkSnapshot CreateSnapshot(Int3 position, Int3 dimensions)
    {
        int count = dimensions.X * dimensions.Y * dimensions.Z;
        return new AtmosChunkSnapshot
        {
            Version = new AtmosChunkVersion(1, 1),
            GridPosition = position,
            Dimensions = dimensions,
            TotalPressure = Enumerable.Repeat(100f, count).ToArray(),
            Temperature = Enumerable.Repeat(293.15f, count).ToArray(),
            VoxelRoomMap = Enumerable.Repeat(1, count).ToArray(),
            Gases = []
        };
    }
}