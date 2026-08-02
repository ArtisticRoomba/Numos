using JetBrains.Annotations;

namespace Numos.Datatypes.Primitives;

/// <summary>
/// Prim datatype that represents the classification of a voxel in the simulation.
/// </summary>
/// <para>In Numos, voxels are grouped together to form rooms,
/// which can thus be used to form <see cref="RoomNode"/>s in the future.
/// For now, their usage is similar to SS14 Atmospherics' AirtightData,
/// which stores data on whether a tile is airtight or not.</para>
public readonly record struct VoxelClassification(int RoomId)
{
    /// <summary>
    /// Voxel is unassigned to any room.
    /// This is the default value for a voxel.
    /// This can also store gas.
    /// </summary>
    public const int RoomUnassigned = 0;

    /// <summary>
    /// Voxel is solid and cannot store gas, blocks any flow.
    /// </summary>
    public const int RoomSolid = -2;

    /// <summary>
    /// Voxel is an infinite sink/true vacuum.
    /// Voids any gas that enters it.
    /// </summary>
    public const int RoomVoid = -1;

    /// <summary>
    /// The ID of the room this voxel belongs to.
    /// </summary>
    public int RoomId { get; } = RoomId;

    /// <summary>
    /// Returns true if the voxel is unassigned to any room.
    /// </summary>
    [PublicAPI]
    public bool IsUnassigned => RoomId == RoomUnassigned;

    /// <summary>
    /// Returns true if the voxel is solid and cannot store gas.
    /// </summary>
    [PublicAPI]
    public bool IsSolid => RoomId == RoomSolid;

    /// <summary>
    /// Returns true if the voxel is a void and will remove any gas that enters it.
    /// </summary>
    [PublicAPI]
    public bool IsVoid => RoomId == RoomVoid;

    /// <summary>
    /// Implicitly converts a <see cref="VoxelClassification"/> to an <see cref="int"/> representing the room ID.
    /// </summary>
    /// <param name="classification">The <see cref="VoxelClassification"/> to convert.</param>
    /// <returns>The room ID as an <see cref="int"/>.</returns>
    public static implicit operator int (VoxelClassification classification) => classification.RoomId;

    /// <summary>
    /// Implicitly converts an <see cref="int"/> representing a room ID to a <see cref="VoxelClassification"/>.
    /// </summary>
    /// <param name="value">The room ID as an <see cref="int"/>.</param>
    /// <returns>A new <see cref="VoxelClassification"/> with the specified room ID.</returns>
    public static implicit operator VoxelClassification(int value) => new(value);

    public VoxelClassification() : this(RoomUnassigned)
    {
    }
}