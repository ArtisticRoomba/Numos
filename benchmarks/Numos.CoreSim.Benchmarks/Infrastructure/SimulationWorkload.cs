using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.EulerFlow;
using Numos.CoreSim.GasReactions;
using Numos.CoreSim.Replay;
using Numos.CoreSim.Solvers;
using Numos.Maths;

namespace Numos.CoreSim.Benchmarks.Infrastructure;

internal sealed class SimulationWorkload : IDisposable
{
    /// <summary>
    ///     The tick <see cref="Initial" /> is restored at, one short of a thermodynamics tick.
    /// </summary>
    internal const int InitialTickCount = AtmosSolverConstants.ThermodynamicsTickInterval - 1;

    /// <summary>
    ///     Moles every active voxel starts with, split evenly over the injected gases before the gradients scale it.
    /// </summary>
    internal const float VoxelMoles = 20f;
    private readonly DefaultAtmosSolvers _solvers;
    private readonly BenchmarkSolverStep? _boundaryFlowStep;

    internal SimulationWorkload(int chunks, int activeVoxels, int gases, bool reactions = false, bool condensation = false)
        : this(
            new ScalingWorkloadOptions
            {
                RegisteredChunkCount = chunks, AwakeChunkCount = chunks, ActiveVoxelFraction = activeVoxels / 512d,
                GasCount = gases, ReactionCount = reactions ? 2 : 0,
                ReactionWorkload = reactions ? ReactionWorkload.ActiveSparse : ReactionWorkload.None,
                CondensingGasCount = condensation ? gases : 0,
                SupersaturatedVoxelFraction = condensation ? 0.25d : 0d
            })
    {
    }

    internal SimulationWorkload(ScalingWorkloadOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.RegisteredChunkCount);
        ArgumentOutOfRangeException.ThrowIfNegative(options.AwakeChunkCount);
        if (options.AwakeChunkCount > options.RegisteredChunkCount)
            throw new ArgumentException("Awake chunks cannot exceed registered chunks.", nameof(options));

        if (options.GasCount <= 0 || options.CondensingGasCount < 0 || options.CondensingGasCount > options.GasCount ||
            options.InjectedGasCount <= 0 || options.InjectedGasCount > options.GasCount)
            throw new ArgumentException("Gas dimensions are invalid.", nameof(options));

        if (options.ActiveVoxelCount is < 0 or > AtmosChunkConstants.MaximumVoxelCount)
            throw new ArgumentException("Active voxel count is invalid.", nameof(options));

        Options = options;
        Kernel = new AtmosKernel(options.ChunkWidth, options.ChunkHeight, options.ChunkDepth)
        {
            TransportModel = options.TransportModel
        };
        _solvers = new DefaultAtmosSolvers(options.ChunkWidth, options.ChunkHeight, options.ChunkDepth);
        var config = CreateConfig(options);
        Kernel.SetAtmosConfig(config.CreateSnapshot());
        Config.Capture(Kernel.GetAtmosConfig());

        Int3[] positions = CreatePositions(options.RegisteredChunkCount, options.BoundaryTopology);
        for (int index = 0; index < positions.Length; index++)
            CreateChunk(options, positions[index], index, index < options.AwakeChunkCount);

        ThermodynamicsStep = new BenchmarkSolverStep("thermodynamics", _solvers.SolveThermodynamics);
        ThermalBoundaryStep = new BenchmarkSolverStep("thermal-boundary", _solvers.SolveThermalBoundary);
        ReactionsStep = new BenchmarkSolverStep("gas-reactions", _solvers.SolveGasReactions);
        switch (options.TransportModel)
        {
            case AtmosTransportModel.Legacy:
                FlowStep = new BenchmarkSolverStep("advection", _solvers.SolveAdvection);
                _boundaryFlowStep = new BenchmarkSolverStep("boundary-flow", _solvers.SolveBoundaryFlow);
                Steps = [FlowStep, _boundaryFlowStep.Value, ThermodynamicsStep, ThermalBoundaryStep, ReactionsStep];
                break;
            case AtmosTransportModel.Euler:
                FlowStep = new BenchmarkSolverStep(
                    "euler-flow",
                    context => LastFlowDiagnostics = _solvers.SolveEulerFlow(context));
                Steps = [FlowStep, ThermodynamicsStep, ThermalBoundaryStep, ReactionsStep];
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(options), options.TransportModel, "Unknown transport model.");
        }

        Kernel.TickCount = InitialTickCount;
        Initial = Kernel.CaptureCheckpoint();
        RefreshContext();
        BoundaryEventCount = GetBoundaryEventCount(options);
        ThermalBoundaryEdgeCount = GetThermalBoundaryEdgeCount(options);
    }

    internal ScalingWorkloadOptions Options { get; }
    internal AtmosKernel Kernel { get; }
    internal AtmosSolverConfigSnapshot Config { get; } = new();
    internal AtmosChunk[] Chunks { get; private set; } = [];
    internal AtmosSolverExecutionContext Context { get; private set; } = null!;

    /// <summary>
    ///     The built-in stages of the selected transport model, in pipeline order. Index into it only through the
    ///     named step properties: the layout differs between models.
    /// </summary>
    internal BenchmarkSolverStep[] Steps { get; }

    /// <summary>
    ///     The stage that moves gas inside chunks: <c>advection</c> under the legacy model, <c>euler-flow</c> under
    ///     Euler.
    /// </summary>
    internal BenchmarkSolverStep FlowStep { get; }

    /// <summary>
    ///     The legacy <c>boundary-flow</c> stage. Euler has no separate cross-chunk stage, so this throws there.
    /// </summary>
    internal BenchmarkSolverStep BoundaryFlowStep =>
        _boundaryFlowStep ?? throw new InvalidOperationException("The Euler transport model has no boundary-flow stage.");

    internal BenchmarkSolverStep ThermodynamicsStep { get; }
    internal BenchmarkSolverStep ThermalBoundaryStep { get; }
    internal BenchmarkSolverStep ReactionsStep { get; }

    /// <summary>
    ///     What the most recent <see cref="FlowStep" /> call did under the Euler model; all zeros under legacy.
    /// </summary>
    internal AtmosFlowDiagnostics LastFlowDiagnostics { get; private set; }

    internal AtmosSimulationCheckpoint Initial { get; }
    internal int BoundaryEventCount { get; }
    internal int ThermalBoundaryEdgeCount { get; }

    public void Dispose()
    {
        _solvers.Dispose();
        Config.SolverData.Clear();
        Kernel.Dispose();
    }

    /// <summary>
    ///     Restores <see cref="Initial" /> and, when a step is given, runs every step that precedes it so its
    ///     transient inputs (boundary events, thermal edges) exist again.
    /// </summary>
    internal void Reset(BenchmarkSolverStep? before = null)
    {
        Restore(Initial);
        if (before == null)
            return;

        int end = Array.IndexOf(Steps, before.Value);
        if (end < 0)
            throw new ArgumentException("The step does not belong to this workload's transport model.", nameof(before));

        for (int stage = 0; stage < end; stage++)
            Steps[stage].Solver(Context);
    }

    /// <summary>
    ///     Installs a checkpoint captured from this workload at <see cref="InitialTickCount" /> and drops solver
    ///     scratch state, exactly as <see cref="Reset" /> does for <see cref="Initial" />.
    /// </summary>
    internal void Restore(AtmosSimulationCheckpoint checkpoint)
    {
        Kernel.RestoreCheckpoint(checkpoint, InitialTickCount);
        _solvers.ClearTransientState();
        Config.SolverData.Clear();
        RefreshContext();
    }

    /// <summary>
    ///     Captures the current grid so a benchmark can return to a state it prepared itself.
    /// </summary>
    internal AtmosSimulationCheckpoint Capture()
    {
        return Kernel.CaptureCheckpoint();
    }

    /// <summary>
    ///     Adds gas to one voxel of the current grid, the way the workload populates its chunks.
    /// </summary>
    internal void Inject(int chunkIndex, Int3 voxel, int gas, float moles, float temperature)
    {
        AtmosChunk chunk = Chunks[chunkIndex];
        GasInjectionSolver.Inject(chunk, chunk.GetIndex(voxel), gas, moles, temperature, Config);
    }

    internal void SetTickCount(int tickCount)
    {
        Kernel.TickCount = tickCount;
        RefreshContext();
    }

    private static AtmosConfig CreateConfig(ScalingWorkloadOptions options)
    {
        var config = new AtmosConfig { SleepThreshold = int.MaxValue, ThermalConductance = 2f };
        for (int gas = 0; gas < options.GasCount; gas++)
        {
            config.GasRegistry.Add(
                new GasProperties
                {
                    Name = $"Gas{gas:D3}", DiffusionCoefficient = 0.02f,
                    MolarHeatCapacityAtConstantVolume = 20f + gas % 11,
                    MolarMass = 0.028f + gas % 5 * 0.004f,
                    CondensationEnabled = gas < options.CondensingGasCount,
                    BoilingPoint = 373f, MolarEnthalpyOfVaporization = 40000f
                });
        }

        if (options.ReactionCount > 0)
            config.SolverConfigurations.Add(new GasReactionConfig(CreateReactions(config, options)));

        return config;
    }

    private static IEnumerable<LinearGasReaction> CreateReactions(AtmosConfig config, ScalingWorkloadOptions options)
    {
        for (int reaction = 0; reaction < options.ReactionCount; reaction++)
        {
            bool dense = options.ReactionWorkload == ReactionWorkload.ActiveDense;
            int inputCount = dense
                ? Math.Max(1, options.GasCount / 2)
                : options.GasCount == 2
                    ? 1
                    : Math.Min(2, options.GasCount);

            var input = new Dictionary<GasProperties, float>();
            for (int gas = 0; gas < inputCount; gas++)
                input[config.GasRegistry[(gas + reaction) % options.GasCount]] = 0.01f;

            int outputId = (reaction + inputCount) % options.GasCount;
            while (input.ContainsKey(config.GasRegistry[outputId]) && options.GasCount > 1)
                outputId = (outputId + 1) % options.GasCount;

            var output = new Dictionary<GasProperties, float>();
            if (!input.ContainsKey(config.GasRegistry[outputId]))
                output[config.GasRegistry[outputId]] = 0.01f;

            bool inactive = options.ReactionWorkload == ReactionWorkload.Inactive;
            yield return new LinearGasReaction(
                input,
                output,
                0f,
                inactive ? 500f : 200f,
                inactive ? 900f : 1000f,
                0.01f,
                0.01f,
                true,
                true,
                new HashSet<LinearGasReaction.LinearSpeedFactor>());
        }
    }

    private void CreateChunk(ScalingWorkloadOptions options, Int3 position, int chunkIndex, bool awake)
    {
        Kernel.CreateAndRegisterChunk(position, options.ChunkWidth, options.ChunkHeight, options.ChunkDepth);
        var chunk = Kernel.GetChunkForDangerousAccess(position);
        if (!awake)
        {
            chunk.SetChunkClassification(VoxelClassification.RoomSolid);
            return;
        }

        int activeVoxels = options.ActiveVoxelCount;
        int supersaturatedVoxels = (int)Math.Round(activeVoxels * options.SupersaturatedVoxelFraction);
        chunk.SetChunkClassification(0);
        for (int voxel = activeVoxels; voxel < options.VoxelCount; voxel++)
            chunk.SetVoxelClassification((ushort)voxel, VoxelClassification.RoomSolid);

        chunk.Wake();
        int injectedGases = options.InjectedGasCount;
        for (ushort voxel = 0; voxel < activeVoxels; voxel++)
        {
            for (int gas = 0; gas < injectedGases; gas++)
            {
                float baseMoles = VoxelMoles / injectedGases;
                float moles = gas < options.CondensingGasCount
                    ? voxel < supersaturatedVoxels ? 80f : 0.02f
                    : baseMoles *
                      GradientScale(options.PressureGradient, chunkIndex) *
                      LocalPressureScale(options.PressureGradient, voxel, gas, options.RandomSeed);

                float temperature = 300f + TemperatureOffset(options.TemperatureGradient, chunkIndex, voxel);
                GasInjectionSolver.Inject(chunk, voxel, gas, moles, temperature, Config);
            }
        }

        int expectedActiveGases = activeVoxels == 0 ? 0 : injectedGases;
        if (!chunk.IsAwake || chunk.ActiveAirCount != activeVoxels || chunk.ActiveGasCount != expectedActiveGases)
            throw new InvalidOperationException("Benchmark dimensions do not match the populated workload.");
    }

    private static float GradientScale(GradientStrength strength, int chunkIndex)
    {
        return strength switch
        {
            GradientStrength.None => 1f,
            GradientStrength.Small => chunkIndex % 2 == 0 ? 1f : 1.05f,
            _ => chunkIndex % 2 == 0 ? 0.5f : 1.5f
        };
    }

    private static float TemperatureOffset(GradientStrength strength, int chunkIndex, int voxel)
    {
        return strength switch
        {
            GradientStrength.None => 0f,
            GradientStrength.Small => (chunkIndex + voxel & 1) == 0 ? -2f : 2f,
            _ => (chunkIndex + voxel & 1) == 0 ? -40f : 40f
        };
    }

    private static float LocalPressureScale(GradientStrength strength, int voxel, int gas, int seed)
    {
        return strength == GradientStrength.None ? 1f : 1f + (voxel + gas + seed & 3) * 0.01f;
    }

    internal static int GetBoundaryEventCount(ScalingWorkloadOptions options)
    {
        return options.AwakeChunkCount * GetBoundaryVoxelCount(options);
    }

    internal static int GetThermalBoundaryEdgeCount(ScalingWorkloadOptions options)
    {
        Int3[] positions = CreatePositions(options.RegisteredChunkCount, options.BoundaryTopology)
            .Take(options.AwakeChunkCount).ToArray();

        return CountAdjacentPairs(positions) * options.ChunkWidth * options.ChunkHeight;
    }

    private static Int3[] CreatePositions(int count, BoundaryTopology topology)
    {
        var positions = new Int3[count];
        int side2 = (int)Math.Ceiling(Math.Sqrt(count));
        int side3 = (int)Math.Ceiling(Math.Pow(count, 1d / 3d));
        for (int index = 0; index < count; index++)
        {
            positions[index] = topology switch
            {
                BoundaryTopology.Isolated => new Int3(index * 2, 0, 0),
                BoundaryTopology.Line => new Int3(index, 0, 0),
                BoundaryTopology.Grid2D => new Int3(index % side2, index / side2, 0),
                BoundaryTopology.Grid3D => new Int3(index % side3, index / side3 % side3, index / (side3 * side3)),
                _ => throw new ArgumentOutOfRangeException(nameof(topology))
            };
        }

        return positions;
    }

    private static int CountAdjacentPairs(IEnumerable<Int3> positions)
    {
        HashSet<Int3> set = positions.ToHashSet();
        int count = 0;
        foreach (var position in set)
        {
            if (set.Contains(position + Int3.PosX)) count++;
            if (set.Contains(position + Int3.PosY)) count++;
            if (set.Contains(position + Int3.PosZ)) count++;
        }

        return count;
    }

    private static int GetBoundaryVoxelCount(ScalingWorkloadOptions options)
    {
        int interiorWidth = Math.Max(0, options.ChunkWidth - 2);
        int interiorHeight = Math.Max(0, options.ChunkHeight - 2);
        int interiorDepth = options.ChunkDepth > 1 ? Math.Max(0, options.ChunkDepth - 2) : 1;
        return options.VoxelCount - interiorWidth * interiorHeight * interiorDepth;
    }

    private void RefreshContext()
    {
        Chunks = Kernel.GetChunkPositions().Select(Kernel.GetChunkForDangerousAccess).ToArray();
        Context = new AtmosSolverExecutionContext(Kernel, Chunks, Config, Kernel.TickCount + 1);
    }
}

/// <summary>
///     One built-in stage as the benchmarks invoke it: its pipeline name and the call that runs it.
/// </summary>
internal readonly record struct BenchmarkSolverStep(string Name, Action<AtmosSolverExecutionContext> Solver);