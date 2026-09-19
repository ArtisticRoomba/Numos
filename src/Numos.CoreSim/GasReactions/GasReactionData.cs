namespace Numos.CoreSim.GasReactions;

/// <summary>
///     Per-registered-gas reaction coefficients and rate factors: dense, reaction-ID-indexed arrays for
///     "what does every reaction do to this gas", plus a sparse list of just the reactions that consume it.
/// </summary>
internal sealed class GasReactionData(int gasId, string gasName, int reactionCount)
{
    internal int GasId { get; } = gasId;
    internal string GasName { get; } = gasName;
    internal Mole[] Changes { get; } = new Mole[reactionCount];

    // Sparse: only the reactions that actually take this gas as an input, in reaction-ID order (the
    // order SetChanges is called in). The material limiter needs "how much does this gas lose, and
    // to which reactions" -- walking only the reactions that touch this gas, instead of every
    // registered reaction, turns its per-gas scan from O(reactionCount) into O(reactions that
    // actually consume it). Coefficient is gross consumption (<= 0), independent of any offsetting
    // production of the same gas by the same reaction -- the limiter must not net input against
    // output, or it would hide a real overdraw.
    internal List<(int ReactionId, Mole Consumed)> ConsumingReactions { get; } = [];

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

    internal PerSecond ApplyRateFactors(int reactionId, Mole moles, PerSecond speed)
    {
        if (LinearFactors[reactionId] is { } factors)
        {
            foreach (var factor in factors)
            {
                float value = factor.GetFactor(moles);
                if (!float.IsNormal(value) || value <= 0f)
                    return 0f;

                speed *= value;
            }
        }

        if (StandardExponents[reactionId] is float exponent)
        {
            float value = MathF.Pow(moles, exponent);
            if (!float.IsNormal(value) || value == 0f)
                return 0f;

            speed *= value;
        }

        return speed;
    }
}