namespace Numos.CoreSim.Replay;

/// <summary>
///     Names the live <see cref="AtmosChunk" /> array a per-voxel <see cref="AtmosChunkCheckpoint" /> property is
///     captured from and restored into.
/// </summary>
/// <remarks>
///     <c>Numos.Replay.Generators</c> turns these into the checkpoint's <c>CaptureLiveArrays</c> and
///     <c>RestoreLiveArrays</c>, so adding a per-voxel array is one property with this attribute and a
///     <see cref="State.StateFieldAttribute" />. The property must be an <see cref="IReadOnlyList{T}" /> with a setter,
///     and the live member a <c>FlatArray</c> of the same element type. Irregular shapes (the active-air prefix, gas
///     channels) are copied by hand.
/// </remarks>
/// <param name="liveMember">The <see cref="AtmosChunk" /> member, e.g. <c>nameof(AtmosChunk.VoxelRoomMap)</c>.</param>
[AttributeUsage(AttributeTargets.Property)]
internal sealed class LiveChunkArrayAttribute(string liveMember) : Attribute
{
    public string LiveMember { get; } = liveMember;
}
