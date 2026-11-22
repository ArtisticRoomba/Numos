namespace Numos.CoreSim.State.Advanced;

/// <summary>
///     Implemented by the source generator for every type with <see cref="NumosSerializeAttribute" /> members.
/// </summary>
/// <typeparam name="TSelf">The implementing type.</typeparam>
/// <remarks>
///     Generated implementations are explicit, so the members do not show up on the type itself. Call them through
///     <see cref="NumosState" /> or a generic method constrained to this interface. Do not implement it by hand. The
///     generator guarantees that every field reaches all three members in the same order.
/// </remarks>
public interface INumosState<TSelf> : INumosState where TSelf : INumosState<TSelf>
{
    /// <summary>
    ///     Gets the constant the type's <c>Key</c> property returns, or <see langword="null" /> when it has none.
    /// </summary>
    /// <remarks>
    ///     Every solver configuration has one, since the generator requires a constant key there. Standalone state files
    ///     record it so a load can check it got the type it asked for.
    /// </remarks>
    static abstract string? StateKey { get; }

    /// <summary>
    ///     Walks <paramref name="value" />'s state fields in ascending id order into <paramref name="sink" />.
    /// </summary>
    /// <param name="sink">The sink to write to.</param>
    /// <param name="value">The value to write.</param>
    /// <typeparam name="TSink">The sink type.</typeparam>
    static abstract void WriteState<TSink>(ref TSink sink, TSelf value) where TSink : struct, IStateSink;

    /// <summary>
    ///     Compares every state field, with the same rules the hash uses: floats by bits, collections in canonical
    ///     order.
    /// </summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    /// <returns>True when every state field matches.</returns>
    static abstract bool StateEquals(TSelf left, TSelf right);

    /// <summary>
    ///     Orders two values by their state fields in ascending id order.
    /// </summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    /// <returns>Negative, zero, or positive, like <see cref="IComparer{T}.Compare" />.</returns>
    /// <remarks>
    ///     A total order that only exists so sets of these values can be hashed and compared in a canonical order. It
    ///     has no physical meaning. It returns zero exactly when <see cref="StateEquals" /> returns true.
    /// </remarks>
    static abstract int CompareState(TSelf left, TSelf right);

    /// <summary>
    ///     Reads one value written by <see cref="WriteState{TSink}" /> through a <see cref="StateBinarySink" />.
    /// </summary>
    /// <param name="reader">The reader, positioned at the value.</param>
    /// <param name="gases">
    ///     Resolves <see cref="GasProperties" /> references by name. May be <see langword="null" /> for types that do not
    ///     reference gases.
    /// </param>
    /// <returns>The value, built through its bound constructor.</returns>
    /// <remarks>
    ///     The reader skips field ids the type does not know. Fields that the data does not contain get their default
    ///     (see <see cref="NumosSerializeAttribute" />). Data from an older or newer version of the type therefore still
    ///     loads.
    /// </remarks>
    /// <exception cref="InvalidDataException">The data is malformed or names a gas <paramref name="gases" /> lacks.</exception>
    /// <exception cref="InvalidOperationException">
    ///     The data references a gas and <paramref name="gases" /> is <see langword="null" />.
    /// </exception>
    static abstract TSelf ReadState(ref StateReader reader, IGasRegistry? gases);
}

/// <summary>
///     The non-generic face of <see cref="INumosState{TSelf}" />, for writing a value whose static type is something
///     else, such as an <see cref="IAtmosSolverConfiguration" /> in a configuration list.
/// </summary>
/// <remarks>
///     Implemented by the generator alongside <see cref="INumosState{TSelf}" />. Writing a world archive uses it to tell
///     schema-backed solver configurations, which it can save, from hand-written ones, which it can't.
/// </remarks>
public interface INumosState
{
    /// <summary>
    ///     Walks this value's state fields into <paramref name="sink" />, exactly as
    ///     <see cref="INumosState{TSelf}.WriteState{TSink}" /> would.
    /// </summary>
    /// <param name="sink">The sink to write to.</param>
    /// <typeparam name="TSink">The sink type.</typeparam>
    void WriteState<TSink>(ref TSink sink) where TSink : struct, IStateSink;
}

/// <summary>
///     Static entry points over <see cref="INumosState{TSelf}" />.
/// </summary>
public static class NumosState
{
    // Field id to wire name, for printing state differences. Only diagnostics read it, so it never reaches a hash or
    // a byte.
    private static readonly Lock FieldNamesLock = new();
    private static readonly Dictionary<int, string> FieldNames = new();

    /// <summary>
    ///     Gets the wire id of a field with the given wire name: its member name, or the name passed to
    ///     <see cref="NumosSerializeAttribute(string)" />.
    /// </summary>
    /// <param name="name">The wire name.</param>
    /// <returns>A positive id.</returns>
    /// <remarks>
    ///     FNV-1a (32-bit) over the name's UTF-8 bytes, with the sign bit cleared so the id is positive, and 0 mapped
    ///     to 1. The generator computes the same thing at build time. Only hand-written codecs that frame their own
    ///     objects need to call it.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="name" /> is <see langword="null" />.</exception>
    public static int FieldId(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        uint hash = 2166136261;
        int length = System.Text.Encoding.UTF8.GetByteCount(name);
        Span<byte> bytes = length <= 256 ? stackalloc byte[length] : new byte[length];
        System.Text.Encoding.UTF8.GetBytes(name, bytes);
        foreach (byte value in bytes)
        {
            hash ^= value;
            hash *= 16777619;
        }

        int id = (int)(hash & 0x7FFFFFFF);
        return id == 0 ? 1 : id;
    }

    /// <summary>
    ///     Records wire names so a state difference can print a field by name instead of by id.
    /// </summary>
    /// <param name="names">Wire names, as <see cref="FieldId" /> takes them.</param>
    /// <remarks>
    ///     Generated module initializers call this for every type with <see cref="NumosSerializeAttribute" /> members.
    ///     The table is process-wide and only feeds diagnostics, so it changes no hash and no saved byte. When two names
    ///     share an id, the first one registered stays.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="names" /> or one of its entries is <see langword="null" />.</exception>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static void RegisterFieldNames(params string[] names)
    {
        ArgumentNullException.ThrowIfNull(names);
        lock (FieldNamesLock)
        {
            foreach (string name in names)
                FieldNames.TryAdd(FieldId(name), name);
        }
    }

    /// <summary>
    ///     Gets the wire name registered for <paramref name="id" />, or <c>#id</c> when none is.
    /// </summary>
    internal static string GetFieldName(int id)
    {
        lock (FieldNamesLock)
        {
            if (FieldNames.TryGetValue(id, out string? name))
                return name;
        }

        return "#" + id.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    ///     Walks <paramref name="value" />'s state fields into <paramref name="sink" />.
    /// </summary>
    /// <param name="sink">The sink to write to.</param>
    /// <param name="value">The value to write.</param>
    /// <typeparam name="T">A type with <see cref="NumosSerializeAttribute" /> members.</typeparam>
    /// <typeparam name="TSink">The sink type.</typeparam>
    public static void Write<T, TSink>(ref TSink sink, T value)
        where T : INumosState<T>
        where TSink : struct, IStateSink
    {
        T.WriteState(ref sink, value);
    }

    /// <summary>
    ///     Reads a value written through a <see cref="StateBinarySink" />.
    /// </summary>
    /// <param name="reader">The reader, positioned at the value.</param>
    /// <param name="gases">Resolves gas references by name. Needed only when the value references gases.</param>
    /// <typeparam name="T">A type with <see cref="NumosSerializeAttribute" /> members.</typeparam>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidDataException">The data is malformed or names a gas <paramref name="gases" /> lacks.</exception>
    /// <exception cref="InvalidOperationException">
    ///     The data references a gas and <paramref name="gases" /> is <see langword="null" />.
    /// </exception>
    public static T Read<T>(ref StateReader reader, IGasRegistry? gases = null) where T : INumosState<T>
    {
        return T.ReadState(ref reader, gases);
    }

    /// <summary>
    ///     Compares two values field by field.
    /// </summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    /// <typeparam name="T">A type with <see cref="NumosSerializeAttribute" /> members.</typeparam>
    /// <returns>True when every state field matches.</returns>
    public static bool StateEquals<T>(T left, T right) where T : INumosState<T>
    {
        return T.StateEquals(left, right);
    }

    /// <summary>
    ///     Hashes a value's state fields with a fresh <see cref="StateHashSink" />.
    /// </summary>
    /// <param name="value">The value to hash.</param>
    /// <typeparam name="T">A type with <see cref="NumosSerializeAttribute" /> members.</typeparam>
    /// <returns>The value's state hash.</returns>
    /// <remarks>
    ///     A solver configuration's generated <see cref="IAtmosSolverConfiguration.ComputeStateHash" /> also hashes its
    ///     key first, so the two results differ.
    /// </remarks>
    public static ulong ComputeHash<T>(T value) where T : INumosState<T>
    {
        var sink = new StateHashSink();
        T.WriteState(ref sink, value);
        return sink.Value;
    }
}

/// <summary>
///     Implemented by the source generator for every type it writes state code for, to name the shape of the state it
///     writes.
/// </summary>
/// <remarks>
///     <para>
///         A world checkpoint stores the fingerprint of each class solver and simulation component next to its
///         payload. A restore compares it with the receiving type's fingerprint and reports the names that differ.
///         Data from another version of a type still loads, because the reader skips unknown fields and keeps
///         missing ones (see <see cref="INumosStateTarget.ReadStateInto" />).
///     </para>
///     <para>
///         The generator computes the value at build time. It is FNV-1a (64-bit) over the fields in ascending id
///         order. Each field adds its wire name and a description of what its codec writes, such as <c>Single</c>,
///         <c>List(Int32)</c>, or <c>Nullable(String)</c>. A C# rename that keeps the wire name with
///         <see cref="NumosSerializeAttribute(string)" /> keeps the fingerprint, and so does reordering members in
///         source. A new, removed, or renamed wire name changes it, and so does a new member type that writes
///         different bytes.
///     </para>
///     <para>
///         A class solver or simulation component that keeps part of its state in a base class writes one wire object
///         for the whole chain. Its fingerprint covers the fields of every class in the chain, the same as one class
///         with all of them, so a change in a base class changes it.
///     </para>
///     <para>
///         The description of a nested state type is the word <c>State</c>, so a change inside a nested type does not
///         change the fingerprint of the type that holds it. An enum contributes its underlying type and a quantity
///         alias its storage type, so swapping one enum or alias for another with the same storage keeps it.
///     </para>
///     <para>
///         Do not implement it by hand. A hand-written type without it is never reported as changed.
///     </para>
/// </remarks>
public interface INumosStateSchema
{
    /// <summary>
    ///     Gets the fingerprint of this type's state schema. It is never 0.
    /// </summary>
    ulong SchemaFingerprint { get; }
}

/// <summary>
///     Implemented by the source generator for a class solver with <see cref="NumosSerializeAttribute" /> members, and
///     for every <c>[SimulationComponent]</c> class. Its state can then be written out and read back into the same
///     instance.
/// </summary>
/// <remarks>
///     <para>
///         A solver is owned by its host and is never rebuilt from data, so it gets no
///         <see cref="INumosState{TSelf}.ReadState" />. Restoring a checkpoint or replaying a recorded edit reads
///         straight into the registered instance instead.
///     </para>
///     <para>
///         Do not implement it by hand. <see cref="INumosState.WriteState{TSink}" /> and <see cref="ReadStateInto" />
///         must agree on every field, and the generator guarantees that.
///     </para>
/// </remarks>
public interface INumosStateTarget : INumosState
{
    /// <summary>
    ///     Reads a payload written by <see cref="INumosState.WriteState{TSink}" /> into this instance.
    /// </summary>
    /// <param name="reader">The reader, positioned at the payload.</param>
    /// <param name="gases">Resolves <see cref="GasProperties" /> references by name.</param>
    /// <remarks>
    ///     <para>
    ///         Every field is decoded before any member is written, so malformed data leaves the instance unchanged.
    ///         Field ids the type doesn't know are skipped, and members the payload doesn't contain keep their current
    ///         value.
    ///     </para>
    ///     <para>
    ///         A <see cref="List{T}" />, <see cref="HashSet{T}" />, or <see cref="Dictionary{TKey, TValue}" /> the
    ///         instance already holds is cleared and refilled, and an array of the same length is copied into. Other
    ///         references to those collections stay valid.
    ///     </para>
    /// </remarks>
    /// <exception cref="InvalidDataException">The data is malformed or names a gas <paramref name="gases" /> lacks.</exception>
    /// <exception cref="InvalidOperationException">
    ///     The data references a gas and <paramref name="gases" /> is <see langword="null" />, or a collection member
    ///     the reader can only refill, such as a <see langword="readonly" /> field, is <see langword="null" />.
    /// </exception>
    void ReadStateInto(ref StateReader reader, IGasRegistry? gases);
}

/// <summary>
///     Implemented by the source generator for a type restored in place that has a member marked
///     <c>[NumosSerialize(HostEditable = false)]</c>.
/// </summary>
/// <remarks>
///     <para>
///         Numos finds host edits by hashing the state at every recording boundary. It hashes
///         <see cref="WriteHostEditableState{TSink}" /> instead of the full state, so a bulky member that only the solver
///         changes costs nothing there. A Debug build of Numos also hashes each such member with
///         <see cref="HashBulkMembers" /> and fails when one changed between ticks.
///     </para>
///     <para>
///         Do not implement it by hand. The generator keeps the member list in step with the state walker.
///     </para>
/// </remarks>
public interface INumosBulkStateTarget
{
    /// <summary>
    ///     Gets the names of the members that are not host-editable, in the order <see cref="HashBulkMembers" /> fills.
    /// </summary>
    IReadOnlyList<string> BulkMemberNames { get; }

    /// <summary>
    ///     Walks the host-editable state fields into <paramref name="sink" /> as one object.
    /// </summary>
    /// <param name="sink">The sink to write to.</param>
    /// <typeparam name="TSink">The sink type.</typeparam>
    void WriteHostEditableState<TSink>(ref TSink sink) where TSink : struct, IStateSink;

    /// <summary>
    ///     Hashes each member that is not host-editable on its own.
    /// </summary>
    /// <param name="hashes">Receives one hash per entry of <see cref="BulkMemberNames" />, in that order.</param>
    void HashBulkMembers(Span<ulong> hashes);
}

/// <summary>
///     Implemented by the source generator for a class solver with <see cref="NumosScratchAttribute" /> members.
/// </summary>
/// <remarks>
///     <para>
///         Numos calls <see cref="ResetScratch" /> after a checkpoint restore, before the solver's <c>OnRestored</c>.
///         A solver with scratch members and no <see cref="NumosSerializeAttribute" /> member implements this interface
///         only. It gets no <see cref="INumosStateTarget" /> and no checkpoint entry.
///     </para>
///     <para>
///         Do not implement it by hand. The generator writes the reset of each member from its declaration.
///     </para>
/// </remarks>
public interface INumosScratchTarget
{
    /// <summary>
    ///     Resets every <see cref="NumosScratchAttribute" /> member to the state a new instance has.
    /// </summary>
    /// <remarks>
    ///     A <see langword="readonly" /> member is cleared. Any other member with an initializer gets the initializer
    ///     again, and the rest get <see langword="default" />.
    /// </remarks>
    void ResetScratch();
}
