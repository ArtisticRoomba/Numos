namespace Numos.Viewer.Rendering.Viewport;

public interface ISimulationViewportRenderer
{
    /// <summary>
    ///     Called while the viewport framebuffer is bound.
    ///     Implementations should draw the simulation scene only.
    ///     Do not call ImGui from here.
    /// </summary>
    void Render(SimulationViewportRenderContext context);
}