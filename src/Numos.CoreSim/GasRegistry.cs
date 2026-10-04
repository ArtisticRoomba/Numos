using System.Collections;

namespace Numos.CoreSim;

/// <summary>
///     Read-only view over a set of registered gases, supporting indexing, enumeration,
///     and name-to-index lookups.
/// </summary>
public interface IGasRegistry : IEnumerable<GasProperties>
{
    /// <summary>
    ///     Gets the number of registered gases.
    /// </summary>
    int Count { get; }

    /// <summary>
    ///     Gets the gas at an index. The index is the gas ID used by chunks and solvers.
    /// </summary>
    /// <param name="index">The gas ID, from zero through <see cref="Count" /> minus one.</param>
    GasProperties this[int index] { get; }

    /// <summary>
    ///     Resolves a gas name to its index.
    /// </summary>
    /// <param name="gasId">The gas's <see cref="GasProperties.Name" />, compared ordinally.</param>
    /// <returns>The gas ID.</returns>
    /// <exception cref="KeyNotFoundException">Thrown if no gas with the given name is registered.</exception>
    int GasIdToIndex(string gasId);

    /// <summary>
    ///     Checks the registry for duplicate gas names.
    /// </summary>
    /// <exception cref="InvalidOperationException">Two or more gases share a name.</exception>
    void ValidateGasRegistry();
}

/// <summary>
///     Owns the list of gases registered to the sim, enforcing unique names and providing
///     name-to-index lookups via <see cref="GasIdToIndex" />.
/// </summary>
public sealed class GasRegistry : IGasRegistry
{
    private readonly List<GasProperties> _gases = [];
    private readonly Dictionary<string, int> _idMap = [];

    /// <inheritdoc />
    public int Count => _gases.Count;

    /// <inheritdoc />
    public GasProperties this[int index] => _gases[index];

    /// <inheritdoc />
    public int GasIdToIndex(string gasId)
    {
        if (_idMap.TryGetValue(gasId, out int index))
            return index;

        throw new KeyNotFoundException($"No gas registered with id '{gasId}'.");
    }

    /// <inheritdoc />
    public void ValidateGasRegistry()
    {
        List<string> duplicates = _gases
            .GroupBy(g => g.Name)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        if (duplicates.Count > 0)
            throw new InvalidOperationException($"Duplicate gas names found: {string.Join(", ", duplicates)}");
    }

    /// <summary>
    ///     Enumerates gases in gas-ID order.
    /// </summary>
    public IEnumerator<GasProperties> GetEnumerator()
    {
        return _gases.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    /// <summary>
    ///     Registers a gas with the next gas ID.
    /// </summary>
    /// <param name="gas">The gas to register.</param>
    /// <exception cref="InvalidOperationException">A gas with this name is already registered.</exception>
    public void Add(GasProperties gas)
    {
        // Gases with a null name skip the uniqueness check and can't be looked up by name. Tests only.
        if (gas.Name != null)
        {
            if (_idMap.ContainsKey(gas.Name))
                throw new InvalidOperationException($"A gas named '{gas.Name}' is already registered.");

            _idMap[gas.Name] = _gases.Count;
        }

        _gases.Add(gas);
    }

    /// <summary>
    ///     Removes the gas at the given index.
    /// </summary>
    /// <param name="index">The gas ID to remove.</param>
    /// <remarks>Every later gas shifts down one ID, so IDs captured before the removal are stale.</remarks>
    public void RemoveAt(int index)
    {
        string removedName = _gases[index].Name;
        _gases.RemoveAt(index);
        _idMap.Remove(removedName);

        // Every gas after the removed one shifted down by one — rebuild rather than patch in place.
        for (int i = index; i < _gases.Count; i++)
            _idMap[_gases[i].Name] = i;
    }

    /// <summary>
    ///     Replaces the gas at the given index, keeping its gas ID.
    /// </summary>
    /// <param name="index">The gas ID to replace.</param>
    /// <param name="gas">The new properties.</param>
    /// <exception cref="InvalidOperationException">
    ///     Thrown if <paramref name="gas" />'s name is already used by a different gas in the registry.
    /// </exception>
    public void Replace(int index, GasProperties gas)
    {
        string oldName = _gases[index].Name;

        if (gas.Name != oldName && _idMap.ContainsKey(gas.Name))
            throw new InvalidOperationException($"A gas named '{gas.Name}' is already registered.");

        _gases[index] = gas;

        if (gas.Name != oldName)
        {
            _idMap.Remove(oldName);
            _idMap[gas.Name] = index;
        }
    }
}

/// <summary>
///     Immutable point-in-time copy of an <see cref="IGasRegistry" />. Exposes indexing,
///     enumeration, and <see cref="GasIdToIndex" />, but no way to add or remove gases.
/// </summary>
public sealed class GasRegistrySnapshot : IGasRegistry
{
    private readonly GasProperties[] _gases;
    private readonly Dictionary<string, int> _idMap;

    /// <summary>
    ///     Copies the gases from <paramref name="source" />.
    /// </summary>
    /// <param name="source">The registry to copy.</param>
    /// <remarks>Duplicate names are not rejected here; call <see cref="ValidateGasRegistry" /> to check.</remarks>
    public GasRegistrySnapshot(IGasRegistry source)
    {
        _gases = source.ToArray();

        _idMap = new Dictionary<string, int>(_gases.Length);
        for (int i = 0; i < _gases.Length; i++)
        {
            if (_gases[i].Name != null)
                _idMap[_gases[i].Name] = i;
        }
    }

    /// <inheritdoc />
    public int Count => _gases.Length;

    /// <inheritdoc />
    public GasProperties this[int index] => _gases[index];

    /// <inheritdoc />
    public int GasIdToIndex(string gasId)
    {
        if (_idMap.TryGetValue(gasId, out int index))
            return index;

        throw new KeyNotFoundException($"No gas registered with id '{gasId}'.");
    }

    /// <inheritdoc />
    public void ValidateGasRegistry()
    {
        List<string> duplicates = _gases
            .GroupBy(g => g.Name)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        if (duplicates.Count > 0)
            throw new InvalidOperationException($"Duplicate gas names found: {string.Join(", ", duplicates)}");
    }

    /// <summary>
    ///     Enumerates gases in gas-ID order.
    /// </summary>
    public IEnumerator<GasProperties> GetEnumerator()
    {
        return ((IEnumerable<GasProperties>)_gases).GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}