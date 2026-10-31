using BenchmarkDotNet.Attributes;

namespace Numos.Viewer.Benchmarks.Localization;

[MemoryDiagnoser]
[BenchmarkCategory("Locale")]
public class LocaleInitializeBenchmark
{
    [Benchmark]
    public void SetCulture_English()
    {
        Locale.SetCulture();
    }
    
    [Benchmark]
    public void SetCulture_English_To_Russian()
    {
        Locale.SetCulture("en");
        Locale.SetCulture("ru");
    }
    
    [Benchmark]
    public void SetCulture_All()
    {
        foreach (var culture in Locale.AvailableCultures)
        {
            Locale.SetCulture(culture);
        }
    }
}
