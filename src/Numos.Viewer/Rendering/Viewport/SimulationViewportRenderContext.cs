namespace Numos.Viewer.Rendering.Viewport;

public readonly record struct SimulationViewportRenderContext(
    int Width,
    int Height,
    float AspectRatio);