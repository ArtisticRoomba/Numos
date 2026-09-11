using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

namespace Numos.CoreSim.Benchmarks.Infrastructure;

/// <summary>
///     Adds constant and derived workload dimensions to CSV and table output.
/// </summary>
public sealed class ScalingColumnsConfig : ManualConfig
{
    /// <summary>
    ///     Creates the scaling metadata columns.
    /// </summary>
    public ScalingColumnsConfig()
    {
        AddColumn(
            new ScalingColumn(
                "WorkloadMode",
                false,
                (options, benchmark) =>
                    benchmark.Descriptor.WorkloadMethod.Name.Contains("Transient", StringComparison.Ordinal)
                        ? "Transient"
                        : "SteadyState"),
            new ScalingColumn("RegisteredChunks", true, (options, _) => options.RegisteredChunkCount.ToString()),
            new ScalingColumn("AwakeChunks", true, (options, _) => options.AwakeChunkCount.ToString()),
            new ScalingColumn("ChunkWidth", true, (options, _) => options.ChunkWidth.ToString()),
            new ScalingColumn("ChunkHeight", true, (options, _) => options.ChunkHeight.ToString()),
            new ScalingColumn("ChunkDepth", true, (options, _) => options.ChunkDepth.ToString()),
            new ScalingColumn("VoxelCount", true, (options, _) => options.VoxelCount.ToString()),
            new ScalingColumn("ActiveVoxelCount", true, (options, _) => options.ActiveVoxelCount.ToString()),
            new ScalingColumn("GasCount", true, (options, _) => options.GasCount.ToString()),
            new ScalingColumn("ReactionCount", true, (options, _) => options.ReactionCount.ToString()),
            new ScalingColumn("CondensingGasCount", true, (options, _) => options.CondensingGasCount.ToString()),
            new ScalingColumn("SupersaturatedVoxelFraction", true, (options, _) => options.SupersaturatedVoxelFraction.ToString("G")),
            new ScalingColumn("BoundaryTopology", false, (options, _) => options.BoundaryTopology.ToString()),
            new ScalingColumn(
                "BoundaryEventCount",
                true,
                (options, _) =>
                    options.ActiveVoxelFraction == 1d ? SimulationWorkload.GetBoundaryEventCount(options).ToString() : "-"),
            new ScalingColumn(
                "ThermalBoundaryEdgeCount",
                true,
                (options, _) =>
                    options.ActiveVoxelFraction == 1d ? SimulationWorkload.GetThermalBoundaryEdgeCount(options).ToString() : "-"));
    }

    private sealed class ScalingColumn(
        string name,
        bool numeric,
        Func<ScalingWorkloadOptions, BenchmarkCase, string> value) : IColumn
    {
        public string Id => $"Numos.{name}";
        public string ColumnName => name;
        public string Legend => $"Numos scaling workload {name}.";
        public ColumnCategory Category => ColumnCategory.Params;
        public int PriorityInCategory => 100;
        public bool IsNumeric => numeric;
        public UnitType UnitType => UnitType.Dimensionless;
        public bool AlwaysShow => true;

        public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase)
        {
            return false;
        }

        public bool IsAvailable(Summary summary)
        {
            return true;
        }

        public string GetValue(Summary summary, BenchmarkCase benchmarkCase)
        {
            return GetValue(benchmarkCase);
        }

        public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style)
        {
            return GetValue(benchmarkCase);
        }

        private string GetValue(BenchmarkCase benchmarkCase)
        {
            var benchmark = (ScalingBenchmarkBase)Activator.CreateInstance(benchmarkCase.Descriptor.Type)!;
            foreach (var parameter in benchmarkCase.Parameters.Items)
            {
                benchmark.GetType().GetProperty(parameter.Name)?.SetValue(benchmark, parameter.Value);
            }

            return value(benchmark.Options, benchmarkCase);
        }
    }
}