namespace Numos.Headless;

internal static class Program
{
    private static Task<int> Main(string[] args)
    {
        return HeadlessApplication.RunAsync(
            args,
            Console.In,
            Console.Out,
            Console.Error,
            CancellationToken.None);
    }
}