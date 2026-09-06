using System.Collections.Frozen;

namespace Numos.CoreSim.GasReactions;

public readonly partial record struct LinearGasReaction
{
    /// <summary>
    ///     The lookup optimized version of a linear gas reaction.
    /// </summary>
    internal readonly record struct Mapped : IGasReaction
    {
        /// <summary>
        ///     Setup the wrapping
        /// </summary>
        /// <param name="original">The Gas Reaction to wrap</param>
        /// <param name="gasRegistry">The gas registry used to resolve gas ids.</param>
        public Mapped(LinearGasReaction original, IGasRegistry gasRegistry)
        {
            Original = original;

            FrozenDictionary<int, float> mappedInputs = original.Input.ToFrozenDictionary(
                e => gasRegistry.GasIdToIndex(e.Key.Name),
                e => e.Value);

            FrozenDictionary<int, float> mappedOutputs = original.Output.ToFrozenDictionary(
                e => gasRegistry.GasIdToIndex(e.Key.Name),
                e => e.Value);

            MappedFactors = original.SpeedFactors.OrderBy(static factor => factor.Gas.Name, StringComparer.Ordinal)
                .ThenBy(static factor => factor.OrderKey).Select(e => new Factor(e, gasRegistry)).ToArray();

            var changeEquation = new Dictionary<int, Mole>();
            foreach (int gas in mappedInputs.Keys.Concat(mappedOutputs.Keys).Distinct())
                changeEquation[gas] = mappedOutputs.GetValueOrDefault(gas) - mappedInputs.GetValueOrDefault(gas);

            ChangeEquation = changeEquation.ToFrozenDictionary();
        }

        private LinearGasReaction Original { get; }

        private Factor[] MappedFactors { get; }

        /// <inheritdoc />
        public FrozenDictionary<int, Mole> ChangeEquation { get; }
        /// <inheritdoc />
        public Joule EnergyBalance => Original.EnergyBalance;

        /// <inheritdoc />
        public PerSecond GetReactionSpeed(Mole[] molarityVector, Kelvin temperature)
        {
            PerSecond result = Original.GetRateConstantForTemperature(temperature);
            if (!float.IsNormal(result) || result <= 0)
                return 0;

            // A parallel reduction changes multiplication order with worker scheduling.
            foreach (var factor in MappedFactors)
            {
                float value = factor.Original.GetFactor(molarityVector[factor.GasId]);
                if (!float.IsNormal(value) || value <= 0f)
                    return 0;

                result *= value;
            }

            return result;
        }

        /// <summary>
        ///     A wrapper for factor
        /// </summary>
        private readonly record struct Factor
        {
            public Factor(LinearSpeedFactor original, IGasRegistry gasRegistry)
            {
                Original = original;
                GasId = gasRegistry.GasIdToIndex(original.Gas.Name);
            }

            public LinearSpeedFactor Original { get; }

            public int GasId { get; }
        }
    }
}