namespace Numos.CoreSim.GasReactions;

/// <summary>
///     Everything the reaction solver derives from one configuration: the reaction set, per-gas reaction data,
///     the canonical rate-factor order, and the change matrix.
/// </summary>
/// <remarks>
///     Nothing here is written after <see cref="Create" /> returns, so one instance is shared by every worker and
///     reused across ticks until the configuration changes.
/// </remarks>
internal sealed class ReactionTables
{
    private ReactionTables(
        GasReactionConfig reactions, GasReactionData[] gasData, GasReactionData[] rateOrder, GasReactionMatrix matrix)
    {
        Reactions = reactions;
        GasData = gasData;
        RateOrder = rateOrder;
        Matrix = matrix;
    }

    internal GasReactionConfig Reactions { get; }

    /// <summary>
    ///     Per-gas reaction data, indexed by gas ID.
    /// </summary>
    internal GasReactionData[] GasData { get; }

    /// <summary>
    ///     <see cref="GasData" /> sorted by ordinal gas name, so rate factors apply in the same order regardless of
    ///     registry indices.
    /// </summary>
    internal GasReactionData[] RateOrder { get; }

    internal GasReactionMatrix Matrix { get; }

    internal static ReactionTables Create(IAtmosConfig config)
    {
        var reactions = GasReactionConfig.Get(config);
        var gasData = new GasReactionData[config.GasPropertyCount];
        for (int gasId = 0; gasId < gasData.Length; gasId++)
        {
            config.TryGetGasProperties(gasId, out var gas);
            gasData[gasId] = reactions.CreateGasData(gasId, gas);
        }

        var rateOrder = gasData.OrderBy(static gas => gas.GasName, StringComparer.Ordinal).ToArray();
        return new ReactionTables(reactions, gasData, rateOrder, new GasReactionMatrix(gasData, reactions.Count));
    }
}
