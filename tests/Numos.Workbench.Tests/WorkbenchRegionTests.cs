using Numos.API;
using Numos.CoreSim;
using Numos.Maths;

namespace Numos.Workbench.Tests;

[TestFixture]
public sealed class WorkbenchRegionTests
{
    private static WorkbenchSession CreateSession(int width = 4, int height = 2, int depth = 1)
    {
        var session = new WorkbenchSession();
        session.CreateProject(
            new WorkbenchProjectOptions("Regions", width, height, depth, false),
            config =>
            {
                config.VoxelVolume = 8.31446262f;
                config.GasRegistry.Add(new GasProperties { Name = "Air", MolarHeatCapacityAtConstantVolume = 1f });
            });

        return session;
    }

    [Test]
    public void CreateProject_AppliesEditsMadeByPrepare()
    {
        using var session = CreateSession();

        Assert.Multiple(() =>
        {
            Assert.That(session.World!.Config.GasRegistry.Count, Is.EqualTo(1));
            Assert.That(session.World.Config.VoxelVolume, Is.EqualTo(8.31446262f).Within(1e-6f));
        });
    }

    [Test]
    public void PartialPressureToMoles_FollowsTheIdealGasLaw()
    {
        using var session = CreateSession();

        // n = pV / (RT) with R = 8.31446262 and V = 8.31446262 gives n = p / T.
        Assert.Multiple(() =>
        {
            Assert.That(session.PartialPressureToMoles(6000f, 300f), Is.EqualTo(20f).Within(1e-3f));
            Assert.That(session.PartialPressureToMoles(0f, 300f), Is.Zero);
            Assert.Throws<ArgumentOutOfRangeException>(() => session.PartialPressureToMoles(-1f, 300f));
            Assert.Throws<ArgumentOutOfRangeException>(() => session.PartialPressureToMoles(1f, 0f));
            Assert.Throws<ArgumentOutOfRangeException>(() => session.PartialPressureToMoles(float.NaN, 300f));
        });
    }

    [Test]
    public void EditRegion_CreatesChunksAndEditsNegativeGlobalCoordinates()
    {
        using var session = CreateSession();

        var result = session.EditRegion(
            new VoxelBox(new Int3(-3, -1, 0), new Int3(4, 2, 0)),
            new RegionEdit(true, 1, false, 300f, [new RegionGas(0, 2f)]));

        float[] moles = session.ReadBox(
            new VoxelBox(new Int3(-4, -2, 0), new Int3(5, 3, 0)),
            ["totalMoles"])[0];

        // The box is 8 x 4 x 1. Chunks are 4 x 2 x 1, so the box spans 3 x 3 chunks.
        Assert.Multiple(() =>
        {
            Assert.That(result.VoxelsEdited, Is.EqualTo(32));
            Assert.That(result.ChunksCreated, Is.EqualTo(9));
            Assert.That(result.ChunksTouched, Is.EqualTo(9));
            Assert.That(session.ActiveSimulation!.ChunkCount, Is.EqualTo(9));
            Assert.That(moles.Count(static value => value == 2f), Is.EqualTo(32));
            Assert.That(moles.Sum(static value => float.IsNaN(value) ? 0 : value), Is.EqualTo(64f).Within(1e-3f));
        });
    }

    [Test]
    public void EditRegion_WithoutCreateChunks_SkipsVoxelsOutsideChunks()
    {
        using var session = CreateSession();
        session.AddChunk(new Int3(0, 0, 0), 1);

        var result = session.EditRegion(
            new VoxelBox(new Int3(0, 0, 0), new Int3(7, 1, 0)),
            new RegionEdit(Classification: 1));

        Assert.Multiple(() =>
        {
            Assert.That(result.VoxelsEdited, Is.EqualTo(8));
            Assert.That(result.VoxelsSkipped, Is.EqualTo(8));
            Assert.That(result.ChunksCreated, Is.Zero);
        });
    }

    [Test]
    public void EditRegion_WritesOneSummaryLogLine()
    {
        using var session = CreateSession();
        int before = session.Log.Snapshot().Length;

        session.EditRegion(
            new VoxelBox(new Int3(0, 0, 0), new Int3(3, 1, 0)),
            new RegionEdit(true, 1, false, 300f, [new RegionGas(0, 1f)]));

        var added = session.Log.Snapshot().Skip(before).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(added, Has.Length.EqualTo(1));
            Assert.That(added[0].Message, Does.Contain("8 voxels"));
        });
    }

    [Test]
    public void EditRegion_RecordsOneOperationForTheWholeRegion()
    {
        using var session = CreateSession();
        session.AddChunk(new Int3(0, 0, 0), 1);
        session.AddChunk(new Int3(1, 0, 0), 1);
        int before = session.Timeline!.Operations.Count;

        var result = session.EditRegion(
            new VoxelBox(new Int3(0, 0, 0), new Int3(7, 1, 0)),
            new RegionEdit(false, 1, true, 300f, [new RegionGas(0, 1f)], null, new RegionVelocity(2f, 0f, 0f)));

        var added = session.Timeline.Operations.Skip(before).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(result.VoxelsEdited, Is.EqualTo(16));
            Assert.That(result.VelocityVoxels, Is.EqualTo(16));
            Assert.That(added, Has.Length.EqualTo(1));
        });
    }

    [Test]
    public void EditRegion_PartialClassificationKeepsOtherVoxels()
    {
        using var session = CreateSession();
        session.EditRegion(new VoxelBox(new Int3(0, 0, 0), new Int3(3, 1, 0)), new RegionEdit(true, 1));
        session.EditRegion(new VoxelBox(new Int3(1, 0, 0), new Int3(2, 0, 0)), new RegionEdit(Classification: -2));

        float[] classes = session.ReadBox(new VoxelBox(new Int3(0, 0, 0), new Int3(3, 1, 0)), ["classification"])[0];

        Assert.That(classes, Is.EqualTo(new float[] { 1, -2, -2, 1, 1, 1, 1, 1 }));
    }

    [Test]
    public void EditRegion_RejectsInvalidInput()
    {
        using var session = CreateSession();
        var box = new VoxelBox(new Int3(0, 0, 0), new Int3(1, 1, 0));

        Assert.Multiple(() =>
        {
            Assert.Throws<ArgumentException>(() =>
                session.EditRegion(new VoxelBox(new Int3(1, 0, 0), new Int3(0, 0, 0)), new RegionEdit()));

            Assert.Throws<ArgumentException>(() =>
                session.EditRegion(box, new RegionEdit(true, Gases: [new RegionGas(0, 1f, 1f)])));

            Assert.Throws<ArgumentException>(() =>
                session.EditRegion(box, new RegionEdit(true, Gases: [new RegionGas(5, 1f)])));

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                session.EditRegion(box, new RegionEdit(true, Gases: [new RegionGas(0, null, 100f)], GasTemperatureK: 0f)));
        });
    }

    [Test]
    public void EditRegion_IsBlockedWhileInspectingHistory()
    {
        using var session = CreateSession();
        session.EditRegion(new VoxelBox(new Int3(0, 0, 0), new Int3(3, 1, 0)), new RegionEdit(true, 1));
        session.Tick(3);
        session.SeekTick(1);

        Assert.Throws<InvalidOperationException>(() =>
            session.EditRegion(new VoxelBox(new Int3(0, 0, 0), new Int3(1, 1, 0)), new RegionEdit(Classification: 1)));
    }

    [Test]
    public void ReadBox_ReturnsCOrderZYXAndNaNForMissingChunks()
    {
        using var session = CreateSession(2, 2, 2);
        session.AddChunk(new Int3(0, 0, 0), 1);
        for (int z = 0; z < 2; z++)
        for (int y = 0; y < 2; y++)
        for (int x = 0; x < 2; x++)
        {
            session.SetVoxelTemperature(session.GetCell(new Int3(0, 0, 0), x, y, z), 100f + x + 10f * y + 100f * z);
        }

        float[] values = session.ReadBox(new VoxelBox(new Int3(0, 0, 0), new Int3(2, 1, 1)), ["temperature"])[0];

        // Shape (z, y, x) = (2, 2, 3). Column x = 2 lies outside the only chunk.
        Assert.That(
            values.Select(static value => float.IsNaN(value) ? -1f : value).ToArray(),
            Is.EqualTo(new float[] { 100, 101, -1, 110, 111, -1, 200, 201, -1, 210, 211, -1 }));
    }

    [Test]
    public void SampleVoxels_ReadsGasFieldsAndTreatsSolidAsNaN()
    {
        using var session = CreateSession();
        session.EditRegion(
            new VoxelBox(new Int3(0, 0, 0), new Int3(3, 1, 0)),
            new RegionEdit(true, 1, false, 300f, [new RegionGas(0, null, 3000f)], 300f));

        session.SetVoxelClassification(session.GetCell(new Int3(0, 0, 0), 3, 1, 0), -2);
        float[] values = session.SampleVoxels(
            [new Int3(0, 0, 0), new Int3(3, 1, 0), new Int3(40, 0, 0)],
            ["pressure", "moles:0", "classification"]);

        Assert.Multiple(() =>
        {
            Assert.That(values[0], Is.EqualTo(3000f).Within(1f));
            Assert.That(values[1], Is.EqualTo(10f).Within(1e-3f));
            Assert.That(values[2], Is.EqualTo(1f));
            Assert.That(float.IsNaN(values[3]), Is.True);
            Assert.That(values[5], Is.EqualTo(-2f));
            Assert.That(float.IsNaN(values[6]), Is.True);
            Assert.That(float.IsNaN(values[8]), Is.True);
        });
    }

    [Test]
    public void FieldRegistry_AcceptsCustomFieldsAndUnknownNamesThrow()
    {
        using var session = CreateSession();
        session.Fields.Register(new WorkbenchField(
            "answer",
            "",
            Numos.CoreSim.Datatypes.Snapshots.AtmosChunkSnapshotFields.None,
            static (in snapshot, index) => 42f));

        session.AddChunk(new Int3(0, 0, 0), 1);
        float[] values = session.SampleVoxels([new Int3(0, 0, 0)], ["ANSWER"]);

        Assert.Multiple(() =>
        {
            Assert.That(values[0], Is.EqualTo(42f));
            Assert.Throws<KeyNotFoundException>(() => session.SampleVoxels([new Int3(0, 0, 0)], ["notAField"]));
            Assert.That(session.Fields.TryResolve("moles:x", out _), Is.False);
        });
    }

    [Test]
    public void ResetSolvers_EnablesStagesEvenWhileTheWorldRecords()
    {
        using var session = CreateSession();
        session.SetSolverEnabled("boundary-flow", false);

        bool full = session.ResetSolvers();

        Assert.Multiple(() =>
        {
            Assert.That(full, Is.False);
            Assert.That(session.GetSolverSteps().All(static step => step.IsEnabled), Is.True);
        });
    }
}
