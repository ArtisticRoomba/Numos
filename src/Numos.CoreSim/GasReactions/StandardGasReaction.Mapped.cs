using System.Collections.Concurrent;
using System.Collections.Frozen;

namespace Numos.CoreSim.GasReactions;

public readonly partial record struct StandardGasReaction
{
    /// <summary>
    ///     A wrapper for StandardGasReactions optimized for lookup speed.
    /// </summary>
    internal readonly record struct Mapped : IGasReaction
    {
        public Mapped(StandardGasReaction original, IList<GasProperties> properties)
        {
            Original = original;

            FrozenDictionary<int, Mole> mappedInputs = original.Input.ToFrozenDictionary(e => properties.IndexOf(e.Key), e => e.Value);
            FrozenDictionary<int, Mole> mappedOutputs = original.Output.ToFrozenDictionary(
                e => properties.IndexOf(e.Key),
                e => e.Value);

            MappedFactors = original.SpeedFactors.ToFrozenDictionary(e => properties.IndexOf(e.Key), e => e.Value);

            var changeEquation = new Dictionary<int, Mole>();
            foreach (int gas in mappedInputs.Keys.Concat(mappedOutputs.Keys).Distinct())
                changeEquation[gas] = mappedOutputs.GetValueOrDefault(gas) - mappedInputs.GetValueOrDefault(gas);

            ChangeEquation = changeEquation.ToFrozenDictionary();
        }

        private StandardGasReaction Original { get; }

        private FrozenDictionary<int, float> MappedFactors { get; }
        /// <inheritdoc />
        public Joule EnergyBalance => Original.EnergyBalance;
        /// <inheritdoc />
        public FrozenDictionary<int, Mole> ChangeEquation { get; }

        /// <inheritdoc />
        public PerSecond GetReactionSpeed(Mole[] molarityVector, Kelvin temperature)
        {
            PerSecond result = Original.GetRateConstant(temperature);
            if (result <= 0 || !float.IsNormal(result))
                return 0;

            var parts = new ConcurrentBag<float>();

            var state = Parallel.ForEach(
                MappedFactors,
                (factor, loop) =>
                {
                    float f = MathF.Pow(molarityVector[factor.Key], factor.Value);
                    if (!float.IsNormal(f) || f == 0)
                    {
                        loop.Stop();
                        return;
                    }

                    parts.Add(f);
                });

            if (!state.IsCompleted)
                return 0;

            foreach (float p in parts)
            {
                result *= p;
            }

            return result;
        }
    }
}