using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.Maths;

namespace Numos.CoreSim.Tests;

[TestFixture]
public sealed class FlowFieldMathTests
{
    private const float Nan = float.NaN;

    private static AtmosConfig CreateConfig(float voxelVolume, float molarMass)
    {
        var config = new AtmosConfig { VoxelVolume = voxelVolume };
        config.GasRegistry.Add(new GasProperties { Name = "Nitrogen", MolarMass = molarMass });
        return config;
    }

    [Test]
    public void GetDensity_SumsMolarMassTimesMolesOverVoxelVolume()
    {
        var config = CreateConfig(2f, 0.028f);
        config.GasRegistry.Add(new GasProperties { Name = "Unweighted" });
        GasSnapshot[] gases =
        [
            new() { GasId = 0, Moles = [1f, float.NaN] },
            new() { GasId = 1, Moles = [2f, 1f] }
        ];

        float first = FlowFieldMath.GetDensity(gases, 0, config);
        float second = FlowFieldMath.GetDensity(gases, 1, config);

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.EqualTo((0.028f + 2f * config.ReferenceMolarMass) / 2f).Within(1e-6f));
            Assert.That(second, Is.EqualTo(config.ReferenceMolarMass / 2f).Within(1e-6f), "a NaN amount is ignored");
        });
    }

    [Test]
    public void ComputeDensity_IsNaNInSolidAndVoidVoxels()
    {
        var config = CreateConfig(1f, 0.028f);
        var snapshot = new AtmosChunkSnapshot
        {
            Dimensions = new Int3(4, 1, 1),
            GridPosition = new Int3(0, 0, 0),
            VoxelRoomMap = [1, VoxelClassification.RoomSolid, VoxelClassification.RoomVoid, 1],
            Gases = [new GasSnapshot { GasId = 0, Moles = [1f, 1f, 1f, 0f] }]
        };

        float[] density = new float[4];
        FlowFieldMath.ComputeDensity(in snapshot, config, density);

        Assert.Multiple(() =>
        {
            Assert.That(density[0], Is.EqualTo(0.028f).Within(1e-6f));
            Assert.That(float.IsNaN(density[1]), Is.True);
            Assert.That(float.IsNaN(density[2]), Is.True);
            Assert.That(density[3], Is.Zero);
        });
    }

    // 3 gases fit the stack table, 70 gases need the heap table.
    [TestCase(3)]
    [TestCase(70)]
    public void ComputeDensity_MatchesGetDensityForEveryVoxel(int gasCount)
    {
        var config = new AtmosConfig { VoxelVolume = 2f };
        var gases = new GasSnapshot[gasCount];
        for (int gas = 0; gas < gasCount; gas++)
        {
            // Every fifth gas has no molar mass, so it weighs the reference.
            config.GasRegistry.Add(new GasProperties
            {
                Name = $"Gas{gas}", MolarMass = gas % 5 == 0 ? 0f : 0.001f * (gas + 1)
            });
            gases[gas] = new GasSnapshot
            {
                GasId = gas, Moles = [0.5f + gas, gas % 3 == 0 ? float.NaN : 0.25f * gas, -1f, 0f]
            };
        }

        var snapshot = new AtmosChunkSnapshot
        {
            Dimensions = new Int3(4, 1, 1),
            GridPosition = new Int3(0, 0, 0),
            VoxelRoomMap = [1, 1, 1, 1],
            Gases = gases
        };

        float[] density = new float[4];
        FlowFieldMath.ComputeDensity(in snapshot, config, density);

        Assert.Multiple(() =>
        {
            for (int index = 0; index < density.Length; index++)
                Assert.That(density[index], Is.EqualTo((float)FlowFieldMath.GetDensity(gases, index, config)));
        });
    }

    [Test]
    public void GradientMagnitude_UsesCentralOneSidedAndZeroDifferences()
    {
        const float edge = 0.5f;

        float central = FlowFieldMath.GradientMagnitude(2f, 1f, 5f, Nan, Nan, Nan, Nan, edge);
        float plusOnly = FlowFieldMath.GradientMagnitude(2f, Nan, 5f, Nan, Nan, Nan, Nan, edge);
        float minusOnly = FlowFieldMath.GradientMagnitude(2f, 1f, Nan, Nan, Nan, Nan, Nan, edge);
        float none = FlowFieldMath.GradientMagnitude(2f, Nan, Nan, Nan, Nan, Nan, Nan, edge);
        float threeAxes = FlowFieldMath.GradientMagnitude(0f, -1f, 1f, -2f, 2f, -2f, 2f, 1f);
        float solidCenter = FlowFieldMath.GradientMagnitude(Nan, 1f, 1f, 1f, 1f, 1f, 1f, edge);

        Assert.Multiple(() =>
        {
            Assert.That(central, Is.EqualTo((5f - 1f) / (2f * edge)));
            Assert.That(plusOnly, Is.EqualTo((5f - 2f) / edge));
            Assert.That(minusOnly, Is.EqualTo((2f - 1f) / edge));
            Assert.That(none, Is.Zero);
            Assert.That(threeAxes, Is.EqualTo(MathF.Sqrt(1f + 4f + 4f)).Within(1e-6f));
            Assert.That(float.IsNaN(solidCenter), Is.True);
        });
    }

    [Test]
    public void ComputeGradientMagnitude_ReadsTheHaloOfALinearRamp()
    {
        // density = 3 * x over a 3 x 2 x 1 interior. Halo cells off the x axis stay NaN, so y and z contribute zero.
        var interior = new Int3(3, 2, 1);
        int paddedX = interior.X + 2;
        int paddedY = interior.Y + 2;
        float[] padded = new float[paddedX * paddedY * (interior.Z + 2)];
        Array.Fill(padded, Nan);
        for (int y = 0; y < interior.Y; y++)
        for (int x = -1; x <= interior.X; x++)
            padded[(1 * paddedX * paddedY) + (y + 1) * paddedX + x + 1] = 3f * x;

        float[] gradient = new float[6];
        FlowFieldMath.ComputeGradientMagnitude(padded, interior, 0.25f, gradient);

        Assert.That(gradient, Is.All.EqualTo(12f).Within(1e-4f));
    }

    [Test]
    public void Schlieren_MapsTheReferenceToExpMinusKAndUniformFieldsToOne()
    {
        Assert.Multiple(() =>
        {
            Assert.That(FlowFieldMath.Schlieren(0f, 4f, 15f), Is.EqualTo(1f));
            Assert.That(FlowFieldMath.Schlieren(4f, 4f, 15f), Is.EqualTo(MathF.Exp(-15f)).Within(1e-12f));
            Assert.That(FlowFieldMath.Schlieren(2f, 4f, 10f), Is.EqualTo(MathF.Exp(-5f)).Within(1e-6f));
            Assert.That(FlowFieldMath.Schlieren(3f, 0f, 15f), Is.EqualTo(1f), "no reference gradient");
            Assert.That(float.IsNaN(FlowFieldMath.Schlieren(Nan, 4f, 15f)), Is.True);
            Assert.That(FlowFieldMath.MaximumFinite([1f, Nan, 3f, float.PositiveInfinity]), Is.EqualTo(3f));
        });
    }
}
