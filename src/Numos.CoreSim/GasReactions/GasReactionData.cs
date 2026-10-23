namespace Numos.CoreSim.GasReactions;

/// <summary>
///     Per-registered-gas reaction coefficients and rate factors.
///     Dense, reaction-ID-indexed arrays answer "what does every reaction do to this gas".
///     A sparse list holds just the reactions that consume it.
/// </summary>
internal sealed class GasReactionData(int gasId, string gasName, int reactionCount)
{
    internal int GasId { get; } = gasId;
    internal string GasName { get; } = gasName;
    internal Mole[] Changes { get; } = new Mole[reactionCount];

    // Sparse: only the reactions that take this gas as an input, in reaction-ID order (the order SetChanges is called in).
    // The material limiter needs to know how much this gas loses and to which reactions.
    // Walking only those reactions makes its per-gas scan O(reactions that consume the gas) instead of O(reactionCount).
    // Coefficient is gross consumption (<= 0), independent of any production of the same gas by the same reaction.
    // The limiter must not net input against output, or it would hide a real overdraw.
    internal List<(int ReactionId, float Consumed)> ConsumingReactions { get; } = [];

    internal LinearGasReaction.LinearSpeedFactor[][] LinearFactors { get; } = new LinearGasReaction.LinearSpeedFactor[reactionCount][];
    internal float?[] StandardExponents { get; } = new float?[reactionCount];

    internal void SetChanges(
        int reactionId, IReadOnlyDictionary<GasProperties, Mole> input, IReadOnlyDictionary<GasProperties, Mole> output)
    {
        Mole consumed = 0f;
        Mole produced = 0f;
        bool isInput = false;
        foreach (KeyValuePair<GasProperties, Mole> entry in input)
        {
            if (entry.Key.Name != GasName)
                continue;

            consumed = entry.Value;
            isInput = true;
        }

        foreach (KeyValuePair<GasProperties, Mole> entry in output)
        {
            if (entry.Key.Name != GasName)
                continue;

            produced = entry.Value;
        }

        Changes[reactionId] = produced - consumed;
        if (isInput)
            ConsumingReactions.Add((reactionId, -consumed));
    }

    internal static void ValidateNames(IEnumerable<GasProperties> gases, IGasRegistry registry, bool unique = true)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var gas in gases)
        {
            registry.GasIdToIndex(gas.Name);
            if (!names.Add(gas.Name) && unique)
                throw new ArgumentException($"Reaction contains duplicate definitions of gas '{gas.Name}'.");
        }
    }

    /// <summary>
    ///     Multiplies <paramref name="speed" /> by this gas's rate factors for <paramref name="reactionId" />.
    /// </summary>
    /// <remarks>
    ///     Accumulates in double, so a trace gas with a negative exponent can push the product past float range
    ///     without being mistaken for a broken rate. The result may still overflow to positive infinity.
    ///     The solver then saturates the rate.
    /// </remarks>
    internal ConcentrationPerSecond64 ApplyRateFactors(
        int reactionId, MolePerCubicMetre64 concentration, ConcentrationPerSecond64 speed)
    {
        if (LinearFactors[reactionId] is { } factors)
        {
            foreach (var factor in factors)
            {
                speed = LinearGasReaction.ApplyFactor(speed, factor.GetFactor((MolePerCubicMetre)concentration));
                if (speed == 0)
                    return 0;
            }
        }

        if (StandardExponents[reactionId] is { } exponent)
            speed = StandardGasReaction.ApplyExponent(speed, concentration, exponent);

        return speed;
    }
}