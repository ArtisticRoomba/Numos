using System.Collections.Frozen;

namespace Numos.CoreSim.GasReactions;

public readonly partial record struct StandardGasReaction
{
    /// <summary>
    ///     A wrapper for StandardGasReactions optimized for lookup speed.
    /// </summary>
    internal readonly record struct Mapped : IGasReaction
    {
        public Mapped(StandardGasReaction original, IGasRegistry gasRegistry)
        {
            Original = original;

            FrozenDictionary<int, Mole> mappedInputs = original.Input.ToFrozenDictionary(
                e => gasRegistry.GasIdToIndex(e.Key.Name),
                e => e.Value);

            FrozenDictionary<int, Mole> mappedOutputs = original.Output.ToFrozenDictionary(
                e => gasRegistry.GasIdToIndex(e.Key.Name),
                e => e.Value);

            MappedFactors = original.SpeedFactors.OrderBy(static factor => factor.Key.Name, StringComparer.Ordinal)
                .Select(e => new KeyValuePair<int, float>(gasRegistry.GasIdToIndex(e.Key.Name), e.Value)).ToArray();

            var changeEquation = new Dictionary<int, Mole>();
            foreach (int gas in mappedInputs.Keys.Concat(mappedOutputs.Keys).Distinct())
                changeEquation[gas] = mappedOutputs.GetValueOrDefault(gas) - mappedInputs.GetValueOrDefault(gas);

            ChangeEquation = changeEquation.ToFrozenDictionary();
        }

        private StandardGasReaction Original { get; }

        private KeyValuePair<int, float>[] MappedFactors { get; }
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

            foreach (KeyValuePair<int, float> factor in MappedFactors)
            {
                float value = MathF.Pow(molarityVector[factor.Key], factor.Value);
                if (!float.IsNormal(value) || value == 0f)
                    return 0;

                result *= value;
            }

            return result;
        }
    }
}