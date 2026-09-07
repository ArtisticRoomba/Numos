using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.GasReactions;
using Numos.CoreSim.Replay;
using Numos.CoreSim.Solvers;
using Numos.Maths;

namespace Numos.CoreSim.Benchmarks.Infrastructure;

internal sealed class SimulationWorkload : IDisposable
{
    internal const int Edge = 8;
    private readonly SolverDataStorage _sharedData = new();
    private readonly DefaultAtmosSolvers _solvers = new(Edge, Edge, Edge);

    internal SimulationWorkload(int chunks, int activeVoxels, int gases, bool reactions = false, bool condensation = false)
    {
        Kernel = new AtmosKernel(Edge, Edge, Edge);
        var config = new AtmosConfig { SleepThreshold = int.MaxValue, ThermalConductance = 2f };
        for (int gas = 0; gas < gases; gas++)
        {
            config.GasRegistry.Add(
                new GasProperties
                {
                    Name = $"Gas{gas:D2}",
                    DiffusionCoefficient = 0.02f,
                    MolarHeatCapacityAtConstantVolume = 20f + gas,
                    CondensationEnabled = condensation,
                    BoilingPoint = 373f,
                    MolarEnthalpyOfVaporization = 40000f
                });
        }

        if (reactions)
        {
            // Opposing reactions keep the mixture stable across BenchmarkDotNet's adaptively batched invocations.
            config.SolverConfigurations.Add(
                new GasReactionConfig(
                [
                    new LinearGasReaction(
                        new Dictionary<GasProperties, float> { [config.GasRegistry[0]] = 1f },
                        new Dictionary<GasProperties, float> { [config.GasRegistry[gases - 1]] = 1f },
                        0f,
                        200f,
                        1000f,
                        0.01f,
                        0.01f,
                        true,
                        true,
                        new HashSet<LinearGasReaction.LinearSpeedFactor>()),
                    new LinearGasReaction(
                        new Dictionary<GasProperties, float> { [config.GasRegistry[gases - 1]] = 1f },
                        new Dictionary<GasProperties, float> { [config.GasRegistry[0]] = 1f },
                        0f,
                        200f,
                        1000f,
                        0.01f,
                        0.01f,
                        true,
                        true,
                        new HashSet<LinearGasReaction.LinearSpeedFactor>())
                ]));
        }

        Kernel.SetAtmosConfig(config.CreateSnapshot());
        Config.Capture(Kernel.GetAtmosConfig());
        for (int index = 0; index < chunks; index++)
        {
            // An X-axis chain guarantees shared faces even in the smallest multi-chunk case.
            var position = new Int3(index, 0, 0);
            Kernel.CreateAndRegisterChunk(position, Edge, Edge, Edge, 1);
            var chunk = Kernel.GetChunkForDangerousAccess(position);
            chunk.SetChunkClassification(VoxelClassification.RoomSolid);
            chunk.Wake();
            for (ushort voxel = 0; voxel < activeVoxels; voxel++)
            {
                chunk.SetVoxelClassification(voxel, 0);
                for (int gas = 0; gas < gases; gas++)
                {
                    // Hold total moles roughly fixed as species count grows; retain composition gradients.
                    float moles = (20f + (voxel + index * 3 + gas) % 7) / gases;
                    GasInjectionSolver.Inject(chunk, voxel, gas, moles, 280f + (voxel + index) % 31, Config);
                }
            }

            chunk.WakeRoom(0);
            if (!chunk.IsAwake || chunk.ActiveAirCount != activeVoxels || chunk.ActiveGasCount != gases)
                throw new InvalidOperationException("Benchmark dimensions do not match the populated workload.");
        }

        Steps = _solvers.CreateSteps();
        Kernel.TickCount = AtmosSolverConstants.ThermodynamicsTickInterval - 1;
        Initial = Kernel.CaptureCheckpoint();
        RefreshContext();
    }

    internal AtmosKernel Kernel { get; }
    internal AtmosSolverConfigSnapshot Config { get; } = new();
    internal AtmosChunk[] Chunks { get; private set; } = [];
    internal AtmosSolverExecutionContext Context { get; private set; } = null!;
    internal SolverStep[] Steps { get; }
    internal AtmosSimulationCheckpoint Initial { get; }

    public void Dispose()
    {
        _solvers.Dispose();
        _sharedData.Clear();
        Config.ClearGasSolverData();
        Kernel.Dispose();
    }

    internal void Reset(int precedingStages = 0)
    {
        Kernel.RestoreCheckpoint(Initial);
        _solvers.ClearTransientState();
        _sharedData.Clear();
        RefreshContext();
        for (int stage = 0; stage < precedingStages; stage++)
            Steps[stage].Solver(Context);
    }

    private void RefreshContext()
    {
        Chunks = Kernel.GetChunkPositions().Select(Kernel.GetChunkForDangerousAccess).ToArray();
        Context = new AtmosSolverExecutionContext(Kernel, Chunks, Config, AtmosSolverConstants.ThermodynamicsTickInterval, _sharedData);
    }
}