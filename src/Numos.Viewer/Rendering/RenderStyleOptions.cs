namespace Numos.Viewer.Rendering;

/// <summary>
///     Optional visual treatments for the 3D simulation view.
/// </summary>
/// <param name="ShowChunkOutlines">Outline each chunk's bounds.</param>
/// <param name="ShowVoxelOutlines">Outline each drawn voxel.</param>
/// <param name="TransparentVoxels">Draw voxels at partial alpha.</param>
/// <param name="ShowVelocityArrows">Draw a bulk-velocity arrow from the centre of each drawn voxel.</param>
/// <param name="VelocityArrowScale">
///     Seconds of travel one arrow represents, so an arrow's length in voxels is speed times this value. See
///     <see cref="VelocityArrows" /> for the clamps applied to it.
/// </param>
public readonly record struct Render3DStyleOptions(
    bool ShowChunkOutlines = false,
    bool ShowVoxelOutlines = true,
    bool TransparentVoxels = false,
    bool ShowVelocityArrows = false,
    float VelocityArrowScale = VelocityArrows.DefaultScaleSeconds);

/// <summary>
///     Optional visual treatments for the 2D simulation view.
/// </summary>
/// <param name="ShowChunkOutlines">Outline the slice's chunk bounds.</param>
/// <param name="ShowVoxelOutlines">Outline each drawn cell.</param>
/// <param name="TransparentVoxels">Draw cells at partial alpha.</param>
/// <param name="ShowVelocityArrows">Draw the in-plane bulk velocity as an arrow from the centre of each cell.</param>
/// <param name="VelocityArrowScale">
///     Seconds of travel one arrow represents, so an arrow's length in cells is in-plane speed times this value.
/// </param>
public readonly record struct Render2DStyleOptions(
    bool ShowChunkOutlines = false,
    bool ShowVoxelOutlines = true,
    bool TransparentVoxels = false,
    bool ShowVelocityArrows = false,
    float VelocityArrowScale = VelocityArrows.DefaultScaleSeconds);
