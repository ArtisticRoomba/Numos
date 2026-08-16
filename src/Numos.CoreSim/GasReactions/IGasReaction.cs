using System.Collections.Frozen;

namespace Numos.CoreSim.GasReactions;

public interface IGasReaction
{
    public FrozenDictionary<int, float> ChangeEquation { get; }
    float EnergyBalance { get; }

    public float GetReactionSpeed(float[] molarityVector, float temperature);
}