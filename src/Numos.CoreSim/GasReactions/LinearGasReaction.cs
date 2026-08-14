using System.Collections.Frozen;

namespace Numos.CoreSim;

/// <summary>
///     A linear gas reaction in which the reaction speed doesn't use standard rate equation, but a series of linear
///     functions.
///     To reduce computational load and make setting them up easier, at the cost of realism.
/// </summary>
public readonly record struct LinearGasReaction
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
    public LinearGasReaction(IDictionary<GasProperties, float> input, IDictionary<GasProperties, float> output,
        float energyBalance, float lowTemperatureBound, float highTemperatureBound, float lowTempSpeed,
        float highTempSpeed, bool lowStrict, bool highStrict, ISet<LinearSpeedFactor> speedFactors)
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
    public FrozenDictionary<GasProperties, float> Input { get; }

    /// <summary>
    ///     Output reactants
    ///     Mol per Reaction
    /// </summary>
    public FrozenDictionary<GasProperties, float> Output { get; }

    /// <summary>
    ///     If this reaction consumes or produces thermal energy.
    ///     In Joules per Reaction
    /// </summary>
    public float EnergyBalance { get; }

    public float LowTemperatureBound { get; }
    public float HighTemperatureBound { get; }
    public float LowTempSpeed { get; }
    public float HighTempSpeed { get; }

    public float BoundaryRange { get; }

    public float SpeedRange { get; }

    /// <summary>
    ///     Can the reaction occur below low temperature bound (extending linear graph)
    /// </summary>
    public bool LowStrict { get; }

    /// <summary>
    ///     Can the reaction occur above high temperature bound (extending linear graph)
    /// </summary>
    public bool HighStrict { get; }

    public FrozenSet<LinearSpeedFactor> SpeedFactors { get; }

    private static float EvalLinear(float value, float boundaryRange, float lowBound, bool lowStrict, bool highStrict,
        float valAtLow, float speedRange)
    {
        // Normalize value into [0, 1].
        var t = (value - lowBound) / boundaryRange;
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
    public float GetRateConstantForTemperature(float temperatureKelvin)
    {
        return EvalLinear(temperatureKelvin, BoundaryRange, LowTemperatureBound, LowStrict, HighStrict, LowTempSpeed,
            SpeedRange);
    }

    /// <summary>
    ///     Gives Reaction speed in units of reaction per second given a mixture and temperature.
    /// </summary>
    /// <param name="gasMolarities">the mixture of gases represented in their molarity (Mol/L)</param>
    /// <param name="temperature">temperature of the mixture in kelvin</param>
    /// <returns>Reactions per Second</returns>
    public double GetReactionSpeed(IDictionary<GasProperties, float> gasMolarities, float temperature)
    {
        return SpeedFactors.AsParallel().Select(factor =>
        {
            gasMolarities.TryGetValue(factor.Gas, out var molarity);
            return factor.GetFactor(molarity);
        }).Append(GetRateConstantForTemperature(temperature)).Aggregate((a, b) => a * b);
    }

    public readonly record struct Mapped
    {
        public Mapped(LinearGasReaction original, IList<GasProperties> properties)
        {
            Original = original;

            MappedInputs = original.Input.ToFrozenDictionary(e => properties.IndexOf(e.Key), e => e.Value);
            MappedOutputs = original.Output.ToFrozenDictionary(e => properties.IndexOf(e.Key), e => e.Value);
            MappedFactors = original.SpeedFactors.Select(e => new Factor(e, properties)).ToFrozenSet();

            var changeEquation = new Dictionary<int, float>();
            foreach (var gas in MappedInputs.Keys.Concat(MappedOutputs.Keys).Distinct())
                changeEquation[gas] = MappedOutputs.GetValueOrDefault(gas) - MappedInputs.GetValueOrDefault(gas);

            changeEquation[properties.Count] = original.EnergyBalance;

            ChangeEquation = changeEquation.ToFrozenDictionary();
        }

        public FrozenDictionary<int, float> MappedInputs { get; }

        public FrozenDictionary<int, float> MappedOutputs { get; }

        public LinearGasReaction Original { get; }

        public FrozenDictionary<int, float> ChangeEquation { get; }

        /// <summary>
        ///     foreach
        /// </summary>
        public FrozenSet<Factor> MappedFactors { get; }

        public float GetReactionSpeed(float[] molarityVector, float temperature)
        {
            var result = Original.GetRateConstantForTemperature(temperature);
            if (result <= 0)
                return 0;

            return result * MappedFactors.AsParallel()
                .Select(factor => factor.Original.GetFactor(molarityVector[factor.GasId]))
                .Aggregate((a, b) => a * b);
        }

        public readonly record struct Factor
        {
            public Factor(LinearSpeedFactor original, IList<GasProperties> properties)
            {
                Original = original;
                GasId = properties.IndexOf(original.Gas);
            }

            public LinearSpeedFactor Original { get; }

            public int GasId { get; }
        }
    }


    public readonly record struct LinearSpeedFactor(
        GasProperties Gas,
        float LowMolarityBound,
        float HighMolarityBound,
        float LowMolaritySpeed,
        float HighMolaritySpeed,
        bool LowStrict,
        bool HighStrict)
    {
        public GasProperties Gas { get; } = Gas;

        /// <summary>
        ///     Can the reaction occur below low molarity bound (extending linear graph)
        /// </summary>
        public bool LowStrict { get; } = LowStrict;

        /// <summary>
        ///     Can the reaction occur above high molarity bound (extending linear graph)
        /// </summary>
        public bool HighStrict { get; } = HighStrict;

        public float BoundaryRange { get; } = HighMolarityBound - LowMolarityBound;

        public float FactorRange { get; } = HighMolaritySpeed - LowMolaritySpeed;

        public float GetFactor(float molarity)
        {
            return EvalLinear(molarity, BoundaryRange, LowMolarityBound, LowStrict, HighStrict, LowMolaritySpeed,
                FactorRange);
        }
    }
}