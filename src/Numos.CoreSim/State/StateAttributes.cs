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
///         On a class implementing <c>IAtmosWorldSolver</c>, marked members are the solver's state: Numos checkpoints,
///         hashes, and saves them, and records an edit the host makes between ticks. They're read back into the
///         registered instance rather than through a constructor, so each one has to be assignable (no
///         <see langword="readonly" /> fields, get-only, or <c>init</c> properties).
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
///     Makes a <see langword="partial" /> class the generated, detached snapshot of a live type: one member per
///     <see cref="NumosSerializeAttribute" /> member of the live type, plus capture, restore, equality, a state hash, and
///     the wire format.
/// </summary>
/// <remarks>
///     <para>
///         Marking a live member is all it takes to add it to the snapshot. Each one becomes a get-only member of the
///         snapshot with the same name (or the name passed to <see cref="NumosSerializeAttribute(string)" />, which is
///         also its wire name) and the live member's visibility, so marked members must be <see langword="internal" />
///         or <see langword="public" />. The live type needs no attribute of its own and doesn't have to be partial,
///         but it must be a class in the same project.
///     </para>
///     <para>
///         How a member is captured depends on what it is. Fields and auto-properties of immutable types (primitives,
///         enums, strings, <c>Int3</c>, quantity aliases, and immutable serializable types) are copied by value.
///         Arrays and <c>FlatArray&lt;T&gt;</c> of such elements are copied into an
///         <see cref="System.Collections.Generic.IReadOnlyList{T}" />, optionally only a prefix (see
///         <see cref="NumosSerializeAttribute.Count" />). A member whose type has a snapshot of its own is captured as
///         that snapshot, and an array or list of them as a list of snapshots. A property with a hand-written getter is
///         an adapter: the snapshot keeps whatever the getter returns, so it must return a value nothing else holds,
///         and restoring passes the value back to its setter.
///     </para>
///     <para>
///         The generator adds <c>internal static Capture(live)</c> and, when every member can be put back, an
///         <c>internal RestoreInto(live)</c> that writes into an existing live object in the live type's declaration
///         order: arrays are copied into the live arrays (their lengths must match), settable members are assigned,
///         and read-only value members (such as dimensions) must already match. Nothing is written until every check
///         has passed. Members that only the owner can rebuild, such as a list of live children, make the snapshot
///         capture-only; the owner then creates the live objects and calls the children's <c>RestoreInto</c> itself.
///     </para>
///     <para>
///         The snapshot can declare members of its own. A hand-written <see cref="NumosSerializeAttribute" /> member is
///         part of the hash and wire format like a generated one; fill it in an optional
///         <c>partial void OnCaptured(TLive live)</c> and use it in <c>partial void OnRestored(TLive live)</c>, which
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
