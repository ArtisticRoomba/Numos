using Numos.CoreSim.Replay;
using Numos.CoreSim.Solvers;

namespace Numos.CoreSim.Datatypes.Snapshots;

/// <summary>
///     Holds a detached copy of one solver array opted into snapshot capture and rollback.
/// </summary>
/// <remarks>
///     Numos restores this data automatically with its owning chunk. Reading values returns another copy, so a caller
///     cannot mutate the saved state. Flat-array views use the dimensions of the owning chunk.
/// </remarks>
public sealed class AtmosSolverArraySnapshot
{
    private readonly SolverArrayStorage _storage;

    internal AtmosSolverArraySnapshot(string key, SolverArrayStorage storage)
    {
        Key = key;
        _storage = storage.Clone();
    }

    /// <summary>
    ///     Gets the ordinal field name used to reacquire this array after rollback.
    /// </summary>
    public string Key { get; }

    /// <summary>
    ///     Gets the exact element type required when reacquiring or reading this array.
    /// </summary>
    public Type ElementType => _storage.ElementType;

    /// <summary>
    ///     Gets the number of stored elements, which may differ from the chunk's voxel count for regular arrays.
    /// </summary>
    public int Length => _storage.Length;

    /// <summary>
    ///     Gets the bytes occupied by the copied values, excluding object headers and the field name.
    /// </summary>
    public long PayloadBytes => _storage.PayloadBytes;

    /// <summary>
    ///     Copies the saved values into a new regular array.
    /// </summary>
    /// <typeparam name="T">The exact captured element type.</typeparam>
    /// <returns>A detached array that can be changed without affecting this snapshot or the simulation.</returns>
    /// <exception cref="InvalidOperationException"><typeparamref name="T" /> is not the captured element type.</exception>
    /// <example>
    ///     <code>
    ///     int[] values = snapshot.SolverArrays.Single(array => array.Key == "fire/burn-count").CopyValues&lt;int&gt;();
    ///     </code>
    /// </example>
    public T[] CopyValues<T>()
    {
        if (_storage is not SolverArrayStorage<T> typed)
            throw new InvalidOperationException("The requested element type does not match the captured solver array.");

        return (T[])typed.Values.Clone();
    }

    internal SolverArrayStorage Materialize()
    {
        return _storage.Clone();
    }

    internal void AppendHash(ref AtmosStateHasher hash)
    {
        hash.Add(Key);
        _storage.AppendHash(ref hash);
    }
}