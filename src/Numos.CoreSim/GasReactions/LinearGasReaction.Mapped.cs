using System.Collections.Concurrent;
using System.Collections.Frozen;

namespace Numos.CoreSim.GasReactions;

public readonly partial record struct LinearGasReaction
{
    /// <summary>
    /// The lookup optimized version of a linear gas reaction.
    /// </summary>
    internal readonly record struct Mapped : IGasReaction
    {
        /// <summary>
        /// Setup the wrapping
        /// </summary>
        /// <param name="original">The Gas Reaction to wrap</param>
        /// <param name="properties">The reference of gas properties to derive the gas ids from.</param>
        public Mapped(LinearGasReaction original, IList<GasProperties> properties)
        {
            Original = original;

            var mappedInputs = original.Input.ToFrozenDictionary(e => properties.IndexOf(e.Key), e => e.Value);
            var mappedOutputs = original.Output.ToFrozenDictionary(e => properties.IndexOf(e.Key), e => e.Value);
            MappedFactors = original.SpeedFactors.Select(e => new Factor(e, properties)).ToFrozenSet();

            var changeEquation = new Dictionary<int, Mole>();
            foreach (var gas in mappedInputs.Keys.Concat(mappedOutputs.Keys).Distinct())
                changeEquation[gas] = mappedOutputs.GetValueOrDefault(gas) - mappedInputs.GetValueOrDefault(gas);

            ChangeEquation = changeEquation.ToFrozenDictionary();
        }

        private LinearGasReaction Original { get; }

        private FrozenSet<Factor> MappedFactors { get; }

        /// <inheritdoc/>
        public FrozenDictionary<int, Mole> ChangeEquation { get; }
        /// <inheritdoc/>
        public Joule EnergyBalance => Original.EnergyBalance;

        /// <inheritdoc/>
        public PerSecond GetReactionSpeed(Mole[] molarityVector, Kelvin temperature)
        {
            PerSecond result = Original.GetRateConstantForTemperature(temperature);
            if (!float.IsNormal(result) || result <= 0)
                return 0;

            var bag = new ConcurrentBag<float>();

            var response = Parallel.ForEach(
                MappedFactors,
                (factor, loopState) =>
                {
                    float f = factor.Original.GetFactor(molarityVector[factor.GasId]);
                    if (!float.IsNormal(f) || f <= 0)
                    {
                        loopState.Stop();
                        return;
                    }

                    bag.Add(f);
                });

            if (!response.IsCompleted)
                return 0;

            foreach (var f in bag)
            {
                result *= f;
            }

            return result;
        }

        /// <summary>
        /// A wrapper for factor
        /// </summary>
        private readonly record struct Factor
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
}