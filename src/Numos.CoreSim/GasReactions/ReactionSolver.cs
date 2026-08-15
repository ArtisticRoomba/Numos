using System.Collections.Frozen;

namespace Numos.CoreSim.GasReactions;

public class ReactionSolver
{
    private AtmosConfig _config = new();

    private FrozenSet<LinearGasReaction.Mapped> _mappedLinGasReacts = [];

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
        for (int i = 0; i < voxelCount; i++)
        {
            ProcessVoxel(chunk, deltaTime, i);
        }
    }

    private void ProcessVoxel(AtmosChunk chunk, float deltaTime, int voxelIndex)
    {
        //get the mixture
        var mixtureVector = new float[_config.GasRegistry.Count + 1];

        for (var i = 0; i < chunk.ActiveGasCount; i++)
            mixtureVector[chunk.ActiveGases[i].GasId] = chunk.ActiveGases[i].Moles[voxelIndex];
        //TODO Get the energy of the mixture. yes i know dirty, but what can you do.
        mixtureVector[^1] = 0;
        var nextMixtureVector = new float[_config.GasRegistry.Count + 1];
        var stepDelta = Math.Min(deltaTime, _config.MaxDeltaForReactionSteps);
        var currentTemperature = chunk.Temperature[voxelIndex];
        var tookStep = TookStep(deltaTime, mixtureVector, ref currentTemperature, nextMixtureVector, stepDelta);
        //check if any reactions could took place at all
        if (!tookStep)
            return;
        //adjust moles from the mixture vector
        foreach (var gasChannel in chunk.ActiveGases)
        {
            gasChannel.Moles[voxelIndex] = mixtureVector[gasChannel.GasId];
            //set gas to 0.
            mixtureVector[gasChannel.GasId] = 0;
        }

        //adjust temperature of the voxel.
        chunk.Temperature[voxelIndex] = currentTemperature;
        //inject remaining gases
        var idxShort = (ushort)voxelIndex;
        for (var i = 0; i < mixtureVector.Length - 1; i++)
        {
            if (mixtureVector[i] == 0) continue;
            chunk.InjectGasToVoxel(idxShort, i, mixtureVector[i], currentTemperature);
        }
        //TODO: report reaction count back. 
    }

    /// <summary>
    ///     This is the meat of our solver. in small steps processing all reactions.
    /// </summary>
    /// <param name="deltaTime"></param>
    /// <param name="mixtureVector"></param>
    /// <param name="currentTemperature"></param>
    /// <param name="nextMixtureVector"></param>
    /// <param name="stepDelta"></param>
    /// <returns></returns>
    private bool TookStep(float deltaTime, float[] mixtureVector, ref float currentTemperature,
        float[] nextMixtureVector,
        float stepDelta)
    {
        var tookStep = false;
        for (float position = 0; position < deltaTime; position += _config.MaxDeltaForReactionSteps)
        {
            var stepTemp = currentTemperature;

            var changeOfMixtureByLinearReactionsPerSecond = _mappedLinGasReacts.AsParallel().SelectMany(e =>
                {
                    var reactionSpeed = e.GetReactionSpeed(mixtureVector, stepTemp);
                    return reactionSpeed > 0
                        ? e.ChangeEquation.Select(f => new KeyValuePair<int, float>(f.Key, f.Value * reactionSpeed))
                        : [];
                }
            ).GroupBy(e => e.Key).ToFrozenDictionary(e => e.Key, e => e.Sum(f => f.Value));

            var changeOfMixtureByStandardReactionsPerSecond = _mappedStandardGasReacts.AsParallel().SelectMany(e =>
                {
                    var reactionSpeed = e.GetReactionSpeed(mixtureVector, stepTemp);
                    return reactionSpeed > 0
                        ? e.ChangeEquation.Select(f => new KeyValuePair<int, float>(f.Key, f.Value * reactionSpeed))
                        : [];
                })
                .GroupBy(e => e.Key).ToFrozenDictionary(e => e.Key, e => e.Sum(f => f.Value));

            var bad = false;
            if (changeOfMixtureByLinearReactionsPerSecond.Count == 0 &&
                changeOfMixtureByStandardReactionsPerSecond.Count == 0)
                break;
            var loopResult = Parallel.For(0, mixtureVector.Length, (i, state) =>
            {
                nextMixtureVector[i] = mixtureVector[i] +
                                       changeOfMixtureByLinearReactionsPerSecond.GetValueOrDefault(i) * stepDelta +
                                       changeOfMixtureByStandardReactionsPerSecond.GetValueOrDefault(i) * stepDelta;
                if (nextMixtureVector[i] < 0)
                {
                    state.Stop();
                    bad = true;
                }
            });
            if (!loopResult.IsCompleted || bad)
                //we ran simulation until nothing could be done anymore
                break;
            //TODO: calculate new temperature of the system

            //write new values back for next iteration.
            nextMixtureVector.CopyTo(mixtureVector);
            tookStep = true;
        }

        return tookStep;
    }
}