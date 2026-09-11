using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.GasReactions;
using Numos.CoreSim.Replay;
using Numos.CoreSim.Solvers;
using Numos.Maths;

namespace Numos.CoreSim.Benchmarks.Infrastructure;

internal sealed class SimulationWorkload : IDisposable
{
    private readonly SolverDataStorage _sharedData = new();
    private readonly DefaultAtmosSolvers _solvers;

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

        if (options.GasCount <= 0 || options.CondensingGasCount < 0 || options.CondensingGasCount > options.GasCount)
            throw new ArgumentException("Gas dimensions are invalid.", nameof(options));

        if (options.ActiveVoxelCount is < 0 or > AtmosChunkConstants.MaximumVoxelCount)
            throw new ArgumentException("Active voxel count is invalid.", nameof(options));

        Options = options;
        Kernel = new AtmosKernel(options.ChunkWidth, options.ChunkHeight, options.ChunkDepth);
        _solvers = new DefaultAtmosSolvers(options.ChunkWidth, options.ChunkHeight, options.ChunkDepth);
        var config = CreateConfig(options);
        Kernel.SetAtmosConfig(config.CreateSnapshot());
        Config.Capture(Kernel.GetAtmosConfig());

        Int3[] positions = CreatePositions(options.RegisteredChunkCount, options.BoundaryTopology);
        for (int index = 0; index < positions.Length; index++)
            CreateChunk(options, positions[index], index, index < options.AwakeChunkCount);

        Steps = _solvers.CreateSteps();
        Kernel.TickCount = AtmosSolverConstants.ThermodynamicsTickInterval - 1;
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
    internal SolverStep[] Steps { get; }
    internal AtmosSimulationCheckpoint Initial { get; }
    internal int BoundaryEventCount { get; }
    internal int ThermalBoundaryEdgeCount { get; }

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
        Kernel.CreateAndRegisterChunk(position, options.ChunkWidth, options.ChunkHeight, options.ChunkDepth, 1);
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
        for (ushort voxel = 0; voxel < activeVoxels; voxel++)
        {
            for (int gas = 0; gas < options.GasCount; gas++)
            {
                float baseMoles = 20f / options.GasCount;
                float moles = gas < options.CondensingGasCount
                    ? voxel < supersaturatedVoxels ? 80f : 0.02f
                    : baseMoles *
                      GradientScale(options.PressureGradient, chunkIndex) *
                      LocalPressureScale(options.PressureGradient, voxel, gas, options.RandomSeed);

                float temperature = 300f + TemperatureOffset(options.TemperatureGradient, chunkIndex, voxel);
                GasInjectionSolver.Inject(chunk, voxel, gas, moles, temperature, Config);
            }
        }

        int expectedActiveGases = activeVoxels == 0 ? 0 : options.GasCount;
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
        Context = new AtmosSolverExecutionContext(Kernel, Chunks, Config, Kernel.TickCount + 1, _sharedData);
    }
}