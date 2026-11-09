using System.Collections.Immutable;
using Numos.API;
using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.Maths;
using Numos.SimDrawer;
using Numos.Viewer.Rendering;

namespace Numos.Viewer.Tests;

[TestFixture]
public sealed class DrawBuildInputsTests
{
    private static readonly SimulationFrameBuilder Builder = new(new AtmosConfig());

    private static AtmosChunkSnapshot Snapshot(Int3 position)
    {
        return new AtmosChunkSnapshot { GridPosition = position, Dimensions = new Int3(1, 1, 1) };
    }

    private static DrawBuildInputs Create(
        ImmutableDictionary<Int3, AtmosChunkSnapshot>? snapshots = null,
        IReadOnlyList<AtmosChunkHandle>? handles = null)
    {
        return new DrawBuildInputs(
            Builder,
            snapshots ?? ImmutableDictionary<Int3, AtmosChunkSnapshot>.Empty,
            handles ?? [],
            0,
            0,
            BuiltInVisualizationIds.Temperature,
            0,
            AtmosChunkSnapshotFields.Temperature,
            32,
            0,
            0f,
            null);
    }

    [Test]
    public void Equality_ComparesTheSnapshotMapByReference()
    {
        var map = ImmutableDictionary<Int3, AtmosChunkSnapshot>.Empty.Add(new Int3(0, 0, 0), Snapshot(new Int3(0, 0, 0)));
        IReadOnlyList<AtmosChunkHandle> handles = [new AtmosChunkHandle(new Int3(0, 0, 0))];
        var first = Create(map, handles);

        Assert.Multiple(() =>
        {
            Assert.That(Create(map, handles), Is.EqualTo(first));
            Assert.That(Create(map.SetItem(new Int3(1, 0, 0), Snapshot(new Int3(1, 0, 0))), handles), Is.Not.EqualTo(first));
            Assert.That(first with { SourceVersion = 1 }, Is.Not.EqualTo(first));
        });
    }

    [Test]
    public void RequiresRemap_OnlyForMappingChanges()
    {
        var inputs = Create();

        Assert.Multiple(() =>
        {
            Assert.That(inputs.RequiresRemap(null), Is.True);
            Assert.That(inputs.RequiresRemap(inputs with { SourceVersion = 7, TimelineRevision = 3 }), Is.False);
            Assert.That(
                inputs.RequiresRemap(inputs with { VisualizationId = BuiltInVisualizationIds.Temperature.ToUpperInvariant() }),
                Is.False);

            Assert.That(inputs.RequiresRemap(inputs with { VisualizationId = "pressure" }), Is.True);
            Assert.That(inputs.RequiresRemap(inputs with { MappingRevision = 1 }), Is.True);
            Assert.That(
                inputs.RequiresRemap(inputs with { Fields = inputs.Fields | AtmosChunkSnapshotFields.Velocity }),
                Is.True);

            Assert.That(inputs.RequiresRemap(inputs with { Resolution = 8 }), Is.True);
            Assert.That(inputs.RequiresRemap(inputs with { LegendRangeRevision = 1 }), Is.True);
        });
    }

    [Test]
    public void OrderSnapshots_FollowsTheHandlesAndSkipsMissingChunks()
    {
        var a = new Int3(0, 0, 0);
        var b = new Int3(1, 0, 0);
        var c = new Int3(2, 0, 0);
        var map = ImmutableDictionary<Int3, AtmosChunkSnapshot>.Empty.Add(a, Snapshot(a)).Add(c, Snapshot(c));
        var inputs = Create(map, [new AtmosChunkHandle(c), new AtmosChunkHandle(b), new AtmosChunkHandle(a)]);

        Assert.That(inputs.OrderSnapshots().Select(static snapshot => snapshot.GridPosition), Is.EqualTo(new[] { c, a }));
    }
}
