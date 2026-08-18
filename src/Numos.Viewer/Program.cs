namespace Numos.Viewer;

internal class Program
{
    private static void Main(string[] args)
    {
        using var viewer = new SimulationViewer();
        viewer.Run();
    }
}