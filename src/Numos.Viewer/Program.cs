using Numos.Workbench;

namespace Numos.Viewer;

internal class Program
{
    private static int Main(string[] args)
    {
        if (ViewerArguments.Prepare(args, new SolverAssemblyLoader(), Console.Out, Console.Error, out var reports) is
            { } exitCode)
            return exitCode;

        using var viewer = new SimulationViewer();
        viewer.LogSolverAssemblies(reports);
        viewer.Run();
        return 0;
    }
}
