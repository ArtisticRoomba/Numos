namespace Numos.CoreSim.State;

/// <summary>
///     Opts a partial type into the state schema: the <c>Numos.API</c> source generator emits its equality, canonical
///     ordering, <see cref="INumosState{TSelf}.WriteState{TSink}" /> walker, and
///     <see cref="INumosState{TSelf}.ReadState" /> reader from its <see cref="StateFieldAttribute" /> members.
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
///         <see cref="IAtmosSolverConfiguration.ComputeStateHash" /> generated, plus
///         <see cref="IAtmosSolverConfiguration.Key" /> when <see cref="Key" /> is set.
///         <see cref="IAtmosSolverConfiguration.CreateSnapshot" /> stays hand-written. Don't write the generated members
///         (or an <c>AppendHash</c>) yourself; the generator reports that as an error instead of letting two
///         definitions disagree.
///     </para>
///     <para>
///         Floats compare by their bits, so <c>0f</c> and <c>-0f</c> differ and a NaN equals itself, which is what the
///         hash sees too. Dictionaries and sets are compared and hashed in a canonical order (gases by
///         <see cref="GasProperties.Name" />, strings ordinally), so insertion order doesn't matter.
///     </para>
///     <para>
///         The generated reader builds the value through a constructor, the way <c>System.Text.Json</c> does: each
///         parameter binds to the state field with the same name, ignoring case, and fields no parameter covers are
///         set through their <c>set</c> or <c>init</c> accessor. A parameter may be any collection type the field's
///         value can be copied into, such as <see cref="IEnumerable{T}" /> or <see cref="IDictionary{TKey, TValue}" />
///         for a frozen field. When several constructors bind, the one with the most parameters wins; when none does,
///         the build fails. A field missing from older data gets the parameter's default value if it declares one,
///         otherwise <c>default</c>, an empty string, or an empty collection.
///     </para>
/// </remarks>
/// <example>
///     <code>
///     [NumosState(Key = "my-mod/fire")]
///     public sealed partial class FireSettings : IAtmosSolverConfiguration
///     {
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
public sealed class NumosStateAttribute : Attribute
{
    /// <summary>
    ///     Gets or sets the type's state key, a stable, namespaced name such as <c>"my-mod/fire"</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Optional. It becomes <see cref="INumosState{TSelf}.StateKey" />, which standalone state files record so a
    ///         load can check it got the type it asked for. On a solver configuration it also becomes the generated
    ///         <see cref="IAtmosSolverConfiguration.Key" />, so the key the world sees and the key the file records
    ///         can't drift apart; don't write <c>Key</c> by hand as well.
    ///     </para>
    ///     <para>
    ///         Leave it out on a solver configuration that's used under more than one key, and keep a hand-written
    ///         <c>Key</c>. Such a type still saves into world archives, but has to be registered under each key it
    ///         uses (<see cref="SolverConfigurationTypes.Register{T}(string)" />) and can't be saved on its own.
    ///     </para>
    ///     <para>Renaming a key is a format change: files saved under the old key no longer load.</para>
    /// </remarks>
    public string? Key { get; set; }
}

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
