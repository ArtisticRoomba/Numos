namespace Numos.CoreSim.GasReactions;

public readonly partial record struct StandardGasReaction
{
    internal void ValidateGasRegistry(IGasRegistry registry)
    {
        GasReactionData.ValidateNames(Input.Keys, registry);
        GasReactionData.ValidateNames(Output.Keys, registry);
        GasReactionData.ValidateNames(SpeedFactors.Keys, registry);
    }

    internal void AppendGasData(GasReactionData data, int reactionId)
    {
        data.SetChanges(reactionId, Input, Output);
        foreach (KeyValuePair<GasProperties, float> factor in SpeedFactors)
        {
            if (factor.Key.Name == data.GasName)
                data.StandardExponents[reactionId] = factor.Value;
        }
    }
}