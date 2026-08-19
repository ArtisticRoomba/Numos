using System.Collections.Frozen;

namespace Numos.CoreSim.GasReactions;

/// <summary>
/// Description of a reaction within a solver.
/// </summary>
internal interface IGasReaction
{
    /// <summary>
    /// GasID to changes in Mol/L. Positive add, negative subtracts
    /// </summary>
    public FrozenDictionary<int, float> ChangeEquation { get; }
    /// <summary>
    /// The amount of J this reaction produces or consumes.
    /// </summary>
    float EnergyBalance { get; }

    /// <summary>
    /// Calculates the reaction speed (Mol/L/s) given a molarityVector (aligned to gas ids) and a temperature value
    /// </summary>
    /// <param name="molarityVector">Index = GasId, length = number of gases</param>
    /// <param name="temperature"></param>
    /// <returns></returns>
    public float GetReactionSpeed(float[] molarityVector, float temperature);
}