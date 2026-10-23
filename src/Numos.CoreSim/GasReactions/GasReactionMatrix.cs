using CommunityToolkit.HighPerformance;

namespace Numos.CoreSim.GasReactions;

/// <summary>
///     The dense stoichiometric matrix S behind a tick's reaction set.
///     Mixture change per unit time is <c>S &#183; reactionSpeeds</c>.
///     Conceptually S has one row per registered gas and one column per reaction.
/// </summary>
/// <remarks>
///     Storage is the transpose of that. It is reaction-major, so <see cref="GetReactionRow" /> returns one
///     reaction's whole column of S as a contiguous span.
///     The per-voxel hot loop applies one reaction at a time to every gas.
///     That is a single lane-independent vector add per reaction, and it visits every (gas, reaction) term in the
///     same order a per-gas accumulation would.
///     Numos builds the matrix once per configuration from the per-gas <see cref="GasReactionData.Changes" /> columns.
///     It is read-only afterwards, so workers share it.
/// </remarks>
internal sealed class GasReactionMatrix
{
    private readonly Mole[] _changesByReaction;

    internal GasReactionMatrix(GasReactionData[] gasReactionData, int reactionCount)
    {
        GasCount = gasReactionData.Length;
        ReactionCount = reactionCount;
        _changesByReaction = new Mole[reactionCount * GasCount];

        // Span2D makes the [reaction, gas] shape explicit while building.
        // The hot path below reads contiguous rows straight from the reaction-major backing array.
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
    internal ReadOnlySpan<Mole> GetReactionRow(int reactionId)
    {
        return _changesByReaction.AsSpan(reactionId * GasCount, GasCount);
    }
}