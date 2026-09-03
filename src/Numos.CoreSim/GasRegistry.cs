using System.Collections;

namespace Numos.CoreSim;

/// <summary>
///     Read-only view over a set of registered gases, supporting indexing, enumeration,
///     and name-to-index lookups.
/// </summary>
public interface IGasRegistry : IEnumerable<GasProperties>
{
    int Count { get; }

    GasProperties this[int index] { get; }

    /// <summary>
    ///     Resolves a gas name to its index.
    /// </summary>
    /// <exception cref="KeyNotFoundException">Thrown if no gas with the given name is registered.</exception>
    int GasIdToIndex(string gasId);
    void ValidateGasRegistry();

}

/// <summary>
///     Owns the list of gases registered to the sim, enforcing unique names and providing
///     name-to-index lookups via <see cref="GasIdToIndex"/>.
/// </summary>
public sealed class GasRegistry : IGasRegistry
{
    private readonly List<GasProperties> _gases = [];
    private readonly Dictionary<string, int> _idMap = [];

    public int Count => _gases.Count;

    public GasProperties this[int index] => _gases[index];

    /// <summary>
    ///     Registers a new gas. Throws if a gas with the same name is already registered.
    /// </summary>
    /// <exception cref="InvalidOperationException">A gas with this name is already registered.</exception>
    public void Add(GasProperties gas)
    {
        // Multiple gasses with null can be added
        // Should be avoid outside of tests
        if (gas.Name != null)
        {
            if (_idMap.ContainsKey(gas.Name))
                throw new InvalidOperationException($"A gas named '{gas.Name}' is already registered.");

            _idMap[gas.Name] = _gases.Count;
        }

        _gases.Add(gas);
    }

    /// <summary>
    ///     Removes the gas at the given index. Invalidates cached indices for every gas after it,
    ///     so the id map is rebuilt.
    /// </summary>
    public void RemoveAt(int index)
    {
        var removedName = _gases[index].Name;
        _gases.RemoveAt(index);
        _idMap.Remove(removedName);

        // Every gas after the removed one shifted down by one — rebuild rather than patch in place.
        for (var i = index; i < _gases.Count; i++)
            _idMap[_gases[i].Name] = i;
    }

    /// <summary>
    ///     Replaces the gas at the given index with a new one, re-validating name uniqueness
    ///     and updating the id map accordingly.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     Thrown if <paramref name="gas"/>'s name is already used by a different gas in the registry.
    /// </exception>
    public void Replace(int index, GasProperties gas)
    {
        var oldName = _gases[index].Name;

        if (gas.Name != oldName && _idMap.ContainsKey(gas.Name))
            throw new InvalidOperationException($"A gas named '{gas.Name}' is already registered.");

        _gases[index] = gas;

        if (gas.Name != oldName)
        {
            _idMap.Remove(oldName);
            _idMap[gas.Name] = index;
        }
    }

    /// <summary>
    ///     Resolves a gas name to its index. O(1) via the cached id map.
    /// </summary>
    /// <exception cref="KeyNotFoundException">Thrown if no gas with the given name is registered.</exception>
    public int GasIdToIndex(string gasId)
    {
        if (_idMap.TryGetValue(gasId, out var index))
            return index;

        throw new KeyNotFoundException($"No gas registered with id '{gasId}'.");
    }

    public void ValidateGasRegistry()
    {
        var duplicates = _gases
            .GroupBy(g => g.Name)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        if (duplicates.Count > 0)
            throw new InvalidOperationException($"Duplicate gas names found: {string.Join(", ", duplicates)}");
    }

    public IEnumerator<GasProperties> GetEnumerator() => _gases.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
///     Immutable point-in-time copy of an <see cref="IGasRegistry"/>. Exposes indexing,
///     enumeration, and <see cref="GasIdToIndex"/>, but no way to add or remove gases.
/// </summary>
public sealed class GasRegistrySnapshot : IGasRegistry
{
    private readonly GasProperties[] _gases = [];
    private readonly Dictionary<string, int> _idMap = [];

    public GasRegistrySnapshot(IGasRegistry source)
    {
        _gases = source.ToArray();

        _idMap = new Dictionary<string, int>(_gases.Length);
        for (var i = 0; i < _gases.Length; i++)
        {
            if (_gases[i].Name != null)
                _idMap[_gases[i].Name] = i;
        }
    }

    public int Count => _gases.Length;

    public GasProperties this[int index] => _gases[index];

    public int GasIdToIndex(string gasId)
    {
        if (_idMap.TryGetValue(gasId, out var index))
            return index;

        throw new KeyNotFoundException($"No gas registered with id '{gasId}'.");
    }

    public void ValidateGasRegistry()
    {
        var duplicates = _gases
            .GroupBy(g => g.Name)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        if (duplicates.Count > 0)
            throw new InvalidOperationException($"Duplicate gas names found: {string.Join(", ", duplicates)}");
    }

    public IEnumerator<GasProperties> GetEnumerator() => ((IEnumerable<GasProperties>)_gases).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}