using Numos.API;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.Maths;

namespace Numos.Workbench;

/// <summary>
///     Keeps the chunk snapshots of one read, so a chunk is copied once however many boxes touch it.
/// </summary>
/// <remarks>
///     <para>
///         A cached snapshot is a detached copy from the moment the cache takes it. Call <see cref="Clear" /> before
///         each read, because the cache cannot tell when the simulation changed.
///     </para>
///     <para>
///         A request for fields the cached snapshot lacks replaces it with a snapshot that holds both field sets.
///         Pass every field the read needs as <c>baseFields</c> to avoid that second copy.
///     </para>
/// </remarks>
internal sealed class ChunkSnapshotCache
{
    private readonly AtmosSimulation _simulation;
    private readonly AtmosChunkSnapshotFields _baseFields;
    private readonly Dictionary<Int3, AtmosChunkSnapshot> _snapshots = [];

    /// <summary>
    ///     Creates an empty cache for one simulation.
    /// </summary>
    /// <param name="simulation">The simulation to read.</param>
    /// <param name="baseFields">Fields that every snapshot of the cache holds.</param>
    public ChunkSnapshotCache(AtmosSimulation simulation, AtmosChunkSnapshotFields baseFields)
    {
        _simulation = simulation;
        _baseFields = baseFields;
    }

    /// <summary>
    ///     Drops every cached snapshot.
    /// </summary>
    public void Clear()
    {
        _snapshots.Clear();
    }

    /// <summary>
    ///     Returns the snapshot of a chunk, copying the chunk only when the cache has none with the required fields.
    /// </summary>
    /// <param name="position">The chunk position.</param>
    /// <param name="required">The fields the caller reads.</param>
    /// <param name="snapshot">The snapshot, which can hold more fields than <paramref name="required" />.</param>
    /// <returns><see langword="false" /> when the simulation returned no snapshot.</returns>
    /// <exception cref="KeyNotFoundException">No chunk is registered at <paramref name="position" />.</exception>
    public bool TryGet(Int3 position, AtmosChunkSnapshotFields required, out AtmosChunkSnapshot snapshot)
    {
        var fields = _baseFields | required;
        if (_snapshots.TryGetValue(position, out snapshot))
        {
            if (snapshot.HasFields(required))
                return true;

            fields |= snapshot.Fields;
        }

        if (!_simulation.TryGetChunkSnapshot(new AtmosChunkHandle(position), default, fields, out snapshot))
            return false;

        _snapshots[position] = snapshot;
        return true;
    }
}
