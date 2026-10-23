using System.Buffers;
using System.Diagnostics;
using System.Numerics.Tensors;
using CommunityToolkit.HighPerformance.Helpers;
using Numos.CoreSim.GasReactions;

namespace Numos.CoreSim.Solvers;

internal class ReactionSolver : IAtmosSolverStage
{
    // Rates that overflow (trace gases with negative exponents) saturate here instead of being dropped.
    // The material limiter then scales them back to what is available.
    // The value must stay below float.MaxValue so the stored speed is finite.
    // Only reactions with a consumed input saturate, because nothing else would limit them.
    private const double MaxReactionSpeed = 1e30;

    // ProcessVoxel runs once per voxel from an already-parallel worker (thousands of calls per chunk per tick).
    // reactionCount is typically a handful of entries.
    // Renting that tiny array from ArrayPool<T>.Shared on every call adds pool bookkeeping to the hottest loop.
    // No other thread ever sees the buffer, so the pool gives no benefit.
    // A per-thread scratch buffer that grows to the high-water mark removes the round trip.
    [ThreadStatic]
    private static Mole[]? _reactionSpeedsScratch;

    public void Solve(AtmosSolverExecutionContext context)
    {
        // Fast skip for empty configs.
        if (GasReactionConfig.Get(context.TickConfig).Count == 0 || context.TickConfig.GasPropertyCount == 0)
            return;

        var tables = context.TickConfig.GetReactionTables();

        // Tells ProcessChunk whether this per-chunk dispatch already saturates the worker pool.
        // This mirrors the awakeChunkCount gate in ThermodynamicsSolver and PhaseChangeSolver.
        // A single dense chunk only has per-voxel work to offer, so its write-back pass needs its own parallelism.
        // Once enough awake chunks occupy every worker, a second layer of parallelism inside each chunk is overhead.
        int awakeChunkCount = 0;
        for (int i = 0; i < context.Chunks.Length; i++)
        {
            if (context.Chunks[i].IsAwake && context.Chunks[i].ActiveAirCount > 0)
                awakeChunkCount++;
        }

        ParallelHelper.ForEach<AtmosChunk, ProcessChunkAction>(
            context.Chunks,
            new ProcessChunkAction(this, context.TickConfig, tables, awakeChunkCount));
    }

    private static Mole[] RentReactionSpeeds(int reactionCount)
    {
        Mole[]? scratch = _reactionSpeedsScratch;
        if (scratch == null || scratch.Length < reactionCount)
        {
            scratch = new Mole[Math.Max(reactionCount, 16)];
            _reactionSpeedsScratch = scratch;
        }

        return scratch;
    }

    /// <summary>
    ///     Runs the reaction solver over every active-air voxel in one chunk and writes the results back.
    /// </summary>
    /// <param name="chunk">The chunk whose voxels should react.</param>
    /// <param name="deltaTime">Over which timespan reactions should occur.</param>
    /// <param name="config">The gas registry and reaction definitions to react against.</param>
    /// <param name="tables">The reaction tables built from <paramref name="config" />.</param>
    /// <param name="reactionCount">
    ///     Optional accumulator, index = reaction ID, incremented by how many times each reaction fired
    ///     across the whole chunk. Pass null to skip collecting this.
    /// </param>
    /// <param name="awakeChunkCount">
    ///     How many awake, gas-bearing chunks <see cref="Solve" /> is dispatching this tick.
    ///     Below worker count, this chunk's write-back pass parallelizes across voxels to help fill idle workers.
    ///     At or above it, the outer per-chunk dispatch already saturates the pool, so the pass runs sequentially.
    ///     Defaults to 1 (parallelize) for callers processing a single chunk directly.
    /// </param>
    internal void ProcessChunk(
        AtmosChunk chunk, Second deltaTime, IAtmosConfig config, ReactionTables tables, Mole[]? reactionCount = null,
        int awakeChunkCount = 1)
    {
        Debug.Assert(tables.GasData.Length == config.GasPropertyCount, "Reaction tables were built from another config.");

        // Restricted to ActiveAirIndices, like ThermalDiffusionSolver and PhaseChangeSolver.
        int activeAirCount = chunk.ActiveAirCount;
        if (activeAirCount == 0)
            return;

        // Registry compatibility is uniform across the chunk. Reject before any worker writes temperatures.
        for (int gas = 0; gas < chunk.ActiveGasCount; gas++)
        {
            if ((uint)chunk.ActiveGases[gas].GasId >= (uint)config.GasPropertyCount)
                return;
        }

        int mixtureLength = config.GasPropertyCount;
        CubicMetre volume = config.GetVoxelVolume();

        // One chunk-wide voxel-by-gas matrix replaces a separately pooled array per voxel.
        // Like the reaction matrix, it slices a shared buffer instead of renting per item.
        // This collapses activeAirCount pool round trips into one.
        Mole[] mixtures = ArrayPool<Mole>.Shared.Rent(checked(activeAirCount * mixtureLength));
        Mole[][]? reactionFeedbacks = reactionCount == null ? null : ArrayPool<Mole[]>.Shared.Rent(activeAirCount);
        Kelvin[] newTemps = ArrayPool<Kelvin>.Shared.Rent(activeAirCount);
        ParallelHelper.For(
            0,
            activeAirCount,
            new ProcessVoxelAction(
                chunk,
                deltaTime,
                volume,
                config,
                reactionCount,
                reactionFeedbacks,
                mixtures,
                newTemps,
                mixtureLength,
                tables));

        // Put data back. This O(activeAirCount * mixtureLength) pass does not shrink with worker count.
        // Run serially, it caps scaling for a dense chunk that is the only source of parallel work
        // (see DenseChunkReactionParallelScalingBenchmarks).
        // Each voxel's diffing and injection touch only its own slot in every per-gas array, so voxels are independent.
        // Two chunk-shared side effects of InjectGasToVoxel are the exception.
        // GetOrCreateGasChannel creates a channel on first use (mutates ActiveGases/ActiveGasCount).
        // Wake() rebuilds ActiveAirIndices.
        // The parallel branch only runs on an awake chunk, so Wake() cannot fire.
        // The branch also creates every missing channel serially before any worker touches a voxel.
        int workerCount = Math.Max(1, Environment.ProcessorCount);
        if (awakeChunkCount < workerCount && chunk.IsAwake)
        {
            // Same discovery order as the serial branch below (voxel-major, then ascending gas id).
            // The chunk's channel-append order then matches the serial path exactly.
            // So does the per-voxel summation order InjectGasToVoxel uses when it recomputes total moles.
            for (int activeIndex = 0; activeIndex < activeAirCount && chunk.ActiveGasCount < mixtureLength; activeIndex++)
            {
                int offset = activeIndex * mixtureLength;
                for (int gasId = 0; gasId < mixtureLength; gasId++)
                {
                    if (mixtures[offset + gasId] > 0f)
                        chunk.GetOrCreateGasChannel(gasId);
                }
            }

            int trackedChannels = chunk.ActiveGasCount;
            ParallelHelper.For(
                0,
                activeAirCount,
                new WriteBackAction(chunk, mixtures, newTemps, mixtureLength, config, trackedChannels));
        }
        else
        {
            for (int activeIndex = 0; activeIndex < activeAirCount; activeIndex++)
            {
                ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
                Span<Mole> mixtureVector = mixtures.AsSpan(activeIndex * mixtureLength, mixtureLength);
                int c = chunk.ActiveGasCount;
                // Adjust moles from the mixture vector.
                foreach (var gasChannel in chunk.ActiveGases.Take(c))
                {
                    Mole diff = mixtureVector[gasChannel.GasId] - gasChannel.Moles[voxelIndex];
                    if (diff != 0)
                    {
                        chunk.InjectGasToVoxel(
                            voxelIndex,
                            gasChannel.GasId,
                            diff,
                            newTemps[activeIndex],
                            config.GetMolarHeatCapacityAtConstantVolume(gasChannel.GasId),
                            config.PressurePerMoleKelvin);

                        // Fix rounding errors that cause bad values.
                        if (gasChannel.Moles[voxelIndex] < 1e-10)
                            gasChannel.Moles[voxelIndex] = 0;
                    }

                    // Set gas to 0.
                    mixtureVector[gasChannel.GasId] = 0;
                }

                // Inject remaining gases.
                for (int i = 0; i < mixtureLength; i++)
                {
                    if (mixtureVector[i] <= 0) continue;

                    chunk.InjectGasToVoxel(
                        voxelIndex,
                        i,
                        mixtureVector[i],
                        newTemps[activeIndex],
                        config.GetMolarHeatCapacityAtConstantVolume(i),
                        config.PressurePerMoleKelvin);
                }
            }
        }

        ArrayPool<float>.Shared.Return(newTemps);
        ArrayPool<float>.Shared.Return(mixtures);

        if (reactionCount != null && reactionFeedbacks != null)
        {
            for (int activeIndex = 0; activeIndex < activeAirCount; activeIndex++)
            {
                Mole[] feedback = reactionFeedbacks[activeIndex];
                for (int i = 0; i < reactionCount.Length; i++)
                {
                    reactionCount[i] += feedback[i];
                }

                ArrayPool<float>.Shared.Return(feedback);
            }

            ArrayPool<float[]>.Shared.Return(reactionFeedbacks, true);
        }
    }

    // Sensible energy of the mixture at its current temperature: sum of moles * Cv * T over gases.
    private static Joule ExtractHeat(
        ReadOnlySpan<Mole> mixtureVector, ref readonly Kelvin temperature, int mixtureLength, IAtmosConfig config)
    {
        Joule result = 0f;
        for (int i = 0; i < mixtureLength; i++)
        {
            if (mixtureVector[i] == 0)
                continue;

            result += mixtureVector[i] * temperature * config.GetMolarHeatCapacityAtConstantVolume(i);
        }

        return result;
    }

    // Same per-gas sum as ExtractHeat, without the temperature factor.
    // Used after the mixture's composition has changed, to convert the unchanged total energy back into a temperature.
    private static JoulePerKelvin ComputeHeatCapacity(ReadOnlySpan<Mole> mixtureVector, int mixtureLength, IAtmosConfig config)
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
    ///     Reacts one voxel's gas mixture over <paramref name="deltaTime" />, in place.
    /// </summary>
    /// <param name="deltaTime">Over which timespan reactions should occur.</param>
    /// <param name="mixtureVector">
    ///     The voxel's mole amount for every registered gas, indexed by gas ID.
    ///     The method updates it in place to reflect the reactions that occurred.
    /// </param>
    /// <param name="currentTemperature">Temperature, updated in place to reflect the post-reaction mixture.</param>
    /// <param name="reactionFeedback">
    ///     Optional accumulator, indexed by reaction ID, incremented by how many times each reaction fired.
    /// </param>
    /// <param name="config">The gas registry and reaction definitions to react against.</param>
    /// <param name="mixtureLength">The number of registered gases, i.e. the valid length of <paramref name="mixtureVector" />.</param>
    internal void ProcessVoxel(
        Second deltaTime, Span<Mole> mixtureVector, ref Kelvin currentTemperature,
        Mole[]? reactionFeedback, IAtmosConfig config, int mixtureLength)
    {
        ProcessMixtureCore(
            deltaTime,
            mixtureVector,
            ref currentTemperature,
            config.GetVoxelVolume(),
            reactionFeedback,
            config,
            mixtureLength,
            ReactionTables.Create(config));
    }

    /// <summary>
    ///     Does the actual per-voxel reaction work against prebuilt <paramref name="tables" />.
    /// </summary>
    private static void ProcessMixtureCore(
        Second deltaTime, Span<Mole> mixtureVector, ref Kelvin currentTemperature, CubicMetre volume,
        Mole[]? reactionFeedback, IAtmosConfig config, int mixtureLength, ReactionTables tables)
    {
        var reactions = tables.Reactions;
        GasReactionData[] gasData = tables.GasData;

        // GetVoxelVolume always returns a finite positive volume, so only the timestep needs checking.
        if (!float.IsFinite(deltaTime))
            return;

        Joule energy = ExtractHeat(mixtureVector, ref currentTemperature, mixtureLength, config);
        int reactionCount = reactions.Count;
        Mole[] reactionSpeeds = RentReactionSpeeds(reactionCount);

        Array.Clear(reactionSpeeds, 0, reactionCount);
        // Get all reaction speeds. This is sequential.
        // reactionCount is typically a handful of items, too few to justify a parallel dispatch.
        // That holds especially for one issued once per voxel from inside an already-parallel voxel worker.
        Kelvin temperature = currentTemperature; //to stop warning about the later change of currentTemperature.
        for (int reactionId = 0; reactionId < reactionCount; reactionId++)
        {
            ConcentrationPerSecond rate = reactions.GetRateConstant(reactionId, temperature);
            if (!float.IsNormal(rate) || rate <= 0f)
                continue;

            // Preserve canonical gas-name and factor order independently of registry indices.
            ConcentrationPerSecond64 rate64 = rate;
            foreach (var gas in tables.RateOrder)
                rate64 = gas.ApplyRateFactors(reactionId, (Mole64)mixtureVector[gas.GasId] / volume, rate64);

            // +inf is a legitimate overflow here and saturates below.
            Mole64 speed = rate64 * deltaTime * volume;
            if (double.IsNaN(speed) || speed <= 0.0)
                continue;

            if (speed > MaxReactionSpeed)
            {
                if (!ConsumesAnything(gasData, reactionId))
                    continue;

                speed = MaxReactionSpeed;
            }

            reactionSpeeds[reactionId] = (Mole)speed;
        }

        bool anyReaction = false;
        for (int i = 0; i < reactionCount; i++)
        {
            if (reactionSpeeds[i] <= 0f)
                continue;

            anyReaction = true;
            break;
        }

        if (!anyReaction)
        {
            return;
        }

        // Adjust reaction speeds so they do not consume more material than is available in a single step.
        // The loop is bounded. Each pass either zeroes out a reaction's speed or converges.
        // This many iterations is far more than any real mixture needs.
        // It still guarantees termination in a pathological state, for example moles already negative from upstream
        // float drift, which would otherwise never find scale > 0 and loop forever.
        int maxIterations = mixtureLength + reactionCount + 8;
        bool converged = false;
        for (int iteration = 0; iteration < maxIterations; iteration++)
        {
            int criticalIndex = -1;
            Mole criticalValue = 0;
            Mole criticalConsumption = 0;
            // Check which consumption might go over available material.
            for (int i = 0; i < mixtureLength; i++)
            {
                // Total gross consumption ignores any production of the same gas by the same reaction.
                // Changes is the produced-minus-consumed net, which would hide a real overdraw.
                // Only reactions that consume this gas can contribute.
                // ConsumingReactions already excludes every zero term a dense reactionCount scan would add.
                // Match Enumerable.Sum's double accumulator while preserving reaction order and float products.
                Mole64 accumulatedConsumption = 0d;
                foreach (var (reactionId, consumed) in gasData[i].ConsumingReactions)
                    accumulatedConsumption += consumed * reactionSpeeds[reactionId];

                Mole consumption = (Mole)accumulatedConsumption;

                // Check how many moles or joules are left after all reactions.
                Mole postReactionMoles = mixtureVector[i] + consumption;
                // If negative and more critical than the current worst, mark it.
                if (postReactionMoles < criticalValue)
                {
                    criticalValue = postReactionMoles;
                    criticalConsumption = consumption;
                    criticalIndex = i;
                }
            }

            if (criticalIndex != -1)
            {
                // Scale every reaction consuming the critical gas down by the same factor.
                // The factor puts its post-reaction moles at exactly 0: scale = available / |consumption|.
                // It is clamped to [0, 1], so a gas with moles to spare never speeds up.
                Scalar scale = MathF.Min(
                    1f,
                    MathF.Max(0f, mixtureVector[criticalIndex] / Math.Abs(criticalConsumption)));

                foreach (var (reactionId, _) in gasData[criticalIndex].ConsumingReactions)
                {
                    // Select the lower reaction speed.
                    reactionSpeeds[reactionId] = MathF.Min(reactionSpeeds[reactionId], reactionSpeeds[reactionId] * scale);
                    if (reactionSpeeds[reactionId] < 1e-10f)
                        reactionSpeeds[reactionId] = 0;
                }

                continue;
            }

            // Material is no longer overdrawn. Heat gets the same treatment.
            // Endothermic reactions (negative energy balance) cannot draw more energy than the mixture has.
            Joule energyConsumption = 0f;
            for (int i = 0; i < reactionCount; i++)
            {
                energyConsumption += MathF.Min(0, reactions.GetEnergyBalance(i)) * reactionSpeeds[i];
            }

            if (energy + energyConsumption >= 0)
            {
                converged = true;
                break;
            }

            // Scale down only the endothermic reactions, with the same "clamp to exactly enough" shape as the
            // material-limiter scale above.
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

        if (!converged)
        {
            // The loop did not reach a non-overdrawn state within budget.
            // Skip this voxel's reactions this tick rather than risk overdraw. The next tick retries.
            Debug.Assert(false, "Reaction material/energy limiter failed to converge within its iteration budget.");
            return;
        }

        // Apply the mixture change. This includes heat, since all change equations contain the energy balance.
        // dN/dt = S * reactionSpeeds. Walk one reaction (matrix row) at a time.
        // Fold its whole gas column into the mixture with one lane-independent vector add.
        // This visits the same (gas, reaction) terms in the same order as a per-gas accumulation.
        // The result is bit-exact regardless of hardware SIMD width.
        Span<Mole> mixture = mixtureVector[..mixtureLength];
        for (int j = 0; j < reactionCount; j++)
            TensorPrimitives.MultiplyAdd(tables.Matrix.GetReactionRow(j), reactionSpeeds[j], mixture, mixture);

        for (int j = 0; j < reactionCount; j++)
        {
            energy += reactions.GetEnergyBalance(j) * reactionSpeeds[j];
        }

        if (reactionFeedback != null)
        {
            for (int j = 0; j < reactionCount; j++)
            {
                reactionFeedback[j] += reactionSpeeds[j];
            }
        }

        energy = Math.Max(0, energy);
        // Adjust temperature from the heat value, using the post-reaction mixture's heat capacity.
        // The composition changed above, so the capacity used to extract heat no longer applies.
        JoulePerKelvin heatCapacity = ComputeHeatCapacity(mixtureVector, mixtureLength, config);
        if (heatCapacity > 0f)
            currentTemperature = energy / heatCapacity;
    }

    // Only reached for an overflowing rate, so the per-gas scan stays off the normal path.
    private static bool ConsumesAnything(GasReactionData[] gasData, int reactionId)
    {
        foreach (var gas in gasData)
        {
            foreach (var (consumingId, consumed) in gas.ConsumingReactions)
            {
                if (consumingId == reactionId && consumed < 0f)
                    return true;
            }
        }

        return false;
    }

    private readonly struct ProcessChunkAction(
        ReactionSolver solver,
        IAtmosConfig config,
        ReactionTables tables,
        int awakeChunkCount) : IInAction<AtmosChunk>
    {
        public void Invoke(in AtmosChunk chunk)
        {
            solver.ProcessChunk(chunk, AtmosSolverConstants.FixedTimeStep, config, tables, awakeChunkCount: awakeChunkCount);
        }
    }

    /// <summary>
    ///     Applies one voxel's post-reaction mixture back to its chunk.
    ///     Every gas id present in <c>mixtures</c> must already have a channel (see the write-back parallel branch in
    ///     <see cref="ProcessChunk" />).
    ///     Each call is then a voxel-disjoint read and write, so it is safe to run across workers.
    /// </summary>
    private readonly struct WriteBackAction(
        AtmosChunk chunk,
        Mole[] mixtures,
        Kelvin[] newTemps,
        int mixtureLength,
        IAtmosConfig config,
        int trackedChannels) : IAction
    {
        public void Invoke(int activeIndex)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            Span<Mole> mixtureVector = mixtures.AsSpan(activeIndex * mixtureLength, mixtureLength);
            foreach (var gasChannel in chunk.ActiveGases.AsSpan(0, trackedChannels))
            {
                Mole diff = mixtureVector[gasChannel.GasId] - gasChannel.Moles[voxelIndex];
                if (diff != 0f)
                {
                    chunk.InjectGasToVoxel(
                        voxelIndex,
                        gasChannel.GasId,
                        diff,
                        newTemps[activeIndex],
                        config.GetMolarHeatCapacityAtConstantVolume(gasChannel.GasId),
                        config.PressurePerMoleKelvin);

                    // Fix rounding errors that cause bad values.
                    if (gasChannel.Moles[voxelIndex] < 1e-10)
                        gasChannel.Moles[voxelIndex] = 0;
                }
            }
        }
    }

    private readonly struct ProcessVoxelAction(
        AtmosChunk chunk,
        Second deltaTime,
        CubicMetre volume,
        IAtmosConfig config,
        Mole[]? reactionCount,
        Mole[][]? reactionFeedbacks,
        Mole[] mixtures,
        Kelvin[] newTemps,
        int mixtureLength,
        ReactionTables tables) : IAction
    {
        public void Invoke(int activeIndex)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            Kelvin temp = chunk.Temperature[voxelIndex];
            Mole[]? reactionFeedback =
                reactionCount == null ? null : ArrayPool<Mole>.Shared.Rent(tables.Reactions.Count);

            if (reactionFeedback != null)
                Array.Clear(reactionFeedback, 0, reactionFeedback.Length);

            if (reactionFeedbacks != null && reactionFeedback != null)
                reactionFeedbacks[activeIndex] = reactionFeedback;

            Span<Mole> mixtureVector = mixtures.AsSpan(activeIndex * mixtureLength, mixtureLength);
            Mole content = 0f;
            mixtureVector.Clear();

            for (int i = 0; i < chunk.ActiveGasCount; i++)
            {
                mixtureVector[chunk.ActiveGases[i].GasId] = chunk.ActiveGases[i].Moles[voxelIndex];
                content += chunk.ActiveGases[i].Moles[voxelIndex];
            }

            newTemps[activeIndex] = temp;
            if (content <= 0.0001)
                return;

            ProcessMixtureCore(
                deltaTime,
                mixtureVector,
                ref temp,
                volume,
                reactionFeedback,
                config,
                mixtureLength,
                tables);

            chunk.Temperature[voxelIndex] = temp;
            newTemps[activeIndex] = temp;
        }
    }
}