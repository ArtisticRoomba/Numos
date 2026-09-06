using System.Collections.Frozen;
using Numos.CoreSim.Replay;
using Numos.Units;

namespace Numos.CoreSim.GasReactions;

/// <summary>
///     A linear gas reaction in which the reaction speed doesn't use standard rate equation, but a series of linear
///     functions.
///     To reduce computational load and make setting them up easier, at the cost of realism.
/// </summary>
public readonly partial record struct LinearGasReaction
{
    /// <summary>
    ///     Main Constructor. For a case study on how the temperature graph is evaluated and thus should be parametrized see
    ///     <see cref="GetRateConstantForTemperature" />
    /// </summary>
    /// <param name="input">Input reactants. Mol/Reaction</param>
    /// <param name="output">Output reactants. Mol/Reaction</param>
    /// <param name="energyBalance">Joules produced or consumed per reaction</param>
    /// <param name="lowTemperatureBound">Kelvin for the temperatures linear graphs lower boundary</param>
    /// <param name="highTemperatureBound">Kelvin for the temperature linear graphs upper boundary</param>
    /// <param name="lowTempSpeed">reactions per second at low bound</param>
    /// <param name="highTempSpeed">reactions per second at high bound</param>
    /// <param name="lowStrict">if the linear graph can be extended below low bound</param>
    /// <param name="highStrict">if the graph can be extended above high bound</param>
    /// <param name="speedFactors">the modifies to the base reaction speed based on the presence of certain gases.</param>
    public LinearGasReaction(
        IDictionary<GasProperties, float> input, IDictionary<GasProperties, float> output,
        [Quantity("energy")] Joule energyBalance,
        [Quantity("temperature")] Kelvin lowTemperatureBound,
        [Quantity("temperature")] Kelvin highTemperatureBound,
        [Quantity("frequency")] PerSecond lowTempSpeed,
        [Quantity("frequency")] PerSecond highTempSpeed,
        bool lowStrict, bool highStrict, ISet<LinearSpeedFactor> speedFactors)
    {
        Input = input.ToFrozenDictionary();
        Output = output.ToFrozenDictionary();
        EnergyBalance = energyBalance;
        LowTemperatureBound = lowTemperatureBound;
        HighTemperatureBound = highTemperatureBound;
        LowTempSpeed = lowTempSpeed;
        HighTempSpeed = highTempSpeed;
        LowStrict = lowStrict;
        HighStrict = highStrict;
        SpeedFactors = speedFactors.ToFrozenSet();
        BoundaryRange = HighTemperatureBound - LowTemperatureBound;
        SpeedRange = HighTempSpeed - LowTempSpeed;
    }

    /// <summary>
    ///     Input reactants.
    ///     Mol per Reaction
    /// </summary>
    private FrozenDictionary<GasProperties, float> Input { get; }

    /// <summary>
    ///     Output reactants
    ///     Mol per Reaction
    /// </summary>
    private FrozenDictionary<GasProperties, float> Output { get; }

    /// <summary>
    ///     If this reaction consumes or produces thermal energy.
    ///     In Joules per Reaction
    /// </summary>
    private Joule EnergyBalance { get; }

    private Kelvin LowTemperatureBound { get; }
    private Kelvin HighTemperatureBound { get; }

    private PerSecond LowTempSpeed { get; }
    private PerSecond HighTempSpeed { get; }

    private Kelvin BoundaryRange { get; }

    private PerSecond SpeedRange { get; }

    /// <summary>
    ///     Can the reaction occur below low temperature bound (extending linear graph)
    /// </summary>
    private bool LowStrict { get; }

    /// <summary>
    ///     Can the reaction occur above high temperature bound (extending linear graph)
    /// </summary>
    private bool HighStrict { get; }

    private FrozenSet<LinearSpeedFactor> SpeedFactors { get; }

    internal void AppendHash(ref AtmosStateHasher hash)
    {
        hash.Add(EnergyBalance);
        hash.Add(LowTemperatureBound);
        hash.Add(HighTemperatureBound);
        hash.Add(LowTempSpeed);
        hash.Add(HighTempSpeed);
        hash.Add(LowStrict);
        hash.Add(HighStrict);
        hash.Add(Input.Count);
        foreach (KeyValuePair<GasProperties, float> entry in Input.OrderBy(static entry => entry.Key.Name, StringComparer.Ordinal))
        {
            hash.Add(entry.Key);
            hash.Add(entry.Value);
        }

        hash.Add(Output.Count);
        foreach (KeyValuePair<GasProperties, float> entry in Output.OrderBy(static entry => entry.Key.Name, StringComparer.Ordinal))
        {
            hash.Add(entry.Key);
            hash.Add(entry.Value);
        }

        hash.Add(SpeedFactors.Count);
        foreach (var factor in SpeedFactors.OrderBy(static factor => factor.Gas.Name, StringComparer.Ordinal)
                     .ThenBy(static factor => factor.OrderKey))
            factor.AppendHash(ref hash);
    }

    internal bool SemanticallyEquals(LinearGasReaction other)
    {
        return EnergyBalance.Equals(other.EnergyBalance) &&
               LowTemperatureBound.Equals(other.LowTemperatureBound) &&
               HighTemperatureBound.Equals(other.HighTemperatureBound) &&
               LowTempSpeed.Equals(other.LowTempSpeed) &&
               HighTempSpeed.Equals(other.HighTempSpeed) &&
               LowStrict == other.LowStrict &&
               HighStrict == other.HighStrict &&
               DictionaryEquals(Input, other.Input) &&
               DictionaryEquals(Output, other.Output) &&
               SpeedFactors.SetEquals(other.SpeedFactors);
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

    private static float EvalLinear(
        float value, float boundaryRange, float lowBound, bool lowStrict, bool highStrict,
        float valAtLow, float speedRange)
    {
        // Normalize value into [0, 1].
        float t = (value - lowBound) / boundaryRange;
        // eval boundaries.
        if (float.IsNaN(t)) return 0;

        if (t < 0f)
        {
            if (lowStrict)
                return 0;
        }
        else if (t > 1f)
        {
            if (highStrict)
                return 0;
        }

        return valAtLow + speedRange * t;
    }

    /// <summary>
    ///     Calculate the Rate Constant of this reaction given a temperature value.
    /// </summary>
    /// <param name="temperatureKelvin"></param>
    /// <returns></returns>
    private PerSecond GetRateConstantForTemperature(Kelvin temperatureKelvin)
    {
        return EvalLinear(
            temperatureKelvin,
            BoundaryRange,
            LowTemperatureBound,
            LowStrict,
            HighStrict,
            LowTempSpeed,
            SpeedRange);
    }

    /// <summary>
    ///     Gives Reaction speed in units of reaction per second given a mixture and temperature.
    /// </summary>
    /// <param name="gasMolarities">the mixture of gases represented in their molarity (Mol/L)</param>
    /// <param name="temperature">temperature of the mixture in kelvin</param>
    /// <returns>Reactions per Second</returns>
    [return: Quantity("frequency")]
    public PerSecond GetReactionSpeed(
        IDictionary<GasProperties, float> gasMolarities,
        [Quantity("temperature")] Kelvin temperature)
    {
        PerSecond result = GetRateConstantForTemperature(temperature);
        foreach (var factor in SpeedFactors.OrderBy(static factor => factor.Gas.Name, StringComparer.Ordinal)
                     .ThenBy(static factor => factor.OrderKey))
        {
            gasMolarities.TryGetValue(factor.Gas, out float molarity);
            result *= factor.GetFactor(molarity);
        }

        return result;
    }
}