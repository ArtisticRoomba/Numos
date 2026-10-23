namespace Numos.CoreSim.Solvers;

/// <summary>
///     Transient solver data for one simulation, indexed by <see cref="SolverDataDescriptor.Id" />.
/// </summary>
/// <remarks>
///     Callers serialize access. Host requests go through the kernel state gate, and built-in stages use the tick
///     thread. Slot ids follow static-initialization order, so never iterate this storage in slot order for anything
///     that affects results. Code only looks up and clears slots.
/// </remarks>
internal sealed class SolverDataStorage
{
    private readonly static object Creating = new();
    private object?[] _values = [];

    /// <summary>
    ///     Gets the value in a slot, running the factory on first use.
    /// </summary>
    /// <param name="descriptor">The slot's descriptor.</param>
    /// <param name="state">Passed to <paramref name="factory" /> so callers can use static lambdas.</param>
    /// <param name="factory">Builds the value. Failures and null results are not cached.</param>
    /// <returns>The cached value.</returns>
    /// <exception cref="InvalidOperationException">
    ///     The factory requests its own slot, returns null, or the slot holds another type.
    /// </exception>
    internal T GetOrCreate<T, TState>(SolverDataDescriptor descriptor, TState state, Func<TState, T> factory)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(factory);
        int slotId = descriptor.Id;
        string name = descriptor.Name;
        if (slotId >= _values.Length)
            Array.Resize(ref _values, Math.Max(slotId + 1, Math.Max(8, _values.Length * 2)));

        object? existing = _values[slotId];
        if (existing != null)
        {
            if (ReferenceEquals(existing, Creating))
            {
                throw new InvalidOperationException($"The factory for solver data '{name}' requested its own value while creating it.");
            }

            return existing as T ??
                   throw new InvalidOperationException($"Solver data '{name}' holds a {existing.GetType()}, not a {typeof(T)}.");
        }

        // Nested requests may grow the array and copy the marker along, so always go through the current array afterwards.
        // A slot that no longer holds the marker was cleared mid-factory. Leave it empty.
        _values[slotId] = Creating;
        T value;
        try
        {
            value = factory(state) ?? throw new InvalidOperationException($"The factory for solver data '{name}' returned null.");
        }
        catch
        {
            if (ReferenceEquals(_values[slotId], Creating))
                _values[slotId] = null;

            throw;
        }

        if (ReferenceEquals(_values[slotId], Creating))
            _values[slotId] = value;

        return value;
    }

    /// <summary>
    ///     Drops every value without disposing it.
    /// </summary>
    internal void Clear()
    {
        Array.Clear(_values);
    }
}