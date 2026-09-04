using System.Collections.Frozen;

namespace Numos.CoreSim.GasReactions;

/// <summary>
/// Description of a reaction within the solver.
/// </summary>
public interface IGasReaction
{
    /// <summary>
    /// GasID to changes in Mol/L. Positive add, negative subtracts
    /// </summary>
    public FrozenDictionary<int, Mole> ChangeEquation { get; }
    /// <summary>
    /// The amount of J this reaction produces or consumes.
    /// </summary>
    Joule EnergyBalance { get; }

    /// <summary>
    /// Calculates the reaction speed (Mol/L/s) given a molarityVector (aligned to gas ids) and a temperature value
    /// </summary>
    /// <param name="molarityVector">Index = GasId</param>
    /// <param name="temperature"></param>
    /// <returns></returns>
    public PerSecond GetReactionSpeed(Mole[] molarityVector, Kelvin temperature);
}