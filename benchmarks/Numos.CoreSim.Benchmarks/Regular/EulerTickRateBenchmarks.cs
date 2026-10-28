using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using Numos.API;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.Maths;

namespace Numos.CoreSim.Benchmarks.Regular;

/// <summary>
///     The scenes <see cref="EulerTickRateBenchmarks" /> runs.
/// </summary>
public enum EulerTickRateScene
{
    /// <summary>
    ///     A uniform 16 m cube of room air that never sleeps, so every tick pays the stage's fixed cost.
    /// </summary>
    QuietRoom,

    /// <summary>
    ///     A 256 m tube with 2 atm on its left quarter and 1 atm on the rest.
    /// </summary>
    ShockTube,

    /// <summary>
    ///     A 16 m cube with a hot 4 atm sphere in the middle.
    /// </summary>
    Blast,

    /// <summary>
    ///     A pressurized 16 m cube with a 4 m by 4 m hole in one wall that opens onto void.
    /// </summary>
    VacuumBreach
}

/// <summary>
///     Compares the Euler transport model at 20 Hz and 960 Hz over the same simulated second.
/// </summary>
/// <remarks>
///     One operation is one simulated second, so the reported mean is nanoseconds per simulated second and the
///     allocation column is bytes per simulated second. A fresh scene is built before every iteration, outside the
///     timing, so a blast or breach does not settle between iterations.
///     `[IterationSetup]` limits BenchmarkDotNet to one invocation per iteration, so a short job would
///     measure mostly untiered code. <see cref="WarmUp" /> runs the scene for a fixed wall time first, so every
///     measured iteration runs tier-1 code.
/// </remarks>
[BenchmarkCategory("Regular", "Solvers", "EulerTickRate")]
[MemoryDiagnoser]
public class EulerTickRateBenchmarks
{
    private static readonly TimeSpan WarmUpTime = TimeSpan.FromSeconds(3);
    private const int CubeSize = 16;
    private const int TubeLength = 256;
    private const float Temperature = AtmosPhysicalConstants.RoomTemperature;
    private const float Atmosphere = AtmosPhysicalConstants.StandardAtmosphericPressure;
    private const string AirName = "Air";

    private AtmosSimulation? _simulation;

    /// <summary>
    ///     Gets or sets the tick rate of the world, in ticks per second.
    /// </summary>
    [Params(20, 960)]
    public int TickRate { get; set; }

    /// <summary>
    ///     Gets or sets the scene.
    /// </summary>
    [ParamsSource(nameof(Scenes))]
    public EulerTickRateScene Scene { get; set; }

    /// <summary>
    ///     Gets the scenes to run, reduced to the quiet room when NUMOS_BENCHMARK_SMOKE=1.
    /// </summary>
    public IEnumerable<EulerTickRateScene> Scenes =>
        Infrastructure.ScalingBenchmark.Smoke ? [EulerTickRateScene.QuietRoom] : Enum.GetValues<EulerTickRateScene>();

    /// <summary>
    ///     Builds and runs the scene repeatedly for a fixed wall time so the JIT promotes the solver methods.
    /// </summary>
    [GlobalSetup]
    public void WarmUp()
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < WarmUpTime)
        {
            BuildScene();
            SimulatedSecond();
        }

        Cleanup();
    }

    /// <summary>
    ///     Builds the scene for the next iteration.
    /// </summary>
    [IterationSetup]
    public void BuildScene()
    {
        _simulation?.Dispose();
        _simulation = Scene switch
        {
            EulerTickRateScene.QuietRoom => BuildQuietRoom(TickRate),
            EulerTickRateScene.ShockTube => BuildShockTube(TickRate),
            EulerTickRateScene.Blast => BuildBlast(TickRate),
            EulerTickRateScene.VacuumBreach => BuildVacuumBreach(TickRate),
            _ => throw new ArgumentOutOfRangeException(nameof(Scene), Scene, null)
        };
    }

    /// <summary>
    ///     Releases the last scene.
    /// </summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _simulation?.Dispose();
        _simulation = null;
    }

    /// <summary>
    ///     Runs one simulated second.
    /// </summary>
    [Benchmark]
    public void SimulatedSecond()
    {
        var world = _simulation!.World;
        for (int tick = 0; tick < TickRate; tick++)
            world.Tick();
    }

    private static AtmosConfig CreateConfig()
    {
        return new AtmosConfig
        {
            DefaultTemperatureFallback = Temperature,
            VoxelVolume = 1f,
            DefaultDiffusionCoefficient = 0f,
            VacuumThreshold = 0f,
            SleepDelay = float.MaxValue,
            SleepEpsilon = 0f,
            GasRegistry =
            [
                new GasProperties
                {
                    Name = AirName,
                    MolarMass = 0.02897f,
                    MolarHeatCapacityAtConstantVolume =
                        AtmosPhysicalConstants.IdealDiatomicMolarHeatCapacityAtConstantVolume,
                    DiffusionCoefficient = 0f
                }
            ]
        };
    }

    private static float MolesAt(float pressure, float temperature)
    {
        return pressure / (AtmosPhysicalConstants.MolarGasConstant * temperature);
    }

    private static AtmosSimulation CreateSimulation(int width, int height, int depth, int tickRate)
    {
        return new AtmosSimulation(CreateConfig(), width, height, depth, AtmosTransportModel.Euler, tickRate);
    }

    private static AtmosChunkHandle OpenChunk(AtmosSimulation simulation, Int3 position)
    {
        var chunk = simulation.CreateAndRegisterChunk(position);
        simulation.SetChunkClassification(chunk, new VoxelClassification(1));
        return chunk;
    }

    private static AtmosSimulation BuildQuietRoom(int tickRate)
    {
        var simulation = CreateSimulation(CubeSize, CubeSize, CubeSize, tickRate);
        var chunk = OpenChunk(simulation, default);
        float moles = MolesAt(Atmosphere, Temperature);
        for (int z = 0; z < CubeSize; z++)
        for (int y = 0; y < CubeSize; y++)
        for (int x = 0; x < CubeSize; x++)
            simulation.AddGasToVoxel(chunk, x, y, z, AirName, moles, Temperature);

        return simulation;
    }

    private static AtmosSimulation BuildShockTube(int tickRate)
    {
        var simulation = CreateSimulation(TubeLength, 1, 1, tickRate);
        var chunk = OpenChunk(simulation, default);
        float high = MolesAt(2f * Atmosphere, Temperature);
        float low = MolesAt(Atmosphere, Temperature);
        for (int x = 0; x < TubeLength; x++)
            simulation.AddGasToVoxel(chunk, x, 0, 0, AirName, x < TubeLength / 4 ? high : low, Temperature);

        return simulation;
    }

    private static AtmosSimulation BuildBlast(int tickRate)
    {
        var simulation = CreateSimulation(CubeSize, CubeSize, CubeSize, tickRate);
        var chunk = OpenChunk(simulation, default);
        float ambient = MolesAt(Atmosphere, Temperature);
        float hot = MolesAt(4f * Atmosphere, 600f);
        const float radius = 4f;
        const float centre = CubeSize / 2f;
        for (int z = 0; z < CubeSize; z++)
        for (int y = 0; y < CubeSize; y++)
        for (int x = 0; x < CubeSize; x++)
        {
            float dx = x + 0.5f - centre;
            float dy = y + 0.5f - centre;
            float dz = z + 0.5f - centre;
            bool inside = dx * dx + dy * dy + dz * dz < radius * radius;
            simulation.AddGasToVoxel(
                chunk, x, y, z, AirName, inside ? hot : ambient, inside ? 600f : Temperature);
        }

        return simulation;
    }

    private static AtmosSimulation BuildVacuumBreach(int tickRate)
    {
        var simulation = CreateSimulation(CubeSize, CubeSize, CubeSize, tickRate);
        var chunk = OpenChunk(simulation, default);
        float moles = MolesAt(Atmosphere, Temperature);
        for (int z = 0; z < CubeSize; z++)
        for (int y = 0; y < CubeSize; y++)
        for (int x = 0; x < CubeSize; x++)
            simulation.AddGasToVoxel(chunk, x, y, z, AirName, moles, Temperature);

        // The breach is a 4 x 4 patch of void on the +X face. The rest of that face stays solid wall.
        for (int z = 0; z < CubeSize; z++)
        for (int y = 0; y < CubeSize; y++)
        {
            bool hole = y is >= 6 and < 10 && z is >= 6 and < 10;
            simulation.SetVoxelClassification(
                chunk, CubeSize - 1, y, z,
                new VoxelClassification(hole ? VoxelClassification.RoomVoid : VoxelClassification.RoomSolid));
        }

        return simulation;
    }
}
