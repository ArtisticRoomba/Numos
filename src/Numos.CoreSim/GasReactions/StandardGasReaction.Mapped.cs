using System.Collections.Concurrent;
using System.Collections.Frozen;

namespace Numos.CoreSim.GasReactions;

public readonly partial record struct StandardGasReaction
{
    internal readonly record struct Mapped : IGasReaction
    {
        public Mapped(StandardGasReaction original, IList<GasProperties> properties)
        {
            Original = original;

            MappedInputs = original.Input.ToFrozenDictionary(e => properties.IndexOf(e.Key), e => e.Value);
            MappedOutputs = original.Output.ToFrozenDictionary(e => properties.IndexOf(e.Key), e => e.Value);
            MappedFactors = original.SpeedFactors.ToFrozenDictionary(e => properties.IndexOf(e.Key), e => e.Value);

            var changeEquation = new Dictionary<int, float>();
            foreach (var gas in MappedInputs.Keys.Concat(MappedOutputs.Keys).Distinct())
                changeEquation[gas] = MappedOutputs.GetValueOrDefault(gas) - MappedInputs.GetValueOrDefault(gas);

            changeEquation[properties.Count] = original.EnergyBalance;

            ChangeEquation = changeEquation.ToFrozenDictionary();
        }

        public FrozenDictionary<int, float> MappedInputs { get; }

        public FrozenDictionary<int, float> MappedOutputs { get; }

        public StandardGasReaction Original { get; }

        public FrozenDictionary<int, float> MappedFactors { get; }
        public float EnergyBalance => Original.EnergyBalance;

        public FrozenDictionary<int, float> ChangeEquation { get; }

        public float GetReactionSpeed(float[] molarityVector, float temperature)
        {
            var result = Original.GetRateConstant(temperature);
            if (result <= 0 || !float.IsNormal(result))
                return 0;
            var parts = new ConcurrentBag<float>();

            var state = Parallel.ForEach(MappedFactors, (factor, loop) =>
            {
                var f = MathF.Pow(molarityVector[factor.Key], factor.Value);
                if (!float.IsNormal(f) || f == 0)
                {
                    loop.Stop();
                    return;
                }

                parts.Add(f);
            });
            if (!state.IsCompleted)
                return 0;
            foreach (var p in parts)
            {
                result *= p;
            }

            return result;
        }
    }
}