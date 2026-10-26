using System.Diagnostics;

namespace Numos.CoreSim.Solvers.Euler;

/// <summary>
///     Maps the gas channels of one chunk to the channels of its face neighbours and back, one direction at a time.
/// </summary>
/// <remarks>
///     <para>
///         Channels are appended in the order gases first reach a chunk. The same gas can therefore sit in different
///         channels of two chunks, and a lookup by gas id is a linear search. A boundary voxel would repeat that
///         search for every gas on every open face.
///     </para>
///     <para>
///         This type searches once per direction and answers every later voxel from a table. The tables are valid
///         while no chunk adds or removes a channel, which holds for the whole read-only phase that uses them.
///         The caller owns the scratch, sized by <see cref="GetScratchLength" />, so the lookup allocates nothing.
///     </para>
///     <para>
///         Every call for one direction must pass the same neighbour.
///     </para>
/// </remarks>
internal ref struct FaceGasChannels
{
    private readonly AtmosChunk _chunk;
    private readonly Span<int> _scratch;
    private readonly int _stride;
    private int _resolved;

    /// <summary>
    ///     Creates the maps for one chunk. Nothing is searched until a map is requested.
    /// </summary>
    /// <param name="chunk">The chunk whose channels are the "own" side of every map.</param>
    /// <param name="stride">The largest channel count among <paramref name="chunk" /> and its neighbours.</param>
    /// <param name="scratch">At least <see cref="GetScratchLength" /> ints that the maps may overwrite.</param>
    internal FaceGasChannels(AtmosChunk chunk, int stride, Span<int> scratch)
    {
        Debug.Assert(scratch.Length >= GetScratchLength(stride));
        _chunk = chunk;
        _stride = stride;
        _scratch = scratch;
        _resolved = 0;
    }

    /// <summary>
    ///     Returns the scratch length that covers both maps of all six directions.
    /// </summary>
    /// <param name="stride">The largest channel count among the chunk and its neighbours.</param>
    internal static int GetScratchLength(int stride)
    {
        return checked(stride * NeighborResolution.NeighborDirections.Length * 2);
    }

    /// <summary>
    ///     Returns, for each channel of the chunk, the channel of the neighbour that holds the same gas, or -1.
    /// </summary>
    /// <param name="direction">The direction slot of the neighbour.</param>
    /// <param name="neighbor">The chunk across that face.</param>
    internal ReadOnlySpan<int> OwnToNeighbor(int direction, AtmosChunk neighbor)
    {
        Resolve(direction, neighbor);
        return _scratch.Slice(direction * 2 * _stride, _chunk.ActiveGasCount);
    }

    /// <summary>
    ///     Returns, for each channel of the neighbour, the channel of the chunk that holds the same gas, or -1.
    /// </summary>
    /// <param name="direction">The direction slot of the neighbour.</param>
    /// <param name="neighbor">The chunk across that face.</param>
    internal ReadOnlySpan<int> NeighborToOwn(int direction, AtmosChunk neighbor)
    {
        Resolve(direction, neighbor);
        return _scratch.Slice((direction * 2 + 1) * _stride, neighbor.ActiveGasCount);
    }

    private void Resolve(int direction, AtmosChunk neighbor)
    {
        if ((_resolved & (1 << direction)) != 0)
            return;

        Debug.Assert(neighbor.ActiveGasCount <= _stride && _chunk.ActiveGasCount <= _stride);
        int ownBase = direction * 2 * _stride;
        int neighborBase = ownBase + _stride;
        for (int gas = 0; gas < _chunk.ActiveGasCount; gas++)
            _scratch[ownBase + gas] = neighbor.FindGasChannel(_chunk.ActiveGases[gas].GasId);

        for (int gas = 0; gas < neighbor.ActiveGasCount; gas++)
            _scratch[neighborBase + gas] = _chunk.FindGasChannel(neighbor.ActiveGases[gas].GasId);

        _resolved |= 1 << direction;
    }
}
