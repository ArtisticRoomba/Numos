namespace Numos.CoreSim.Replay;

/// <summary>
///     Marks an <see cref="AtmosChunkCheckpoint" /> property as one of the flat per-voxel arrays copied from a live
///     <see cref="AtmosChunk" />. <c>Numos.Replay.Generators</c> generates the capture, restore, hashing, and
///     wire read/write code from this one declaration.
/// </summary>
/// <remarks>
///     Covers only fields with a fixed one-array-per-voxel shape (e.g. <see cref="AtmosChunkCheckpoint.Temperatures" />).
///     Fields with an irregular shape (a companion count prefix, a nested per-gas-channel array, host-owned solver
///     arrays) stay hand-written. Forcing them through this attribute would need a schema more complex than the
///     hand-written code it replaces.
/// </remarks>
/// <param name="liveMember">
///     The corresponding field name on <see cref="AtmosChunk" /> to capture from and restore into (e.g.
///     <c>nameof(AtmosChunk.VoxelRoomMap)</c>). The checkpoint-side property name and the live member name diverge, so
///     the attribute cannot infer it from the property alone.
/// </param>
/// <param name="order">
///     Emission order relative to other <see cref="ChunkCheckpointFieldAttribute" />-tagged properties. Wire bytes are
///     golden-pinned, so this must reproduce the existing field order exactly, not just be internally consistent.
/// </param>
[AttributeUsage(AttributeTargets.Property)]
internal sealed class ChunkCheckpointFieldAttribute(string liveMember, int order) : Attribute
{
    public string LiveMember { get; } = liveMember;

    public int Order { get; } = order;
}

/// <summary>
///     Marks an <see cref="AtmosConfigSnapshot" /> scalar property as one of the flat fields hashed, compared, and
///     wire-encoded without bespoke defaulting or clamping logic. The generator emits its hash, equality, and wire
///     read/write code from this one declaration.
/// </summary>
/// <remarks>
///     <para>
///         Tagged fields are compared by <see cref="AtmosConfigSnapshot.SemanticallyEquals" /> through the generated
///         equality, which compares floats by their bits to agree with the hash: <c>0f</c> and <c>-0f</c> differ.
///     </para>
///     <para>
///         Does not cover the constructor: <see cref="AtmosConfigSnapshot" />'s constructor applies per-field
///         defaulting/clamping rules that only run once per config change and are not mechanical, so it stays
///         hand-written. The corresponding property on the mutable <see cref="AtmosConfig" /> must share this
///         property's name and type.
///     </para>
/// </remarks>
/// <param name="order">
///     Emission order relative to other <see cref="ConfigCheckpointFieldAttribute" />-tagged properties. Wire bytes
///     are golden-pinned, so this must reproduce the existing field order exactly.
/// </param>
[AttributeUsage(AttributeTargets.Property)]
internal sealed class ConfigCheckpointFieldAttribute(int order) : Attribute
{
    public int Order { get; } = order;
}

/// <summary>
///     Marks a <see cref="GasProperties" /> field as part of its flat hashed, compared, and wire-encoded field list.
///     The generator emits that list's hash, equality, and wire read/write code from this one declaration.
/// </summary>
/// <remarks>
///     The generated equality backs the gas-registry comparison in <see cref="AtmosConfigSnapshot.SemanticallyEquals" />
///     and compares floats by their bits, matching the hash.
/// </remarks>
/// <param name="order">
///     Emission order relative to other <see cref="GasCheckpointFieldAttribute" />-tagged fields. Wire bytes are
///     golden-pinned, so this must reproduce the existing field order exactly.
/// </param>
[AttributeUsage(AttributeTargets.Field)]
internal sealed class GasCheckpointFieldAttribute(int order) : Attribute
{
    public int Order { get; } = order;
}