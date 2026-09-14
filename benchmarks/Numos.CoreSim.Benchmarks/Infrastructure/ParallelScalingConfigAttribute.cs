using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;

namespace Numos.CoreSim.Benchmarks.Infrastructure;

[AttributeUsage(AttributeTargets.Class)]
internal sealed class ParallelScalingConfigAttribute : Attribute, IConfigSource
{
    internal ParallelScalingConfigAttribute()
    {
        var config = ManualConfig.Create(DefaultConfig.Instance);
        foreach (int workers in new[] { 1, 2, 4, 8, 16 })
        {
            config.AddJob(
                Job.Default
                    .WithId($"Workers={workers}")
                    .WithEnvironmentVariable("DOTNET_PROCESSOR_COUNT", workers.ToString())
                    // Iteration setup limits each sample to one solve. Disable tiering so worker-dependent
                    // promotion timing cannot masquerade as solver scaling.
                    .WithEnvironmentVariable("DOTNET_TieredCompilation", "0"));
        }

        Config = config;
    }

    public IConfig Config { get; }
}