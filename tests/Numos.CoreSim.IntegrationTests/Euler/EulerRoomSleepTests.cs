using Numos.API;
using Numos.Maths;

namespace Numos.CoreSim.IntegrationTests.Euler;

/// <summary>
///     A block of Euler chunks falls asleep after a blast. Awake chunks must not keep re-waking neighbours that
///     just went to sleep.
/// </summary>
[TestFixture]
public sealed class EulerRoomSleepTests
{
    private const int ChunkSize = 8;
    private const int BlockSize = 3;
    private const int MaxTicks = 400;
    private const float Temperature = 293f;
    private const float Atmosphere = AtmosPhysicalConstants.StandardAtmosphericPressure;

    [Test]
    public void BlastInA3x3x3Block_EveryChunkFallsAsleepAndMolesAreConserved()
    {
        var config = SimTestHelpers.CreateEulerConfig();
        var defaults = new AtmosConfig();
        config.SleepThreshold = 100;
        config.SleepEpsilon = defaults.SleepEpsilon;
        config.DefaultDiffusionCoefficient = defaults.DefaultDiffusionCoefficient;
        config.VacuumThreshold = defaults.VacuumThreshold;
        using var simulation = SimTestHelpers.CreateEulerSimulation(
            config, ChunkSize, ChunkSize, ChunkSize, out _, exactGasDynamics: false);
        float ambient = SimTestHelpers.MolesFor(config, Atmosphere, Temperature);
        float hot = 2f * ambient;
        const float radius = 4f;
        const float centre = BlockSize * ChunkSize / 2f;
        var chunks = new List<AtmosChunkHandle>();
        for (int cz = 0; cz < BlockSize; cz++)
        for (int cy = 0; cy < BlockSize; cy++)
        for (int cx = 0; cx < BlockSize; cx++)
        {
            var position = new Int3(cx, cy, cz);
            var chunk = position == default
                ? simulation.GetChunkHandles()[0]
                : SimTestHelpers.CreateOpenChunk(simulation, position);
            chunks.Add(chunk);
            for (int z = 0; z < ChunkSize; z++)
            for (int y = 0; y < ChunkSize; y++)
            for (int x = 0; x < ChunkSize; x++)
            {
                float dx = cx * ChunkSize + x + 0.5f - centre;
                float dy = cy * ChunkSize + y + 0.5f - centre;
                float dz = cz * ChunkSize + z + 0.5f - centre;
                bool inside = dx * dx + dy * dy + dz * dz < radius * radius;
                simulation.AddGasToVoxel(
                    chunk, x, y, z, SimTestHelpers.EulerAirGasName,
                    inside ? hot : ambient, inside ? 600f : Temperature);
            }
        }

        double initialMoles = TotalMoles(simulation, chunks);
        int asleepAt = -1;
        for (int tick = 1; tick <= MaxTicks && asleepAt < 0; tick++)
        {
            simulation.Tick();
            if (chunks.All(chunk => !simulation.GetChunkSnapshot(chunk).IsAwake))
                asleepAt = tick;
        }

        TestContext.Out.WriteLine($"all chunks asleep at tick {asleepAt}");
        Assert.Multiple(() =>
        {
            Assert.That(asleepAt, Is.GreaterThan(0), $"some chunk was still awake after {MaxTicks} ticks");
            Assert.That(TotalMoles(simulation, chunks), Is.EqualTo(initialMoles).Within(1e-3).Percent);
        });
    }

    private static double TotalMoles(AtmosSimulation simulation, IEnumerable<AtmosChunkHandle> chunks)
    {
        double total = 0d;
        foreach (var chunk in chunks)
        foreach (var gas in simulation.GetChunkSnapshot(chunk).Gases)
        foreach (float moles in gas.Moles)
            total += moles;

        return total;
    }
}
