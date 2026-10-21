namespace Numos.CoreSim.State;

/// <summary>
///     Opts a partial type into the state schema: the <c>Numos.API</c> source generator emits its equality, canonical
///     ordering, and <see cref="INumosState{TSelf}.WriteState{TSink}" /> walker from its <see cref="StateFieldAttribute" />
///     members.
/// </summary>
/// <remarks>
///     <para>
///         Every instance field and auto-property on the type must carry either <see cref="StateFieldAttribute" /> or
///         <see cref="NotStateAttribute" />. An untagged one is a build error, so adding a field can't silently leave it
///         out of equality and the hash. Computed properties with no backing storage are ignored.
///     </para>
///     <para>
///         A type that also implements <see cref="IAtmosSolverConfiguration" /> gets
///         <see cref="IAtmosSolverConfiguration.SemanticallyEquals" /> and
///         <see cref="IAtmosSolverConfiguration.ComputeStateHash" /> generated; <see cref="IAtmosSolverConfiguration.Key" />
///         and <see cref="IAtmosSolverConfiguration.CreateSnapshot" /> stay hand-written. Don't write those two methods
///         (or an <c>AppendHash</c>) yourself; the generator reports that as an error instead of letting two
///         definitions disagree.
///     </para>
///     <para>
///         Floats compare by their bits, so <c>0f</c> and <c>-0f</c> differ and a NaN equals itself, which is what the
///         hash sees too. Dictionaries and sets are compared and hashed in a canonical order (gases by
///         <see cref="GasProperties.Name" />, strings ordinally), so insertion order doesn't matter.
///     </para>
/// </remarks>
/// <example>
///     <code>
///     [NumosState]
///     public sealed partial class FireSettings : IAtmosSolverConfiguration
///     {
///         public string Key => "my-mod/fire";
///
///         [StateField(1)] public Kelvin IgnitionTemperature { get; init; }
///         [StateField(2)] public float SpreadRate { get; init; }
///
///         [NotState] private float[]? _derivedTable; // rebuilt from the fields above
///
///         public IAtmosSolverConfiguration CreateSnapshot(IGasRegistry gases) => this;
///     }
///     </code>
/// </example>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class NumosStateAttribute : Attribute;

/// <summary>
///     Marks a field or auto-property of a <see cref="NumosStateAttribute" /> type as authoritative state, under a
///     stable id.
/// </summary>
/// <remarks>
///     <para>
///         Fields are hashed and written in ascending id order, not declaration order, so members can be moved or
///         renamed freely. Ids must be positive and unique within the type. Once a field is removed, list its id in
///         <see cref="RetiredStateFieldsAttribute" /> so it can't be reused with a different meaning.
///     </para>
///     <para>
///         Supported types are <see langword="bool" />, the integer types, <see langword="float" />,
///         <see langword="double" />, enums, <see langword="string" />, <c>Int3</c>, Magnitudo quantity aliases,
///         <see cref="GasProperties" /> (by name), other <see cref="NumosStateAttribute" /> types, arrays,
///         <see cref="IReadOnlyList{T}" />, <c>ImmutableArray&lt;T&gt;</c>, read-only, frozen, or plain dictionaries,
///         read-only or frozen sets, and nullable forms of these.
///     </para>
///     <para>
///         Not allowed on types implementing <c>IAtmosWorldSolver</c>: solver objects sit outside checkpoints, so a
///         value saved there would never be reproduced by replay. Put settings in an
///         <see cref="IAtmosSolverConfiguration" /> and per-voxel state in a rollback chunk field.
///     </para>
/// </remarks>
/// <param name="id">The field's stable id, greater than zero.</param>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = false)]
public sealed class StateFieldAttribute(int id) : Attribute
{
    /// <summary>
    ///     Gets the field's stable id.
    /// </summary>
    public int Id { get; } = id;
}

/// <summary>
///     Excludes a field or auto-property of a <see cref="NumosStateAttribute" /> type from the state schema.
/// </summary>
/// <remarks>
///     For derived caches rebuilt from the tagged fields, such as a precomputed range or lookup table. Anything that
///     changes solver behavior on its own belongs in a <see cref="StateFieldAttribute" /> instead.
/// </remarks>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = false)]
public sealed class NotStateAttribute : Attribute;

/// <summary>
///     Lists <see cref="StateFieldAttribute" /> ids that a <see cref="NumosStateAttribute" /> type used to have and must
///     never reuse.
/// </summary>
/// <param name="ids">The retired ids.</param>
/// <example>
///     <code>
///     [NumosState, RetiredStateFields(3)] // 3 was SpreadDelay, removed in 0.4
///     public sealed partial class FireSettings : IAtmosSolverConfiguration { ... }
///     </code>
/// </example>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class RetiredStateFieldsAttribute(params int[] ids) : Attribute
{
    /// <summary>
    ///     Gets the retired ids.
    /// </summary>
    public IReadOnlyList<int> Ids { get; } = ids;
}
