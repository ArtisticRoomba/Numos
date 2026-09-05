using System.Collections.Frozen;
using Numos.Units;

namespace Numos.CoreSim.GasReactions;

/// <summary>
///     Description of a reaction within the solver.
/// </summary>
public interface IGasReaction
{
    /// <summary>
    ///     GasID to changes in Mol/L. Positive add, negative subtracts
    /// </summary>
    [ElementQuantity("amount")]
    FrozenDictionary<int, Mole> ChangeEquation { get; }
    /// <summary>
    ///     The amount of J this reaction produces or consumes.
    /// </summary>
    [Quantity("energy")]
    Joule EnergyBalance { get; }

    /// <summary>
    ///     Calculates the reaction speed (Mol/L/s) given a molarityVector (aligned to gas ids) and a temperature value
    /// </summary>
    /// <param name="molarityVector">Index = GasId</param>
    /// <param name="temperature"></param>
    /// <returns></returns>
    [return: Quantity("frequency")]
    PerSecond GetReactionSpeed(
        [ElementQuantity("amount")] Mole[] molarityVector,
        [Quantity("temperature")] Kelvin temperature);
}