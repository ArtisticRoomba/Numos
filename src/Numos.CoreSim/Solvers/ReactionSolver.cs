using System.Buffers;
using System.Diagnostics;
using System.Numerics.Tensors;
using CommunityToolkit.HighPerformance.Helpers;
using Numos.CoreSim.GasReactions;

namespace Numos.CoreSim.Solvers;

internal class ReactionSolver : IAtmosSolverStage
{
    // ProcessVoxel is invoked once per voxel from an already-parallel worker (thousands of calls per
    // chunk per tick), and reactionCount is typically a handful of entries. Renting/returning that tiny
    // array from ArrayPool<T>.Shared on every call adds pool bookkeeping to the hottest part of the loop
    // for no benefit, since the buffer never needs to be seen by another thread. A per-thread scratch
    // buffer that just grows to the high-water mark removes that round trip entirely.
    [ThreadStatic]
    private static Scalar[]? _reactionSpeedsScratch;
    private readonly object _gasDataKey = new();
    private GasReactionMatrix? _changesMatrix;
    private GasReactionData[] _gasRateOrder = [];
    private GasReactionData[] _gasReactionData = [];
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
            // Told to ProcessChunk so it knows whether this outer per-chunk dispatch already saturates
            // the worker pool, mirroring ThermodynamicsSolver/PhaseChangeSolver's awakeChunkCount gate:
            // a single dense chunk only has per-voxel work to offer, so its write-back pass (see the
            // ProcessChunk doc) needs its own parallelism to use the other workers; once there are
            // enough awake chunks to occupy every worker on their own, adding a second layer of
            // parallelism inside each chunk would just add dispatch overhead for no benefit.
            int awakeChunkCount = 0;
            for (int i = 0; i < context.Chunks.Length; i++)
            {
                if (context.Chunks[i].IsAwake && context.Chunks[i].ActiveAirCount > 0)
                    awakeChunkCount++;
            }

            ParallelHelper.ForEach<AtmosChunk, ProcessChunkAction>(
                context.Chunks,
                new ProcessChunkAction(this, context.TickConfig, awakeChunkCount));
        }
        finally
        {
            _preparedConfig = null;
            _changesMatrix = null;
            Array.Clear(_gasReactionData);
            _gasRateOrder = [];
        }
    }

    private static Scalar[] RentReactionSpeeds(int reactionCount)
    {
        Scalar[]? scratch = _reactionSpeedsScratch;
        if (scratch == null || scratch.Length < reactionCount)
        {
            scratch = new Scalar[Math.Max(reactionCount, 16)];
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
    /// <param name="reactionCount">
    ///     Optional accumulator, index = reaction ID, incremented by how many times each reaction fired
    ///     across the whole chunk. Pass null to skip collecting this.
    /// </param>
    /// <param name="awakeChunkCount">
    ///     How many awake, gas-bearing chunks <see cref="Solve" /> is dispatching this tick. Below worker
    ///     count, this chunk's write-back pass parallelizes across voxels to help fill idle workers; at or
    ///     above it, the outer per-chunk dispatch already saturates the pool, so it runs sequentially
    ///     instead. Defaults to 1 (i.e. "parallelize") for callers processing a single chunk directly.
    /// </param>
    internal void ProcessChunk(
        AtmosChunk chunk, Second deltaTime, IAtmosConfig config, Scalar[]? reactionCount = null,
        int awakeChunkCount = 1)
    {
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
        // Resolved once for the whole chunk instead of once per voxel.
        ResolveReactionState(
            config,
            mixtureLength,
            out var reactions,
            out GasReactionData[] gasData,
            out GasReactionData[] rateOrder,
            out var changesMatrix);

        // One chunk-wide voxel-by-gas matrix instead of a separately pooled array per voxel: this is
        // the same "stop renting per item, slice a shared buffer instead" move as the reaction matrix,
        // and it collapses activeAirCount pool round-trips into one.
        Mole[] mixtures = ArrayPool<Mole>.Shared.Rent(checked(activeAirCount * mixtureLength));
        Scalar[][]? reactionFeedbacks = reactionCount == null ? null : ArrayPool<Scalar[]>.Shared.Rent(activeAirCount);
        Kelvin[] newTemps = ArrayPool<Kelvin>.Shared.Rent(activeAirCount);
        ParallelHelper.For(
            0,
            activeAirCount,
            new ProcessVoxelAction(
                this,
                chunk,
                deltaTime,
                config,
                reactionCount,
                reactionFeedbacks,
                mixtures,
                newTemps,
                mixtureLength,
                reactions,
                gasData,
                rateOrder,
                changesMatrix));

        // Put data back. This is a genuine O(activeAirCount * mixtureLength) pass that never shrinks
        // with worker count, so on a chunk that's the only source of parallel work (the dense-chunk
        // case: no other chunk-level dispatch to fill the pool), this used to be the scaling ceiling
        // (DenseChunkReactionParallelScalingBenchmarks: ~12% efficient at 16 workers). Each voxel's
        // diffing/injection only touches its own slot in every per-gas array, so voxels are independent
        // of each other; the one thing that isn't voxel-independent is GetOrCreateGasChannel's
        // first-time channel creation (it mutates the chunk's shared ActiveGases/ActiveGasCount) and
        // Wake() (it rebuilds the chunk's shared
        // ActiveAirIndices) -- both are resolved once, serially, below before any worker touches a voxel,
        // so parallelizing is only attempted once neither can fire mid-loop.
        int workerCount = Math.Max(1, Environment.ProcessorCount);
        if (awakeChunkCount < workerCount && chunk.IsAwake)
        {
            // Mirrors the discovery order the fully serial loop below used to walk (voxel-major, then
            // ascending gas id), so a chunk's channel-append order -- and therefore the per-voxel
            // summation order InjectGasToVoxel uses when it recomputes total moles/heat capacity --
            // stays identical to before this pass was split off the write-back it used to be part of.
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
                            newTemps[activeIndex],
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
                Scalar[] feedback = reactionFeedbacks[activeIndex];
                for (int i = 0; i < reactionCount.Length; i++)
                {
                    reactionCount[i] += feedback[i];
                }

                ArrayPool<float>.Shared.Return(feedback);
            }

            ArrayPool<float[]>.Shared.Return(reactionFeedbacks, true);
        }
    }

    /// <summary>
    ///     Resolves the per-gas reaction data, canonical rate-factor order, and change matrix for a
    ///     configuration, reusing this tick's prepared arrays when possible. A config other than the one
    ///     <see cref="Solve" /> prepared (e.g. from a direct kernel caller) is rebuilt instead of cached.
    /// </summary>
    private void ResolveReactionState(
        IAtmosConfig config, int mixtureLength,
        out GasReactionConfig reactions, out GasReactionData[] gasData,
        out GasReactionData[] rateOrder, out GasReactionMatrix changesMatrix)
    {
        reactions = GasReactionConfig.Get(config);
        if (ReferenceEquals(_preparedConfig, config))
        {
            gasData = _gasReactionData;
            rateOrder = _gasRateOrder;
            changesMatrix = _changesMatrix!;
            return;
        }

        gasData = new GasReactionData[mixtureLength];
        for (int gasId = 0; gasId < mixtureLength; gasId++)
            gasData[gasId] = CreateGasReactionData(config, gasId);

        rateOrder = gasData.OrderBy(static gas => gas.GasName, StringComparer.Ordinal).ToArray();
        changesMatrix = new GasReactionMatrix(gasData, reactions.Count);
    }

    // Total thermal energy the mixture holds at its current temperature: for each gas, moles * heat
    // capacity gives Joules-per-Kelvin, times temperature gives Joules.
    private Joule ExtractHeat(ReadOnlySpan<Mole> mixtureVector, ref readonly Kelvin temperature, int mixtureLength, IAtmosConfig config)
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

    // Same per-gas sum as ExtractHeat, minus the temperature factor -- used after the mixture's
    // composition has changed, to convert the (unchanged) total energy back into a temperature.
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
    ///     The voxel's mole amount for every registered gas, indexed by gas ID; updated in place to
    ///     reflect the reactions that occurred.
    /// </param>
    /// <param name="currentTemperature">Temperature, updated in place to reflect the post-reaction mixture.</param>
    /// <param name="reactionFeedback">optional array to which we write how often each reaction occured, index = reaction id</param>
    /// <param name="config">The gas registry and reaction definitions to react against.</param>
    /// <param name="mixtureLength">The number of registered gases, i.e. the valid length of <paramref name="mixtureVector" />.</param>
    internal void ProcessVoxel(
        Second deltaTime, Span<Mole> mixtureVector, ref Kelvin currentTemperature,
        Scalar[]? reactionFeedback, IAtmosConfig config, int mixtureLength)
    {
        ResolveReactionState(
            config,
            mixtureLength,
            out var reactions,
            out GasReactionData[] gasData,
            out GasReactionData[] rateOrder,
            out var changesMatrix);

        ProcessVoxelCore(
            deltaTime,
            mixtureVector,
            ref currentTemperature,
            reactionFeedback,
            config,
            mixtureLength,
            reactions,
            gasData,
            rateOrder,
            changesMatrix);
    }

    /// <summary>
    ///     Does the actual per-voxel reaction work once the caller has resolved <paramref name="reactions" />,
    ///     <paramref name="gasData" />, <paramref name="rateOrder" />, and <paramref name="changesMatrix" />.
    /// </summary>
    private void ProcessVoxelCore(
        Second deltaTime, Span<Mole> mixtureVector, ref Kelvin currentTemperature,
        Scalar[]? reactionFeedback, IAtmosConfig config, int mixtureLength,
        GasReactionConfig reactions, GasReactionData[] gasData, GasReactionData[] rateOrder, GasReactionMatrix changesMatrix)
    {
        Joule energy = ExtractHeat(mixtureVector, ref currentTemperature, mixtureLength, config);
        int reactionCount = reactions.Count;
        Scalar[] reactionSpeeds = RentReactionSpeeds(reactionCount);

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
            return;
        }

        // Adjust reaction speeds so they don't consume more material than is available in a single step.
        // Bounded defensively: each pass either zeroes out a reaction's speed or converges, so this many
        // iterations is far more than any real mixture needs, but guarantees termination even in a
        // pathological state (e.g. moles already negative from upstream float drift) that would otherwise
        // never find scale > 0 and loop forever.
        int maxIterations = mixtureLength + reactionCount + 8;
        bool converged = false;
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
                // Only reactions that actually consume this gas can contribute, so ConsumingReactions
                // already excludes every zero term a dense reactionCount scan would've added for nothing.
                // Match Enumerable.Sum's double accumulator while preserving reaction order and float products.
                Mole64 accumulatedConsumption = 0d;
                foreach (var (reactionId, consumed) in gasData[i].ConsumingReactions)
                    accumulatedConsumption += consumed * reactionSpeeds[reactionId];

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
                // Scale every reaction consuming the critical gas down by the same factor, just enough
                // that its post-reaction moles land at exactly 0: scale = available / |consumption|,
                // clamped to [0, 1] so a gas with moles to spare never gets sped up.
                Scalar scale = MathF.Min(
                    1f,
                    MathF.Max(0f, mixtureVector[criticalIndex] / Math.Abs(criticalConsumption)));

                foreach (var (reactionId, _) in gasData[criticalIndex].ConsumingReactions)
                {
                    //select the lower reaction speed.
                    reactionSpeeds[reactionId] = MathF.Min(reactionSpeeds[reactionId], reactionSpeeds[reactionId] * scale);
                    if (reactionSpeeds[reactionId] < 1e-10f)
                        reactionSpeeds[reactionId] = 0;
                }

                continue;
            }

            // Material is no longer overdrawn. Now the same idea applied to heat: endothermic reactions
            // (negative energy balance) can't draw more energy than the mixture actually has.
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

            // Scale down just the endothermic reactions, same "clamp to exactly enough" shape as the
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
            // Didn't reach a non-overdrawn state in budget -- skip this voxel's reactions this tick
            // rather than risk overdraw. Retried next tick.
            Debug.Assert(false, "Reaction material/energy limiter failed to converge within its iteration budget.");
            return;
        }

        //apply mixture, this also includes heat, since all change equations contain energy balance.
        //dN/dt = S * reactionSpeeds: walk one reaction (matrix row) at a time and fold its whole gas
        //column into the mixture with one lane-independent vector add. This visits the exact same
        //(gas, reaction) terms in the exact same order as a per-gas accumulation would, so it is
        //bit-exact regardless of hardware SIMD width.
        Span<Mole> mixture = mixtureVector[..mixtureLength];
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
    }

    private static GasReactionData CreateGasReactionData(IAtmosConfig config, int gasId)
    {
        config.TryGetGasProperties(gasId, out var gas);
        return GasReactionConfig.Get(config).CreateGasData(gasId, gas);
    }

    private readonly struct ProcessChunkAction(
        ReactionSolver solver,
        IAtmosConfig config,
        int awakeChunkCount) : IInAction<AtmosChunk>
    {
        public void Invoke(in AtmosChunk chunk)
        {
            solver.ProcessChunk(chunk, AtmosSolverConstants.FixedTimeStep, config, awakeChunkCount: awakeChunkCount);
        }
    }

    /// <summary>
    ///     Applies one voxel's post-reaction mixture back to its chunk. Requires every gas id present in
    ///     <c>mixtures</c> to already have a channel (see the write-back parallel branch in
    ///     <see cref="ProcessChunk" />), so every call here is a pure, voxel-disjoint read/write and safe
    ///     to run across workers.
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
                if (diff > 0)
                {
                    chunk.InjectGasToVoxel(
                        voxelIndex,
                        gasChannel.GasId,
                        diff,
                        newTemps[activeIndex],
                        config.GetMolarHeatCapacityAtConstantVolume(gasChannel.GasId),
                        config.PressurePerMoleKelvin);

                    //fix rounding errors causing bad value.
                    if (gasChannel.Moles[voxelIndex] < 1e-10)
                        gasChannel.Moles[voxelIndex] = 0;
                }
            }
        }
    }

    private readonly struct ProcessVoxelAction(
        ReactionSolver solver,
        AtmosChunk chunk,
        Second deltaTime,
        IAtmosConfig config,
        Scalar[]? reactionCount,
        Scalar[][]? reactionFeedbacks,
        Mole[] mixtures,
        Kelvin[] newTemps,
        int mixtureLength,
        GasReactionConfig reactions,
        GasReactionData[] gasData,
        GasReactionData[] rateOrder,
        GasReactionMatrix changesMatrix) : IAction
    {
        public void Invoke(int activeIndex)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            Kelvin temp = chunk.Temperature[voxelIndex];
            Scalar[]? reactionFeedback =
                reactionCount == null ? null : ArrayPool<Scalar>.Shared.Rent(reactions.Count);

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

            solver.ProcessVoxelCore(
                deltaTime,
                mixtureVector,
                ref temp,
                reactionFeedback,
                config,
                mixtureLength,
                reactions,
                gasData,
                rateOrder,
                changesMatrix);

            chunk.Temperature[voxelIndex] = temp;
            newTemps[activeIndex] = temp;
        }
    }
}