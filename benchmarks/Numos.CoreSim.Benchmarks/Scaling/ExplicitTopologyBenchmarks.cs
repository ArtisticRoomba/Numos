using BenchmarkDotNet.Attributes;
using Numos.API;
using Numos.CoreSim.Datatypes.Primitives;

namespace Numos.CoreSim.Benchmarks.Scaling;

/// <summary>
///     Measures sparse explicit-edge traversal and repeated topology compilation.
/// </summary>
/// <remarks>
///     Existing regular solver benchmarks remain the zero-edge baseline. This workload keeps all explicit endpoints in
///     one chunk so topology size, rather than simulation or chunk count, is the principal scaling variable.
/// </remarks>
[MemoryDiagnoser]
[BenchmarkCategory("Scaling", "ExplicitTopology")]
public class ExplicitTopologyBenchmarks
{
    private AtmosChunkHandle _chunk;
    private ExplicitLinkDefinition[] _definitions = [];
    private ExplicitLinkSetHandle _links;
    private AtmosSimulation _simulation = null!;
    private AtmosWorld _world = null!;

    /// <summary>
    ///     Gets or sets the number of sparse edges in the compiled topology.
    /// </summary>
    [Params(1, 10, 100, 1_000, 10_000)]
    public int EdgeCount { get; set; }

    /// <summary>
    ///     Builds one source cell and the requested number of arbitrary destinations.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        var config = new AtmosConfig
        {
            ThermalConductance = 0f,
            DefaultDiffusionCoefficient = 0f
        };

        config.GasRegistry.Add(
            new GasProperties
            {
                Name = "BenchmarkGas",
                MolarHeatCapacityAtConstantVolume = 20f,
                DiffusionCoefficient = 0f
            });

        _world = new AtmosWorld(config);
        _simulation = _world.CreateSimulation(EdgeCount + 1, 1, 1);
        _chunk = _simulation.CreateAndRegisterChunk(default);
        _simulation.SetChunkClassification(_chunk, new VoxelClassification(1));
        for (ushort index = 0; index <= EdgeCount; index++)
            _simulation.SetVoxelTemperature(_chunk, index, 300f);

        _simulation.AddGasToVoxel(_chunk, 0, "BenchmarkGas", EdgeCount, 300f);
        var source = _simulation.GetCellRef(_chunk, 0);
        _definitions = new ExplicitLinkDefinition[EdgeCount];
        for (int index = 0; index < EdgeCount; index++)
        {
            _definitions[index] = new ExplicitLinkDefinition(
                source,
                _simulation.GetCellRef(_chunk, checked((ushort)(index + 1))),
                ExplicitLinkFlags.GasTransport);
        }

        _links = _world.CreateLinks(_definitions);
        _world.Tick();
    }

    /// <summary>
    ///     Releases the world and every benchmark-owned simulation.
    /// </summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _world.Dispose();
    }

    /// <summary>
    ///     Measures a fixed tick with a stable compiled explicit topology.
    /// </summary>
    [Benchmark(Baseline = true)]
    public void Tick()
    {
        _world.Tick();
    }

    /// <summary>
    ///     Measures repeated batch removal, slot recycling, creation, and topology recompilation.
    /// </summary>
    [Benchmark]
    public void Rebuild()
    {
        _world.DestroyLinks(_links);
        _world.Tick();
        _links = _world.CreateLinks(_definitions);
        _world.Tick();
    }
}