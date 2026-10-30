using BenchmarkDotNet.Attributes;

namespace Numos.Viewer.Benchmarks.Localization;

[MemoryDiagnoser]
[BenchmarkCategory("Locale")]
public class GetStringBenchmark
{
    [GlobalSetup]
    public void Setup()
    {
        Locale.SetCulture();
    }

    [Benchmark]
    public void GetString_NoArgs()
    {
        Locale.GetString("test-no-args");
    }
    
    [Benchmark]
    public void GetString_Arg1()
    {
        Locale.GetString("test-arg-one", ("arg1", 1));
    }
    
    [Benchmark]
    public void GetString_Arg2()
    {
        Locale.GetString("test-arg-two", ("arg1", 1), ("arg2", 2));
    }
    
    [Benchmark]
    public void GetString_Arg3()
    {
        Locale.GetString("test-arg-three", ("arg1", 1), ("arg2", 2), ("arg3", 3));
    }
    
    [Benchmark]
    public void GetString_ArgMany()
    {
        Locale.GetString("test-arg-many", ("arg1", 1), ("arg2", 2), ("arg3", 3), ("arg4", 4), ("arg5", 5), ("arg6", 6));
    }
}