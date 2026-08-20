using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.Maths;

namespace Numos.SimDrawer.Tests;

[TestFixture]
public sealed class SimulationFrameBuilderTests
{
    [Test]
    public void BuildSimulation_TwoChunksIncludingEmpty_PreservesBothChunks()
    {
        var builder = CreateBuilder();
        var visible = CreateSnapshot(new Int3(0, 0, 0), new Int3(1, 1, 1));
        var empty = CreateSnapshot(
            new Int3(1, 0, 0),
            new Int3(1, 1, 1),
            rooms: [VoxelClassification.RoomVoid]);

        var frame = builder.BuildSimulation(
            [visible, empty],
            BuiltInVisualizationIds.Temperature,
            1);

        Assert.Multiple(() =>
        {
            Assert.That(frame.Chunks, Has.Count.EqualTo(2));
            Assert.That(frame.Chunks[new Int3(0, 0, 0)].VisibleCellCount, Is.EqualTo(1));
            Assert.That(frame.Chunks[new Int3(1, 0, 0)].VisibleCellCount, Is.Zero);
        });
    }

    [Test]
    public void TemperatureVisualization_MinimalSnapshot_DoesNotRequirePressureOrGasCopies()
    {
        var builder = CreateBuilder();
        var snapshot = CreateSnapshot(new Int3(0, 0, 0), new Int3(1, 1, 1));
        snapshot.Fields =
            AtmosChunkSnapshotFields.Temperature |
            AtmosChunkSnapshotFields.VoxelClassification |
            AtmosChunkSnapshotFields.VoxelSnapping;
        snapshot.TotalPressure = [];
        snapshot.Gases = [];

        var chunk = builder.BuildSimulation(
            [snapshot],
            BuiltInVisualizationIds.Temperature,
            1).Chunks[snapshot.GridPosition];

        Assert.Multiple(() =>
        {
            Assert.That(builder.GetRequiredSnapshotFields(BuiltInVisualizationIds.Temperature),
                Is.EqualTo(snapshot.Fields));
            Assert.That(chunk.GetCell(0).IsVisible, Is.True);
            Assert.That(float.IsNaN(chunk.GetCell(0).Pressure), Is.True);
        });
    }

    [Test]
    public void BuildSimulation_TwoAdjacentCells_OmitsTheirSharedFaces()
    {
        var builder = CreateBuilder();
        var snapshot = CreateSnapshot(
            new Int3(0, 0, 0),
            new Int3(2, 1, 1));

        var chunk = builder.BuildSimulation(
            [snapshot],
            BuiltInVisualizationIds.Temperature,
            1).Chunks[snapshot.GridPosition];

        Assert.Multiple(() =>
        {
            Assert.That(chunk.SurfaceFaceCount, Is.EqualTo(10));
            Assert.That(
                chunk.GetCell(0).VisibleFaces & VoxelFaceMask.PositiveX,
                Is.EqualTo(VoxelFaceMask.None));
            Assert.That(
                chunk.GetCell(1).VisibleFaces & VoxelFaceMask.NegativeX,
                Is.EqualTo(VoxelFaceMask.None));
        });
    }

    [Test]
    public void BuildSimulation_AwakeChunk_MapsDistinctAggregateGroupsToStableDistinctColors()
    {
        var builder = CreateBuilder();
        var snapshot = CreateSnapshot(
            new Int3(0, 0, 0),
            new Int3(5, 1, 1),
            voxelSnapGroupMap: [0, 0, 2, 2, -1]);

        var firstChunk = builder.BuildSimulation(
            [snapshot],
            BuiltInVisualizationIds.Temperature,
            1).Chunks[snapshot.GridPosition];
        var secondChunk = builder.BuildSimulation(
            [snapshot],
            BuiltInVisualizationIds.Temperature,
            2).Chunks[snapshot.GridPosition];

        Assert.Multiple(() =>
        {
            Assert.That(firstChunk.GetCell(0).StateMarker, Is.EqualTo(VoxelStateMarker.Snapped));
            Assert.That(firstChunk.GetCell(1).StateMarker, Is.EqualTo(VoxelStateMarker.Snapped));
            Assert.That(firstChunk.GetCell(2).StateMarker, Is.EqualTo(VoxelStateMarker.Snapped));
            Assert.That(firstChunk.GetCell(3).StateMarker, Is.EqualTo(VoxelStateMarker.Snapped));
            Assert.That(firstChunk.GetCell(4).StateMarker, Is.EqualTo(VoxelStateMarker.None));
            Assert.That(firstChunk.GetCell(0).SnapGroupId, Is.EqualTo(0));
            Assert.That(firstChunk.GetCell(2).SnapGroupId, Is.EqualTo(2));
            Assert.That(firstChunk.GetCell(4).SnapGroupId, Is.EqualTo(-1));
            Assert.That(firstChunk.GetCell(1).StateMarkerColor,
                Is.EqualTo(firstChunk.GetCell(0).StateMarkerColor));
            Assert.That(firstChunk.GetCell(3).StateMarkerColor,
                Is.EqualTo(firstChunk.GetCell(2).StateMarkerColor));
            Assert.That(firstChunk.GetCell(2).StateMarkerColor,
                Is.Not.EqualTo(firstChunk.GetCell(0).StateMarkerColor));
            Assert.That(secondChunk.GetCell(0).StateMarkerColor,
                Is.EqualTo(firstChunk.GetCell(0).StateMarkerColor));
            Assert.That(secondChunk.GetCell(2).StateMarkerColor,
                Is.EqualTo(firstChunk.GetCell(2).StateMarkerColor));
        });
    }

    [Test]
    public void BuildSimulation_DistantAggregateRootsRemainDistinctAfterDisplayQuantization()
    {
        const int voxelCount = 622;
        int[] groupMap = Enumerable.Repeat(-1, voxelCount).ToArray();
        groupMap[10] = 10;
        groupMap[11] = 10;
        groupMap[620] = 620;
        groupMap[621] = 620;
        var snapshot = CreateSnapshot(
            new Int3(0, 0, 0),
            new Int3(voxelCount, 1, 1),
            voxelSnapGroupMap: groupMap);

        var chunk = CreateBuilder().BuildSimulation(
            [snapshot],
            BuiltInVisualizationIds.Temperature,
            1).Chunks[snapshot.GridPosition];

        Assert.That(
            QuantizeDisplayColor(chunk.GetCell(10).StateMarkerColor),
            Is.Not.EqualTo(QuantizeDisplayColor(chunk.GetCell(620).StateMarkerColor)),
            "Every local aggregate group must retain its own color after the viewer converts it to RGB bytes.");
    }

    [Test]
    public void BuildSimulation_SleepingChunk_OverridesSnapMapAndMarksEveryVoxel()
    {
        var builder = CreateBuilder();
        var snapshot = CreateSnapshot(
            new Int3(0, 0, 0),
            new Int3(3, 1, 1),
            rooms:
            [
                1,
                VoxelClassification.RoomSolid,
                VoxelClassification.RoomVoid
            ],
            voxelSnapGroupMap: [0, -1, 0],
            isAwake: false);

        var frame = builder.BuildSimulation(
            [snapshot],
            BuiltInVisualizationIds.Temperature,
            1);
        var chunk = frame.Chunks[snapshot.GridPosition];
        var slice = builder.BuildChunkSlice(frame, chunk.Identity, SliceAxis.Z, 0);

        Assert.Multiple(() =>
        {
            Assert.That(chunk.Cells.ToArray().Select(cell => cell.StateMarker),
                Is.All.EqualTo(VoxelStateMarker.Sleeping));
            Assert.That(chunk.Cells.ToArray().Select(cell => cell.StateMarkerColor),
                Is.All.EqualTo(new ColorRgba(1f, 0.25f, 0.2f)));
            Assert.That(chunk.GetCell(0).SnapGroupId, Is.EqualTo(0),
                "Sleeping presentation must retain diagnostic group identity while overriding its marker color.");
            Assert.That(chunk.VisibleCellCount, Is.EqualTo(1));
            Assert.That(slice.Cells.Length, Is.EqualTo(3),
                "Sleeping markers must remain present in a slice even where its visualization hides the voxel.");
            Assert.That(slice.TryGetCell(1, 0, out var solid), Is.True);
            Assert.That(solid.Voxel.IsVisible, Is.False);
            Assert.That(solid.Voxel.StateMarker, Is.EqualTo(VoxelStateMarker.Sleeping));
            Assert.That(slice.TryGetCell(2, 0, out var voidCell), Is.True);
            Assert.That(voidCell.Voxel.IsVisible, Is.False);
            Assert.That(voidCell.Voxel.StateMarker, Is.EqualTo(VoxelStateMarker.Sleeping));
        });
    }

    [Test]
    public void BuildSimulation_SnappedToSleepingMarker_ChangesStyleAndSliceRenderVersion()
    {
        var builder = CreateBuilder();
        var snappedSnapshot = CreateSnapshot(
            new Int3(0, 0, 0),
            new Int3(1, 1, 1),
            voxelSnapGroupMap: [0]);
        var sleepingSnapshot = snappedSnapshot;
        sleepingSnapshot.IsAwake = false;

        var snappedFrame = builder.BuildSimulation(
            [snappedSnapshot],
            BuiltInVisualizationIds.Temperature,
            1);
        var sleepingFrame = builder.BuildSimulation(
            [sleepingSnapshot],
            BuiltInVisualizationIds.Temperature,
            2);
        var snappedChunk = snappedFrame.Chunks.Values.Single();
        var sleepingChunk = sleepingFrame.Chunks.Values.Single();
        var snappedSlice = builder.BuildChunkSlice(snappedFrame, snappedChunk.Identity, SliceAxis.Z, 0);
        var sleepingSlice = builder.BuildChunkSlice(sleepingFrame, sleepingChunk.Identity, SliceAxis.Z, 0);

        Assert.Multiple(() =>
        {
            Assert.That(snappedChunk.GetCell(0).StateMarker, Is.EqualTo(VoxelStateMarker.Snapped));
            Assert.That(sleepingChunk.GetCell(0).StateMarker, Is.EqualTo(VoxelStateMarker.Sleeping));
            Assert.That(sleepingChunk.TopologyVersion, Is.EqualTo(snappedChunk.TopologyVersion));
            Assert.That(sleepingChunk.StyleVersion, Is.Not.EqualTo(snappedChunk.StyleVersion));
            Assert.That(sleepingSlice.RenderVersion, Is.Not.EqualTo(snappedSlice.RenderVersion));
        });
    }

    [Test]
    public void BuildSimulation_SnapGroupsMerge_ChangesStyleAndSliceRenderVersion()
    {
        var builder = CreateBuilder();
        var separateSnapshot = CreateSnapshot(
            new Int3(0, 0, 0),
            new Int3(4, 1, 1),
            voxelSnapGroupMap: [0, 0, 2, 2]);
        var mergedSnapshot = separateSnapshot;
        mergedSnapshot.VoxelSnapGroupMap = [0, 0, 0, 0];

        var separateFrame = builder.BuildSimulation(
            [separateSnapshot],
            BuiltInVisualizationIds.Temperature,
            1);
        var mergedFrame = builder.BuildSimulation(
            [mergedSnapshot],
            BuiltInVisualizationIds.Temperature,
            2);
        var separateChunk = separateFrame.Chunks.Values.Single();
        var mergedChunk = mergedFrame.Chunks.Values.Single();
        var separateSlice = builder.BuildChunkSlice(separateFrame, separateChunk.Identity, SliceAxis.Z, 0);
        var mergedSlice = builder.BuildChunkSlice(mergedFrame, mergedChunk.Identity, SliceAxis.Z, 0);

        Assert.Multiple(() =>
        {
            Assert.That(separateChunk.GetCell(2).StateMarkerColor,
                Is.Not.EqualTo(separateChunk.GetCell(0).StateMarkerColor));
            Assert.That(mergedChunk.GetCell(2).StateMarkerColor,
                Is.EqualTo(mergedChunk.GetCell(0).StateMarkerColor));
            Assert.That(mergedChunk.TopologyVersion, Is.EqualTo(separateChunk.TopologyVersion));
            Assert.That(mergedChunk.StyleVersion, Is.Not.EqualTo(separateChunk.StyleVersion),
                "A group-only color change must invalidate the cached slice style.");
            Assert.That(mergedSlice.RenderVersion, Is.Not.EqualTo(separateSlice.RenderVersion));
        });
    }

    [Test]
    public void BuildSimulation_AdjacentChunks_RetainsSelfContainedBoundaryFacesForFocus()
    {
        var builder = CreateBuilder();
        var first = CreateSnapshot(new Int3(0, 0, 0), new Int3(1, 1, 1));
        var second = CreateSnapshot(new Int3(1, 0, 0), new Int3(1, 1, 1));

        var frame = builder.BuildSimulation(
            [first, second],
            BuiltInVisualizationIds.Temperature,
            1);

        Assert.Multiple(() =>
        {
            Assert.That(frame.Chunks[first.GridPosition].SurfaceFaceCount, Is.EqualTo(6));
            Assert.That(frame.Chunks[second.GridPosition].SurfaceFaceCount, Is.EqualTo(6));
        });
    }

    [Test]
    public void BuildSimulation_MalformedSnapshot_ThrowsInsteadOfSilentlyCreatingEmptyChunk()
    {
        var builder = CreateBuilder();

        Assert.That(
            () => builder.BuildSimulation([default], BuiltInVisualizationIds.Temperature, 1),
            Throws.ArgumentException);
    }

    [Test]
    public void BuildSimulation_SolidThreeCubedVolume_InteriorCellHasNoFaces()
    {
        var builder = CreateBuilder();
        var snapshot = CreateSnapshot(
            new Int3(0, 0, 0),
            new Int3(3, 3, 3));

        var chunk = builder.BuildSimulation(
            [snapshot],
            BuiltInVisualizationIds.Temperature,
            1).Chunks[snapshot.GridPosition];

        ushort center = chunk.GetLocalIndex(1, 1, 1);
        Assert.Multiple(() =>
        {
            Assert.That(chunk.SurfaceFaceCount, Is.EqualTo(54));
            Assert.That(chunk.GetCell(center).VisibleFaces, Is.EqualTo(VoxelFaceMask.None));
        });
    }

    [Test]
    public void ActiveOnly_AtOrBelowVacuumThreshold_FiltersCellsInsteadOfDarkeningThem()
    {
        var config = new AtmosConfig { VacuumThreshold = 1f };
        var builder = new SimulationFrameBuilder(config);
        var snapshot = CreateSnapshot(
            new Int3(0, 0, 0),
            new Int3(3, 1, 1),
            [0.5f, 1f, 1.01f]);

        var chunk = builder.BuildSimulation(
            [snapshot],
            BuiltInVisualizationIds.ActiveOnly,
            1).Chunks[snapshot.GridPosition];

        Assert.Multiple(() =>
        {
            Assert.That(chunk.GetCell(0).IsVisible, Is.False);
            Assert.That(chunk.GetCell(1).IsVisible, Is.False);
            Assert.That(chunk.GetCell(2).IsVisible, Is.True);
            Assert.That(chunk.VisibleCellCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void GasComposition_ReorderedChannels_UsesActualGasIdAndStableColor()
    {
        var builder = CreateBuilder();
        var gasSeven = new GasSnapshot { GasId = 7, Moles = [2f] };
        var gasTwo = new GasSnapshot { GasId = 2, Moles = [1f] };
        var first = CreateSnapshot(
            new Int3(0, 0, 0),
            new Int3(1, 1, 1),
            gases: [gasSeven, gasTwo]);
        var second = CreateSnapshot(
            new Int3(1, 0, 0),
            new Int3(1, 1, 1),
            gases: [gasTwo, gasSeven]);

        var frame = builder.BuildSimulation(
            [first, second],
            BuiltInVisualizationIds.GasComposition,
            1);
        var firstCell = frame.Chunks[first.GridPosition].GetCell(0);
        var secondCell = frame.Chunks[second.GridPosition].GetCell(0);

        Assert.Multiple(() =>
        {
            Assert.That(firstCell.PrimaryGasId, Is.EqualTo(7));
            Assert.That(secondCell.PrimaryGasId, Is.EqualTo(7));
            Assert.That(secondCell.Color, Is.EqualTo(firstCell.Color));
            Assert.That(frame.Visualization.Legend.Entries.Any(entry => entry.Label == "Gas 7"), Is.True);
        });
    }

    [Test]
    public void RegisterCustomVisualization_BuildsWithoutChangingFrameBuilder()
    {
        var config = new AtmosConfig();
        var registry = VisualizationRegistry.CreateDefault(config);
        registry.Register(new RoomVisualization());
        var builder = new SimulationFrameBuilder(config, registry);

        var frame = builder.BuildSimulation(
            [CreateSnapshot(new Int3(0, 0, 0), new Int3(1, 1, 1), rooms: [42])],
            RoomVisualization.VisualizationId,
            1);

        Assert.Multiple(() =>
        {
            Assert.That(frame.Visualization.Id, Is.EqualTo(RoomVisualization.VisualizationId));
            Assert.That(frame.Visualization.Legend.Title, Is.EqualTo("Room"));
            Assert.That(frame.Chunks.Values.Single().GetCell(0).Color, Is.EqualTo(RoomVisualization.RoomColor));
        });
    }

    [Test]
    public void CustomVisualization_CanOptIntoSolidAndVoidTopology()
    {
        var config = new AtmosConfig();
        var registry = VisualizationRegistry.CreateDefault(config);
        registry.Register(new TopologyVisualization());
        var builder = new SimulationFrameBuilder(config, registry);
        var snapshot = CreateSnapshot(
            default,
            new Int3(2, 1, 1),
            rooms: [VoxelClassification.RoomSolid, VoxelClassification.RoomVoid]);

        var chunk = builder.BuildSimulation(
            [snapshot],
            TopologyVisualization.VisualizationId,
            1).Chunks.Values.Single();

        Assert.That(chunk.VisibleCellCount, Is.EqualTo(2));
    }

    [Test]
    public void BuildSimulation_SourceArraysMutatedAfterBuild_DoesNotChangePresentation()
    {
        var builder = CreateBuilder();
        var snapshot = CreateSnapshot(
            new Int3(0, 0, 0),
            new Int3(1, 1, 1),
            [12f],
            [250f]);
        var frame = builder.BuildSimulation(
            [snapshot],
            BuiltInVisualizationIds.Temperature,
            1);

        snapshot.TotalPressure[0] = 999f;
        snapshot.Temperature[0] = 999f;
        snapshot.VoxelRoomMap[0] = VoxelClassification.RoomVoid;

        var cell = frame.Chunks.Values.Single().GetCell(0);
        Assert.Multiple(() =>
        {
            Assert.That(cell.Pressure, Is.EqualTo(12f));
            Assert.That(cell.Temperature, Is.EqualTo(250f));
            Assert.That(cell.IsVisible, Is.True);
        });
    }

    [Test]
    public void BuildSimulation_UnchangedVersionAndMode_ReusesImmutableChunk()
    {
        var builder = CreateBuilder();
        var snapshot = CreateSnapshot(
            new Int3(0, 0, 0),
            new Int3(1, 1, 1),
            version: new AtmosChunkVersion(10, 20));
        var first = builder.BuildSimulation(
            [snapshot],
            BuiltInVisualizationIds.Temperature,
            1);
        var second = builder.BuildSimulation(
            [snapshot],
            BuiltInVisualizationIds.Temperature,
            2,
            first);

        Assert.That(second.Chunks[snapshot.GridPosition], Is.SameAs(first.Chunks[snapshot.GridPosition]));
    }

    [Test]
    public void BuildSimulation_FocusedScope_ModeChangeRemapsOnlyFocusedChunkUntilScopeExpands()
    {
        var builder = CreateBuilder();
        var focusedTemperature = CreateSnapshot(
            new Int3(0, 0, 0),
            new Int3(1, 1, 1),
            version: new AtmosChunkVersion(10, 1));
        focusedTemperature.Fields =
            AtmosChunkSnapshotFields.Temperature | AtmosChunkSnapshotFields.VoxelClassification;
        focusedTemperature.HasExplicitFields = true;
        focusedTemperature.TotalPressure = [];
        focusedTemperature.Gases = [];

        var hiddenTemperature = CreateSnapshot(
            new Int3(1, 0, 0),
            new Int3(1, 1, 1),
            version: new AtmosChunkVersion(11, 1));
        hiddenTemperature.Fields = focusedTemperature.Fields;
        hiddenTemperature.HasExplicitFields = true;
        hiddenTemperature.TotalPressure = [];
        hiddenTemperature.Gases = [];

        var focusedPressureSnapshot = focusedTemperature;
        focusedPressureSnapshot.Fields =
            AtmosChunkSnapshotFields.Pressure | AtmosChunkSnapshotFields.VoxelClassification;
        focusedPressureSnapshot.Temperature = [];
        focusedPressureSnapshot.TotalPressure = [125f];

        var temperature = builder.BuildSimulation(
            [focusedTemperature, hiddenTemperature],
            BuiltInVisualizationIds.Temperature,
            1);

        var focusedPressureFrame = builder.BuildSimulation(
            [focusedPressureSnapshot, hiddenTemperature],
            BuiltInVisualizationIds.Pressure,
            2,
            temperature,
            new HashSet<Int3> { focusedPressureSnapshot.GridPosition });

        Assert.Multiple(() =>
        {
            Assert.That(focusedPressureFrame.Chunks[focusedPressureSnapshot.GridPosition],
                Is.Not.SameAs(temperature.Chunks[focusedPressureSnapshot.GridPosition]));
            Assert.That(focusedPressureFrame.Chunks[focusedPressureSnapshot.GridPosition].VisualizationId,
                Is.EqualTo(BuiltInVisualizationIds.Pressure));
            Assert.That(focusedPressureFrame.HasCurrentVisualizationMapping(
                focusedPressureFrame.Chunks[focusedPressureSnapshot.GridPosition]), Is.True);
            Assert.That(focusedPressureFrame.Chunks[hiddenTemperature.GridPosition],
                Is.SameAs(temperature.Chunks[hiddenTemperature.GridPosition]));
            Assert.That(focusedPressureFrame.Chunks[hiddenTemperature.GridPosition].VisualizationId,
                Is.EqualTo(BuiltInVisualizationIds.Temperature));
            Assert.That(focusedPressureFrame.HasCurrentVisualizationMapping(
                focusedPressureFrame.Chunks[hiddenTemperature.GridPosition]), Is.False);
        });

        var hiddenPressure = hiddenTemperature;
        hiddenPressure.Fields = focusedPressureSnapshot.Fields;
        hiddenPressure.Temperature = [];
        hiddenPressure.TotalPressure = [250f];

        var caughtUp = builder.BuildSimulation(
            [focusedPressureSnapshot, hiddenPressure],
            BuiltInVisualizationIds.Pressure,
            3,
            focusedPressureFrame);

        Assert.Multiple(() =>
        {
            Assert.That(caughtUp.Chunks[hiddenPressure.GridPosition],
                Is.Not.SameAs(focusedPressureFrame.Chunks[hiddenPressure.GridPosition]));
            Assert.That(caughtUp.Chunks[hiddenPressure.GridPosition].VisualizationId,
                Is.EqualTo(BuiltInVisualizationIds.Pressure));
            Assert.That(caughtUp.Chunks.Values.All(caughtUp.HasCurrentVisualizationMapping), Is.True);
        });
    }

    [Test]
    public void BuildSimulation_FirstScopedFrame_OmitsOutOfScopeChunkUntilItHasRetainedState()
    {
        var builder = CreateBuilder();
        var focused = CreateSnapshot(
            new Int3(0, 0, 0),
            new Int3(1, 1, 1),
            version: new AtmosChunkVersion(10, 1));
        var hidden = CreateSnapshot(
            new Int3(1, 0, 0),
            new Int3(1, 1, 1),
            version: new AtmosChunkVersion(11, 1));

        var frame = builder.BuildSimulation(
            [focused, hidden],
            BuiltInVisualizationIds.Temperature,
            1,
            mappingScope: new HashSet<Int3> { focused.GridPosition });

        Assert.That(frame.Chunks.Keys, Is.EqualTo(new[] { focused.GridPosition }));
    }

    [Test]
    public void BuildSimulation_ModeChange_ChangesStyleWithoutChangingSurfaceTopology()
    {
        var builder = CreateBuilder();
        var snapshot = CreateSnapshot(
            new Int3(0, 0, 0),
            new Int3(2, 1, 1),
            [10f, 200f],
            [100f, 300f]);
        var temperature = builder.BuildSimulation(
            [snapshot],
            BuiltInVisualizationIds.Temperature,
            1).Chunks[snapshot.GridPosition];
        var pressure = builder.BuildSimulation(
            [snapshot],
            BuiltInVisualizationIds.Pressure,
            1).Chunks[snapshot.GridPosition];

        Assert.Multiple(() =>
        {
            Assert.That(pressure.TopologyVersion, Is.EqualTo(temperature.TopologyVersion));
            Assert.That(pressure.StyleVersion, Is.Not.EqualTo(temperature.StyleVersion));
        });
    }

    [Test]
    public void BuildSimulation_VisualizationSettingRevision_InvalidatesReusedChunk()
    {
        var config = new AtmosConfig { VacuumThreshold = 1f };
        var builder = new SimulationFrameBuilder(config);
        var snapshot = CreateSnapshot(
            new Int3(0, 0, 0),
            new Int3(1, 1, 1),
            [2f],
            version: new AtmosChunkVersion(3, 7));
        var first = builder.BuildSimulation(
            [snapshot],
            BuiltInVisualizationIds.ActiveOnly,
            1);

        config.VacuumThreshold = 3f;
        var second = builder.BuildSimulation(
            [snapshot],
            BuiltInVisualizationIds.ActiveOnly,
            1,
            first);

        Assert.Multiple(() =>
        {
            Assert.That(second.Chunks[snapshot.GridPosition], Is.Not.SameAs(first.Chunks[snapshot.GridPosition]));
            Assert.That(first.Chunks[snapshot.GridPosition].GetCell(0).IsVisible, Is.True);
            Assert.That(second.Chunks[snapshot.GridPosition].GetCell(0).IsVisible, Is.False);
        });
    }

    [Test]
    public void GasComposition_GasRegistryNameChange_AdvancesLegendRevision()
    {
        var config = new AtmosConfig();
        config.GasRegistry.Add(new GasProperties { Name = "Before" });
        var registry = VisualizationRegistry.CreateDefault(config);
        var visualization = registry.GetRequired(BuiltInVisualizationIds.GasComposition);
        ulong before = visualization.MappingRevision;

        config.GasRegistry[0] = new GasProperties { Name = "After" };

        Assert.That(visualization.MappingRevision, Is.Not.EqualTo(before));
    }

    [Test]
    public void BuildSimulation_TransparentCustomSurfaceColor_IsNormalizedToOpaque()
    {
        var config = new AtmosConfig();
        var registry = VisualizationRegistry.CreateDefault(config);
        registry.Register(new TransparentVisualization());
        var builder = new SimulationFrameBuilder(config, registry);

        var cell = builder.BuildSimulation(
                [CreateSnapshot(new Int3(0, 0, 0), new Int3(1, 1, 1))],
                TransparentVisualization.VisualizationId,
                1)
            .Chunks.Values.Single()
            .GetCell(0);

        Assert.That(cell.Color, Is.EqualTo(new ColorRgba(1f, 0f, 0.5f)));
    }

    private static SimulationFrameBuilder CreateBuilder()
    {
        return new SimulationFrameBuilder(new AtmosConfig());
    }

    private static int QuantizeDisplayColor(ColorRgba color)
    {
        static byte ToByte(float value) => (byte)(Math.Clamp(value, 0f, 1f) * byte.MaxValue);

        return ToByte(color.R) << 16 |
               ToByte(color.G) << 8 |
               ToByte(color.B);
    }

    private static AtmosChunkSnapshot CreateSnapshot(
        Int3 position,
        Int3 dimensions,
        float[]? pressure = null,
        float[]? temperature = null,
        int[]? rooms = null,
        GasSnapshot[]? gases = null,
        AtmosChunkVersion version = default,
        int[]? voxelSnapGroupMap = null,
        bool isAwake = true)
    {
        int count = dimensions.X * dimensions.Y * dimensions.Z;
        return new AtmosChunkSnapshot
        {
            Version = version,
            GridPosition = position,
            Dimensions = dimensions,
            TotalPressure = pressure ?? Enumerable.Repeat(100f, count).ToArray(),
            Temperature = temperature ?? Enumerable.Repeat(293.15f, count).ToArray(),
            VoxelRoomMap = rooms ?? Enumerable.Repeat(1, count).ToArray(),
            VoxelSnapGroupMap = voxelSnapGroupMap ?? Enumerable.Repeat(-1, count).ToArray(),
            Gases = gases ?? [],
            ActiveAirCount = count,
            ActiveGasCount = gases?.Length ?? 0,
            IsAwake = isAwake
        };
    }

    private sealed class RoomVisualization : IVisualizationMethod
    {
        public const string VisualizationId = "test-room";
        public readonly static ColorRgba RoomColor = new(0.25f, 0.5f, 0.75f);

        public string Id => VisualizationId;

        public string DisplayName => "Room";

        public bool TryGetColor(in VoxelSample sample, out ColorRgba color)
        {
            color = RoomColor;
            return sample.RoomId > 0;
        }

        public VisualizationLegend CreateLegend(IReadOnlyCollection<int> activeGasIds)
        {
            return new VisualizationLegend(
                "Room",
                "",
                VisualizationLegendKind.Categories,
                [new VisualizationLegendEntry("Room", RoomColor)]);
        }
    }

    private sealed class TransparentVisualization : IVisualizationMethod
    {
        public const string VisualizationId = "test-transparent";

        public string Id => VisualizationId;

        public string DisplayName => "Transparent";

        public bool TryGetColor(in VoxelSample sample, out ColorRgba color)
        {
            color = new ColorRgba(2f, -1f, 0.5f, 0.25f);
            return true;
        }

        public VisualizationLegend CreateLegend(IReadOnlyCollection<int> activeGasIds)
        {
            return new VisualizationLegend("Transparent", "", VisualizationLegendKind.Categories, []);
        }
    }

    private sealed class TopologyVisualization : IVisualizationMethod
    {
        public const string VisualizationId = "test-topology";

        public string Id => VisualizationId;

        public string DisplayName => "Topology";

        public VisualizationCellDomain CellDomain => VisualizationCellDomain.AllCells;

        public VisualizationDataRequirements RequiredData => VisualizationDataRequirements.None;

        public bool TryGetColor(in VoxelSample sample, out ColorRgba color)
        {
            color = sample.RoomId == VoxelClassification.RoomSolid
                ? new ColorRgba(0.4f, 0.4f, 0.4f)
                : new ColorRgba(0.1f, 0.1f, 0.1f);
            return true;
        }

        public VisualizationLegend CreateLegend(IReadOnlyCollection<int> activeGasIds)
        {
            return new VisualizationLegend("Topology", "", VisualizationLegendKind.Categories, []);
        }
    }
}
