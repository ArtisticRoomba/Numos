namespace Numos.CoreSim.Solvers;

/// <summary>
///     Hands out process-wide slot ids for solver data. Built-in slots and public keys share one id space, so both
///     can live in the same <see cref="SolverDataStorage" />.
/// </summary>
/// <remarks>
///     Ids follow static-initialization order and differ between processes. They index storage and nothing else:
///     never hash, serialize, or iterate in slot order.
/// </remarks>
internal static class SolverDataSlotIds
{
    private static int _next = -1;

    /// <summary>
    ///     Reserves the next slot id. Ids are never reclaimed.
    /// </summary>
    /// <returns>A unique nonnegative slot id.</returns>
    internal static int Next()
    {
        return Interlocked.Increment(ref _next);
    }
}

/// <summary>
///     Names one piece of shared solver data used by a built-in stage.
/// </summary>
/// <typeparam name="T">The stored reference type.</typeparam>
/// <remarks>
///     Identity is the slot instance, so keep slots in <see langword="static readonly" /> fields. The factory runs on
///     the tick thread the first time the slot is read for a simulation, or after the storage was cleared.
/// </remarks>
internal sealed class SolverDataSlot<T> where T : class
{
    /// <summary>
    ///     Creates a slot with its own id.
    /// </summary>
    /// <param name="name">A diagnostic name, used in error messages only.</param>
    /// <param name="factory">Builds the value from the current tick's inputs. Must not return null.</param>
    /// <param name="resetOnConfigChange">
    ///     <see langword="true" /> to store the value with the tick configuration, so a configuration change drops it;
    ///     <see langword="false" /> to keep it until restore or disposal.
    /// </param>
    internal SolverDataSlot(string name, Func<AtmosSolverExecutionContext, T> factory, bool resetOnConfigChange = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(factory);
        Id = SolverDataSlotIds.Next();
        Name = name;
        Factory = factory;
        ResetOnConfigChange = resetOnConfigChange;
    }

    internal int Id { get; }
    internal string Name { get; }
    internal bool ResetOnConfigChange { get; }
    internal Func<AtmosSolverExecutionContext, T> Factory { get; }
}
