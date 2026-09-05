using System.Buffers;

namespace Numos.CoreSim.Solvers;

internal class ReactionSolver : IAtmosSolverStage
{
    public void Solve(AtmosSolverExecutionContext context)
    {
        //fast skip for empty configs.
        if (context.TickConfig.GasReactionCount == 0 || context.TickConfig.GasPropertyCount == 0)
            return;

        Parallel.ForEach(context.Chunks, chunk => ProcessChunk(chunk, AtmosSolverConstants.FixedTimeStep, context.TickConfig));

        //foreach (var chunk in context.Chunks)
        {
            //    ProcessChunk(chunk, AtmosSolverConstants.FixedTimeStep, context.TickConfig, null);
        }
    }

    /// <summary>
    ///     wrapping core solver into a chunk.
    /// </summary>
    /// <param name="chunk"></param>
    /// <param name="deltaTime">Over which timespan reactions should occur.</param>
    /// <param name="config"></param>
    /// <param name="reactionCount"></param>
    internal void ProcessChunk(AtmosChunk chunk, Second deltaTime, IAtmosConfig config, Scalar[]? reactionCount = null)
    {
        //process each voxel in parallel
        int voxelCount = chunk.VoxelCount;

        float[][] newMixtures = ArrayPool<float[]>.Shared.Rent(voxelCount);
        Scalar[][]? reactionFeedbacks = reactionCount == null ? null : ArrayPool<Scalar[]>.Shared.Rent(voxelCount);
        Kelvin[] newTemps = ArrayPool<Kelvin>.Shared.Rent(voxelCount);
        int mixtureLength = config.GasPropertyCount;
        bool badGas = false;
        Parallel.For(
            0,
            voxelCount,
            voxelIndex =>
                //  for (var voxelIndex = 0; voxelIndex < voxelCount; voxelIndex++)
            {
                Kelvin temp = chunk.Temperature[voxelIndex];
                Scalar[]? reactionFeedback = reactionCount == null ? null : ArrayPool<Scalar>.Shared.Rent(config.GasReactionCount);
                if (reactionFeedback != null)
                    Array.Clear(reactionFeedback, 0, reactionFeedback.Length);

                if (reactionFeedbacks != null && reactionFeedback != null)
                    reactionFeedbacks[voxelIndex] = reactionFeedback;

                // get the mixture
                Mole[] mixtureVector = ArrayPool<Mole>.Shared.Rent(mixtureLength);
                Mole content = 0f;
                Array.Clear(mixtureVector, 0, mixtureLength);

                for (int i = 0; i < chunk.ActiveGasCount; i++)
                {
                    //check if gas id is outside of config.
                    if (chunk.ActiveGases[i].GasId >= config.GasPropertyCount || chunk.ActiveGases[i].GasId < 0)
                    {
                        badGas = true;
                        break;
                    }

                    mixtureVector[chunk.ActiveGases[i].GasId] = chunk.ActiveGases[i].Moles[voxelIndex];
                    content += chunk.ActiveGases[i].Moles[voxelIndex];
                }

                newMixtures[voxelIndex] = mixtureVector;
                newTemps[voxelIndex] = temp;
                if (content <= 0.0001 || badGas)
                    return;

                //continue;

                // do actual evaluation of the mixture for reactions.
                ProcessVoxel(deltaTime, mixtureVector, ref temp, reactionFeedback, config, mixtureLength);

                // adjust temperature of the voxel.
                chunk.Temperature[voxelIndex] = temp;
                newTemps[voxelIndex] = temp;
            });

        //do not process bad gas chunk.
        if (badGas)
            return;

        //put data back in a single thread.
        for (ushort voxelIndex = 0; voxelIndex < voxelCount; voxelIndex++)
        {
            Mole[] mixtureVector = newMixtures[voxelIndex];
            int c = chunk.ActiveGasCount;
            //adjust moles from the mixture vector
            foreach (var gasChannel in chunk.ActiveGases.Take(c))
            {
                Mole diff = mixtureVector[gasChannel.GasId] - gasChannel.Moles[voxelIndex];
                if (diff > 0)
                {
                    chunk.InjectGasToVoxel(
                        voxelIndex,
                        gasChannel.GasId,
                        diff,
                        newTemps[voxelIndex],
                        config.GetMolarHeatCapacityAtConstantVolume(gasChannel.GasId),
                        config.PressurePerMoleKelvin);

                    //fix rounding errors causing bad value.
                    if (gasChannel.Moles[voxelIndex] < 1e-10)
                        gasChannel.Moles[voxelIndex] = 0;
                }

                //set gas to 0.
                mixtureVector[gasChannel.GasId] = 0;
            }

            //inject remaining gases
            for (int i = 0; i < mixtureLength; i++)
            {
                if (mixtureVector[i] <= 0) continue;

                chunk.InjectGasToVoxel(
                    voxelIndex,
                    i,
                    mixtureVector[i],
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
            foreach (Scalar[] feedback in reactionFeedbacks)
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

    private Joule ExtractHeat(Mole[] mixtureVector, ref readonly Kelvin temperature, int mixtureLength, IAtmosConfig config)
    {
        Joule result = 0f;
        // Use specific heat capacity of each gas to calculate the necessary energy to keep at temperature
        for (int i = 0; i < mixtureLength; i++)
        {
            if (mixtureVector[i] == 0)
                continue;

            // multiply by mole amounts
            //sum together
            result += mixtureVector[i] * temperature * config.GetMolarHeatCapacityAtConstantVolume(i);
        }

        return result;
    }

    /// <summary>
    ///     Core solver.
    /// </summary>
    /// <param name="deltaTime"></param>
    /// <param name="mixtureVector">a vector describing molarity of the mixture (mol/l)</param>
    /// <param name="currentTemperature">temperature, which will be adjusted to reflect final temperature of the mixture</param>
    /// <param name="reactionFeedback">optional array to which we write how often each reaction occured, index = reaction id</param>
    /// <param name="config"></param>
    /// <param name="mixtureLength"></param>
    internal void ProcessVoxel(
        Second deltaTime, Mole[] mixtureVector, ref Kelvin currentTemperature,
        Scalar[]? reactionFeedback, IAtmosConfig config, int mixtureLength)
    {
        Joule energy = ExtractHeat(mixtureVector, ref currentTemperature, mixtureLength, config);
        //make sure in single step we dont overstep.

        //split our time interval into smaller steps.
        int reactionCount = config.GasReactionCount;
        Scalar[] reactionSpeeds = ArrayPool<Scalar>.Shared.Rent(reactionCount);

        //prep array.
        Array.Clear(reactionSpeeds, 0, reactionCount);
        //get all reaction speeds
        bool anyReaction = false;

        Kelvin temperature = currentTemperature; //to stop warning about the later change of currentTemperature.
        Parallel.For(
            0,
            reactionCount,
            i =>
            {
                if (!config.TryGetGasReaction(i, out var e))
                    return;

                Scalar speed = e.GetReactionSpeed(mixtureVector, temperature) * deltaTime;
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
            int criticalIndex = -1;
            Mole criticalValue = 0;
            Mole criticalConsumption = 0;
            //check which consumption might go over available material
            for (int i = 0; i < mixtureLength; i++)
            {
                //we calculate total consumption, ignoring production by reactions.
                Mole consumption = Enumerable.Range(0, config.GasReactionCount).Select(e =>
                {
                    config.TryGetGasReaction(e, out var reaction);
                    return reaction!;
                }).Select((e, j) =>
                    MathF.Min(0, e.ChangeEquation.GetValueOrDefault(i)) * reactionSpeeds[j]).Sum();

                //check how much moles/joules are left over after all reactions.
                Mole postReactionMoles = mixtureVector[i] + consumption;
                //if negative and even more critical, mark.
                if (postReactionMoles < criticalValue)
                {
                    criticalValue = postReactionMoles;
                    criticalConsumption = consumption;
                    criticalIndex = i;
                }
            }

            if (criticalIndex != -1)
            {
                // adjust reaction speeds so our post reaction moles are 0.
                // our equation we try to optimize looks like this (((change * speed)/total change in scaled reaction)*available moles)/(change * speed) = (available volume)/(total change in scaled reaction)
                // so we calculate the scale, to scale reaction speeds down to balance the equation of consumption.
                Scalar scale = MathF.Min(
                    1f,
                    MathF.Max(0f, mixtureVector[criticalIndex] / Math.Abs(criticalConsumption)));

                for (int i = 0; i < reactionCount; i++)
                {
                    if (!config.TryGetGasReaction(i, out var reaction) ||
                        !reaction.ChangeEquation.ContainsKey(criticalIndex))
                        continue;

                    //select the lower reaction speed.
                    reactionSpeeds[i] = MathF.Min(reactionSpeeds[i], reactionSpeeds[i] * scale);
                    if (reactionSpeeds[i] < 1e-10f)
                        reactionSpeeds[i] = 0;
                }

                continue;
            }

            Joule energyConsumption = 0f;
            for (int i = 0; i < reactionCount; i++)
            {
                config.TryGetGasReaction(i, out var reaction);
                energyConsumption += MathF.Min(0, reaction!.EnergyBalance) * reactionSpeeds[i];
            }

            if (energy + energyConsumption >= 0)
                break;

            Scalar energyScale = MathF.Min(1f, MathF.Max(0f, energy / Math.Abs(energyConsumption)));
            for (int i = 0; i < reactionCount; i++)
            {
                if (!config.TryGetGasReaction(i, out var reaction) || reaction.EnergyBalance >= 0)
                    continue;

                reactionSpeeds[i] = MathF.Min(reactionSpeeds[i], reactionSpeeds[i] * energyScale);
                if (reactionSpeeds[i] < 1e-10f)
                    reactionSpeeds[i] = 0;
            }
        }

        //apply mixture, this also includes heat, since all change equations contain energy balance.
        for (int i = 0; i < mixtureLength; i++)
        {
            for (int j = 0; j < reactionCount; j++)
            {
                config.TryGetGasReaction(j, out var reaction);
                mixtureVector[i] += reaction!.ChangeEquation.GetValueOrDefault(i) * reactionSpeeds[j];
            }
        }

        for (int j = 0; j < reactionCount; j++)
        {
            config.TryGetGasReaction(j, out var reaction);
            energy += reaction!.EnergyBalance * reactionSpeeds[j];
        }

        if (reactionFeedback != null)
        {
            //report feedback
            for (int j = 0; j < reactionCount; j++)
            {
                reactionFeedback[j] += reactionSpeeds[j];
            }
        }

        energy = Math.Max(0, energy);
        //adjust temperature based on heat value.
        currentTemperature = UpdateTemperature(energy);
        //cleanup speeds.
        ArrayPool<float>.Shared.Return(reactionSpeeds);
    }

    private Kelvin UpdateTemperature(Joule totalKineticEnergy)
    {
        const JoulePerKelvin constantHelper = 3 * AtmosPhysicalConstants.BoltzmannConstant;
        // KE = (3/2) k * T <- see  Kinetic Molecular Theory. k is boltzmann constant, KE is kinetic energy.
        // Solving for T we get:
        // (KE * 2 )/3k = T
        return totalKineticEnergy * 2 / constantHelper;
    }
}