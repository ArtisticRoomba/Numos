using BenchmarkDotNet.Attributes;
using Numos.CoreSim.Benchmarks.Infrastructure;
using Numos.CoreSim.Datatypes.Events;
using Numos.CoreSim.Solvers;

namespace Numos.CoreSim.Benchmarks.Regular;

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
            Workload.Steps[0].Solver(Workload.Context);
        }

        /// <summary>
        ///     Measures intra-chunk transport followed by serial cross-chunk transport.
        /// </summary>
        /// <remarks>
        ///     Boundary flow consumes transient events, so its repeatable benchmark includes advection as its producer.
        /// </remarks>
        [Benchmark]
        public void AdvectionAndBoundaryFlow()
        {
            Workload.Steps[0].Solver(Workload.Context);
            Workload.Steps[1].Solver(Workload.Context);
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
            Workload.Steps[2].Solver(Workload.Context);
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
            Workload.Steps[2].Solver(Workload.Context);
            Workload.Steps[3].Solver(Workload.Context);
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
            Workload.Steps[4].Solver(Workload.Context);
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
                _solver.Solve(chunk, Workload.Config, _boundaryEvents);
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
                _solver.Solve(chunk, Workload.Config);
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