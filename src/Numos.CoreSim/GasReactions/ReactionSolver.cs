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
        var voxelCount = chunk.Depth * chunk.Width * chunk.Height;
        //  Parallel.For(0, voxelCount, voxelIndex => ProcessVoxel(chunk, deltaTime, voxelIndex));
        for (var i = 0; i < voxelCount; i++) ProcessVoxel(chunk, deltaTime, i);
    }

    private void ProcessVoxel(AtmosChunk chunk, float deltaTime, int voxelIndex)
    {
        //get the mixture
        var mixtureVector = new float[_config.GasRegistry.Count + 1];

        for (var i = 0; i < chunk.ActiveGasCount; i++)
            mixtureVector[chunk.ActiveGases[i].GasId] = chunk.ActiveGases[i].Moles[voxelIndex];

        //TODO Get the energy of the mixture. yes i know dirty, but what can you do.
        mixtureVector[^1] = 0;
        var currentTemperature = chunk.Temperature[voxelIndex];
        //make sure in single step we dont overstep.
        var stepSize = MathF.Min(_config.MaxDeltaForReactionSteps, deltaTime);
        //split our time interval into smaller steps.
        for (float position = 0; position < deltaTime; position += _config.MaxDeltaForReactionSteps)
        {
            //get all reaction speeds
            bool anyReaction = false;
            var reactionSpeeds = new float[_mappedReactions.Length];
            var temperature = currentTemperature;//to stop warning about the later change of currentTemperature.
            Parallel.For(0, _mappedReactions.Length, i =>
            {
                var e = _mappedReactions[i];
                var speed = e.GetReactionSpeed(mixtureVector, temperature) * stepSize;
                if (reactionSpeeds[i] <= 0)
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
                for (var i = 0; i < mixtureVector.Length; i++)
                {
                    //we calculate total consumption, ignoring production by reactions.
                    var consumption = _mappedReactions.Select((e, j) =>
                        MathF.Min(0, e.ChangeEquation.GetValueOrDefault(i)) * reactionSpeeds[j]).Sum();
                    var postReactionMoles = mixtureVector[i] + consumption;
                    //if negative and even more critical, mark.
                    if (postReactionMoles < criticalValue)
                    {
                        criticalValue = consumption;
                        criticalIndex = i;
                    }
                }

                //check if all reaction speeds have been balanced
                if (criticalIndex == -1)
                    break;
                //adjust reaction speeds so our post reaction moles are 0, eliminating any reaction we cannot solve for.
                // our equation we try to optimize looks like this (((change * speed)/total change in scaled reaction)*available moles)/(change * speed) = (available volume)/(total change in scaled reaction)
                var scale = mixtureVector[criticalIndex] / Math.Abs(criticalValue);
                for (var i = 0; i < reactionSpeeds.Length; i++)
                {
                    if (!_mappedReactions[i].ChangeEquation.ContainsKey(criticalIndex))
                        continue;
                    //select the lower reaction speed
                    reactionSpeeds[i] = MathF.Min(reactionSpeeds[i], reactionSpeeds[i] * scale);
                }
            }
            for (var i = 0; i < mixtureVector.Length - 1; i++)
            {
                for (var j = 0; j < reactionSpeeds.Length; j++)
                    mixtureVector[i] += _mappedReactions[j].ChangeEquation.GetValueOrDefault(i) * reactionSpeeds[j];
            }

            //calculate next heat value
            for (var j = 0; j < reactionSpeeds.Length; j++)
            {
                mixtureVector[^1] += _mappedReactions[j].EnergyBalance * reactionSpeeds[j];
            }

            mixtureVector[^1] = Math.Max(0, mixtureVector[^1]);
            //TODO: adjust temperature based on thermal energy and pressure.
            currentTemperature = currentTemperature;
        }

        //adjust moles from the mixture vector
        foreach (var gasChannel in chunk.ActiveGases.Take(chunk.ActiveGasCount))
        {
            gasChannel.Moles[voxelIndex] = MathF.Max(0, mixtureVector[gasChannel.GasId]);
            //set gas to 0.
            mixtureVector[gasChannel.GasId] = 0;
        }

        //adjust temperature of the voxel.
        chunk.Temperature[voxelIndex] = currentTemperature;
        //inject remaining gases
        var idxShort = (ushort)voxelIndex;
        for (var i = 0; i < mixtureVector.Length - 1; i++)
        {
            if (mixtureVector[i] <= 0) continue;
            chunk.InjectGasToVoxel(idxShort, i, mixtureVector[i], currentTemperature);
        }
        //TODO: report reaction count back. 
    }
}