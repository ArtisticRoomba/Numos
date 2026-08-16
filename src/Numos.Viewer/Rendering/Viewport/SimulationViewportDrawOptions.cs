namespace Numos.Viewer.Rendering.Viewport;

public sealed class SimulationViewportDrawOptions
{
    public readonly static SimulationViewportDrawOptions Default = new();

    public bool NoTitleBar { get; init; }
}