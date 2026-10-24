using Numos.CoreSim.State.Advanced;

namespace Numos.CoreSim.State;

/// <summary>
///     Marks a field or auto-property as state. The <c>Numos.API</c> source generator then writes equality, a state hash,
///     and a binary reader and writer for the declaring type. For solver configurations it also writes a snapshot copy
///     and the registration for loading. The author writes nothing else.
/// </summary>
/// <remarks>
///     <para>
///         Any type with at least one marked member takes part. It must be <see langword="partial" /> (along with every
///         type it is nested in) and non-generic. A class must derive from <see cref="object" />. On a positional
///         record, mark the parameter with <c>[property: NumosSerialize]</c>.
///     </para>
///     <para>
///         A field's wire id is a hash of its name, so members can be reordered freely and an id never needs picking.
///         Renaming a member changes its id. Pass the old name (<c>[NumosSerialize("OldName")]</c>) to keep reading
///         files written before the rename. Removing a member needs nothing, because a reader skips ids it does not know.
///         Two names in one type that hash to the same id fail the build.
///     </para>
///     <para>
///         A mutable instance member on a participating type with neither this attribute nor
///         <see cref="NumosIgnoreAttribute" /> is a build warning. A new setting therefore cannot quietly miss the hash.
///         The generator assumes readonly fields and get-only auto-properties are derived caches and does not flag them.
///     </para>
///     <para>
///         Supported types are <see langword="bool" />, the integer types, <see langword="float" />,
///         <see langword="double" />, enums, <see langword="string" />, <c>Int3</c>, and Numos.Units quantity aliases.
///         <see cref="GasProperties" /> is saved by name and <see cref="GasRegistry" /> by value. Other supported types
///         are participating types, arrays, <see cref="IReadOnlyList{T}" />, <c>ImmutableArray&lt;T&gt;</c>, read-only,
///         frozen, or plain dictionaries, read-only or frozen sets, and nullable forms of these. Anything else needs a
///         <see cref="Codec" />.
///     </para>
///     <para>
///         Floats compare by their bits, so <c>0f</c> and <c>-0f</c> differ and a NaN equals itself, which is what the
///         hash sees too. Dictionaries and sets are compared and hashed in a canonical order (gases by
///         <see cref="GasProperties.Name" />, strings ordinally), so insertion order doesn't matter.
///     </para>
///     <para>
///         The generated reader builds a value through a constructor, the way <c>System.Text.Json</c> does. Each
///         parameter binds to the marked member with the same name, ignoring case. Members that no parameter covers are
///         assigned through their <c>set</c> or <c>init</c> accessor. A member missing from older data gets the
///         parameter's default value, keeps what the constructor gave it, or gets <c>default</c>.
///     </para>
///     <para>
///         On a class implementing <c>IAtmosWorldSolver</c>, marked members are the state of the solver. Numos
///         checkpoints, hashes, and saves them, and it records an edit the host makes between ticks. The reader writes
///         them back into the registered instance instead of using a constructor. Each one must therefore be assignable
///         (no <see langword="readonly" /> fields, get-only, or <c>init</c> properties).
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
///         Use it for shapes the schema cannot describe on its own, such as a count-prefixed list of runtime-typed
///         values. Use it also to change how a supported type is written, such as a <see langword="string" /> that may
///         hold <see langword="null" />. The codec owns the whole member, so it must keep its hash and bytes in step.
///         Write both through the same sink calls.
    ///     </para>
    ///     <para>
///         The codec must implement <see cref="IStateCodec{T}" /> for the member's type and be accessible from the
///         declaring type. The reader sets a member with a codec through its setter, not a constructor parameter. Such a
///         member makes the generated snapshot of a solver configuration a copy and not the same instance.
    ///     </para>
    /// </remarks>
    /// <example>
    ///     <code>
    ///     [NumosSerialize(Codec = typeof(StateCodecs.Nullable&lt;string, StateCodecs.String&gt;))]
    ///     public string Name;
    ///     </code>
    /// </example>
    public Type? Codec { get; set; }

    /// <summary>
    ///     Gets or sets the name of an <see langword="int" /> member holding how many leading elements of this array
    ///     are in use. Only applies on a type with a <see cref="NumosSnapshotOfAttribute" /> snapshot.
    /// </summary>
    /// <remarks>
    ///     For pooled or over-allocated live storage, such as an index list with a separate count. The snapshot holds
    ///     only that prefix, and restoring copies it back and sets the count member to its length.
    /// </remarks>
    /// <example>
    ///     <code>
    ///     public int ActiveCount;
    ///
    ///     [NumosSerialize(Count = nameof(ActiveCount))]
    ///     public ushort[] Active = new ushort[64];
    ///     </code>
    /// </example>
    public string? Count { get; set; }
}

/// <summary>
///     Makes a <see langword="partial" /> class the generated, detached snapshot of a live type. It has one member per
///     <see cref="NumosSerializeAttribute" /> member of the live type, plus capture, restore, equality, a state hash, and
///     the wire format.
/// </summary>
/// <remarks>
///     <para>
///         Marking a live member is all it takes to add it to the snapshot. Each one becomes a get-only member of the
///         snapshot with the visibility of the live member, so marked members must be <see langword="internal" /> or
///         <see langword="public" />. The member has the name of the live member, or the name passed to
///         <see cref="NumosSerializeAttribute(string)" />, which is also its wire name. The live type needs no attribute
///         of its own and does not have to be partial. It must be a class in the same project.
///     </para>
///     <para>
///         How a member is captured depends on what it is. Fields and auto-properties of immutable types (primitives,
///         enums, strings, <c>Int3</c>, quantity aliases, and immutable serializable types) are copied by value.
///         Arrays and <c>FlatArray&lt;T&gt;</c> of such elements are copied into an
///         <see cref="System.Collections.Generic.IReadOnlyList{T}" />, optionally only a prefix (see
///         <see cref="NumosSerializeAttribute.Count" />). A member whose type has a snapshot of its own is captured as
///         that snapshot. An array or list of such members is captured as a list of snapshots. A property with a
///         hand-written getter is an adapter. The snapshot keeps whatever the getter returns, so the getter must return a
///         value nothing else holds. Restoring passes the value back to its setter.
///     </para>
///     <para>
///         The generator adds <c>internal static Capture(live)</c>. When every member can be put back, it also adds
///         <c>internal RestoreInto(live)</c>. That method writes into an existing live object in the declaration order of
///         the live type. Arrays are copied into the live arrays, and their lengths must match. Settable members are assigned.
///         Read-only value members (such as dimensions) must already match. Nothing is written until every check
///         has passed. Members that only the owner can rebuild, such as a list of live children, make the snapshot
///         capture-only. The owner then creates the live objects and calls the <c>RestoreInto</c> of the children itself.
///     </para>
///     <para>
///         To give a generated member different visibility or its own docs, declare it on the snapshot as a get-only
///         partial property (<c>public partial IReadOnlyList&lt;Foo&gt; Foos { get; }</c>). The generator implements it.
///         That is how an <see langword="internal" /> live member becomes a <see langword="public" /> snapshot member.
///     </para>
///     <para>
///         The snapshot can declare members of its own. A hand-written <see cref="NumosSerializeAttribute" /> member is
///         part of the hash and wire format like a generated one. Fill it in an optional
///         <c>partial void OnCaptured(TLive live)</c>. Use it in <c>partial void OnRestored(TLive live)</c>, which
///         runs after the generated restore. <c>partial void OnDecoded()</c> runs after a snapshot is read from bytes,
///         for validation.
///     </para>
/// </remarks>
/// <example>
///     <code>
///     internal sealed class Tank
///     {
///         [NumosSerialize] public float Pressure;
///         [NumosSerialize] public float[] Cells = new float[16];
///     }
///
///     [NumosSnapshotOf(typeof(Tank))]
///     public sealed partial class TankSnapshot;
///
///     var snapshot = TankSnapshot.Capture(tank);
///     snapshot.RestoreInto(otherTank);
///     </code>
/// </example>
/// <param name="liveType">The live class whose marked members the snapshot mirrors.</param>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class NumosSnapshotOfAttribute(Type liveType) : Attribute
{
    /// <summary>
    ///     Gets the live class whose marked members the snapshot mirrors.
    /// </summary>
    public Type LiveType { get; } = liveType;
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
