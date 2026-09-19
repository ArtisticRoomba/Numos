using System.Buffers;
using System.Numerics.Tensors;
using CommunityToolkit.HighPerformance.Helpers;
using Numos.CoreSim.GasReactions;

namespace Numos.CoreSim.Solvers;

internal class ReactionSolver : IAtmosSolverStage
{
    private readonly object _gasDataKey = new();
    private GasReactionData[] _gasRateOrder = [];
    private GasReactionData[] _gasReactionData = [];
    private GasReactionMatrix? _changesMatrix;
    private IAtmosConfig? _preparedConfig;

    public void Solve(AtmosSolverExecutionContext context)
    {
        int reactionCount = GasReactionConfig.Get(context.TickConfig).Count;

        //fast skip for empty configs.
        if (reactionCount == 0 || context.TickConfig.GasPropertyCount == 0)
            return;

        // Build once before workers start. Array indices retain the configured reaction order.
        if (_gasReactionData.Length != context.TickConfig.GasPropertyCount)
            _gasReactionData = new GasReactionData[context.TickConfig.GasPropertyCount];

        for (int gasId = 0; gasId < _gasReactionData.Length; gasId++)
        {
            int registeredGasId = gasId;
            _gasReactionData[gasId] = context.TickConfig.GetOrCreateGasSolverData(
                gasId,
                _gasDataKey,
                _ => CreateGasReactionData(context.TickConfig, registeredGasId));
        }

        _gasRateOrder = _gasReactionData.OrderBy(static gas => gas.GasName, StringComparer.Ordinal).ToArray();
        _changesMatrix = new GasReactionMatrix(_gasReactionData, reactionCount);
        _preparedConfig = context.TickConfig;
        try
        {
            ParallelHelper.ForEach<AtmosChunk, ProcessChunkAction>(
                context.Chunks,
                new ProcessChunkAction(this, context.TickConfig));
        }
        finally
        {
            _preparedConfig = null;
            _changesMatrix = null;
            Array.Clear(_gasReactionData);
            _gasRateOrder = [];
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

        // Registry compatibility is uniform across the chunk. Reject before any worker writes temperatures.
        for (int gas = 0; gas < chunk.ActiveGasCount; gas++)
        {
            if ((uint)chunk.ActiveGases[gas].GasId >= (uint)config.GasPropertyCount)
                return;
        }

        float[][] newMixtures = ArrayPool<float[]>.Shared.Rent(voxelCount);
        Scalar[][]? reactionFeedbacks = reactionCount == null ? null : ArrayPool<Scalar[]>.Shared.Rent(voxelCount);
        Kelvin[] newTemps = ArrayPool<Kelvin>.Shared.Rent(voxelCount);
        int mixtureLength = config.GasPropertyCount;
        ParallelHelper.For(
            0,
            voxelCount,
            new ProcessVoxelAction(
                this,
                chunk,
                deltaTime,
                config,
                reactionCount,
                reactionFeedbacks,
                newMixtures,
                newTemps,
                mixtureLength));

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
        ArrayPool<float[]>.Shared.Return(newMixtures, true);

        if (reactionCount != null && reactionFeedbacks != null)
        {
            for (int voxelIndex = 0; voxelIndex < voxelCount; voxelIndex++)
            {
                Scalar[] feedback = reactionFeedbacks[voxelIndex];
                for (int i = 0; i < reactionCount.Length; i++)
                {
                    reactionCount[i] += feedback[i];
                }

                ArrayPool<float>.Shared.Return(feedback);
            }

            ArrayPool<float[]>.Shared.Return(reactionFeedbacks, true);
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

    private static JoulePerKelvin ComputeHeatCapacity(Mole[] mixtureVector, int mixtureLength, IAtmosConfig config)
    {
        JoulePerKelvin result = 0f;
        for (int i = 0; i < mixtureLength; i++)
        {
            if (mixtureVector[i] == 0)
                continue;

            result += mixtureVector[i] * config.GetMolarHeatCapacityAtConstantVolume(i);
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
        var reactions = GasReactionConfig.Get(config);
        GasReactionData[] gasData;
        GasReactionData[] rateOrder;
        GasReactionMatrix changesMatrix;
        if (ReferenceEquals(_preparedConfig, config))
        {
            gasData = _gasReactionData;
            rateOrder = _gasRateOrder;
            changesMatrix = _changesMatrix!;
        }
        else
        {
            // Direct kernel callers can supply editable configurations, so their mappings cannot be cached.
            gasData = new GasReactionData[mixtureLength];
            for (int gasId = 0; gasId < mixtureLength; gasId++)
                gasData[gasId] = CreateGasReactionData(config, gasId);

            rateOrder = gasData.OrderBy(static gas => gas.GasName, StringComparer.Ordinal).ToArray();
            changesMatrix = new GasReactionMatrix(gasData, reactions.Count);
        }

        Joule energy = ExtractHeat(mixtureVector, ref currentTemperature, mixtureLength, config);
        //make sure in single step we dont overstep.

        //split our time interval into smaller steps.
        int reactionCount = reactions.Count;
        Scalar[] reactionSpeeds = ArrayPool<Scalar>.Shared.Rent(reactionCount);

        //prep array.
        Array.Clear(reactionSpeeds, 0, reactionCount);
        //get all reaction speeds. Sequential: reactionCount is typically a handful of items, far too
        //small to justify a parallel dispatch, especially one issued once per voxel from inside an
        //already-parallel voxel worker.
        Kelvin temperature = currentTemperature; //to stop warning about the later change of currentTemperature.
        for (int reactionId = 0; reactionId < reactionCount; reactionId++)
        {
            PerSecond rate = reactions.GetRateConstant(reactionId, temperature);
            if (!float.IsNormal(rate) || rate <= 0f)
                continue;

            // Preserve canonical gas-name and factor order independently of registry indices.
            foreach (var gas in rateOrder)
                rate = gas.ApplyRateFactors(reactionId, mixtureVector[gas.GasId], rate);

            Scalar speed = rate * deltaTime;
            if (speed > 0f)
                reactionSpeeds[reactionId] = speed;
        }

        bool anyReaction = false;
        for (int i = 0; i < reactionCount; i++)
        {
            if (reactionSpeeds[i] <= 0f)
                continue;

            anyReaction = true;
            break;
        }

        //check if there was even a reaction.
        if (!anyReaction)
        {
            ArrayPool<float>.Shared.Return(reactionSpeeds);
            return;
        }

        //adjusts reactions speed as to not consume our available material in a single step.
        // Bounded defensively: each pass either zeroes out a reaction's speed or converges, so this many
        // iterations is far more than any real mixture needs, but guarantees termination even in a
        // pathological state (e.g. moles already negative from upstream float drift) that would otherwise
        // never find scale > 0 and loop forever.
        int maxIterations = mixtureLength + reactionCount + 8;
        for (int iteration = 0; iteration < maxIterations; iteration++)
        {
            int criticalIndex = -1;
            Mole criticalValue = 0;
            Mole criticalConsumption = 0;
            //check which consumption might go over available material
            for (int i = 0; i < mixtureLength; i++)
            {
                //we calculate total gross consumption, ignoring any offsetting production of the same gas
                //by the same reaction (Changes is produced-consumed net, which would hide real overdraw).
                // Match Enumerable.Sum's double accumulator while preserving reaction order and float products.
                Mole64 accumulatedConsumption = 0d;
                for (int j = 0; j < reactionCount; j++)
                    accumulatedConsumption += gasData[i].Consumed[j] * reactionSpeeds[j];

                Mole consumption = (Mole)accumulatedConsumption;

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
                    if (!gasData[criticalIndex].Consumes[i])
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
                energyConsumption += MathF.Min(0, reactions.GetEnergyBalance(i)) * reactionSpeeds[i];
            }

            if (energy + energyConsumption >= 0)
                break;

            Scalar energyScale = MathF.Min(1f, MathF.Max(0f, energy / Math.Abs(energyConsumption)));
            for (int i = 0; i < reactionCount; i++)
            {
                if (reactions.GetEnergyBalance(i) >= 0)
                    continue;

                reactionSpeeds[i] = MathF.Min(reactionSpeeds[i], reactionSpeeds[i] * energyScale);
                if (reactionSpeeds[i] < 1e-10f)
                    reactionSpeeds[i] = 0;
            }
        }

        //apply mixture, this also includes heat, since all change equations contain energy balance.
        //dN/dt = S * reactionSpeeds: walk one reaction (matrix row) at a time and fold its whole gas
        //column into the mixture with one lane-independent vector add. This visits the exact same
        //(gas, reaction) terms in the exact same order as a per-gas accumulation would, so it is
        //bit-exact regardless of hardware SIMD width.
        Span<Mole> mixture = mixtureVector.AsSpan(0, mixtureLength);
        for (int j = 0; j < reactionCount; j++)
            TensorPrimitives.MultiplyAdd(changesMatrix.GetReactionRow(j), reactionSpeeds[j], mixture, mixture);

        for (int j = 0; j < reactionCount; j++)
        {
            energy += reactions.GetEnergyBalance(j) * reactionSpeeds[j];
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
        //adjust temperature based on heat value, using the post-reaction mixture's heat capacity
        //(composition changed above, so the capacity used to extract heat no longer applies).
        JoulePerKelvin heatCapacity = ComputeHeatCapacity(mixtureVector, mixtureLength, config);
        if (heatCapacity > 0f)
            currentTemperature = energy / heatCapacity;
        //cleanup speeds.
        ArrayPool<float>.Shared.Return(reactionSpeeds);
    }

    private static GasReactionData CreateGasReactionData(IAtmosConfig config, int gasId)
    {
        config.TryGetGasProperties(gasId, out var gas);
        return GasReactionConfig.Get(config).CreateGasData(gasId, gas);
    }

    private readonly struct ProcessChunkAction(
        ReactionSolver solver,
        IAtmosConfig config) : IInAction<AtmosChunk>
    {
        public void Invoke(in AtmosChunk chunk)
        {
            solver.ProcessChunk(chunk, AtmosSolverConstants.FixedTimeStep, config);
        }
    }

    private readonly struct ProcessVoxelAction(
        ReactionSolver solver,
        AtmosChunk chunk,
        Second deltaTime,
        IAtmosConfig config,
        Scalar[]? reactionCount,
        Scalar[][]? reactionFeedbacks,
        float[][] newMixtures,
        Kelvin[] newTemps,
        int mixtureLength) : IAction
    {
        public void Invoke(int voxelIndex)
        {
            Kelvin temp = chunk.Temperature[voxelIndex];
            Scalar[]? reactionFeedback =
                reactionCount == null ? null : ArrayPool<Scalar>.Shared.Rent(GasReactionConfig.Get(config).Count);

            if (reactionFeedback != null)
                Array.Clear(reactionFeedback, 0, reactionFeedback.Length);

            if (reactionFeedbacks != null && reactionFeedback != null)
                reactionFeedbacks[voxelIndex] = reactionFeedback;

            Mole[] mixtureVector = ArrayPool<Mole>.Shared.Rent(mixtureLength);
            Mole content = 0f;
            Array.Clear(mixtureVector, 0, mixtureLength);

            for (int i = 0; i < chunk.ActiveGasCount; i++)
            {
                mixtureVector[chunk.ActiveGases[i].GasId] = chunk.ActiveGases[i].Moles[voxelIndex];
                content += chunk.ActiveGases[i].Moles[voxelIndex];
            }

            newMixtures[voxelIndex] = mixtureVector;
            newTemps[voxelIndex] = temp;
            if (content <= 0.0001)
                return;

            solver.ProcessVoxel(deltaTime, mixtureVector, ref temp, reactionFeedback, config, mixtureLength);

            chunk.Temperature[voxelIndex] = temp;
            newTemps[voxelIndex] = temp;
        }
    }
}