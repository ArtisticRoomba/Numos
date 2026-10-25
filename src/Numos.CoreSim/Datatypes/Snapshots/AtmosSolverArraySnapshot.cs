using Numos.CoreSim.Solvers;
using Numos.CoreSim.State;
using Numos.CoreSim.State.Advanced;

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

    internal AtmosSolverArraySnapshot(SolverArrayStorage storage)
        : this(storage.Clone(), true)
    {
    }

    // Takes ownership of storage nobody else references, such as an array just decoded from a file.
    private AtmosSolverArraySnapshot(SolverArrayStorage storage, bool _)
    {
        _storage = storage;
    }

    /// <summary>
    ///     Gets the registered key of the rollback <c>ChunkField&lt;T&gt;</c> this array was captured from.
    /// </summary>
    public string Key => _storage.Field.Key;

    /// <summary>
    ///     Gets the field's exact element type, required when reading this array.
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

    /// <summary>
    ///     Gets whether the element type can be written to a portable replay file.
    /// </summary>
    internal bool IsPortable => _storage.Field.IsPortable;

    internal int ElementSize => _storage.Field.ElementSize;

    internal ChunkFieldDescriptor Field => _storage.Field;

    internal static AtmosSolverArraySnapshot Adopt(SolverArrayStorage storage)
    {
        return new AtmosSolverArraySnapshot(storage, true);
    }

    internal void WriteValues<TSink>(ref TSink sink) where TSink : struct, IStateSink
    {
        _storage.WriteValues(ref sink);
    }

    internal int CompareValues(AtmosSolverArraySnapshot other)
    {
        return _storage.Bytes.SequenceCompareTo(other._storage.Bytes);
    }

    internal SolverArrayStorage Materialize()
    {
        return _storage.Clone();
    }

}