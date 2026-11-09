using System.Numerics;
using Numos.API;
using Numos.SimDrawer;
using Numos.Workbench;
using Raylib_cs;

namespace Numos.Viewer.Rendering.Viewport;

/// <summary>
///     The presentation of one simulation: its 3D viewport, camera, background draw-data build, and chunk meshes.
/// </summary>
internal sealed class SimulationSurface : IDisposable
{
    internal SimulationSurface(WorkbenchSimulationFrame frame, SimulationFrameBuilder? builder)
    {
        Frame = frame;
        Builder = builder;
    }

    internal AtmosSimulationId Id => Frame.Id;

    /// <summary>
    ///     The simulation's entry in the latest frame.
    /// </summary>
    internal WorkbenchSimulationFrame Frame { get; set; }

    internal SimulationViewport? Viewport { get; set; }
    internal Camera3D Camera { get; set; } = new()
    {
        Position = new Vector3(24, 24, 24),
        Target = new Vector3(8, 8, 8),
        Up = Vector3.UnitZ,
        FovY = 45f,
        Projection = CameraProjection.Perspective
    };

    /// <summary>
    ///     The builder for this surface's background builds. It runs one build at a time.
    /// </summary>
    internal SimulationFrameBuilder? Builder { get; set; }

    internal Task<SimulationDrawData>? PendingBuild { get; set; }
    internal DrawBuildInputs? PendingInputs { get; set; }

    /// <summary>
    ///     The inputs <see cref="DrawData" /> was built from.
    /// </summary>
    internal DrawBuildInputs? BuiltInputs { get; set; }

    internal SimulationDrawData? DrawData { get; set; }

    /// <summary>
    ///     GPU meshes for this simulation's chunks. Chunk positions repeat between simulations, so each surface owns a
    ///     cache.
    /// </summary>
    internal ChunkMeshCache MeshCache { get; } = new();

    public void Dispose()
    {
        MeshCache.Dispose();
        Viewport?.Dispose();
        Viewport = null;
    }
}
