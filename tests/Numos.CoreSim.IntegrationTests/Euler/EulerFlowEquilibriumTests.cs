using Numos.API;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.Maths;

namespace Numos.CoreSim.IntegrationTests.Euler;

/// <summary>
///     Where the Euler stage ends up: a small pressure step in a room settles to rest and the chunk sleeps, and a
///     blast centred on the corner shared by a 2×2×2 block of chunks keeps the cube's full symmetry through the chunk
///     faces and the alternating sweep order.
/// </summary>
[TestFixture]
public sealed class EulerFlowEquilibriumTests
{
    private const float Atmosphere = AtmosPhysicalConstants.StandardAtmosphericPressure;
    private const float Temperature = AtmosPhysicalConstants.RoomTemperature;

    [Test]
    public void SmallPressureStep_SettlesWithinASecondAndSleeps()
    {
        const int width = 10;
        const int height = 10;
        const int depth = 3;
        var config = SimTestHelpers.CreateEulerConfig();
        config.SleepThreshold = 20;
        config.SleepEpsilon = 0.5f;
        using var simulation = SimTestHelpers.CreateEulerSimulation(config, width, height, depth, out var chunk);
        float ambient = SimTestHelpers.MolesFor(config, Atmosphere, Temperature);
        float raised = SimTestHelpers.MolesFor(config, 1.01f * Atmosphere, Temperature);
        for (int z = 0; z < depth; z++)
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
            simulation.AddGasToVoxel(chunk, x, y, z, SimTestHelpers.EulerAirGasName, x < width / 2 ? raised : ambient, Temperature);

        double initialMoles = SimTestHelpers.TotalMoles(simulation.GetChunkSnapshot(chunk));
        for (int tick = 0; tick < 20; tick++)
        {
            simulation.Tick();
        }

        var afterOneSecond = simulation.GetChunkSnapshot(chunk);
        int sleptAt = -1;
        for (int tick = 20; tick < 140 && sleptAt < 0; tick++)
        {
            simulation.Tick();
            if (!simulation.GetChunkSnapshot(chunk).IsAwake)
                sleptAt = tick + 1;
        }

        TestContext.Out.WriteLine(
            $"after 1 s: max speed {MaxSpeed(afterOneSecond):E2} m/s, max Δp/p {MaxRelativePressureDeviation(afterOneSecond):E2}; slept at tick {sleptAt}");
        Assert.Multiple(() =>
        {
            Assert.That(MaxSpeed(afterOneSecond), Is.LessThan(1e-2f));
            Assert.That(MaxRelativePressureDeviation(afterOneSecond), Is.LessThan(1e-3));
            Assert.That(sleptAt, Is.InRange(21, 140));
            Assert.That(SimTestHelpers.TotalMoles(simulation.GetChunkSnapshot(chunk)), Is.EqualTo(initialMoles).Within(1e-4).Percent);
        });
    }

    [Test]
    public void CornerBlast_InAChunkCube_KeepsTheCubeSymmetry()
    {
        const int size = 8;
        const float radius = 2.5f;
        var config = SimTestHelpers.CreateEulerConfig();
        using var simulation = SimTestHelpers.CreateEulerSimulation(config, size, size, size, out _);
        float ambient = SimTestHelpers.MolesFor(config, Atmosphere, Temperature);
        float hot = SimTestHelpers.MolesFor(config, 4f * Atmosphere, 600f);
        var chunks = new AtmosChunkHandle[2, 2, 2];
        for (int cz = 0; cz < 2; cz++)
        for (int cy = 0; cy < 2; cy++)
        for (int cx = 0; cx < 2; cx++)
        {
            var position = new Int3(cx, cy, cz);
            var chunk = position == default
                ? simulation.GetChunkHandles()[0]
                : SimTestHelpers.CreateOpenChunk(simulation, position);

            chunks[cx, cy, cz] = chunk;
            for (int z = 0; z < size; z++)
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = cx * size + x + 0.5f - size;
                float dy = cy * size + y + 0.5f - size;
                float dz = cz * size + z + 0.5f - size;
                bool inside = dx * dx + dy * dy + dz * dz < radius * radius;
                simulation.AddGasToVoxel(
                    chunk, x, y, z, SimTestHelpers.EulerAirGasName,
                    inside ? hot : ambient, inside ? 600f : Temperature);
            }
        }

        simulation.Tick();
        simulation.Tick();

        // Two symmetry groups, with different expectations. Reflections in the three mid-planes are exact for the
        // sweep up to rounding, since a mirrored pencil solves the mirrored Riemann problems. Axis permutations are
        // not: the sweep order inside a tick is fixed (and only alternates between ticks), so two voxels that swap
        // axes see the operators in a different order and agree only to the splitting error.
        var byMirror = new Dictionary<(float, float, float), Sample>();
        var byPermutation = new Dictionary<(float, float, float), Sample>();
        double worstMirror = 0d;
        double worstMirrorSpeed = 0d;
        double worstPermutation = 0d;
        double worstPermutationSpeed = 0d;
        float maxSpeed = 0f;
        for (int cz = 0; cz < 2; cz++)
        for (int cy = 0; cy < 2; cy++)
        for (int cx = 0; cx < 2; cx++)
        {
            var snapshot = simulation.GetChunkSnapshot(chunks[cx, cy, cz]);
            for (int z = 0; z < size; z++)
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int index = SimTestHelpers.Index(x, y, z, size, size);
                (float Coordinate, float Speed)[] axes =
                [
                    (MathF.Abs(cx * size + x + 0.5f - size), MathF.Abs(snapshot.VelocityX[index])),
                    (MathF.Abs(cy * size + y + 0.5f - size), MathF.Abs(snapshot.VelocityY[index])),
                    (MathF.Abs(cz * size + z + 0.5f - size), MathF.Abs(snapshot.VelocityZ[index]))
                ];

                var sample = new Sample(
                    snapshot.TotalPressure[index],
                    snapshot.Temperature[index],
                    snapshot.Gases.Sum(gas => gas.Moles[index]),
                    [axes[0].Speed, axes[1].Speed, axes[2].Speed]);
                maxSpeed = MathF.Max(maxSpeed, sample.Speed.Max());
                Compare(byMirror, (axes[0].Coordinate, axes[1].Coordinate, axes[2].Coordinate), sample, ref worstMirror, ref worstMirrorSpeed);

                Array.Sort(axes, static (a, b) => b.Coordinate.CompareTo(a.Coordinate));
                var permuted = sample with { Speed = [axes[0].Speed, axes[1].Speed, axes[2].Speed] };
                Compare(byPermutation, (axes[0].Coordinate, axes[1].Coordinate, axes[2].Coordinate), permuted, ref worstPermutation, ref worstPermutationSpeed);
            }
        }

        TestContext.Out.WriteLine(
            $"{simulation.LastFlowDiagnostics}; mirror classes={byMirror.Count} worst={worstMirror:E2} speed={worstMirrorSpeed:E2}; " +
            $"permutation classes={byPermutation.Count} worst={worstPermutation:E2} speed={worstPermutationSpeed:E2}; max speed {maxSpeed:F2} m/s");
        // Measured on x64: mirrors exact, permutations 1.2e-2 on the scalars and 3.8% of the peak speed.
        Assert.Multiple(() =>
        {
            Assert.That(worstMirror, Is.Zero);
            Assert.That(worstMirrorSpeed, Is.Zero);
            Assert.That(worstPermutation, Is.LessThan(3e-2));
            Assert.That(worstPermutationSpeed, Is.LessThan(0.1 * maxSpeed));
        });
    }

    private static void Compare(
        Dictionary<(float, float, float), Sample> classes, (float, float, float) key, Sample sample,
        ref double worstScalar, ref double worstSpeed)
    {
        if (!classes.TryGetValue(key, out var expected))
        {
            classes[key] = sample;
            return;
        }

        worstScalar = Math.Max(worstScalar, Math.Abs(sample.Pressure - expected.Pressure) / expected.Pressure);
        worstScalar = Math.Max(worstScalar, Math.Abs(sample.Temperature - expected.Temperature) / expected.Temperature);
        worstScalar = Math.Max(worstScalar, Math.Abs(sample.Moles - expected.Moles) / expected.Moles);
        for (int axis = 0; axis < 3; axis++)
            worstSpeed = Math.Max(worstSpeed, Math.Abs(sample.Speed[axis] - expected.Speed[axis]));
    }

    private sealed record Sample(float Pressure, float Temperature, float Moles, float[] Speed);

    private static float MaxSpeed(AtmosChunkSnapshot snapshot)
    {
        float max = 0f;
        for (int index = 0; index < snapshot.Temperature.Length; index++)
        {
            float speed = MathF.Sqrt(
                snapshot.VelocityX[index] * snapshot.VelocityX[index] +
                snapshot.VelocityY[index] * snapshot.VelocityY[index] +
                snapshot.VelocityZ[index] * snapshot.VelocityZ[index]);
            max = MathF.Max(max, speed);
        }

        return max;
    }

    private static double MaxRelativePressureDeviation(AtmosChunkSnapshot snapshot)
    {
        double mean = snapshot.TotalPressure.Average();
        return snapshot.TotalPressure.Max(p => Math.Abs(p - mean)) / mean;
    }
}
