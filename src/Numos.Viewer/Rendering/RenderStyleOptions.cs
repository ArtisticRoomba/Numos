namespace Numos.Viewer.Rendering;

/// <summary>
///     Optional visual treatments for the 3D simulation view.
/// </summary>
public readonly record struct Render3DStyleOptions(
    bool ShowChunkOutlines = false,
    bool ShowVoxelOutlines = true,
    bool TransparentVoxels = false);

/// <summary>
///     Optional visual treatments for the 2D simulation view.
/// </summary>
public readonly record struct Render2DStyleOptions(
    bool ShowChunkOutlines = false,
    bool ShowVoxelOutlines = true,
    bool TransparentVoxels = false);