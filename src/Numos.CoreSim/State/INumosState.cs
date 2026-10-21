namespace Numos.CoreSim.State;

/// <summary>
///     Implemented by the source generator for every <see cref="NumosStateAttribute" /> type.
/// </summary>
/// <typeparam name="TSelf">The implementing type.</typeparam>
/// <remarks>
///     Generated implementations are explicit, so the members don't show up on the type itself. Call them through
///     <see cref="NumosState" /> or a generic method constrained to this interface. Don't implement it by hand; the
///     generator is what guarantees that every field reaches all three members in the same order.
/// </remarks>
public interface INumosState<TSelf> where TSelf : INumosState<TSelf>
{
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
}

/// <summary>
///     Static entry points over <see cref="INumosState{TSelf}" />.
/// </summary>
public static class NumosState
{
    /// <summary>
    ///     Walks <paramref name="value" />'s state fields into <paramref name="sink" />.
    /// </summary>
    /// <param name="sink">The sink to write to.</param>
    /// <param name="value">The value to write.</param>
    /// <typeparam name="T">A <see cref="NumosStateAttribute" /> type.</typeparam>
    /// <typeparam name="TSink">The sink type.</typeparam>
    public static void Write<T, TSink>(ref TSink sink, T value)
        where T : INumosState<T>
        where TSink : struct, IStateSink
    {
        T.WriteState(ref sink, value);
    }

    /// <summary>
    ///     Compares two values field by field.
    /// </summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    /// <typeparam name="T">A <see cref="NumosStateAttribute" /> type.</typeparam>
    /// <returns>True when every state field matches.</returns>
    public static bool StateEquals<T>(T left, T right) where T : INumosState<T>
    {
        return T.StateEquals(left, right);
    }

    /// <summary>
    ///     Hashes a value's state fields with a fresh <see cref="StateHashSink" />.
    /// </summary>
    /// <param name="value">The value to hash.</param>
    /// <typeparam name="T">A <see cref="NumosStateAttribute" /> type.</typeparam>
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
