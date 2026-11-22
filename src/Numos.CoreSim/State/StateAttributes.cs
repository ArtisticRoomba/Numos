using Numos.CoreSim.State.Advanced;

namespace Numos.CoreSim.State;

/// <summary>
///     Marks a field or auto-property as state. The <c>Numos.API</c> source generator then writes equality, a state hash,
///     and a binary reader and writer for the declaring type. For solver configurations it also writes a snapshot copy
///     and the registration for loading.
/// </summary>
/// <remarks>
///     <para>
///         Any type with at least one marked member takes part. It must be <see langword="partial" /> (along with every
///         type it is nested in) and non-generic. A class must derive from <see cref="object" />. On a positional
///         record, mark the parameter with <c>[property: NumosSerialize]</c>.
///     </para>
///     <para>
///         A <c>[ChunkComponent]</c> struct does not take part. On its fields the attribute marks a saved chunk field,
///         and the name passed to <see cref="NumosSerializeAttribute(string)" /> is the last part of the field key.
///     </para>
///     <para>
///         A field's wire id is a hash of its name, so reordering members does not change ids.
///         Renaming a member changes its id. Pass the old name (<c>[NumosSerialize("OldName")]</c>) to keep reading
///         files written before the rename. A reader skips ids it does not know, so removing a member keeps old files readable.
///         Two names in one type that hash to the same id fail the build.
///     </para>
///     <para>
///         A mutable instance member with neither this attribute nor <see cref="NumosIgnoreAttribute" /> is a build
///         warning. That holds on a participating type and on the live type of a
///         <see cref="NumosSnapshotOfAttribute" /> snapshot. A new setting therefore cannot quietly miss the hash. The
///         generator assumes readonly fields and get-only auto-properties are derived caches and does not flag them.
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
///         (no <see langword="readonly" /> fields, get-only, or <c>init</c> properties), unless it is a
///         <see cref="List{T}" />, <see cref="HashSet{T}" />, or <see cref="Dictionary{TKey, TValue}" /> that the reader
///         refills. A <c>[SimulationComponent]</c> class follows the same rules. The reader refills a set or dictionary
///         in canonical order, so its enumeration order after a restore can differ from the live instance's order. A
///         solver whose results depend on that order diverges on replay.
///     </para>
/// </remarks>
/// <example>
///     A solver configuration. The generator emits its equality, hash, wire code, loading registration, and
///     <see cref="IAtmosSolverConfiguration.CreateSnapshot" />:
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

    /// <summary>
    ///     Gets or sets whether the host may change the member between ticks. The default is <see langword="true" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Only applies to a class solver or a <c>[SimulationComponent]</c> class. Numos finds host edits to those
    ///         by hashing their marked members at every recording boundary. Set this to <see langword="false" /> on a
    ///         large member that only the solver changes, such as a list that grows every tick. The boundary hash then
    ///         skips it. The checkpoint, the world state hash, and a recorded state edit still cover it.
    ///     </para>
    ///     <para>
    ///         The host must change such a member only through a solver command, which replay runs again. A direct
    ///         edit between ticks is not recorded, so replay would diverge. A Debug build of Numos still hashes the
    ///         member at each boundary and throws <see cref="InvalidOperationException" /> naming it when it changed.
    ///     </para>
    /// </remarks>
    /// <example>
    ///     <code>
    ///     [NumosSerialize(HostEditable = false)]
    ///     public readonly List&lt;PipeNode&gt; Nodes = [];
    ///     </code>
    /// </example>
    public bool HostEditable { get; set; } = true;
}

/// <summary>
///     Makes a <see langword="partial" /> class the generated, detached snapshot of a live type. It has one member per
///     <see cref="NumosSerializeAttribute" /> member of the live type, plus capture, restore, equality, a state hash, and
///     the wire format.
/// </summary>
/// <remarks>
///     <para>
///         Each marked live member becomes a get-only snapshot member with the same visibility, so marked members must
///         be <see langword="internal" /> or <see langword="public" />. The snapshot member has the live member's name,
///         or the name passed to <see cref="NumosSerializeAttribute(string)" />, which is also its wire name. The live
///         type needs no attribute and does not have to be partial. It must be a class in the same project.
///     </para>
///     <para>
///         An unmarked mutable member of the live type is a build warning, as on a participating type. Mark it
///         <see cref="NumosIgnoreAttribute" /> when the restore rebuilds it, when it is runtime-only, or when a marked
///         adapter property saves it. A member that a <see cref="NumosSerializeAttribute.Count" /> argument names needs
///         neither attribute, because the snapshot saves and restores it.
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
///         capture-only. So do a get-only reference member and a nested snapshot that is capture-only. The owner then
///         creates the live objects and calls the <c>RestoreInto</c> of the children itself. A capture-only snapshot
///         still has a <c>RestoreInto</c>, but a call to it fails the build (CS0619). The error message names the first
///         member that blocks the restore.
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
///     Says a field or property of a type with <see cref="NumosSerializeAttribute" /> members, or of the live type of a
///     <see cref="NumosSnapshotOfAttribute" /> snapshot, is deliberately not state. That silences the warning for an
///     unmarked mutable member.
/// </summary>
/// <remarks>
///     For derived caches rebuilt from the marked members, scratch buffers, and runtime-only flags. It also fits a
///     private field that a marked adapter property saves and restores, such as the storage behind a hand-written
///     getter. Anything that changes solver behavior on its own belongs in a <see cref="NumosSerializeAttribute" />
///     member instead, or replay will diverge.
/// </remarks>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = false)]
public sealed class NumosIgnoreAttribute : Attribute;

/// <summary>
///     Says a field or property of a class solver is scratch: Numos resets it after a checkpoint restore, before
///     <c>OnRestored</c> runs. Use it for buffers and caches that the solver refills on its own.
/// </summary>
/// <remarks>
///     <para>
///         The source generator writes <see cref="INumosScratchTarget.ResetScratch" /> for the class. The class must be
///         <see langword="partial" />. The member is not saved, hashed, or recorded. A solver with scratch members and
///         no <see cref="NumosSerializeAttribute" /> member still has no checkpoint entry.
///     </para>
///     <para>
///         The reset follows the member's declaration:
///     </para>
///     <list type="bullet">
///         <item>
///             <description>
///                 An assignable member with an initializer, such as <c>= new()</c>, gets the initializer again. An
///                 assignable member is a field that is not <see langword="readonly" />, or a property with a
///                 <see langword="set" /> accessor.
///             </description>
///         </item>
///         <item>
///             <description>
///                 A <see langword="readonly" /> field or get-only property whose type is a class or interface with a
///                 parameterless <c>Clear()</c> gets that call, so a <c>List&lt;T&gt;</c> keeps its instance and loses
///                 its items.
///             </description>
///         </item>
///         <item>
///             <description>
///                 An assignable member with no initializer gets <see langword="default" />.
///             </description>
///         </item>
///     </list>
///     <para>
///         Any other member is a build error. The generator copies an initializer into a method body, so an initializer
///         that reads a primary constructor parameter is also an error. Reset such a member in <c>OnRestored</c> and mark
///         it <see cref="NumosIgnoreAttribute" />. The attribute cannot combine with <see cref="NumosSerializeAttribute" />
///         or <see cref="NumosIgnoreAttribute" />. It only applies to class solvers and <c>[SimulationComponent]</c>
///         classes. Numos resets a component's scratch members after a world checkpoint restore.
///     </para>
///     <para>
///         A field of a <c>[ChunkComponent]</c> struct is the one exception. There the attribute makes a transient chunk
///         field, which a restore clears, and the reset rules above do not apply.
///     </para>
/// </remarks>
/// <example>
///     <code>
///     public sealed partial class FireSolver : IAtmosWorldSolver
///     {
///         [NumosScratch] private readonly List&lt;int&gt; _hot = new();
///         [NumosScratch] private int _cursor;
///     }
///     </code>
/// </example>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = false)]
public sealed class NumosScratchAttribute : Attribute;
