using System.Collections.Frozen;
using Numos.CoreSim.Replay;
using Numos.Units;

namespace Numos.CoreSim.GasReactions;

/// <summary>
///     A gas reaction whose speed comes from piecewise-linear functions of temperature and gas amount instead of a
///     standard rate equation.
/// </summary>
/// <remarks>
///     Cheaper to evaluate and easier to set up than <see cref="StandardGasReaction" />, at the cost of realism.
/// </remarks>
public readonly partial record struct LinearGasReaction
{
    /// <summary>
    ///     Creates a linear reaction. The rate constant is a straight line through
    ///     (<paramref name="lowTemperatureBound" />, <paramref name="lowTempSpeed" />) and
    ///     (<paramref name="highTemperatureBound" />, <paramref name="highTempSpeed" />).
    /// </summary>
    /// <param name="input">Moles of each reactant consumed per reaction.</param>
    /// <param name="output">Moles of each product produced per reaction.</param>
    /// <param name="energyBalance">Joules released (positive) or absorbed (negative) per reaction.</param>
    /// <param name="lowTemperatureBound">Temperature of the line's lower point, in kelvins (K).</param>
    /// <param name="highTemperatureBound">Temperature of the line's upper point, in kelvins (K).</param>
    /// <param name="lowTempSpeed">Reactions per second at <paramref name="lowTemperatureBound" />.</param>
    /// <param name="highTempSpeed">Reactions per second at <paramref name="highTemperatureBound" />.</param>
    /// <param name="lowStrict">
    ///     When <see langword="true" />, the reaction stops below <paramref name="lowTemperatureBound" />; when
    ///     <see langword="false" />, the line is extrapolated.
    /// </param>
    /// <param name="highStrict">
    ///     When <see langword="true" />, the reaction stops above <paramref name="highTemperatureBound" />; when
    ///     <see langword="false" />, the line is extrapolated.
    /// </param>
    /// <param name="speedFactors">Multipliers on the rate constant that depend on the amount of specific gases.</param>
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
    ///     Moles of each reactant consumed per reaction.
    /// </summary>
    private FrozenDictionary<GasProperties, float> Input { get; }

    /// <summary>
    ///     Moles of each product produced per reaction.
    /// </summary>
    private FrozenDictionary<GasProperties, float> Output { get; }

    /// <summary>
    ///     Joules released (positive) or absorbed (negative) per reaction.
    /// </summary>
    internal Joule EnergyBalance { get; }

    private Kelvin LowTemperatureBound { get; }
    private Kelvin HighTemperatureBound { get; }

    private PerSecond LowTempSpeed { get; }
    private PerSecond HighTempSpeed { get; }

    private Kelvin BoundaryRange { get; }

    private PerSecond SpeedRange { get; }

    /// <summary>
    ///     Whether the rate is zero below <see cref="LowTemperatureBound" /> instead of extrapolated.
    /// </summary>
    private bool LowStrict { get; }

    /// <summary>
    ///     Whether the rate is zero above <see cref="HighTemperatureBound" /> instead of extrapolated.
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
        // t is 0 at the low bound and 1 at the high bound; outside [0, 1] the line is extrapolated unless strict.
        float t = (value - lowBound) / boundaryRange;
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
    ///     Evaluates the temperature line at <paramref name="temperatureKelvin" />, before any speed factors.
    /// </summary>
    /// <param name="temperatureKelvin">Mixture temperature, in kelvins (K).</param>
    /// <returns>
    ///     Reactions per second, or zero outside a strict bound. Can be negative if the line is extrapolated past
    ///     zero; the reaction solver treats nonpositive rates as no reaction.
    /// </returns>
    internal PerSecond GetRateConstantForTemperature(Kelvin temperatureKelvin)
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
    ///     Computes the reaction speed for a mixture: the temperature rate constant multiplied by every speed factor.
    /// </summary>
    /// <param name="gasMolarities">Amount of each gas, in the units the speed factors' bounds were authored in.</param>
    /// <param name="temperature">Mixture temperature, in kelvins (K).</param>
    /// <returns>Reactions per second.</returns>
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