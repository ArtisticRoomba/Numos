using Numos.API;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.CoreSim.EulerFlow;
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
    private const int MaxSeconds = 20;
    private const float Temperature = 293f;
    private const float Atmosphere = AtmosPhysicalConstants.StandardAtmosphericPressure;

    [TestCase(20)]
    [TestCase(960)]
    public void BlastInA3x3x3Block_EveryChunkFallsAsleepAndMolesAreConserved(int tickRate)
    {
        int maxTicks = MaxSeconds * tickRate;
        var config = SimTestHelpers.CreateEulerConfig();
        var defaults = new AtmosConfig();
        config.SleepDelay = 5f;
        config.SleepEpsilon = defaults.SleepEpsilon;
        config.DefaultDiffusionCoefficient = defaults.DefaultDiffusionCoefficient;
        config.VacuumThreshold = defaults.VacuumThreshold;
        using var simulation = SimTestHelpers.CreateEulerSimulation(
            config, ChunkSize, ChunkSize, ChunkSize, out _, exactGasDynamics: false, tickRate: tickRate);
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
        for (int tick = 1; tick <= maxTicks && asleepAt < 0; tick++)
        {
            simulation.Tick();
            if (chunks.All(chunk => !simulation.GetChunkSnapshot(chunk).IsAwake))
                asleepAt = tick;
        }

        TestContext.Out.WriteLine($"all chunks asleep at tick {asleepAt}");
        Assert.Multiple(() =>
        {
            Assert.That(asleepAt, Is.GreaterThan(0), $"some chunk was still awake after {maxTicks} ticks");
            Assert.That(TotalMoles(simulation, chunks), Is.EqualTo(initialMoles).Within(1e-3).Percent);
        });
    }

    // Measured on x64 for a 2% step across half of a 6x6 grid of 16x16x1 chunks, at the default WakePressureDelta
    // of 50 Pa: every chunk asleep at 36.7 s (20 Hz) and 34.7 s (960 Hz) with SleepSpeed 0.25 m/s, and at 55.65 s
    // (20 Hz) with SleepSpeed 0. The bound sits between the two, about 30% above the first and 14% below the second.
    // At 10 Pa the first two are 40.35 s and 39.75 s and the third does not change.
    private const double OpenGridBoundSeconds = 48d;
    private const int OpenGridChunks = 6;
    private const int FlatChunkSize = 16;

    [TestCase(0.25f, 20, true)]
    [TestCase(0.25f, 960, true)]
    [TestCase(0f, 20, false)]
    public void PressureStepInAnOpenGridOfFlatChunks_FallsAsleepOnlyWhenSlowMotionMaySleep(
        float sleepSpeed, int tickRate, bool expectAsleep)
    {
        AtmosConfig config = CreateSleepConfig(new EulerFlowConfig { MaxSubstepsPerSecond = 0, SleepSpeed = sleepSpeed });
        using var simulation = SimTestHelpers.CreateEulerSimulation(
            config, FlatChunkSize, FlatChunkSize, 1, out _, exactGasDynamics: false, tickRate: tickRate);
        float ambient = SimTestHelpers.MolesFor(config, Atmosphere, Temperature);
        var chunks = new List<AtmosChunkHandle>();
        for (int cy = 0; cy < OpenGridChunks; cy++)
        for (int cx = 0; cx < OpenGridChunks; cx++)
        {
            var position = new Int3(cx, cy, 0);
            var chunk = position == default
                ? simulation.GetChunkHandles()[0]
                : SimTestHelpers.CreateOpenChunk(simulation, position);
            chunks.Add(chunk);
            for (int y = 0; y < FlatChunkSize; y++)
            for (int x = 0; x < FlatChunkSize; x++)
            {
                bool high = cx * FlatChunkSize + x < OpenGridChunks * FlatChunkSize / 2;
                simulation.AddGasToVoxel(
                    chunk, x, y, 0, SimTestHelpers.EulerAirGasName, high ? 1.02f * ambient : ambient, Temperature);
            }
        }

        double initialMoles = TotalMoles(simulation, chunks);
        int maxTicks = (int)(OpenGridBoundSeconds * tickRate);
        int asleepAt = -1;
        for (int tick = 1; tick <= maxTicks && asleepAt < 0; tick++)
        {
            simulation.Tick();
            if (chunks.All(chunk => !simulation.GetChunkSnapshot(chunk).IsAwake))
                asleepAt = tick;
        }

        TestContext.Out.WriteLine($"all chunks asleep at tick {asleepAt} of {maxTicks}");
        Assert.Multiple(() =>
        {
            Assert.That(asleepAt > 0, Is.EqualTo(expectAsleep), $"all asleep at tick {asleepAt}, bound {maxTicks}");
            // Flux sums in float drift by about 2e-7 of the total over the run.
            Assert.That(TotalMoles(simulation, chunks), Is.EqualTo(initialMoles).Within(1e-4).Percent);
        });
    }

    [TestCase(20)]
    [TestCase(960)]
    public void ChunkFallingAsleepWithMotion_StopsItAndKeepsTheEnergy(int tickRate)
    {
        // Thresholds high enough that a chunk sloshing at up to 20 m/s may sleep. When it does, its kinetic energy
        // is still 7e-5 to 1.5e-4 of the total, which is several hundred times the tolerance. Dropping that energy
        // instead of heating the gas fails the test. The measured error is about 1e-9.
        AtmosConfig config = SimTestHelpers.CreateEulerConfig(
            new EulerFlowConfig { MaxSubstepsPerSecond = 0, SleepSpeed = 50f });
        config.SleepDelay = 0.2f;
        config.SleepEpsilon = 50f;
        const int size = 16;
        using var simulation = SimTestHelpers.CreateEulerSimulation(config, size, size, 1, out var chunk, tickRate: tickRate);
        float moles = SimTestHelpers.MolesFor(config, Atmosphere, Temperature);
        float mass = moles * SimTestHelpers.EulerAirMolarMass;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            simulation.AddGasToVoxel(chunk, x, y, 0, SimTestHelpers.EulerAirGasName, moles, Temperature);
            simulation.SetVoxelMomentum(chunk, x, y, 0, mass * 20f, 0f, 0f);
        }

        AtmosChunkSnapshot before = simulation.GetChunkSnapshot(chunk);
        AtmosChunkSnapshot after = before;
        for (int tick = 0; tick < tickRate && after.IsAwake; tick++)
        {
            before = after;
            simulation.Tick();
            after = simulation.GetChunkSnapshot(chunk);
        }

        double energyBefore = SimTestHelpers.TotalEulerEnergy(config, before);
        double energyAfter = SimTestHelpers.TotalEulerEnergy(config, after);
        double kineticBefore = energyBefore - SimTestHelpers.TotalThermalEnergyPrecise(config, before);
        TestContext.Out.WriteLine(
            $"kinetic fraction {kineticBefore / energyBefore:E3}, energy error {(energyAfter - energyBefore) / energyBefore:E3}");
        Assert.Multiple(() =>
        {
            Assert.That(after.IsAwake, Is.False, "the chunk fell asleep within a second");
            Assert.That(kineticBefore / energyBefore, Is.GreaterThan(1e-5), "the chunk was still moving the tick before");
            Assert.That(after.VelocityX.Concat(after.VelocityY).Concat(after.VelocityZ), Is.All.EqualTo(0f));
            Assert.That(energyAfter, Is.EqualTo(energyBefore).Within(1e-7 * energyBefore));
        });
    }

    // Uniform flow at 0.01 m/s into the wall the sleeping chunk presents raises the face pressure by about
    // ρ·c·u ≈ 4 Pa, below WakePressureDelta, so only the speed can wake the neighbour.
    [TestCase(0f, true)]
    [TestCase(0.25f, false)]
    public void RingingNeighbourSlowerThanSleepSpeed_DoesNotWakeASleepingChunk(float sleepSpeed, bool wakes)
    {
        const int width = 4;
        const int calls = 20;
        const float speed = 0.01f;
        AtmosConfig config = SimTestHelpers.CreateEulerConfig(
            new EulerFlowConfig { MaxSubstepsPerSecond = 0, SleepSpeed = sleepSpeed });
        using var simulation = SimTestHelpers.CreateEulerSimulation(config, width, width, width, out var ringing);
        var sleeping = SimTestHelpers.CreateOpenChunk(simulation, Int3.PosX);
        float moles = SimTestHelpers.MolesFor(config, Atmosphere, Temperature);
        float mass = moles * SimTestHelpers.EulerAirMolarMass;
        for (int z = 0; z < width; z++)
        for (int y = 0; y < width; y++)
        for (int x = 0; x < width; x++)
        {
            simulation.AddGasToVoxel(ringing, x, y, z, SimTestHelpers.EulerAirGasName, moles, Temperature);
            simulation.AddGasToVoxel(sleeping, x, y, z, SimTestHelpers.EulerAirGasName, moles, Temperature);
            simulation.SetVoxelMomentum(ringing, x, y, z, mass * speed, 0f, 0f);
        }

        simulation.SleepChunk(sleeping);
        int wokeAt = -1;
        for (int call = 1; call <= calls && wokeAt < 0; call++)
        {
            simulation.Tick();
            if (simulation.GetChunkSnapshot(sleeping).IsAwake)
                wokeAt = call;
        }

        Assert.That(wokeAt > 0, Is.EqualTo(wakes), $"woke on call {wokeAt}");
    }

    private static AtmosConfig CreateSleepConfig(EulerFlowConfig eulerFlow)
    {
        AtmosConfig config = SimTestHelpers.CreateEulerConfig(eulerFlow);
        var defaults = new AtmosConfig();
        config.SleepDelay = 5f;
        config.SleepEpsilon = defaults.SleepEpsilon;
        config.DefaultDiffusionCoefficient = defaults.DefaultDiffusionCoefficient;
        config.VacuumThreshold = defaults.VacuumThreshold;
        return config;
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
