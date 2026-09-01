using System.Collections.Concurrent;
using System.Collections.Frozen;

namespace Numos.CoreSim.GasReactions;

public readonly partial record struct LinearGasReaction
{
    internal readonly record struct Mapped : IGasReaction
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

        /// <summary>
        ///     foreach
        /// </summary>
        public FrozenSet<Factor> MappedFactors { get; }

        public FrozenDictionary<int, float> ChangeEquation { get; }
        public float EnergyBalance => Original.EnergyBalance;

        public float GetReactionSpeed(float[] molarityVector, float temperature)
        {
            var result = Original.GetRateConstantForTemperature(temperature);
            if (!float.IsNormal(result) || result <= 0)
                return 0;

            var bag = new ConcurrentBag<float>();

            var response = Parallel.ForEach(MappedFactors, (factor, loopState) =>
            {
                var f = factor.Original.GetFactor(molarityVector[factor.GasId]);
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
}