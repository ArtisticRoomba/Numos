using System.Buffers;
using System.Collections.Frozen;

namespace Numos.CoreSim.GasReactions;

public class ReactionSolver
{
    private AtmosConfig _config = new();

    private FrozenSet<LinearGasReaction.Mapped> _mappedLinGasReacts = [];

    private IGasReaction[] _mappedReactions = [];

    private FrozenSet<StandardGasReaction.Mapped> _mappedStandardGasReacts = [];


    internal void SetAtmosConfig(AtmosConfig config)
    {
        _config = config;
        //update cached master matrices
        _mappedLinGasReacts =
            config.LinearReactionRegistry.Select(e => new LinearGasReaction.Mapped(e, config.GasRegistry))
                .ToFrozenSet();
        _mappedStandardGasReacts = config.StandardReactionRegistry
            .Select(e => new StandardGasReaction.Mapped(e, config.GasRegistry)).ToFrozenSet();
        _mappedReactions =
        [
            .. _mappedLinGasReacts.Cast<IGasReaction>(),
            .. _mappedStandardGasReacts.Cast<IGasReaction>()
        ];
    }

    /// <summary>
    /// wrapping core solver into a chunk.
    /// </summary>
    /// <param name="chunk"></param>
    /// <param name="deltaTime">Over which timespan reactions should occur.</param>
    /// <param name="reactionCount"></param>
    internal void ProcessChunk(AtmosChunk chunk, float deltaTime, float[]? reactionCount = null)
    {
        //process each voxel in parallel
        int voxelCount = chunk.Depth * chunk.Width * chunk.Height;

        var newMixtures = ArrayPool<float[]>.Shared.Rent(voxelCount);
        var reactionFeedbacks = reactionCount == null ? null : ArrayPool<float[]>.Shared.Rent(voxelCount);
        var newTemps = ArrayPool<float>.Shared.Rent(voxelCount);
        var mixtureLength = _config.GasRegistry.Count + 1;

        Parallel.For(0, voxelCount, (int voxelIndex) =>
            //   for (var voxelIndex = 0; voxelIndex < voxelCount; voxelIndex++)
        {
            var temp = chunk.Temperature[voxelIndex];
            var reactionFeedback = reactionCount == null ? null : ArrayPool<float>.Shared.Rent(_mappedReactions.Length);
            if (reactionFeedback != null)
                Array.Clear(reactionFeedback, 0, reactionFeedback.Length);
            if (reactionFeedbacks != null && reactionFeedback != null)
                reactionFeedbacks[voxelIndex] = reactionFeedback;
            // get the mixture
            var mixtureVector = ArrayPool<float>.Shared.Rent(mixtureLength);
            var content = 0f;
            Array.Clear(mixtureVector, 0, mixtureLength);
            for (var i = 0; i < chunk.ActiveGasCount; i++)
            {
                mixtureVector[chunk.ActiveGases[i].GasId] = chunk.ActiveGases[i].Moles[voxelIndex];
                content += chunk.ActiveGases[i].Moles[voxelIndex];
            }

            newMixtures[voxelIndex] = mixtureVector;
            newTemps[voxelIndex] = temp;
            if (content <= 0.0001)
                return;
            //continue;
            // do actual evaluation of the mixture for reactions.
            ProcessVoxel(deltaTime, mixtureVector, ref temp, reactionFeedback);

            // adjust temperature of the voxel.
            chunk.Temperature[voxelIndex] = temp;
            newTemps[voxelIndex] = temp;
        });
        //put data back in a single thread.
        for (ushort voxelIndex = 0; voxelIndex < voxelCount; voxelIndex++)
        {
            var mixtureVector = newMixtures[voxelIndex];
            var c = chunk.ActiveGasCount;
            //adjust moles from the mixture vector
            foreach (var gasChannel in chunk.ActiveGases.Take(c))
            {
                var diff = gasChannel.Moles[voxelIndex] - mixtureVector[gasChannel.GasId];
                if (diff != 0)
                {
                    gasChannel.Moles[voxelIndex] = MathF.Max(0, mixtureVector[gasChannel.GasId]);
                    chunk.MarkChanged();
                }
                //set gas to 0.
                mixtureVector[gasChannel.GasId] = 0;
            }
            
            //inject remaining gases
            for (var i = 0; i < mixtureLength - 1; i++)
            {
                if (mixtureVector[i] <= 0) continue;
                chunk.InjectGasToVoxel(voxelIndex, i, mixtureVector[i], newTemps[voxelIndex]);
            }

            //collect feedbacks and respond back.
            ArrayPool<float>.Shared.Return(mixtureVector);
        }

        ArrayPool<float>.Shared.Return(newTemps);
        ArrayPool<float[]>.Shared.Return(newMixtures);

        if (reactionCount != null && reactionFeedbacks != null)
        {
            foreach (var feedback in reactionFeedbacks)
            {
                for (int i = 0; i < reactionCount.Length; i++)
                {
                    reactionCount[i] += feedback[i];
                }

                ArrayPool<float>.Shared.Return(feedback);
            }

            ArrayPool<float[]>.Shared.Return(reactionFeedbacks);
        }
    }


    private void ExtractHeat(float[] mixtureVector, ref readonly float temperature, int mixtureLength)
    {
        var result = 0f;
        // Use specific heat capacity of each gas to calculate the necessary energy to keep at temperature
        for (var i = 0; i < mixtureLength; i++)
        {
            if (mixtureVector[i] == 0)
                continue;
            // multiply by mole amounts
            //sum together
            result += mixtureVector[i] * temperature * _config.GasRegistry[i].SpecificHeatCapacity;
        }

        mixtureVector[mixtureLength] = result;
    }


    private float UpdateTemperature(float[] mixtureVector)
    {
        const float constantHelper = 3 * 1.380649E-23f;
        var totalKineticEnergy = mixtureVector[^1];
        // KE = (3/2) k * T <- see  Kinetic Molecular Theory. k is boltzman constant, KE is kinetic energy.
        // Solving for T we get:
        // (KE * 2 )/3k = T
        return (totalKineticEnergy * 2) / constantHelper;
    }

    /// <summary>
    /// Core solver.
    /// </summary>
    /// <param name="deltaTime"></param>
    /// <param name="mixtureVector">a vector describing molarity of the mixture (mol/l)</param>
    /// <param name="currentTemperature">temperature, which will be adjusted to reflect final temperature of the mixture</param>
    /// <param name="reactionFeedback">optional array to which we write how often each reaction occured, index = reaction id</param>
    internal void ProcessVoxel(float deltaTime, float[] mixtureVector, ref float currentTemperature,
        float[]? reactionFeedback)
    {
        int mixtureLength = _config.GasRegistry.Count;
        ExtractHeat(mixtureVector, ref currentTemperature, mixtureLength);
        //make sure in single step we dont overstep.
        var stepSize = MathF.Min(_config.MaxDeltaForReactionSteps, deltaTime);
        //split our time interval into smaller steps.
        var reactionCount = _mappedReactions.Length;
        var reactionSpeeds = ArrayPool<float>.Shared.Rent(reactionCount);
        for (float position = 0; position < deltaTime; position += _config.MaxDeltaForReactionSteps)
        {
            //prep array.
            Array.Clear(reactionSpeeds, 0, reactionCount);
            //get all reaction speeds
            bool anyReaction = false;

            var temperature = currentTemperature; //to stop warning about the later change of currentTemperature.
            Parallel.For(0, reactionCount, (int i) =>
            {
                var e = _mappedReactions[i];
                var speed = e.GetReactionSpeed(mixtureVector, temperature) * stepSize;
                if (speed <= 0)
                    return;
                reactionSpeeds[i] = speed;
                anyReaction = true;
            });
            //check if there was even a reaction.
            if (!anyReaction)
                break;
            //adjusts reactions speed as to not consume our available material in a single step.
            while (true)
            {
                var criticalIndex = -1;
                float criticalValue = 0;
                float criticalConsumption = 0;
                //check which consumption might go over available material
                for (var i = 0; i < mixtureLength; i++)
                {
                    //we calculate total consumption, ignoring production by reactions.
                    var consumption = _mappedReactions.Select((e, j) =>
                        MathF.Min(0, e.ChangeEquation.GetValueOrDefault(i)) * reactionSpeeds[j]).Sum();
                    //check how much moles/joules are left over after all reactions.
                    var postReactionMoles = mixtureVector[i] + consumption;
                    //if negative and even more critical, mark.
                    if (postReactionMoles < criticalValue)
                    {
                        criticalValue = postReactionMoles;
                        criticalConsumption = consumption;
                        criticalIndex = i;
                    }
                }

                //check if all reaction speeds have been balanced / are without conflict
                if (criticalIndex == -1)
                    break;
                // adjust reaction speeds so our post reaction moles are 0.
                // our equation we try to optimize looks like this (((change * speed)/total change in scaled reaction)*available moles)/(change * speed) = (available volume)/(total change in scaled reaction)
                // so we calculate the scale, to scale reaction speeds down to balance the equation of consumption.
                var scale = MathF.Min(1f, MathF.Max(0f, mixtureVector[criticalIndex] / Math.Abs(criticalConsumption)));
                for (var i = 0; i < reactionCount; i++)
                {
                    if (!_mappedReactions[i].ChangeEquation.ContainsKey(criticalIndex))
                        continue;
                    //select the lower reaction speed.
                    reactionSpeeds[i] = MathF.Min(reactionSpeeds[i], reactionSpeeds[i] * scale);
                }
            }

            //apply mixture.
            for (var i = 0; i < mixtureLength; i++)
            {
                for (var j = 0; j < reactionCount; j++)
                    mixtureVector[i] += _mappedReactions[j].ChangeEquation.GetValueOrDefault(i) * reactionSpeeds[j];
            }

            if (reactionFeedback != null)
            {
                //calculate next heat value and report feedback
                for (var j = 0; j < reactionCount; j++)
                {
                    mixtureVector[mixtureLength] += _mappedReactions[j].EnergyBalance * reactionSpeeds[j];
                    reactionFeedback[j] += reactionSpeeds[j];
                }
            }
            else
            {
                //calculate next heat value
                for (var j = 0; j < reactionCount; j++)
                {
                    mixtureVector[mixtureLength] += _mappedReactions[j].EnergyBalance * reactionSpeeds[j];
                }
            }

            mixtureVector[mixtureLength] = Math.Max(0, mixtureVector[mixtureLength]);
            //adjust temperature based on heat value.
            currentTemperature = UpdateTemperature(mixtureVector);
        }

        //cleanup speeds.
        ArrayPool<float>.Shared.Return(reactionSpeeds);
    }
}