using System.Buffers;
using Numos.CoreSim.GasReactions;

namespace Numos.CoreSim.Solvers;

internal class ReactionSolver : IAtmosSolverStage
{
    public void Solve(AtmosSolverExecutionContext context)
    {
        Parallel.ForEach(context.Chunks, (chunk) => ProcessChunk(chunk, AtmosSolverConstants.FixedTimeStep, context.TickConfig, null));
    }

    /// <summary>
    /// wrapping core solver into a chunk.
    /// </summary>
    /// <param name="chunk"></param>
    /// <param name="deltaTime">Over which timespan reactions should occur.</param>
    /// <param name="config"></param>
    /// <param name="reactionCount"></param>
    internal void ProcessChunk(AtmosChunk chunk, float deltaTime, IAtmosConfig config, float[]? reactionCount = null)
    {
        //process each voxel in parallel
        int voxelCount = chunk.Depth * chunk.Width * chunk.Height;

        var newMixtures = ArrayPool<float[]>.Shared.Rent(voxelCount);
        var reactionFeedbacks = reactionCount == null ? null : ArrayPool<float[]>.Shared.Rent(voxelCount);
        var newTemps = ArrayPool<float>.Shared.Rent(voxelCount);
        var mixtureLength = config.GasPropertyCount + 1;

        Parallel.For(
            0,
            voxelCount,
            (int voxelIndex) =>
                //   for (var voxelIndex = 0; voxelIndex < voxelCount; voxelIndex++)
            {
                var temp = chunk.Temperature[voxelIndex];
                var reactionFeedback = reactionCount == null ? null : ArrayPool<Scalar>.Shared.Rent(config.GasReactionCount);
                if (reactionFeedback != null)
                    Array.Clear(reactionFeedback, 0, reactionFeedback.Length);

                if (reactionFeedbacks != null && reactionFeedback != null)
                    reactionFeedbacks[voxelIndex] = reactionFeedback;

                // get the mixture
                var mixtureVector = ArrayPool<Mole>.Shared.Rent(mixtureLength);
                var content = 0f;
                Array.Clear(mixtureVector, 0, mixtureLength);
                for (var i = 0; i < chunk.ActiveGasCount; i++)
                {
                    mixtureVector[chunk.ActiveGases[i].GasId] = chunk.ActiveGases[i].Moles[voxelIndex] * config.VoxelVolume;
                    content += chunk.ActiveGases[i].Moles[voxelIndex];
                }

                newMixtures[voxelIndex] = mixtureVector;
                newTemps[voxelIndex] = temp;
                if (content <= 0.0001)
                    return;

                //continue;
                // do actual evaluation of the mixture for reactions.
                ProcessVoxel(deltaTime, mixtureVector, ref temp, reactionFeedback, config);

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
                var diff = (mixtureVector[gasChannel.GasId] / config.VoxelVolume) - gasChannel.Moles[voxelIndex];
                if (diff != 0)
                {
                    chunk.InjectGasToVoxel(
                        voxelIndex,
                        gasChannel.GasId,
                        diff,
                        newTemps[voxelIndex],
                        config.GetMolarHeatCapacityAtConstantVolume(gasChannel.GasId),
                        config.PressurePerMoleKelvin);
                }

                //set gas to 0.
                mixtureVector[gasChannel.GasId] = 0;
            }

            //inject remaining gases
            for (var i = 0; i < mixtureLength - 1; i++)
            {
                if (mixtureVector[i] <= 0) continue;

                chunk.InjectGasToVoxel(
                    voxelIndex,
                    i,
                    mixtureVector[i]/config.VoxelVolume,
                    newTemps[voxelIndex],
                    config.GetMolarHeatCapacityAtConstantVolume(i),
                    config.PressurePerMoleKelvin);
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

    private void ExtractHeat(float[] mixtureVector, ref readonly Kelvin temperature, int mixtureLength, IAtmosConfig config)
    {
        var result = 0f;
        // Use specific heat capacity of each gas to calculate the necessary energy to keep at temperature
        for (var i = 0; i < mixtureLength; i++)
        {
            if (mixtureVector[i] == 0)
                continue;

            // multiply by mole amounts
            //sum together
            result += mixtureVector[i] * temperature * config.GetMolarHeatCapacityAtConstantVolume(i);
        }

        mixtureVector[mixtureLength] = result;
    }

    /// <summary>
    /// Core solver.
    /// </summary>
    /// <param name="deltaTime"></param>
    /// <param name="mixtureVector">a vector describing molarity of the mixture (mol/l)</param>
    /// <param name="currentTemperature">temperature, which will be adjusted to reflect final temperature of the mixture</param>
    /// <param name="reactionFeedback">optional array to which we write how often each reaction occured, index = reaction id</param>
    /// <param name="config"></param>
    internal void ProcessVoxel(
        Second deltaTime, float[] mixtureVector, ref Kelvin currentTemperature,
        float[]? reactionFeedback, IAtmosConfig config)
    {
        int mixtureLength = config.GasPropertyCount;
        ExtractHeat(mixtureVector, ref currentTemperature, mixtureLength, config);
        //make sure in single step we dont overstep.

        //split our time interval into smaller steps.
        var reactionCount = config.GasReactionCount;
        var reactionSpeeds = ArrayPool<float>.Shared.Rent(reactionCount);

        //prep array.
        Array.Clear(reactionSpeeds, 0, reactionCount);
        //get all reaction speeds
        bool anyReaction = false;

        var temperature = currentTemperature; //to stop warning about the later change of currentTemperature.
        Parallel.For(
            0,
            reactionCount,
            (int i) =>
            {
                if (!config.TryGetGasReaction(i, out var e))
                    return;

                var speed = e.GetReactionSpeed(mixtureVector, temperature) * deltaTime;
                if (speed <= 0)
                    return;

                reactionSpeeds[i] = speed;
                anyReaction = true;
            });

        //check if there was even a reaction.
        if (!anyReaction)
            return;

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
                var consumption = Enumerable.Range(0, config.GasReactionCount).Select(e =>
                {
                    config.TryGetGasReaction(e, out var reaction);
                    return reaction!;
                }).Select((e, j) =>
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
                if ((!config.TryGetGasReaction(i, out var reaction)) || !reaction.ChangeEquation.ContainsKey(criticalIndex))
                    continue;

                //select the lower reaction speed.
                reactionSpeeds[i] = MathF.Min(reactionSpeeds[i], reactionSpeeds[i] * scale);
            }
        }

        //apply mixture.
        for (var i = 0; i < mixtureLength; i++)
        {
            for (var j = 0; j < reactionCount; j++)
            {
                config.TryGetGasReaction(i, out var reaction);
                mixtureVector[i] += reaction!.ChangeEquation.GetValueOrDefault(i) * reactionSpeeds[j];
            }
        }

        if (reactionFeedback != null)
        {
            //calculate next heat value and report feedback
            for (var j = 0; j < reactionCount; j++)
            {
                config.TryGetGasReaction(j, out var reaction);
                mixtureVector[mixtureLength] += reaction!.EnergyBalance * reactionSpeeds[j];
                reactionFeedback[j] += reactionSpeeds[j];
            }
        }
        else
        {
            //calculate next heat value
            for (var j = 0; j < reactionCount; j++)
            {
                config.TryGetGasReaction(j, out var reaction);
                mixtureVector[mixtureLength] += reaction!.EnergyBalance * reactionSpeeds[j];
            }
        }

        mixtureVector[mixtureLength] = Math.Max(0, mixtureVector[mixtureLength]);
        //adjust temperature based on heat value.
        currentTemperature = UpdateTemperature(mixtureVector);
        //cleanup speeds.
        ArrayPool<float>.Shared.Return(reactionSpeeds);
    }

    private Kelvin UpdateTemperature(float[] mixtureVector)
    {
        const float constantHelper = 3 * AtmosPhysicalConstants.BoltzmannConstant;
        var totalKineticEnergy = mixtureVector[^1];
        // KE = (3/2) k * T <- see  Kinetic Molecular Theory. k is boltzmann constant, KE is kinetic energy.
        // Solving for T we get:
        // (KE * 2 )/3k = T
        return (totalKineticEnergy * 2) / constantHelper;
    }
}