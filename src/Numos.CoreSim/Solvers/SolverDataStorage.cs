namespace Numos.CoreSim.Solvers;

/// <summary>
///     Shared transient dependencies for one simulation. Resolve values on the tick thread before starting workers.
/// </summary>
internal sealed class SolverDataStorage
{
    private readonly static object Creating = new();
    private readonly Dictionary<object, object> _data = new(SolverArrayKeyComparer.Instance);

    internal T GetOrCreate<T>(object key, Func<T> factory) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(factory);
        if (_data.TryGetValue(key, out object? existing))
        {
            if (existing is Entry<T> entry)
                return entry.Value;

            throw new InvalidOperationException(
                ReferenceEquals(existing, Creating)
                    ? "A solver data factory cannot recursively request its own slot."
                    : "This solver data key already holds a different type.");
        }

        _data.Add(key, Creating);
        try
        {
            var value = factory();
            if (value is null)
                throw new InvalidOperationException("A solver data factory cannot return null.");

            _data[key] = new Entry<T>(value);
            return value;
        }
        catch
        {
            _data.Remove(key);
            throw;
        }
    }

    internal void Clear()
    {
        _data.Clear();
    }

    private sealed record Entry<T>(T Value);
}