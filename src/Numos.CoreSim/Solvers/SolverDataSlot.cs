namespace Numos.CoreSim.Solvers;

/// <summary>
///     Identifies one piece of solver data. Built-in slots and public keys both hold one, so they share one id space
///     and can live in the same <see cref="SolverDataStorage" />.
/// </summary>
/// <remarks>
///     Ids follow static-initialization order and differ between processes. They index storage and nothing else:
///     never hash, serialize, or iterate in id order. Ids are never reclaimed.
/// </remarks>
internal sealed class SolverDataDescriptor
{
    private static int _nextId = -1;

    /// <summary>
    ///     Creates a descriptor with its own id.
    /// </summary>
    /// <param name="name">A diagnostic name, used in error messages only.</param>
    internal SolverDataDescriptor(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Id = Interlocked.Increment(ref _nextId);
        Name = name;
    }

    internal int Id { get; }
    internal string Name { get; }
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
    internal SolverDataSlot(string name, Func<AtmosSolverExecutionContext, T> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        Descriptor = new SolverDataDescriptor(name);
        Factory = factory;
    }

    internal SolverDataDescriptor Descriptor { get; }
    internal Func<AtmosSolverExecutionContext, T> Factory { get; }
}
