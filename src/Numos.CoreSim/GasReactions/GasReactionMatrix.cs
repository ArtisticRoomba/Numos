using CommunityToolkit.HighPerformance;

namespace Numos.CoreSim.GasReactions;

/// <summary>
///     The dense stoichiometric matrix S behind a tick's reaction set: mixture change per unit time is
///     <c>S &#183; reactionSpeeds</c>, one column per reaction and one row per registered gas. Rebuilt once
///     per solve from the per-gas <see cref="GasReactionData.Changes" /> columns so the per-voxel hot loop
///     can apply a whole reaction (one matrix row, since storage is reaction-major) as a single
///     order-preserving vector add instead of walking every gas's reaction array by hand.
/// </summary>
internal sealed class GasReactionMatrix
{
    private readonly Mole[] _changesByReaction;

    internal GasReactionMatrix(GasReactionData[] gasReactionData, int reactionCount)
    {
        GasCount = gasReactionData.Length;
        ReactionCount = reactionCount;
        _changesByReaction = new Mole[reactionCount * GasCount];

        // Span2D makes the [reaction, gas] shape explicit while building; the hot path below reads
        // contiguous rows directly out of the backing array instead, since it's already reaction-major.
        Span2D<Mole> changes = _changesByReaction.AsSpan().AsSpan2D(reactionCount, GasCount);
        for (int gasId = 0; gasId < GasCount; gasId++)
        {
            Mole[] gasChanges = gasReactionData[gasId].Changes;
            for (int reactionId = 0; reactionId < reactionCount; reactionId++)
                changes[reactionId, gasId] = gasChanges[reactionId];
        }
    }

    internal int GasCount { get; }
    internal int ReactionCount { get; }

    /// <summary>
    ///     Every gas's mole change for one reaction, in gas-ID order.
    /// </summary>
    internal Span<Mole> GetReactionRow(int reactionId) => _changesByReaction.AsSpan(reactionId * GasCount, GasCount);
}
