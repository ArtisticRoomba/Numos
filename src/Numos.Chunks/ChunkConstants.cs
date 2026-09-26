namespace Numos.Chunks;

/// <summary>
/// Default constants for Numos chunks.
/// </summary>
public static class ChunkConstants
{
    /// <summary>Default number of voxels along a chunk's x-axis.</summary>
    public const int DefaultWidth = 16;

    /// <summary>Default number of voxels along a chunk's y-axis.</summary>
    public const int DefaultHeight = 16;

    /// <summary>Default number of voxels along a chunk's z-axis.</summary>
    public const int DefaultDepth = 16;
    
    /// <summary>Maximum voxel count representable by the chunk's unsigned 16-bit flat indices.</summary>
    public const int MaximumVoxelCount = ushort.MaxValue;
}
