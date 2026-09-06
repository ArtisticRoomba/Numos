namespace Numos.CoreSim.GasReactions;

public readonly partial record struct LinearGasReaction
{
    internal void ValidateGasRegistry(IGasRegistry registry)
    {
        GasReactionData.ValidateNames(Input.Keys, registry);
        GasReactionData.ValidateNames(Output.Keys, registry);
        GasReactionData.ValidateNames(SpeedFactors.Select(static factor => factor.Gas), registry, false);
    }

    internal void AppendGasData(GasReactionData data, int reactionId)
    {
        data.SetChanges(reactionId, Input, Output);
        // Gas-name order is handled by the solver; multiple factors for this gas retain their canonical order.
        data.LinearFactors[reactionId] = SpeedFactors.Where(factor => factor.Gas.Name == data.GasName)
            .OrderBy(static factor => factor.OrderKey).ToArray();
    }
}