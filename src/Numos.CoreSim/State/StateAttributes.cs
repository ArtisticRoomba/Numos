using Numos.CoreSim.State.Advanced;

namespace Numos.CoreSim.State;

/// <summary>
///     Marks a field or auto-property as state. The <c>Numos.API</c> source generator then gives the declaring type
///     equality, a state hash, a binary reader and writer, and (for solver configurations) a snapshot copy and
///     registration for loading, with nothing else to write.
/// </summary>
/// <remarks>
///     <para>
///         Any type with at least one marked member takes part, and must be <see langword="partial" /> (along with
///         every type it's nested in), non-generic, and derive from <see cref="object" /> if it's a class. On a
///         positional record, mark the parameter with <c>[property: NumosSerialize]</c>.
///     </para>
///     <para>
///         A field's wire id is a hash of its name, so members can be reordered freely and an id never needs picking.
///         Renaming a member changes its id; pass the old name (<c>[NumosSerialize("OldName")]</c>) to keep reading
///         files written before the rename. Removing a member needs nothing: ids a reader doesn't know are skipped.
///         Two names in one type that hash to the same id fail the build.
///     </para>
///     <para>
///         A mutable instance member on a participating type with neither this attribute nor
///         <see cref="NumosIgnoreAttribute" /> is a build warning, so a new setting can't quietly miss the hash.
///         Readonly fields and get-only auto-properties are assumed to be derived caches and aren't flagged.
///     </para>
///     <para>
///         Supported types are <see langword="bool" />, the integer types, <see langword="float" />,
///         <see langword="double" />, enums, <see langword="string" />, <c>Int3</c>, Numos.Units quantity aliases,
///         <see cref="GasProperties" /> (saved by name), <see cref="GasRegistry" /> (saved by value), other
///         participating types, arrays, <see cref="IReadOnlyList{T}" />, <c>ImmutableArray&lt;T&gt;</c>, read-only,
///         frozen, or plain dictionaries, read-only or frozen sets, and nullable forms of these. Anything else needs a
///         <see cref="Codec" />.
///     </para>
///     <para>
///         Floats compare by their bits, so <c>0f</c> and <c>-0f</c> differ and a NaN equals itself, which is what the
///         hash sees too. Dictionaries and sets are compared and hashed in a canonical order (gases by
///         <see cref="GasProperties.Name" />, strings ordinally), so insertion order doesn't matter.
///     </para>
///     <para>
///         The generated reader builds a value through a constructor, the way <c>System.Text.Json</c> does: each
///         parameter binds to the marked member with the same name, ignoring case, and members no parameter covers are
///         assigned through their <c>set</c> or <c>init</c> accessor. A member missing from older data gets the
///         parameter's default value, keeps what the constructor gave it, or gets <c>default</c>.
///     </para>
///     <para>
///         Not allowed on types implementing <c>IAtmosWorldSolver</c> yet: solver objects sit outside checkpoints, so a
///         value saved there would never be reproduced by replay. Put settings in an
///         <see cref="IAtmosSolverConfiguration" /> and per-voxel state in a <c>[ChunkData]</c> field.
///     </para>
/// </remarks>
/// <example>
///     A complete solver configuration. It saves with the world, loads without registration, and gets
///     <see cref="IAtmosSolverConfiguration.CreateSnapshot" /> generated:
///     <code>
///     public sealed partial class FireSettings : IAtmosSolverConfiguration
///     {
///         public string Key => "my-mod/fire";
///
///         [NumosSerialize] public Kelvin IgnitionTemperature { get; init; } = 600f;
///         [NumosSerialize] public float SpreadRate { get; init; }
///     }
///     </code>
/// </example>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = false)]
public sealed class NumosSerializeAttribute : Attribute
{
    /// <summary>
    ///     Marks a member as state under its own name.
    /// </summary>
    public NumosSerializeAttribute()
    {
    }

    /// <summary>
    ///     Marks a member as state under a different wire name, usually its name before a rename.
    /// </summary>
    /// <param name="name">The name whose hash is the member's wire id. Must not be blank.</param>
    public NumosSerializeAttribute(string name)
    {
        Name = name;
    }

    /// <summary>
    ///     Gets the wire name, or <see langword="null" /> when the member's own name is used.
    /// </summary>
    public string? Name { get; }

    /// <summary>
    ///     Gets or sets an <see cref="IStateCodec{T}" /> that writes, compares, and reads the member in place of the
    ///     one its type would get.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         For shapes the schema can't describe on its own, such as a count-prefixed list of runtime-typed values,
    ///         or to change how a supported type is written, such as a <see langword="string" /> that may hold
    ///         <see langword="null" />. The codec owns the whole member, so it's also responsible for keeping its hash
    ///         and bytes in step: write both through the same sink calls.
    ///     </para>
    ///     <para>
    ///         The codec must implement <see cref="IStateCodec{T}" /> for the member's type and be accessible from the
    ///         declaring type. A member with a codec is read through its setter, not a constructor parameter, and makes
    ///         a solver configuration's generated snapshot a copy rather than the same instance.
    ///     </para>
    /// </remarks>
    /// <example>
    ///     <code>
    ///     [NumosSerialize(Codec = typeof(StateCodecs.Nullable&lt;string, StateCodecs.String&gt;))]
    ///     public string Name;
    ///     </code>
    /// </example>
    public Type? Codec { get; set; }
}

/// <summary>
///     Says a field or property of a type with <see cref="NumosSerializeAttribute" /> members is deliberately not
///     state, which silences the warning for an unmarked mutable member.
/// </summary>
/// <remarks>
///     For derived caches rebuilt from the marked members, scratch buffers, and the like. Anything that changes solver
///     behavior on its own belongs in a <see cref="NumosSerializeAttribute" /> member instead, or replay will diverge.
/// </remarks>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = false)]
public sealed class NumosIgnoreAttribute : Attribute;
