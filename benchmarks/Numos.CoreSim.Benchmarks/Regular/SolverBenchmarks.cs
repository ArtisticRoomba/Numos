using BenchmarkDotNet.Attributes;
using Numos.CoreSim.Benchmarks.Infrastructure;
using Numos.CoreSim.Datatypes.Events;
using Numos.CoreSim.Replay;
using Numos.CoreSim.Solvers;
using Numos.Maths;

namespace Numos.CoreSim.Benchmarks.Regular;

/// <summary>
///     Registry width and species present in <see cref="SolverBenchmarks.EulerFlowBenchmarks" />.
/// </summary>
public enum EulerMixture
{
    /// <summary>
    ///     One registered gas, present everywhere.
    /// </summary>
    OneGas,

    /// <summary>
    ///     Thirty-two registered gases with only the first present, so the cost of registry width shows on its own.
    /// </summary>
    OneOfThirtyTwo,

    /// <summary>
    ///     Four registered gases, all present in equal parts.
    /// </summary>
    FourGasMix
}

/// <summary>
///     Groups independently selectable benchmarks for built-in solver stages.
/// </summary>
public static class SolverBenchmarks
{
    /// <summary>
    ///     Measures complete kernel ticks over the normal solver cadence.
    /// </summary>
    [BenchmarkCategory("Regular", "Solvers", "Tick")]
    public class TickBenchmarks : SolverBenchmarkBase
    {
        /// <summary>
        ///     Measures one kernel tick, including scheduling and configuration capture.
        /// </summary>
        [Benchmark]
        public void Tick()
        {
            Workload.Kernel.Tick();
        }

        /// <summary>
        ///     Restores the representative initial state before a transient tick.
        /// </summary>
        [IterationSetup(Target = nameof(Tick_Transient))]
        public void ResetTransient()
        {
            Workload.Reset();
        }

        /// <summary>
        ///     Measures a representative tick from a fixed initial state.
        /// </summary>
        [Benchmark]
        public void Tick_Transient()
        {
            Workload.Kernel.Tick();
        }
    }

    /// <summary>
    ///     Measures the advection solver independently and together with its boundary consumer.
    /// </summary>
    [BenchmarkCategory("Regular", "Solvers", "Advection")]
    public class AdvectionBenchmarks : SolverBenchmarkBase
    {
        /// <summary>
        ///     Measures parallel intra-chunk gas transport and boundary-event production.
        /// </summary>
        [Benchmark]
        public void Advection()
        {
            Workload.FlowStep.Solver(Workload.Context);
        }

        /// <summary>
        ///     Measures intra-chunk transport followed by cross-chunk boundary flow.
        /// </summary>
        /// <remarks>
        ///     Boundary flow consumes transient events, so its repeatable benchmark includes advection as its producer.
        /// </remarks>
        [Benchmark]
        public void AdvectionAndBoundaryFlow()
        {
            Workload.FlowStep.Solver(Workload.Context);
            Workload.BoundaryFlowStep.Solver(Workload.Context);
        }

        /// <summary>
        ///     Restores the initial gradients before transient advection.
        /// </summary>
        [IterationSetup(Target = nameof(Advection_Transient))]
        public void ResetTransient()
        {
            Workload.Reset();
        }

        /// <summary>
        ///     Measures advection from fixed initial gradients.
        /// </summary>
        [Benchmark]
        public void Advection_Transient()
        {
            Workload.FlowStep.Solver(Workload.Context);
        }
    }

    /// <summary>
    ///     Measures the Euler transport stage in one dense 16×16×16 chunk of room air.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Every case keeps a live pressure feature. Once a chunk's air has evened out the stage has nothing to
    ///         do with it: the pipeline puts it to sleep and skips it, and before that point the stage is only
    ///         substepping over uniform air, which times the sweep loop rather than transport. So
    ///         <see cref="EulerFlow" /> restores a chunk the blast has already rung through for
    ///         <see cref="WarmUpTicks" /> ticks and re-seeds a small central pulse before every solve, while
    ///         <see cref="EulerFlow_Transient" /> restores the fresh blast.
    ///     </para>
    ///     <para>
    ///         The parameter varies how many gases the registry holds and how many are present, which the stage's
    ///         per-species rows pay for separately; see <see cref="EulerMixture" />.
    ///     </para>
    /// </remarks>
    [BenchmarkCategory("Regular", "Solvers", "EulerFlow")]
    [MemoryDiagnoser]
    public class EulerFlowBenchmarks
    {
        /// <summary>
        ///     Ticks the blast is advanced before the steady-state checkpoint is taken.
        /// </summary>
        public const int WarmUpTicks = 32;

        private const int ChunkSize = 16;
        private const float BlastRadius = 4f;
        private static readonly Int3 Centre = new(ChunkSize / 2, ChunkSize / 2, ChunkSize / 2);
        private AtmosSimulationCheckpoint _blast = null!;
        private int _presentGases;
        private AtmosSimulationCheckpoint _settled = null!;

        /// <summary>
        ///     Gets or sets the registry width and present species.
        /// </summary>
        [ParamsSource(nameof(Mixtures))]
        public EulerMixture Mixture { get; set; }

        /// <summary>
        ///     Gets the mixtures to run, reduced to the single-gas case when NUMOS_BENCHMARK_SMOKE=1.
        /// </summary>
        public IEnumerable<EulerMixture> Mixtures =>
            ScalingBenchmark.Smoke ? [EulerMixture.OneGas] : Enum.GetValues<EulerMixture>();

        internal SimulationWorkload Workload { get; private set; } = null!;

        /// <summary>
        ///     Builds the chunk, seeds the blast, and captures both the fresh and the rung-through state.
        /// </summary>
        [GlobalSetup]
        public void Setup()
        {
            (int registered, int present) = Mixture switch
            {
                EulerMixture.OneGas => (1, 1),
                EulerMixture.OneOfThirtyTwo => (32, 1),
                EulerMixture.FourGasMix => (4, 4),
                _ => throw new ArgumentOutOfRangeException(nameof(Mixture), Mixture, null)
            };

            _presentGases = present;
            Workload = new SimulationWorkload(
                new ScalingWorkloadOptions
                {
                    TransportModel = AtmosTransportModel.Euler,
                    ChunkWidth = ChunkSize, ChunkHeight = ChunkSize, ChunkDepth = ChunkSize,
                    RegisteredChunkCount = 1, AwakeChunkCount = 1,
                    GasCount = registered, PresentGasCount = present,
                    PressureGradient = GradientStrength.None, TemperatureGradient = GradientStrength.None,
                    BoundaryTopology = BoundaryTopology.Isolated
                });

            // A hot sphere at four times ambient pressure: twice the moles at twice the temperature.
            float ambientPerGas = SimulationWorkload.VoxelMoles / present;
            for (int z = 0; z < ChunkSize; z++)
            for (int y = 0; y < ChunkSize; y++)
            for (int x = 0; x < ChunkSize; x++)
            {
                float dx = x + 0.5f - Centre.X;
                float dy = y + 0.5f - Centre.Y;
                float dz = z + 0.5f - Centre.Z;
                if (dx * dx + dy * dy + dz * dz >= BlastRadius * BlastRadius)
                    continue;

                for (int gas = 0; gas < present; gas++)
                    Workload.Inject(0, new Int3(x, y, z), gas, ambientPerGas, 600f);
            }

            _blast = Workload.Capture();
            for (int tick = 0; tick < WarmUpTicks; tick++)
                Workload.FlowStep.Solver(Workload.Context);

            if (Workload.LastFlowDiagnostics.Substeps == 0)
                throw new InvalidOperationException("The Euler stage did not advance the warmed-up chunk.");

            _settled = Workload.Capture();
        }

        /// <summary>
        ///     Releases chunk storage and worker buffers after the selected case.
        /// </summary>
        [GlobalCleanup]
        public void Cleanup()
        {
            Workload.Dispose();
        }

        /// <summary>
        ///     Restores the rung-through chunk and adds half a voxel of air to its centre so the solve has a front.
        /// </summary>
        [IterationSetup(Target = nameof(EulerFlow))]
        public void ReseedPulse()
        {
            Workload.Restore(_settled);
            float pulsePerGas = 0.5f * SimulationWorkload.VoxelMoles / _presentGases;
            for (int gas = 0; gas < _presentGases; gas++)
                Workload.Inject(0, Centre, gas, pulsePerGas, 300f);
        }

        /// <summary>
        ///     Measures one Euler tick over a chunk that has settled from the blast, with a fresh central pulse.
        /// </summary>
        [Benchmark]
        public void EulerFlow()
        {
            Workload.FlowStep.Solver(Workload.Context);
        }

        /// <summary>
        ///     Restores the fresh blast before a transient solve.
        /// </summary>
        [IterationSetup(Target = nameof(EulerFlow_Transient))]
        public void ResetTransient()
        {
            Workload.Restore(_blast);
        }

        /// <summary>
        ///     Measures the first Euler tick of the blast, the tick with the fastest front and the most substeps.
        /// </summary>
        [Benchmark]
        public void EulerFlow_Transient()
        {
            Workload.FlowStep.Solver(Workload.Context);
        }
    }

    /// <summary>
    ///     Measures the thermodynamics solver independently and together with its boundary consumer.
    /// </summary>
    [BenchmarkCategory("Regular", "Solvers", "Thermodynamics")]
    public class ThermodynamicsBenchmarks : SolverBenchmarkBase
    {
        /// <summary>
        ///     Measures parallel thermal diffusion, phase change, and thermal-boundary-event production.
        /// </summary>
        [Benchmark]
        public void Thermodynamics()
        {
            Workload.ThermodynamicsStep.Solver(Workload.Context);
        }

        /// <summary>
        ///     Measures thermodynamics followed by cross-chunk heat exchange.
        /// </summary>
        /// <remarks>
        ///     Thermal boundary consumes transient events, so its repeatable benchmark includes thermodynamics as its producer.
        /// </remarks>
        [Benchmark]
        public void ThermodynamicsAndThermalBoundary()
        {
            Workload.ThermodynamicsStep.Solver(Workload.Context);
            Workload.ThermalBoundaryStep.Solver(Workload.Context);
        }

        /// <summary>
        ///     Restores initial temperature and saturation gradients before thermodynamics.
        /// </summary>
        [IterationSetup(Target = nameof(Thermodynamics_Transient))]
        public void ResetTransient()
        {
            Workload.Reset();
        }

        /// <summary>
        ///     Measures thermodynamics from a fixed initial state.
        /// </summary>
        [Benchmark]
        public void Thermodynamics_Transient()
        {
            Workload.ThermodynamicsStep.Solver(Workload.Context);
        }
    }

    /// <summary>
    ///     Measures the configured reaction solver.
    /// </summary>
    [BenchmarkCategory("Regular", "Solvers", "Reactions")]
    public class ReactionBenchmarks : SolverBenchmarkBase
    {
        /// <summary>
        ///     Measures two enabled, opposing reactions that preserve composition between invocations.
        /// </summary>
        [Benchmark]
        public void Reactions()
        {
            Workload.ReactionsStep.Solver(Workload.Context);
        }

        /// <summary>
        ///     Restores reactants before a transient reaction solve.
        /// </summary>
        [IterationSetup(Target = nameof(Reactions_Transient))]
        public void ResetTransient()
        {
            Workload.Reset();
        }

        /// <summary>
        ///     Measures reactions from a fixed initial mixture.
        /// </summary>
        [Benchmark]
        public void Reactions_Transient()
        {
            Workload.ReactionsStep.Solver(Workload.Context);
        }
    }

    /// <summary>
    ///     Measures thermal diffusion without stage dispatch or phase change.
    /// </summary>
    [BenchmarkCategory("Regular", "Solvers", "ThermalDiffusion")]
    public class ThermalDiffusionBenchmarks : SolverBenchmarkBase
    {
        private readonly ThermalBoundaryEvent[] _boundaryEvents = new ThermalBoundaryEvent[512];
        private readonly ThermalDiffusionSolver _solver = new();

        /// <summary>
        ///     Measures serial direct calls to thermal diffusion for every configured chunk.
        /// </summary>
        [Benchmark]
        public void ThermalDiffusion()
        {
            foreach (var chunk in Workload.Chunks)
                _solver.Solve(chunk, Workload.Config, _boundaryEvents, int.MaxValue);
        }
    }

    /// <summary>
    ///     Measures phase change without stage dispatch or thermal diffusion.
    /// </summary>
    [BenchmarkCategory("Regular", "Solvers", "PhaseChange")]
    public class PhaseChangeBenchmarks : SolverBenchmarkBase
    {
        private readonly PhaseChangeSolver _solver = new();

        /// <summary>
        ///     Measures serial direct calls to phase change for every configured chunk.
        /// </summary>
        [Benchmark]
        public void PhaseChange()
        {
            foreach (var chunk in Workload.Chunks)
                _solver.Solve(chunk, Workload.Config, 1);
        }
    }

    /// <summary>
    ///     Owns the shared parameterized simulation fixture for one selected solver benchmark class.
    /// </summary>
    public abstract class SolverBenchmarkBase : ScalingBenchmark
    {
        /// <summary>
        ///     Gets the fixture owned by the current BenchmarkDotNet case.
        /// </summary>
        internal SimulationWorkload Workload { get; private set; } = null!;

        /// <summary>
        ///     Creates one fixture for the selected solver and parameter combination.
        /// </summary>
        [GlobalSetup]
        public void Setup()
        {
            Workload = new SimulationWorkload(ChunkCount, ActiveVoxelsPerChunk, GasCount, true, true);
        }

        /// <summary>
        ///     Releases chunk storage and worker buffers after the selected benchmark case.
        /// </summary>
        [GlobalCleanup]
        public void Cleanup()
        {
            Workload.Dispose();
        }
    }
}