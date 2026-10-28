using System.Numerics;
using Numos.API;
using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.Maths;
using Numos.SimDrawer;

namespace Numos.Workbench.Tests;

[TestFixture]
public sealed class WorkbenchFlowFieldTests
{
    private const float MolarMass = 0.028f;

    /// <summary>
    ///     A row of 8 voxels in two chunks of 4. The voxel volume is 1 m^3, so the edge is 1 m.
    /// </summary>
    private static WorkbenchSession CreateRow(float voxelVolume = 1f)
    {
        var session = new WorkbenchSession();
        session.CreateProject(
            new WorkbenchProjectOptions("Flow", 4, 1, 1, false),
            config =>
            {
                config.VoxelVolume = voxelVolume;
                config.GasRegistry.Add(new GasProperties
                {
                    Name = "Nitrogen",
                    MolarHeatCapacityAtConstantVolume = 20.8f,
                    MolarMass = MolarMass
                });
            });

        session.EditRegion(
            new VoxelBox(new Int3(0, 0, 0), new Int3(7, 0, 0)),
            new RegionEdit(CreateChunks: true, Classification: 1, TemperatureK: 300f));

        return session;
    }

    private static void SetMoles(WorkbenchSession session, int x, float moles)
    {
        session.EditRegion(
            new VoxelBox(new Int3(x, 0, 0), new Int3(x, 0, 0)),
            new RegionEdit(ClearGas: true, Gases: [new RegionGas(0, moles)], GasTemperatureK: 300f));
    }

    private static VoxelBox Row => new(new Int3(0, 0, 0), new Int3(7, 0, 0));

    [Test]
    public void Density_OfOneMoleInATwoCubicMetreVoxelIsHalfTheMolarMass()
    {
        using var session = CreateRow(2f);
        SetMoles(session, 3, 1f);

        float[] density = session.ReadBox(Row, ["density"])[0];

        Assert.Multiple(() =>
        {
            Assert.That(density[3], Is.EqualTo(MolarMass / 2f).Within(1e-7f));
            Assert.That(density[2], Is.Zero);
        });
    }

    [Test]
    public void Density_IsNaNInSolidAndVoidVoxels()
    {
        using var session = CreateRow();
        for (int x = 0; x < 8; x++)
            SetMoles(session, x, 1f);

        session.EditRegion(Row with { Min = new Int3(1, 0, 0), Max = new Int3(1, 0, 0) }, new RegionEdit(Classification: VoxelClassification.RoomSolid));
        session.EditRegion(Row with { Min = new Int3(2, 0, 0), Max = new Int3(2, 0, 0) }, new RegionEdit(Classification: VoxelClassification.RoomVoid));
        float[] density = session.ReadBox(Row, ["density"])[0];

        Assert.Multiple(() =>
        {
            Assert.That(float.IsNaN(density[1]), Is.True);
            Assert.That(float.IsNaN(density[2]), Is.True);
            Assert.That(density[0], Is.EqualTo(MolarMass).Within(1e-7f));
        });
    }

    [Test]
    public void DensityGradient_AcrossAChunkBoundaryEqualsTheGradientInsideAChunk()
    {
        using var session = CreateRow();
        for (int x = 0; x < 8; x++)
            SetMoles(session, x, x + 1f);

        float[] gradient = session.ReadBox(Row, ["densityGradient"])[0];

        // The ramp rises by 0.028 kg/m3 per voxel. The chunk border sits between x = 3 and x = 4.
        Assert.That(gradient, Is.All.EqualTo(MolarMass).Within(1e-6f));
    }

    [Test]
    public void DensityGradient_ReadsTheHaloOutsideTheRequestedBox()
    {
        using var session = CreateRow();
        for (int x = 0; x < 8; x++)
            SetMoles(session, x, x * x + 1f);

        float[] whole = session.ReadBox(Row, ["densityGradient"])[0];
        float[] part = session.ReadBox(
            new VoxelBox(new Int3(3, 0, 0), new Int3(4, 0, 0)),
            ["densityGradient"])[0];

        Assert.Multiple(() =>
        {
            Assert.That(part[0], Is.EqualTo(whole[3]));
            Assert.That(part[1], Is.EqualTo(whole[4]));
            Assert.That(whole[3], Is.EqualTo((17f - 5f) * MolarMass / 2f).Within(1e-5f), "central difference over the border");
        });
    }

    [Test]
    public void DensityGradient_UsesOneSidedDifferencesAtSolidWallsAndMissingChunks()
    {
        using var session = CreateRow();
        for (int x = 0; x < 8; x++)
            SetMoles(session, x, MathF.Pow(2f, x));

        session.EditRegion(
            new VoxelBox(new Int3(7, 0, 0), new Int3(7, 0, 0)),
            new RegionEdit(Classification: VoxelClassification.RoomSolid));

        float[] gradient = session.ReadBox(Row, ["densityGradient"])[0];

        Assert.Multiple(() =>
        {
            Assert.That(gradient[0], Is.EqualTo((2f - 1f) * MolarMass).Within(1e-6f), "no chunk at x = -1");
            Assert.That(gradient[6], Is.EqualTo((64f - 32f) * MolarMass).Within(1e-5f), "solid at x = 7");
            Assert.That(float.IsNaN(gradient[7]), Is.True);
        });
    }

    [Test]
    public void Schlieren_NormalizesByTheRegionMaximumOrAnExplicitReference()
    {
        using var session = CreateRow();
        for (int x = 0; x < 8; x++)
            SetMoles(session, x, x < 4 ? 1f : 5f);

        float[] region = session.ReadBox(Row, ["schlieren"])[0];
        float[] tight = session.ReadBox(
            new VoxelBox(new Int3(0, 0, 0), new Int3(1, 0, 0)), ["schlieren"])[0];
        float[] referenced = session.ReadBox(
            new VoxelBox(new Int3(0, 0, 0), new Int3(1, 0, 0)),
            ["schlieren"],
            options: new WorkbenchFieldOptions(2f * MolarMass))[0];

        Assert.Multiple(() =>
        {
            Assert.That(region, Is.All.InRange(0f, 1f));
            Assert.That(region[3], Is.EqualTo(MathF.Exp(-15f)).Within(1e-9f), "the steepest gradient is black");
            Assert.That(region[0], Is.EqualTo(1f));
            Assert.That(tight, Is.All.EqualTo(1f), "a region without a gradient is white");
            Assert.That(referenced, Is.All.EqualTo(1f));
            Assert.That(
                session.ReadBox(Row, ["schlieren"], options: new WorkbenchFieldOptions(4f * MolarMass))[0][3],
                Is.EqualTo(MathF.Exp(-15f * 0.5f)).Within(1e-6f));
        });
    }

    [Test]
    public void Schlieren_AcceptsASteepnessSuffixAndRejectsBadOnes()
    {
        using var session = CreateRow();
        for (int x = 0; x < 8; x++)
            SetMoles(session, x, x < 4 ? 1f : 5f);

        float[] steep = session.ReadBox(Row, ["schlieren:20"])[0];

        Assert.Multiple(() =>
        {
            Assert.That(steep[3], Is.EqualTo(MathF.Exp(-20f)).Within(1e-12f));
            Assert.That(session.Fields.TryResolve("schlieren:0", out _), Is.False);
            Assert.That(session.Fields.TryResolve("schlieren:-3", out _), Is.False);
            Assert.That(session.Fields.TryResolve("schlieren:x", out _), Is.False);
        });
    }

    [Test]
    public void PointSampling_ComputesGradientsLocallyAndNeedsAReferenceForSchlieren()
    {
        using var session = CreateRow();
        for (int x = 0; x < 8; x++)
            SetMoles(session, x, x < 4 ? 1f : 5f);

        var points = new[] { new Int3(3, 0, 0), new Int3(6, 0, 0) };
        float[] gradient = session.SampleVoxels(points, ["densityGradient"]);
        float[] schlieren = session.SampleVoxels(
            points, ["schlieren"], options: new WorkbenchFieldOptions(2f * MolarMass));

        Assert.Multiple(() =>
        {
            Assert.That(gradient[0], Is.EqualTo(2f * MolarMass).Within(1e-6f));
            Assert.That(gradient[1], Is.Zero);
            Assert.That(schlieren[0], Is.EqualTo(MathF.Exp(-15f)).Within(1e-9f));
            Assert.That(schlieren[1], Is.EqualTo(1f));
            Assert.Throws<ArgumentException>(() => session.SampleVoxels(points, ["schlieren"]));
        });
    }

    [Test]
    public void Sampler_MatchesReadBoxAcrossChunkBordersAndFollowsChanges()
    {
        using var session = CreateRow();
        float[] moles = [1f, 2f, 4f, 3f, 7f, 2f, 5f, 6f];
        for (int x = 0; x < moles.Length; x++)
            SetMoles(session, x, moles[x]);

        var points = Enumerable.Range(0, 8).Select(static x => new Int3(x, 0, 0)).ToArray();
        string[] names = ["pressure", "density", "densityGradient", "moles:0", "schlieren"];
        var options = new WorkbenchFieldOptions(2f * MolarMass);
        var sampler = session.CreateSampler(points, names, options: options);
        float[] values = new float[points.Length * names.Length];

        void AssertMatchesReadBox()
        {
            sampler.Sample(values);
            float[][] expected = session.ReadBox(Row, names, options: options);
            Assert.Multiple(() =>
            {
                for (int point = 0; point < points.Length; point++)
                for (int column = 0; column < names.Length; column++)
                {
                    Assert.That(
                        values[point * names.Length + column],
                        Is.EqualTo(expected[column][point]).Within(1e-6f),
                        $"{names[column]} at x={point}");
                }
            });
        }

        AssertMatchesReadBox();

        SetMoles(session, 4, 20f);
        AssertMatchesReadBox();

        // The new chunk lies next to the last point, so the gradient there now reads a neighbor.
        session.AddChunk(new Int3(2, 0, 0), 1);
        for (int x = 8; x < 12; x++)
            SetMoles(session, x, 11f);

        AssertMatchesReadBox();
    }

    [Test]
    public void EditRegion_VelocityRoundTripsThroughTheVelocityFields()
    {
        using var session = CreateRow();
        session.EditRegion(
            Row,
            new RegionEdit(ClearGas: true, Gases: [new RegionGas(0, 3f)], GasTemperatureK: 300f));

        var result = session.EditRegion(
            new VoxelBox(new Int3(0, 0, 0), new Int3(3, 0, 0)),
            new RegionEdit(Velocity: new Vector3(251f, -4f, 0.5f)));

        float[][] values = session.ReadBox(Row, ["velocityX", "velocityY", "velocityZ", "speed"]);
        float expectedSpeed = MathF.Sqrt(251f * 251f + 16f + 0.25f);
        Assert.Multiple(() =>
        {
            Assert.That(result.VelocityVoxels, Is.EqualTo(4));
            for (int x = 0; x < 4; x++)
            {
                Assert.That(values[0][x], Is.EqualTo(251f).Within(0.01f));
                Assert.That(values[1][x], Is.EqualTo(-4f).Within(0.01f));
                Assert.That(values[2][x], Is.EqualTo(0.5f).Within(0.01f));
                Assert.That(values[3][x], Is.EqualTo(expectedSpeed).Within(0.01f));
            }

            Assert.That(values[0].Skip(4), Is.All.Zero);
        });
    }

    [Test]
    public void EditRegion_VelocitySkipsEmptyAndSolidVoxelsAndRejectsNonFiniteValues()
    {
        using var session = CreateRow();
        SetMoles(session, 0, 2f);
        session.EditRegion(
            new VoxelBox(new Int3(2, 0, 0), new Int3(2, 0, 0)),
            new RegionEdit(Classification: VoxelClassification.RoomSolid));

        var result = session.EditRegion(
            new VoxelBox(new Int3(0, 0, 0), new Int3(3, 0, 0)),
            new RegionEdit(Velocity: new Vector3(10f, 0f, 0f)));

        Assert.Multiple(() =>
        {
            Assert.That(result.VelocityVoxels, Is.EqualTo(1));
            Assert.That(result.VoxelsEdited, Is.EqualTo(4));
            Assert.Throws<ArgumentException>(() => session.EditRegion(
                Row, new RegionEdit(Velocity: new Vector3(float.NaN, 0f, 0f))));
            Assert.Throws<ArgumentException>(() => session.EditRegion(
                Row, new RegionEdit(Velocity: new Vector3(0f, float.PositiveInfinity, 0f))));
        });
    }

    [Test]
    public void RenderSlice_SchlierenDrawsTheStepDarkAndHonorsAReference()
    {
        using var session = CreateRow();
        for (int x = 0; x < 8; x++)
            SetMoles(session, x, x < 4 ? 1f : 5f);

        var image = session.RenderSlice(SliceAxis.Z, 0, "schlieren");
        var referenced = session.RenderSlice(
            SliceAxis.Z, 0, "schlieren", options: new WorkbenchFieldOptions(4f * MolarMass));

        byte Red(SliceImage picture, int x) => picture.Rgba[x * 4];
        Assert.Multiple(() =>
        {
            Assert.That(image.Width, Is.EqualTo(8));
            Assert.That(image.RangeMinimum, Is.Zero);
            Assert.That(image.RangeMaximum, Is.EqualTo(2f * MolarMass).Within(1e-6f));
            Assert.That(Red(image, 0), Is.EqualTo(255));
            Assert.That(Red(image, 3), Is.EqualTo(0));
            Assert.That(Red(image, 4), Is.EqualTo(0));
            Assert.That(referenced.RangeMaximum, Is.EqualTo(4f * MolarMass).Within(1e-6f));
            Assert.That(Red(referenced, 3), Is.EqualTo((byte)MathF.Round(MathF.Exp(-7.5f) * 255f)).Within(1));
        });
    }

    [Test]
    public void RenderSlice_DensityAndVelocityVisualizationsRender()
    {
        using var session = CreateRow();
        for (int x = 0; x < 8; x++)
            SetMoles(session, x, x + 1f);

        session.EditRegion(Row, new RegionEdit(Velocity: new Vector3(5f, 0f, 0f)));
        var density = session.RenderSlice(SliceAxis.Z, 0, "density");
        var velocity = session.RenderSlice(SliceAxis.Z, 0, "velocity-x");

        Assert.Multiple(() =>
        {
            Assert.That(density.RangeMinimum, Is.EqualTo(MolarMass).Within(1e-6f));
            Assert.That(density.RangeMaximum, Is.EqualTo(8f * MolarMass).Within(1e-6f));
            Assert.That(velocity.RangeMinimum, Is.EqualTo(-velocity.RangeMaximum));
            Assert.That(velocity.RangeMaximum, Is.GreaterThan(0f));
        });
    }
}
