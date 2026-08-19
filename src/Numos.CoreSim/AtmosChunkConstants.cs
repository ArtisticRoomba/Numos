namespace Numos.CoreSim;

/// <summary>
///     Canonical default dimensions and implementation limits for atmospheric chunks.
/// </summary>
public static class AtmosChunkConstants
{
    /// <summary>Default number of voxels along a chunk's x-axis.</summary>
    public const int DefaultWidth = 16;

    /// <summary>Default number of voxels along a chunk's y-axis.</summary>
    public const int DefaultHeight = 16;

    /// <summary>Default number of voxels along a chunk's z-axis.</summary>
    public const int DefaultDepth = 16;

    /// <summary>Default maximum number of simultaneously active rooms in a chunk.</summary>
    public const int DefaultMaxActiveRooms = 64;

    /// <summary>Maximum number of distinct gas channels supported by one chunk.</summary>
    public const int MaximumGasChannelsPerChunk = 16;

    /// <summary>Maximum voxel count representable by the chunk's unsigned 16-bit flat indices.</summary>
    public const int MaximumVoxelCount = ushort.MaxValue;
}