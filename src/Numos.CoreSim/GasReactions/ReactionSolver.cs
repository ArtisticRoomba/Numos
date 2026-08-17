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
    /// </summary>
    /// <param name="chunk"></param>
    /// <param name="deltaTime">Over which timespan reactions should occur.</param>
    internal void ProcessChunk(AtmosChunk chunk, float deltaTime)
    {
        //process each voxel in parallel
        int voxelCount = chunk.Depth * chunk.Width * chunk.Height;

        var newMixtures = ArrayPool<float[]>.Shared.Rent(voxelCount);
        var newTemps = ArrayPool<float>.Shared.Rent(voxelCount);
        var mixtureLength = _config.GasRegistry.Count + 1;
        Parallel.For(0, voxelCount, (int voxelIndex) =>
        {
            var temp = chunk.Temperature[voxelIndex];
          
            // get the mixture
            var mixtureVector = ArrayPool<float>.Shared.Rent(mixtureLength);
            Array.Clear(mixtureVector, 0, mixtureLength);
            for (var i = 0; i < chunk.ActiveGasCount; i++)
                mixtureVector[chunk.ActiveGases[i].GasId] = chunk.ActiveGases[i].Moles[voxelIndex];
            // do actual evaluation of the mixture for reactions.
            ProcessVoxel(deltaTime, mixtureVector, ref temp, mixtureLength);
            newMixtures[voxelIndex] = mixtureVector;
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
                gasChannel.Moles[voxelIndex] = MathF.Max(0, mixtureVector[gasChannel.GasId]);
                //set gas to 0.
                mixtureVector[gasChannel.GasId] = 0;
            }

            //inject remaining gases
            for (var i = 0; i < mixtureLength - 1; i++)
            {
                if (mixtureVector[i] <= 0) continue;
                chunk.InjectGasToVoxel(voxelIndex, i, mixtureVector[i], newTemps[voxelIndex]);
            }

            ArrayPool<float>.Shared.Return(mixtureVector);
        }

        ArrayPool<float>.Shared.Return(newTemps);
        ArrayPool<float[]>.Shared.Return(newMixtures);
    }


    private void ProcessVoxel(float deltaTime, float[] mixtureVector, ref float currentTemperature, int mixtureLength)
    {
        //TODO Get the energy of the mixture. yes i know dirty, but what can you do.
        mixtureVector[mixtureLength-1] = 0;
        //make sure in single step we dont overstep.
        var stepSize = MathF.Min(_config.MaxDeltaForReactionSteps, deltaTime);
        //split our time interval into smaller steps.
        for (float position = 0; position < deltaTime; position += _config.MaxDeltaForReactionSteps)
        {
            //get all reaction speeds
            bool anyReaction = false;
            var reactionCount = _mappedReactions.Length;
            var reactionSpeeds = ArrayPool<float>.Shared.Rent(reactionCount);
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
                        criticalValue = consumption;
                        criticalIndex = i;
                    }
                }

                //check if all reaction speeds have been balanced / are without conflict
                if (criticalIndex == -1)
                    break;
                // adjust reaction speeds so our post reaction moles are 0.
                // our equation we try to optimize looks like this (((change * speed)/total change in scaled reaction)*available moles)/(change * speed) = (available volume)/(total change in scaled reaction)
                // so we calculate the scale, to scale reaction speeds down to balance the equation of consumption.
                var scale = mixtureVector[criticalIndex] / Math.Abs(criticalValue);
                for (var i = 0; i < reactionCount; i++)
                {
                    if (!_mappedReactions[i].ChangeEquation.ContainsKey(criticalIndex))
                        continue;
                    //select the lower reaction speed.
                    reactionSpeeds[i] = MathF.Min(reactionSpeeds[i], reactionSpeeds[i] * scale);
                }
                ArrayPool<float>.Shared.Return(reactionSpeeds);
            }

            //apply mixture.
            for (var i = 0; i < mixtureLength - 1; i++)
            {
                for (var j = 0; j < reactionCount; j++)
                    mixtureVector[i] += _mappedReactions[j].ChangeEquation.GetValueOrDefault(i) * reactionSpeeds[j];
            }

            //calculate next heat value
            for (var j = 0; j < reactionCount; j++)
            {
                mixtureVector[mixtureLength-1] += _mappedReactions[j].EnergyBalance * reactionSpeeds[j];
            }
            mixtureVector[mixtureLength-1] = Math.Max(0, mixtureVector[mixtureLength-1]);
            //TODO: adjust temperature based on thermal energy and pressure.
            currentTemperature = currentTemperature;
            //TODO: report reaction count back. 
        }
    }
}