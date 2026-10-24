using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.Maths;

namespace Numos.SimDrawer.Tests;

[TestFixture]
public sealed class FlowFieldVisualizationTests
{
    private const float MolarMass = 0.028f;

    private static AtmosConfig CreateConfig()
    {
        var config = new AtmosConfig { VoxelVolume = 1f };
        config.GasRegistry.Add(new GasProperties { Name = "Nitrogen", MolarMass = MolarMass });
        return config;
    }

    private static AtmosChunkSnapshot CreateSnapshot(
        Int3 position,
        float[] moles,
        int[]? rooms = null,
        float[]? velocityX = null,
        AtmosChunkVersion version = default)
    {
        int count = moles.Length;
        return new AtmosChunkSnapshot
        {
            Version = version,
            GridPosition = position,
            Dimensions = new Int3(count, 1, 1),
            TotalPressure = new float[count],
            Temperature = new float[count],
            VoxelRoomMap = rooms ?? Enumerable.Repeat(1, count).ToArray(),
            Gases = [new GasSnapshot { GasId = 0, Moles = moles }],
            VelocityX = velocityX ?? new float[count],
            VelocityY = new float[count],
            VelocityZ = new float[count],
            ActiveAirCount = count,
            ActiveGasCount = 1
        };
    }

    private static float Brightness(SimulationDrawData frame, Int3 chunk, int index)
    {
        return frame.Chunks[chunk].GetCell((ushort)index).Color.R;
    }

    [Test]
    public void Schlieren_GradientAcrossChunkBoundaryMatchesTheInterior()
    {
        // A linear ramp of 0.028 kg/m3 per voxel, split in two chunks. Every voxel has the same gradient.
        var builder = new SimulationFrameBuilder(CreateConfig());
        var left = CreateSnapshot(new Int3(0, 0, 0), [1f, 2f]);
        var right = CreateSnapshot(new Int3(1, 0, 0), [3f, 4f]);

        var frame = builder.BuildSimulation(
            [left, right],
            BuiltInVisualizationIds.Schlieren,
            1,
            rangeOverride: new VisualizationRange(0f, 0.028f * 2f));

        float expected = MathF.Exp(-15f * 0.5f);
        Assert.Multiple(() =>
        {
            Assert.That(Brightness(frame, new Int3(0, 0, 0), 0), Is.EqualTo(expected).Within(1e-5f), "one-sided at the domain edge");
            Assert.That(Brightness(frame, new Int3(0, 0, 0), 1), Is.EqualTo(expected).Within(1e-5f), "chunk boundary");
            Assert.That(Brightness(frame, new Int3(1, 0, 0), 0), Is.EqualTo(expected).Within(1e-5f), "chunk boundary");
            Assert.That(Brightness(frame, new Int3(1, 0, 0), 1), Is.EqualTo(expected).Within(1e-5f), "one-sided at the domain edge");
        });
    }

    [Test]
    public void Schlieren_AutomaticRangeSpansZeroToTheLargestGradient()
    {
        var builder = new SimulationFrameBuilder(CreateConfig());
        var snapshot = CreateSnapshot(new Int3(0, 0, 0), [1f, 1f, 1f, 5f, 5f, 5f]);

        var frame = builder.BuildSimulation([snapshot], BuiltInVisualizationIds.Schlieren, 1);

        // The step of 4 mol * 0.028 kg/mol between voxels 2 and 3 gives a central difference of half the step on
        // each side, so the largest gradient is 0.028 * 4 / 2.
        float steepest = 0.028f * 2f;
        Assert.Multiple(() =>
        {
            Assert.That(frame.Visualization.Range.Minimum, Is.Zero);
            Assert.That(frame.Visualization.Range.Maximum, Is.EqualTo(steepest).Within(1e-6f));
            Assert.That(Brightness(frame, new Int3(0, 0, 0), 0), Is.EqualTo(1f));
            Assert.That(Brightness(frame, new Int3(0, 0, 0), 2), Is.LessThan(1e-6f));
            Assert.That(Brightness(frame, new Int3(0, 0, 0), 3), Is.LessThan(1e-6f));
        });
    }

    [Test]
    public void Schlieren_UniformFieldIsWhiteAndEveryValueStaysInUnitRange()
    {
        var builder = new SimulationFrameBuilder(CreateConfig());
        var uniform = CreateSnapshot(new Int3(0, 0, 0), [2f, 2f, 2f]);
        var ramp = CreateSnapshot(new Int3(0, 0, 0), [1f, 3f, 9f]);

        var flat = builder.BuildSimulation([uniform], BuiltInVisualizationIds.Schlieren, 1);
        var sloped = builder.BuildSimulation([ramp], BuiltInVisualizationIds.Schlieren, 2);

        Assert.Multiple(() =>
        {
            for (int index = 0; index < 3; index++)
            {
                Assert.That(Brightness(flat, new Int3(0, 0, 0), index), Is.EqualTo(1f));
                Assert.That(Brightness(sloped, new Int3(0, 0, 0), index), Is.InRange(0f, 1f));
            }
        });
    }

    [Test]
    public void Schlieren_SolidNeighborGivesAOneSidedDifference()
    {
        var builder = new SimulationFrameBuilder(CreateConfig());
        var snapshot = CreateSnapshot(
            new Int3(0, 0, 0),
            [1f, 3f, 100f],
            [1, 1, VoxelClassification.RoomSolid]);

        var frame = builder.BuildSimulation(
            [snapshot],
            BuiltInVisualizationIds.Schlieren,
            1,
            rangeOverride: new VisualizationRange(0f, 0.028f * 2f));

        // Voxel 1 sees the solid on its right, so it uses (3 - 1) * 0.028, not a central difference with the solid.
        Assert.That(Brightness(frame, new Int3(0, 0, 0), 1), Is.EqualTo(MathF.Exp(-15f)).Within(1e-9f));
        Assert.That(frame.Chunks[new Int3(0, 0, 0)].GetCell(2).IsVisible, Is.False);
    }

    [Test]
    public void Schlieren_ReusesAChunkOnlyWhenItsNeighborsAreUnchanged()
    {
        var builder = new SimulationFrameBuilder(CreateConfig());
        var range = new VisualizationRange(0f, 1f);
        var left = CreateSnapshot(new Int3(0, 0, 0), [1f, 2f], version: new AtmosChunkVersion(1, 1));
        var right = CreateSnapshot(new Int3(1, 0, 0), [3f, 4f], version: new AtmosChunkVersion(1, 1));
        var first = builder.BuildSimulation([left, right], BuiltInVisualizationIds.Schlieren, 1, rangeOverride: range);

        var unchanged = builder.BuildSimulation(
            [left, right], BuiltInVisualizationIds.Schlieren, 2, first, rangeOverride: range);

        var changedRight = CreateSnapshot(new Int3(1, 0, 0), [30f, 40f], version: new AtmosChunkVersion(1, 2));
        var neighborChanged = builder.BuildSimulation(
            [left, changedRight], BuiltInVisualizationIds.Schlieren, 3, first, rangeOverride: range);

        var neighborGone = builder.BuildSimulation(
            [left], BuiltInVisualizationIds.Schlieren, 4, first, rangeOverride: range);

        var leftKey = new Int3(0, 0, 0);
        Assert.Multiple(() =>
        {
            Assert.That(unchanged.Chunks[leftKey], Is.SameAs(first.Chunks[leftKey]));
            Assert.That(neighborChanged.Chunks[leftKey], Is.Not.SameAs(first.Chunks[leftKey]));
            Assert.That(
                Brightness(neighborChanged, leftKey, 1),
                Is.Not.EqualTo(Brightness(first, leftKey, 1)));
            Assert.That(neighborGone.Chunks[leftKey], Is.Not.SameAs(first.Chunks[leftKey]));
        });
    }

    [Test]
    public void Density_ColorsFollowTheMassDensityAndTheAutomaticRangeSpansIt()
    {
        var builder = new SimulationFrameBuilder(CreateConfig());
        var snapshot = CreateSnapshot(new Int3(0, 0, 0), [1f, 2f, 3f]);

        var frame = builder.BuildSimulation([snapshot], BuiltInVisualizationIds.Density, 1, resolution: 256);

        var chunk = frame.Chunks[new Int3(0, 0, 0)];
        Assert.Multiple(() =>
        {
            Assert.That(frame.Visualization.Range.Minimum, Is.EqualTo(MolarMass).Within(1e-7f));
            Assert.That(frame.Visualization.Range.Maximum, Is.EqualTo(3f * MolarMass).Within(1e-7f));
            Assert.That(chunk.GetCell(0).Color, Is.Not.EqualTo(chunk.GetCell(2).Color));
            Assert.That(frame.Visualization.Legend.Units, Is.EqualTo("kg/m³"));
        });
    }

    [Test]
    public void VelocityX_UsesASymmetricRangeAroundZero()
    {
        var builder = new SimulationFrameBuilder(CreateConfig());
        var snapshot = CreateSnapshot(new Int3(0, 0, 0), [1f, 1f, 1f], velocityX: [-3f, 0f, 1f]);

        var frame = builder.BuildSimulation(
            [snapshot], BuiltInVisualizationIds.VelocityX, 1, resolution: 257);

        var chunk = frame.Chunks[new Int3(0, 0, 0)];
        var negative = chunk.GetCell(0).Color;
        var zero = chunk.GetCell(1).Color;
        var positive = chunk.GetCell(2).Color;
        Assert.Multiple(() =>
        {
            Assert.That(frame.Visualization.Range.Minimum, Is.EqualTo(-3f));
            Assert.That(frame.Visualization.Range.Maximum, Is.EqualTo(3f));
            Assert.That(negative.B, Is.GreaterThan(negative.R), "negative is blue");
            Assert.That(positive.R, Is.GreaterThan(positive.B), "positive is red");
            Assert.That(zero.R, Is.EqualTo(zero.B).Within(0.02f), "zero is neutral");
        });
    }

    [Test]
    public void FlowVisualizations_RequestTheGasesTheyNeed()
    {
        var builder = new SimulationFrameBuilder(CreateConfig());

        Assert.Multiple(() =>
        {
            foreach (string id in new[] { BuiltInVisualizationIds.Density, BuiltInVisualizationIds.Schlieren })
                Assert.That(builder.GetRequiredSnapshotFields(id).HasFlag(AtmosChunkSnapshotFields.Gases), Is.True, id);

            Assert.That(
                builder.GetRequiredSnapshotFields(BuiltInVisualizationIds.VelocityY)
                    .HasFlag(AtmosChunkSnapshotFields.Velocity),
                Is.True);
        });
    }
}
