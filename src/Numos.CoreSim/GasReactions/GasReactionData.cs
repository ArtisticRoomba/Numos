namespace Numos.CoreSim.GasReactions;

/// <summary>
///     Reaction-ID-indexed coefficients and rate factors attached to one registered gas.
/// </summary>
internal sealed class GasReactionData(int gasId, string gasName, int reactionCount)
{
    internal int GasId { get; } = gasId;
    internal string GasName { get; } = gasName;
    internal Mole[] Changes { get; } = new Mole[reactionCount];
    // An explicit zero coefficient still participates in the existing speed-limiting rule.
    internal bool[] Participates { get; } = new bool[reactionCount];
    internal LinearGasReaction.LinearSpeedFactor[][] LinearFactors { get; } = new LinearGasReaction.LinearSpeedFactor[reactionCount][];
    internal float?[] StandardExponents { get; } = new float?[reactionCount];

    internal void SetChanges(
        int reactionId, IReadOnlyDictionary<GasProperties, Mole> input, IReadOnlyDictionary<GasProperties, Mole> output)
    {
        Mole consumed = 0f;
        Mole produced = 0f;
        foreach (KeyValuePair<GasProperties, Mole> entry in input)
        {
            if (entry.Key.Name != GasName)
                continue;

            consumed = entry.Value;
            Participates[reactionId] = true;
        }

        foreach (KeyValuePair<GasProperties, Mole> entry in output)
        {
            if (entry.Key.Name != GasName)
                continue;

            produced = entry.Value;
            Participates[reactionId] = true;
        }

        Changes[reactionId] = produced - consumed;
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