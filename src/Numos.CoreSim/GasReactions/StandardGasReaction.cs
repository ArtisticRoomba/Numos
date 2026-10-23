using System.Collections.Frozen;
using Numos.CoreSim.Replay;
using Magnitudo;

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
    /// <param name="energyBalance">
    ///     Joules released (positive) or absorbed (negative) per mole of reaction, i.e. per mole of whichever
    ///     reactant has a coefficient of one.
    /// </param>
    /// <param name="arrheniusFactor">
    ///     Pre-exponential factor <c>A</c>. Its units depend on the rate law's overall order; the solver evaluates
    ///     the rate equation in mol/m³ and treats the result as mol/(m³·s).
    /// </param>
    /// <param name="activationEnergy">
    ///     Molar activation energy <c>Ea</c>, in J/mol. Convert values authored in kJ/mol first.
    /// </param>
    /// <param name="speedFactors">Rate-equation exponent for each gas that affects the speed.</param>
    /// <param name="requiresIgnition">Whether the reaction should use the ignition system</param>
    public StandardGasReaction(
        IDictionary<GasProperties, float> input, IDictionary<GasProperties, float> output,
        [Quantity("molarEnergy")] JoulePerMole energyBalance,
        float arrheniusFactor, float activationEnergy,
        IDictionary<GasProperties, float> speedFactors,
        bool requiresIgnition = false)
    {
        Input = input.ToFrozenDictionary();
        Output = output.ToFrozenDictionary();
        EnergyBalance = energyBalance;
        ArrheniusFactor = arrheniusFactor;
        ActivationEnergy = activationEnergy;
        SpeedFactors = speedFactors.ToFrozenDictionary();
        RequiresIgnition = requiresIgnition;
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
    ///     Joules released (positive) or absorbed (negative) per mole of reaction.
    /// </summary>
    /// <remarks>
    ///     Per mole of whichever reactant has a coefficient of one, so for 2H₂ + O₂ -> 2H₂O it is the energy released
    ///     per mole of oxygen consumed.
    /// </remarks>
    internal JoulePerMole EnergyBalance { get; }

    /// <summary>
    ///     Arrhenius pre-exponential factor. Its units depend on the rate law's overall order.
    /// </summary>
    private float ArrheniusFactor { get; }

    /// <summary>
    ///     Molar activation energy, in J/mol. The Arrhenius exponent divides it by
    ///     <see cref="AtmosPhysicalConstants.MolarGasConstant" /> (J/(mol*K)).
    ///     Convert values authored in kJ/mol at the call site, for example with
    ///     <see cref="UnitConversions.FromKilojoulePerMole" />.
    /// </summary>
    private JoulePerMole ActivationEnergy { get; }

    /// <summary>
    ///     The exponents of the rate equation:
    ///     k * Gas_1^{a} * Gas_2 ^ {b}...
    /// </summary>
    private FrozenDictionary<GasProperties, float> SpeedFactors { get; }

    /// <summary>
    ///     Whether the reaction should use the ignition system.
    /// </summary>
    internal bool RequiresIgnition { get; }

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
        hash.Add(RequiresIgnition);
    }

    internal bool SemanticallyEquals(StandardGasReaction other)
    {
        return EnergyBalance.Equals(other.EnergyBalance) &&
               ArrheniusFactor.Equals(other.ArrheniusFactor) &&
               ActivationEnergy.Equals(other.ActivationEnergy) &&
               DictionaryEquals(Input, other.Input) &&
               DictionaryEquals(Output, other.Output) &&
               DictionaryEquals(SpeedFactors, other.SpeedFactors) &&
               RequiresIgnition.Equals(other.RequiresIgnition);
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
    /// <returns>The rate constant, treated by the solver as mol/(m³·s) once the rate law is applied.</returns>
    /// <remarks>there are more sophisticated models for k but good luck having anyone setup all the parameters necessary.</remarks>
    internal ConcentrationPerSecond GetRateConstant(Kelvin temperatureKelvin)
    {
        return ArrheniusFactor *
               MathF.Exp(-ActivationEnergy / (temperatureKelvin * AtmosPhysicalConstants.MolarGasConstant));
    }

    /// <summary>
    ///     Computes the reaction speed for a mixture.
    /// </summary>
    /// <param name="gasMolarities">
    ///     Concentration of each gas, in mol/m³. Gases missing from the dictionary count as zero.
    /// </param>
    /// <param name="temperatureKelvin">Mixture temperature, in kelvins (K).</param>
    /// <returns>
    ///     Reaction rate in mol/(m³·s), or zero when any factor drives the speed to zero or below, or when a gas with
    ///     a negative exponent is absent. Can be positive infinity when a trace gas has a negative exponent.
    /// </returns>
    [return: Quantity("concentrationPerSecond")]
    public ConcentrationPerSecond GetReactionSpeed(
        IDictionary<GasProperties, float> gasMolarities,
        [Quantity("temperature")] Kelvin temperatureKelvin)
    {
        ConcentrationPerSecond rateConstant = GetRateConstant(temperatureKelvin);
        if (!float.IsNormal(rateConstant) || rateConstant <= 0f)
            return 0;

        // Uses the solver's per-factor math, so passing the solver's concentrations reproduces its rate exactly.
        ConcentrationPerSecond64 result = rateConstant;
        foreach (KeyValuePair<GasProperties, float> factor in SpeedFactors.OrderBy(
                     static factor => factor.Key.Name,
                     StringComparer.Ordinal))
        {
            if (!gasMolarities.TryGetValue(factor.Key, out float molar)) molar = 0;

            MolePerCubicMetre64 concentration = molar;
            result = ApplyExponent(result, concentration, factor.Value);
            if (result == 0)
                return 0;
        }

        return (ConcentrationPerSecond)result;
    }

    /// <summary>
    ///     Multiplies <paramref name="speed" /> by <c>concentration^exponent</c>, or returns zero when that factor is
    ///     NaN, nonpositive, or infinite because the gas is absent.
    /// </summary>
    /// <remarks>
    ///     A trace gas with a negative exponent may overflow the result to positive infinity.
    ///     The method keeps that value so the solver can saturate the rate.
    ///     An absent gas (0^negative) means no reaction.
    /// </remarks>
    internal static ConcentrationPerSecond64 ApplyExponent(
        ConcentrationPerSecond64 speed, MolePerCubicMetre64 concentration, float exponent)
    {
        double value = Math.Pow(concentration, exponent);
        if (double.IsNaN(value) || value <= 0 || double.IsPositiveInfinity(value) && concentration <= 0)
            return 0;

        return speed * value;
    }
}