namespace Numos.CoreSim.Replay;

/// <summary>
///     Marks an <see cref="AtmosChunkCheckpoint" /> property as one of the flat per-voxel arrays copied from a live
///     <see cref="AtmosChunk" />, so <c>Numos.Replay.CheckpointGen</c> can generate its capture, restore, hashing, and
///     wire read/write code from this one declaration instead of by hand in each of those places.
/// </summary>
/// <remarks>
///     Covers only fields with a fixed one-array-per-voxel shape (e.g. <see cref="AtmosChunkCheckpoint.Temperatures" />).
///     Fields with an irregular shape -- a companion count prefix, a nested per-gas-channel array, host-owned solver
///     arrays -- stay hand-written; forcing them through this attribute would need a schema more complex than the
///     hand-written code it replaces.
/// </remarks>
/// <param name="liveMember">
///     The corresponding field name on <see cref="AtmosChunk" /> to capture from and restore into (e.g.
///     <c>nameof(AtmosChunk.VoxelRoomMap)</c>). The checkpoint-side property name and the live member name diverge, so
///     this can't be inferred from the property alone.
/// </param>
/// <param name="order">
///     Emission order relative to other <see cref="ChunkCheckpointFieldAttribute" />-tagged properties. Wire bytes are
///     golden-pinned, so this must reproduce today's exact field order, not just be internally consistent.
/// </param>
[AttributeUsage(AttributeTargets.Property)]
internal sealed class ChunkCheckpointFieldAttribute(string liveMember, int order) : Attribute
{
    public string LiveMember { get; } = liveMember;

    public int Order { get; } = order;
}

/// <summary>
///     Marks an <see cref="AtmosConfigSnapshot" /> scalar property as one of the flat fields hashed and wire-encoded
///     without any bespoke defaulting/clamping logic, so its hash and wire read/write code can be generated from this
///     one declaration.
/// </summary>
/// <remarks>
///     Does not cover the constructor: <see cref="AtmosConfigSnapshot" />'s constructor applies per-field
///     defaulting/clamping rules that only run once per config change and aren't mechanical, so it stays hand-written.
///     The corresponding property on the mutable <see cref="AtmosConfig" /> must share this property's name and type.
/// </remarks>
/// <param name="order">
///     Emission order relative to other <see cref="ConfigCheckpointFieldAttribute" />-tagged properties. Wire bytes
///     are golden-pinned, so this must reproduce today's exact field order.
/// </param>
[AttributeUsage(AttributeTargets.Property)]
internal sealed class ConfigCheckpointFieldAttribute(int order) : Attribute
{
    public int Order { get; } = order;
}

/// <summary>
///     Marks a <see cref="GasProperties" /> field as part of its flat hashed and wire-encoded field list, so that
///     list's hash and wire read/write code can be generated from this one declaration.
/// </summary>
/// <param name="order">
///     Emission order relative to other <see cref="GasCheckpointFieldAttribute" />-tagged fields. Wire bytes are
///     golden-pinned, so this must reproduce today's exact field order.
/// </param>
[AttributeUsage(AttributeTargets.Field)]
internal sealed class GasCheckpointFieldAttribute(int order) : Attribute
{
    public int Order { get; } = order;
}
