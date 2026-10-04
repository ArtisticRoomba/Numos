using System.Collections.Frozen;
using Numos.CoreSim.Replay;
using Numos.Units;

namespace Numos.CoreSim.GasReactions;

/// <summary>
///     A gas reaction whose speed follows a standard rate equation with an Arrhenius rate constant.
/// </summary>
/// <remarks>
///     Much slower to evaluate than <see cref="LinearGasReaction" /> because of the exponentials and powers involved.
/// </remarks>
public readonly partial record struct StandardGasReaction
{
    /// <summary>
    ///     Creates a reaction with speed <c>k(T) · Π [gas]^exponent</c>, where <c>k(T) = A · exp(-Ea / (R·T))</c>.
    /// </summary>
    /// <param name="input">Moles of each reactant consumed per reaction.</param>
    /// <param name="output">Moles of each product produced per reaction.</param>
    /// <param name="energyBalance">Joules released (positive) or absorbed (negative) per reaction.</param>
    /// <param name="arrheniusFactor">Pre-exponential factor <c>A</c>, in reactions per second.</param>
    /// <param name="activationEnergy">
    ///     Molar activation energy <c>Ea</c>, in J/mol. Convert values authored in kJ/mol first.
    /// </param>
    /// <param name="speedFactors">Rate-equation exponent for each gas that affects the speed.</param>
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
    ///     Moles of each reactant consumed per reaction.
    /// </summary>
    private FrozenDictionary<GasProperties, Mole> Input { get; }

    /// <summary>
    ///     Moles of each product produced per reaction.
    /// </summary>
    private FrozenDictionary<GasProperties, Mole> Output { get; }

    /// <summary>
    ///     Joules released (positive) or absorbed (negative) per reaction.
    /// </summary>
    internal Joule EnergyBalance { get; }

    /// <summary>
    ///     Arrhenius pre-exponential factor, in reactions per second.
    /// </summary>
    private float ArrheniusFactor { get; }

    /// <summary>
    ///     Molar activation energy, in J/mol. Used directly against <see cref="AtmosPhysicalConstants.MolarGasConstant" />
    ///     (J/(mol*K)) in the Arrhenius exponent, so values authored in kJ/mol must be converted at the call site,
    ///     e.g. via <see cref="Numos.Units.Generated.UnitConversions.FromKilojoulePerMole" />.
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
    ///     Calculates the rate constant at a temperature using the original Arrhenius equation.
    /// </summary>
    /// <param name="temperatureKelvin">Mixture temperature, in kelvins (K).</param>
    /// <returns>Reactions per second.</returns>
    /// <remarks>there are more sophisticated models for k but good luck having anyone setup all the parameters necessary.</remarks>
    internal PerSecond GetRateConstant(Kelvin temperatureKelvin)
    {
        return ArrheniusFactor *
               MathF.Exp(-ActivationEnergy / (temperatureKelvin * AtmosPhysicalConstants.MolarGasConstant));
    }

    /// <summary>
    ///     Computes the reaction speed for a mixture.
    /// </summary>
    /// <param name="gasMolarities">Amount of each gas. Gases missing from the dictionary count as zero.</param>
    /// <param name="temperatureKelvin">Mixture temperature, in kelvins (K).</param>
    /// <returns>Reactions per second, or zero when any factor drives the speed to zero or below.</returns>
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