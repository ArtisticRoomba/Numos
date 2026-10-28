using BenchmarkDotNet.Attributes;
using Numos.API;
using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.Maths;

namespace Numos.CoreSim.Benchmarks.Scaling;

/// <summary>
///     Measures per-tick cost at 960 Hz, where the idle world runs no stage and the Euler-only world runs the Euler stage every tick.
/// </summary>
[BenchmarkCategory("PR", "TickOverhead")]
public class WorldTickOverheadBenchmarks
{
    private AtmosWorld _idle = null!;
    private AtmosWorld _eulerOnly = null!;

    /// <summary>
    ///     Builds a world with every stage disabled and a world that runs only the Euler stage, with its interval set to 1 so it is due every tick.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        _idle = CreateWorld();
        foreach (var step in _idle.Solvers.Steps.ToArray())
            _idle.Solvers.SetEnabled(step.Name, false);

        _eulerOnly = CreateWorld();
        foreach (var step in _eulerOnly.Solvers.Steps.ToArray())
        {
            if (step.Name != AtmosBuiltInSolvers.EulerFlow)
                _eulerOnly.Solvers.SetEnabled(step.Name, false);
        }

        _eulerOnly.Solvers.SetInterval(AtmosBuiltInSolvers.EulerFlow, 1);
    }

    /// <summary>
    ///     Releases both worlds.
    /// </summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _idle.Dispose();
        _eulerOnly.Dispose();
    }

    /// <summary>
    ///     Measures a tick on which no stage is due.
    /// </summary>
    [Benchmark]
    public void IdleTick()
    {
        _idle.Tick();
    }

    /// <summary>
    ///     Measures a tick that runs only the Euler stage.
    /// </summary>
    [Benchmark]
    public void EulerOnlyTick()
    {
        _eulerOnly.Tick();
    }

    private static AtmosWorld CreateWorld()
    {
        var config = new AtmosConfig
        {
            GasRegistry = [new GasProperties { Name = "Air", MolarHeatCapacityAtConstantVolume = 20.8f }]
        };
        var world = new AtmosWorld(config, AtmosTransportModel.Euler, 960);
        var simulation = world.CreateSimulation(8, 8, 8);
        for (int x = 0; x < 4; x++)
        {
            var chunk = simulation.CreateAndRegisterChunk(new Int3(x, 0, 0));
            simulation.SetChunkClassification(chunk, new VoxelClassification(1));
            simulation.AddGasToVoxel(chunk, 0, "Air", 1f, 293.15f);
        }

        return world;
    }
}
