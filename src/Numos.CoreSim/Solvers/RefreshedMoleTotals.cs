using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Datatypes.Snapshots;

namespace Numos.CoreSim.Solvers;

/// <summary>
///     Hands the per-voxel mole totals that <c>derived-refresh</c> sums to the <c>advection</c> run that follows it,
///     so the legacy layout sums each voxel's gas channels once per gas run.
/// </summary>
/// <remarks>
///     <para>
///         The totals are transient scratch. They are not checkpointed. The simulation's solver data storage drops
///         them on restore and on a config change, and the next refresh rebuilds them from the chunk fields.
///     </para>
///     <para>
///         <see cref="TryGet" /> accepts a chunk's totals only when nothing can have changed its moles since the
///         refresh. It needs three things:
///     </para>
///     <list type="number">
///         <item>The refresh ran on the current tick. Host writes happen between ticks, so this excludes them,
///         including raw <c>Numos.API.Dangerous</c> writes that skip <see cref="AtmosChunk.MarkChanged" />.</item>
///         <item>The chunk has the same generation, so it is the same incarnation the refresh summed.</item>
///         <item>The revision is exactly one past the one recorded after the refresh.</item>
///     </list>
///     <para>
///         The one step is the bump that <c>advection</c> itself receives. <c>advection</c> does not mark its own
///         writes, so the world pipeline and <c>AtmosKernel.TickSimulation</c> both advance every awake chunk's
///         revision once before it runs. Any stage that runs in between advances it again. A custom stage or a
///         built-in stage without its own marking gets the same blanket bump. <c>euler-flow</c> and
///         <c>vacuum-cleanup</c> mark each chunk they write, and a sleeping chunk that a stage writes is woken,
///         which also advances the revision. Any other count fails the check, and <c>advection</c> sums the moles
///         itself.
///     </para>
/// </remarks>
internal sealed class RefreshedMoleTotals
{
    private static readonly SolverDataSlot<RefreshedMoleTotals> Slot =
        new("derived-refresh/mole-totals", static _ => new RefreshedMoleTotals());

    private Entry[] _entries = [];
    private int _count;
    private long _tick = -1;

    /// <summary>
    ///     Gets the storage of the simulation that <paramref name="context" /> belongs to.
    /// </summary>
    /// <param name="context">The stage context.</param>
    /// <returns>The storage, created on first use.</returns>
    /// <remarks>Call this on the tick thread.</remarks>
    internal static RefreshedMoleTotals Get(AtmosSolverExecutionContext context)
    {
        return context.GetSolverData(Slot);
    }

    /// <summary>
    ///     Discards the totals of the previous refresh and starts recording for <paramref name="tick" />.
    /// </summary>
    /// <param name="tick">The tick the refresh runs on.</param>
    internal void Begin(long tick)
    {
        _tick = tick;
        _count = 0;
    }

    /// <summary>
    ///     Reserves the totals buffer of the next refreshed chunk.
    /// </summary>
    /// <param name="chunk">The chunk the refresh is about to sum.</param>
    /// <returns>A voxel-indexed buffer at least <see cref="AtmosChunk.VoxelCount" /> long.</returns>
    /// <remarks>
    ///     Buffers are reused across ticks, so a steady state allocates nothing. Storage grows to the largest number
    ///     of chunks one refresh has covered and does not shrink.
    /// </remarks>
    internal Mole[] Add(AtmosChunk chunk)
    {
        if (_count == _entries.Length)
            Array.Resize(ref _entries, Math.Max(4, _count * 2));

        ref Entry entry = ref _entries[_count++];
        if (entry.Totals == null || entry.Totals.Length < chunk.VoxelCount)
            entry.Totals = new Mole[chunk.VoxelCount];

        entry.Generation = chunk.Version.Generation;
        entry.Revision = long.MinValue;
        return entry.Totals;
    }

    /// <summary>
    ///     Records the revision of every chunk added since <see cref="Begin" />.
    /// </summary>
    /// <param name="chunks">The chunks, in the order they were added.</param>
    /// <remarks>
    ///     Call this after the refresh has finished writing, because the refresh can empty voxels.
    /// </remarks>
    internal void Seal(ReadOnlySpan<AtmosChunk> chunks)
    {
        for (int index = 0; index < _count; index++)
            _entries[index].Revision = chunks[index].Version.Revision;
    }

    /// <summary>
    ///     Gets the refreshed totals of a chunk if they still match its moles.
    /// </summary>
    /// <param name="chunk">The chunk <c>advection</c> is about to solve.</param>
    /// <param name="tick">The current tick.</param>
    /// <param name="cursor">
    ///     The search start. Start at zero for each stage run and pass the same variable for each chunk. The
    ///     search continues from the last match, because both stages visit chunks in the simulation's order.
    /// </param>
    /// <returns>
    ///     The voxel-indexed totals, or <see langword="null" /> when the check in the class remarks fails. The
    ///     caller only reads the buffer, and only until the next refresh.
    /// </returns>
    internal Mole[]? TryGet(AtmosChunk chunk, long tick, ref int cursor)
    {
        if (tick != _tick)
            return null;

        AtmosChunkVersion version = chunk.Version;
        for (int index = cursor; index < _count; index++)
        {
            ref Entry entry = ref _entries[index];
            if (entry.Generation != version.Generation)
                continue;

            cursor = index + 1;
            return entry.Revision != long.MinValue && version.Revision == entry.Revision + 1 ? entry.Totals : null;
        }

        return null;
    }

    private struct Entry
    {
        public long Generation;
        public long Revision;
        public Mole[]? Totals;
    }
}
