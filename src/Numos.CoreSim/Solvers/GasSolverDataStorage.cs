namespace Numos.CoreSim.Solvers;

/// <summary>
///     Solver-owned, derived data for one applied gas registry. Access is serialized by the kernel state gate.
/// </summary>
internal sealed class GasSolverDataStorage(int gasCount)
{
    private readonly static object Creating = new();
    private readonly Dictionary<object, object>?[] _gases = new Dictionary<object, object>?[gasCount];

    internal T GetOrCreate<T>(int gasId, object key, GasProperties properties, Func<GasProperties, T> factory)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(factory);
        if ((uint)gasId >= (uint)_gases.Length)
            throw new ArgumentOutOfRangeException(nameof(gasId), "The gas is not registered.");

        Dictionary<object, object> data = _gases[gasId] ??= new Dictionary<object, object>(SolverArrayKeyComparer.Instance);
        if (data.TryGetValue(key, out object? existing))
        {
            if (existing is Entry<T> entry)
                return entry.Value;

            throw new InvalidOperationException(
                ReferenceEquals(existing, Creating)
                    ? "A gas data factory cannot recursively request its own slot."
                    : "This gas data key already holds a different type.");
        }

        data.Add(key, Creating);
        try
        {
            var value = factory(properties);
            if (value is null)
                throw new InvalidOperationException("A gas data factory cannot return null.");

            data[key] = new Entry<T>(value);
            return value;
        }
        catch
        {
            data.Remove(key);
            throw;
        }
    }

    private sealed record Entry<T>(T Value);
}