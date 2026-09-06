using System.Collections.Frozen;
using Numos.CoreSim.Replay;
using Numos.Units;

namespace Numos.CoreSim.GasReactions;

/// <summary>
///     A gas reaction using standard rate equation and Arrhenius equation to calculate its reaction speed.
///     These are much slower to evaluate than <see cref="LinearGasReaction" /> because of the <see cref="Math.Pow" />
///     involved.
/// </summary>
public readonly partial record struct StandardGasReaction
{
    public StandardGasReaction(
        IDictionary<GasProperties, float> input, IDictionary<GasProperties, float> output,
        [Quantity("energy")] Joule energyBalance,
        float arrheniusFactor, float activationEnergy,
        IDictionary<GasProperties, float> speedFactors)
    {
        Input = input.ToFrozenDictionary();
        Output = output.ToFrozenDictionary();
        EnergyBalance = energyBalance;
        ArrheniusFactor = arrheniusFactor;
        ActivationEnergy = activationEnergy;
        SpeedFactors = speedFactors.ToFrozenDictionary();
    }

    /// <summary>
    ///     Input reactants.
    ///     Mol per Reaction
    /// </summary>
    private FrozenDictionary<GasProperties, Mole> Input { get; }

    /// <summary>
    ///     Output reactants
    ///     Mol per Reaction
    /// </summary>
    private FrozenDictionary<GasProperties, Mole> Output { get; }

    /// <summary>
    ///     If this reaction consumes or produces thermal energy.
    ///     In Joules per Reaction
    /// </summary>
    internal Joule EnergyBalance { get; }

    /// <summary>
    ///     Arrhenius factor
    /// </summary>
    private float ArrheniusFactor { get; }

    /// <summary>
    ///     Molar activation energy kJ/mol
    /// </summary>
    private JoulePerMole ActivationEnergy { get; }

    /// <summary>
    ///     The exponents of the rate equation:
    ///     k * Gas_1^{a} * Gas_2 ^ {b}...
    /// </summary>
    private FrozenDictionary<GasProperties, float> SpeedFactors { get; }

    internal void AppendHash(ref AtmosStateHasher hash)
    {
        hash.Add(EnergyBalance);
        hash.Add(ArrheniusFactor);
        hash.Add(ActivationEnergy);
        hash.Add(Input.Count);
        foreach (KeyValuePair<GasProperties, Mole> entry in Input.OrderBy(static entry => entry.Key.Name, StringComparer.Ordinal))
        {
            hash.Add(entry.Key);
            hash.Add(entry.Value);
        }

        hash.Add(Output.Count);
        foreach (KeyValuePair<GasProperties, Mole> entry in Output.OrderBy(static entry => entry.Key.Name, StringComparer.Ordinal))
        {
            hash.Add(entry.Key);
            hash.Add(entry.Value);
        }

        hash.Add(SpeedFactors.Count);
        foreach (KeyValuePair<GasProperties, float> entry in SpeedFactors.OrderBy(
                     static entry => entry.Key.Name,
                     StringComparer.Ordinal))
        {
            hash.Add(entry.Key);
            hash.Add(entry.Value);
        }
    }

    internal bool SemanticallyEquals(StandardGasReaction other)
    {
        return EnergyBalance.Equals(other.EnergyBalance) &&
               ArrheniusFactor.Equals(other.ArrheniusFactor) &&
               ActivationEnergy.Equals(other.ActivationEnergy) &&
               DictionaryEquals(Input, other.Input) &&
               DictionaryEquals(Output, other.Output) &&
               DictionaryEquals(SpeedFactors, other.SpeedFactors);
    }

    private static bool DictionaryEquals(
        IReadOnlyDictionary<GasProperties, float> first,
        IReadOnlyDictionary<GasProperties, float> second)
    {
        if (first.Count != second.Count)
            return false;

        foreach ((var gas, float amount) in first)
        {
            if (!second.TryGetValue(gas, out float otherAmount) || !amount.Equals(otherAmount))
                return false;
        }

        return true;
    }

    /// <summary>
    ///     Calculates the reactions rate constant based on temperature using original Arrhenius equation.
    /// </summary>
    /// <param name="temperatureKelvin"></param>
    /// <returns>Reactions per seconds.</returns>
    /// <remarks>there are more sophisticated models for k but good luck having anyone setup all the parameters necessary.</remarks>
    internal PerSecond GetRateConstant(Kelvin temperatureKelvin)
    {
        return ArrheniusFactor *
               MathF.Exp(-ActivationEnergy * 0.001f / (temperatureKelvin * AtmosPhysicalConstants.MolarGasConstant));
    }

    /// <summary>
    ///     Calculate the reaction speed given gas molarities and temperature of a gas mixture.
    /// </summary>
    /// <param name="gasMolarities">molar concentration in moles per litre</param>
    /// <param name="temperatureKelvin"></param>
    /// <returns>reactions per second.</returns>
    [return: Quantity("frequency")]
    public PerSecond GetReactionSpeed(
        IDictionary<GasProperties, float> gasMolarities,
        [Quantity("temperature")] Kelvin temperatureKelvin)
    {
        PerSecond result = GetRateConstant(temperatureKelvin);
        if (result <= 0)
            return 0;

        foreach (KeyValuePair<GasProperties, float> factor in SpeedFactors.OrderBy(
                     static factor => factor.Key.Name,
                     StringComparer.Ordinal))
        {
            if (!gasMolarities.TryGetValue(factor.Key, out float molar)) molar = 0;

            result *= MathF.Pow(molar, factor.Value);
            if (result <= 0)
                return 0;
        }

        return result;
    }
}