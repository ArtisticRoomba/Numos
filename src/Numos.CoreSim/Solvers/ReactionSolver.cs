using System.Buffers;
using System.Diagnostics;
using System.Numerics.Tensors;
using CommunityToolkit.HighPerformance.Helpers;
using Numos.Collections;
using Numos.CoreSim.GasReactions;
using Numos.Maths;

namespace Numos.CoreSim.Solvers;

internal class ReactionSolver : IAtmosSolverStage
{
    private const string IgnitionPercentKey = "IgnitionPercent";
    private const string IgnitedVolumeTempKey = "IgnitedVolumeTemp";
    private const string IgnitionSourceTempKey = "IgnitionSourceTemp";
    private const string IgnitionSourceVolumeKey = "IgnitionSourceVolume";

    private static readonly ChunkFieldDescriptor<Scalar> IgnitionPercentField =
        ChunkFieldRegistry.Register<Scalar>(IgnitionPercentKey, true, null);
    private static readonly ChunkFieldDescriptor<Kelvin> IgnitedVolumeTempField =
        ChunkFieldRegistry.Register<Kelvin>(IgnitedVolumeTempKey, true, null);
    private static readonly ChunkFieldDescriptor<Kelvin> IgnitionSourceTempField =
        ChunkFieldRegistry.Register<Kelvin>(IgnitionSourceTempKey, true, null);
    private static readonly ChunkFieldDescriptor<CubicMetre> IgnitionSourceVolumeField =
        ChunkFieldRegistry.Register<CubicMetre>(IgnitionSourceVolumeKey, true, null);

    // Radius factor of a sphere measured against its volume fraction of the voxel:
    // fraction = (4/3 * pi) * (r / edge)^3, so cbrt(fraction) = (4/3 * pi)^(1/3) * r / edge.
    private static readonly Scalar SphereRadiusFactor = MathF.Cbrt(4f * MathF.PI / 3f);

    // A flame smaller than this is treated as burnt out.
    private const Scalar MinimumIgnitionPercent = 1e-9f;

    // If the seed is set above this it will break as it could then spread many voxels in one tick.
    private const Scalar MaximumSeedFraction = 0.5f;

    internal Scalar FlameSpeedScale { get; set; } = 1f;

    /// <summary>
    ///     Slowest flame speed that still sustains a flame. Below it a flame kernel recedes, and a voxel that
    ///     burns across its whole volume stops counting as ignited. The gas itself keeps reacting normally.
    /// </summary>
    internal MeterPerSecond MinimumFlameSpeed { get; set; } = 0.01f;

    /// <summary>
    ///     Whether a fully ignited voxel may start a flame kernel in its face neighbors. Heat transfer is
    ///     the main way fire spreads between voxels; this is only a supplement.
    /// </summary>
    internal bool IgnitionTransferEnabled { get; set; } = true;

    /// <summary>
    ///     Fraction of a voxel that a transferred flame kernel starts with. After that the kernel grows or
    ///     recedes by the same flame speed rules as any other flame. Capped below <see cref="MaximumSeedFraction"/>.
    /// </summary>
    internal Scalar TransferSeedFraction { get; set; } = 0.01f;

    // Set by the voxel pass if any voxel is completely ignited, so the transfer stage costs nothing otherwise.
    private volatile bool _anyFullyIgnited;

    // Rates that overflow (trace gases with negative exponents) saturate here instead of being dropped, and the
    // material limiter then scales them back to what's actually available. Must stay below float.MaxValue so the
    // stored speed is still finite. Only reactions with a consumed input saturate, since nothing else would limit
    // them.
    private const double MaxReactionSpeed = 1e30;

    // ProcessVoxel is invoked once per voxel from an already-parallel worker (thousands of calls per
    // chunk per tick), and reactionCount is typically a handful of entries. Renting/returning that tiny
    // array from ArrayPool<T>.Shared on every call adds pool bookkeeping to the hottest part of the loop
    // for no benefit, since the buffer never needs to be seen by another thread. A per-thread scratch
    // buffer that just grows to the high-water mark removes that round trip entirely.
    [ThreadStatic]
    private static Mole[]? _reactionSpeedsScratch;
    // Per-thread scratch for the intrinsic (unscaled) rate of every ignition reaction, same reasoning as
    // _reactionSpeedsScratch above.
    // Maybe super mem inefficient.
    [ThreadStatic]
    private static ConcentrationPerSecond64[]? _intrinsicRatesScratch;

    public void Solve(AtmosSolverExecutionContext context)
    {
        //fast skip for empty configs.
        if (GasReactionConfig.Get(context.TickConfig).Count == 0 || context.TickConfig.GasPropertyCount == 0)
            return;

        var tables = context.TickConfig.GetReactionTables();

        // Told to ProcessChunk so it knows whether this outer per-chunk dispatch already saturates
        // the worker pool, mirroring ThermodynamicsSolver/PhaseChangeSolver's awakeChunkCount gate.
        // A single dense chunk only has per-voxel work to offer, so its write-back pass needs its
        // own parallelism to use the other workers; once enough awake chunks occupy every worker,
        // a second layer of parallelism inside each chunk is just dispatch overhead.
        int awakeChunkCount = 0;
        for (int i = 0; i < context.Chunks.Length; i++)
        {
            if (context.Chunks[i].IsAwake && context.Chunks[i].ActiveAirCount > 0)
                awakeChunkCount++;
        }

        _anyFullyIgnited = false;

        ParallelHelper.ForEach<AtmosChunk, ProcessChunkAction>(
            context.Chunks,
            new ProcessChunkAction(this, context.TickConfig, tables, awakeChunkCount));

        if (IgnitionTransferEnabled && _anyFullyIgnited)
        {
            // The transfer stage reads neighboring chunks' ignition arrays from workers, and a chunk
            // without active air never created them in ProcessChunk. Create them serially here so
            // no worker ever creates one concurrently.
            // Removable??
            for (int i = 0; i < context.Chunks.Length; i++)
            {
                context.Chunks[i].GetOrCreateField(IgnitionPercentField);
                context.Chunks[i].GetOrCreateField(IgnitedVolumeTempField);
            }

            ParallelHelper.ForEach<AtmosChunk, TransferIgnitionAction>(
                context.Chunks,
                new TransferIgnitionAction(this, context));
        }
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

    private static ConcentrationPerSecond64[] RentIntrinsicRates(int reactionCount)
    {
        ConcentrationPerSecond64[]? scratch = _intrinsicRatesScratch;
        if (scratch == null || scratch.Length < reactionCount)
        {
            scratch = new ConcentrationPerSecond64[Math.Max(reactionCount, 16)];
            _intrinsicRatesScratch = scratch;
        }

        return scratch;
    }

    /// <summary>
    ///     Requests an ignition source in one voxel for the next reaction pass: a very small volume at a
    ///     specific temperature. It starts a flame in that voxel even when the gas itself is far below the
    ///     usual ignition temperature, provided the mixture can actually burn at the source temperature.
    ///     A request lasts one tick; a persistent source (torch, welder, ...) calls this every tick.
    ///     Several requests on the same voxel merge to the hottest temperature and largest volume.
    ///     Call between ticks (not from inside solver workers).
    /// </summary>
    /// <param name="chunk">The chunk that owns the voxel.</param>
    /// <param name="voxelIndex">The voxel inside <paramref name="chunk" />. Only active-air voxels are processed.</param>
    /// <param name="temperature">Temperature of the source, in kelvin.</param>
    /// <param name="volume">Volume the source ignites, in cubic metres. Clamped to the voxel volume.</param>
    internal void SetIgnitionSource(AtmosChunk chunk, ushort voxelIndex, Kelvin temperature, CubicMetre volume)
    {
        Kelvin[] temperatures = chunk.GetOrCreateField(IgnitionSourceTempField);
        CubicMetre[] volumes = chunk.GetOrCreateField(IgnitionSourceVolumeField);
        temperatures[voxelIndex] = MathF.Max(temperatures[voxelIndex], temperature);
        volumes[voxelIndex] = MathF.Max(volumes[voxelIndex], volume);
    }

    /// <summary>
    ///     Folds an ignition source into one voxel's burning sub-volume. Idempotent: a source never shrinks
    ///     an existing flame, and one that is no bigger than the flame already burning there does nothing,
    ///     so a source that is re-asserted every tick doesn't keep inflating the fire.
    /// </summary>
    /// <param name="ignitionPercent">Burning fraction of the voxel (0..1), updated in place.</param>
    /// <param name="ignitedVolumeTemp">Temperature of the burning fraction, updated in place.</param>
    /// <param name="sourceTemperature">Temperature of the source.</param>
    /// <param name="sourceVolume">Volume the source ignites.</param>
    /// <param name="voxelVolume">Volume of the whole voxel.</param>
    internal static void ApplyIgnitionSource(
        ref Scalar ignitionPercent, ref Kelvin ignitedVolumeTemp,
        Kelvin sourceTemperature, CubicMetre sourceVolume, CubicMetre voxelVolume)
    {
        if (!(sourceTemperature > 0f) || !(sourceVolume > 0f) || !(voxelVolume > 0f))
            return;

        Scalar sourceFraction = MathF.Min(1f, sourceVolume / voxelVolume);
        Scalar currentFraction = ignitionPercent > 0f ? ignitionPercent : 0f;
        if (sourceFraction <= currentFraction)
            return;

        // Volume-weighted temperature of what was already burning plus the newly ignited part.
        Kelvin existingHeat = currentFraction > 0f ? currentFraction * MathF.Max(0f, ignitedVolumeTemp) : 0f;
        ignitedVolumeTemp = (existingHeat + (sourceFraction - currentFraction) * sourceTemperature) / sourceFraction;
        ignitionPercent = sourceFraction;
    }

    /// <summary>
    ///     Moves a burning fraction by one tick of flame-front travel. A positive advance grows the flame,
    ///     a negative one makes it recede.
    /// </summary>
    /// <param name="ignitionPercent">Burning fraction of the voxel before the step, in (0..1].</param>
    /// <param name="frontAdvance">Signed distance the front travels this tick, in voxel edge lengths.</param>
    /// <returns>The burning fraction after the step, between 0 and 1.</returns>
    /// <remarks>
    ///     A small flame kernel is a sphere, whose surface (and so growth rate) is tiny at first. Once the
    ///     front is wider than the voxel cross-section it behaves like a planar sheet that sweeps volume at
    ///     a constant rate. Growth takes whichever of the two is slower. A receding flame shrinks as a sphere.
    /// </remarks>
    internal static Scalar GrowIgnition(Scalar ignitionPercent, Scalar frontAdvance)
    {
        Scalar radius = MathF.Cbrt(ignitionPercent) + SphereRadiusFactor * frontAdvance;
        if (frontAdvance < 0f)
        {
            radius = MathF.Max(0f, radius);
            return radius * radius * radius;
        }

        Scalar planar = ignitionPercent + frontAdvance;
        Scalar sphere = radius * radius * radius;
        return MathF.Min(1f, MathF.Min(planar, sphere));
    }

    /// <summary>
    ///     Estimates the flame speed from the thermal flame theory <c>speed = scale * sqrt(diffusivity * rate)</c>,
    ///     where the rate is how fast the limiting reactant is being used up.
    /// </summary>
    /// <param name="depletionRate">Fractional depletion rate of the limiting reactant, see <see cref="ComputeDepletionRate" />.</param>
    /// <param name="thermalDiffusivity">Thermal diffusivity of the gas, see <see cref="ComputeThermalDiffusivity" />.</param>
    internal MeterPerSecond ComputeFlameSpeed(PerSecond depletionRate, SquareMetrePerSecond thermalDiffusivity)
    {
        if (!(depletionRate > 0f))
            return 0f;

        return FlameSpeedScale * MathF.Sqrt(thermalDiffusivity * depletionRate);
    }

    /// <summary>
    ///     Fractional rate at which the limiting reactant is used up by the ignition reactions, in 1/s.
    ///     It comes from the intrinsic rates (rate constant at the flame temperature times the concentration
    ///     factors), not from how much a tick managed to burn: that is capped at 1 / deltaTime and would cap
    ///     the flame speed with it. It still follows the fuel running out, because the concentration factors
    ///     fall with the concentrations.
    /// </summary>
    /// <param name="mixtureVector">Moles of every gas before the reactions are applied.</param>
    /// <param name="mixtureLength">The number of registered gases.</param>
    /// <param name="volume">Volume of the voxel, to turn moles into concentrations.</param>
    /// <param name="reactions">The reaction definitions.</param>
    /// <param name="gasData">The per-gas reaction data, for the reactions that consume each gas.</param>
    /// <param name="intrinsicRates">
    ///     Rate of every ignition reaction inside the burning region, indexed by reaction ID.
    /// </param>
    private static PerSecond ComputeDepletionRate(
        ReadOnlySpan<Mole> mixtureVector, int mixtureLength, CubicMetre volume, GasReactionConfig reactions,
        GasReactionData[] gasData, ConcentrationPerSecond64[] intrinsicRates)
    {
        PerSecond result = 0f;
        for (int gasId = 0; gasId < mixtureLength; gasId++)
        {
            if (!(mixtureVector[gasId] > 0f))
                continue;

            ConcentrationPerSecond consumption = 0f;
            foreach (var (reactionId, consumed) in gasData[gasId].ConsumingReactions)
            {
                if (reactions.GetReactionIsIgnition(reactionId))
                    consumption -= consumed * (ConcentrationPerSecond)intrinsicRates[reactionId];
            }

            if (consumption <= 0f)
                continue;

            MolePerCubicMetre concentration = mixtureVector[gasId] / volume;
            result = MathF.Max(result, consumption / concentration);
        }

        return result;
    }

    /// <summary>
    ///     Brings the burning sub-volume's state into a valid shape before it is used: no flame without a
    ///     positive fraction, a fully burning voxel sits at the mean temperature, and a partly burning
    ///     one is never colder than the voxel average (other solvers only ever touch the mean).
    /// </summary>
    private static void SanitizeIgnition(Kelvin meanTemperature, ref Scalar ignitionPercent, ref Kelvin ignitedVolumeTemp)
    {
        if (!(ignitionPercent > 0f))
        {
            ignitionPercent = 0f;
            ignitedVolumeTemp = 0f;
            return;
        }

        if (ignitionPercent >= 1f)
        {
            ignitionPercent = 1f;
            ignitedVolumeTemp = meanTemperature;
            return;
        }

        if (!(ignitedVolumeTemp > meanTemperature))
            ignitedVolumeTemp = meanTemperature;
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
    ///     How many awake, gas-bearing chunks <see cref="Solve" /> is dispatching this tick. Below worker
    ///     count, this chunk's write-back pass parallelizes across voxels to help fill idle workers; at or
    ///     above it, the outer per-chunk dispatch already saturates the pool, so it runs sequentially
    ///     instead. Defaults to 1 (i.e. "parallelize") for callers processing a single chunk directly.
    /// </param>
    internal void ProcessChunk(
        AtmosChunk chunk, Second deltaTime, IAtmosConfig config, ReactionTables tables, Scalar[]? reactionCount = null,
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

        // Resolved once for the whole chunk; the voxel workers index straight into them.
        Scalar[] ignitionPercents = chunk.GetOrCreateField(IgnitionPercentField);
        Kelvin[] ignitedVolumeTemps = chunk.GetOrCreateField(IgnitedVolumeTempField);
        CubicMetre[] sourceVolumes = chunk.GetOrCreateField(IgnitionSourceVolumeField);
        Kelvin[] sourceTemps = chunk.GetOrCreateField(IgnitionSourceTempField);


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
                volume,
                config,
                reactionCount,
                reactionFeedbacks,
                mixtures,
                newTemps,
                ignitionPercents,
                ignitedVolumeTemps,
                sourceVolumes,
                sourceTemps,
                mixtureLength,
                tables));

        // Put data back. This is an O(activeAirCount * mixtureLength) pass that doesn't shrink with
        // worker count, so run serially it caps scaling for a dense chunk that is the only source of
        // parallel work (see DenseChunkReactionParallelScalingBenchmarks). Each voxel's diffing/injection
        // only touches its own slot in every per-gas array, so voxels are independent, except for two
        // chunk-shared side effects of InjectGasToVoxel: GetOrCreateGasChannel's first-time channel
        // creation (mutates ActiveGases/ActiveGasCount) and Wake() (rebuilds ActiveAirIndices). The
        // parallel branch only runs on an awake chunk, so Wake() can't fire, and it creates every
        // missing channel serially before any worker touches a voxel.
        int workerCount = Math.Max(1, Environment.ProcessorCount);
        if (awakeChunkCount < workerCount && chunk.IsAwake)
        {
            // Same discovery order as the serial branch below (voxel-major, then ascending gas id), so
            // the chunk's channel-append order -- and with it the per-voxel summation order
            // InjectGasToVoxel uses when it recomputes total moles -- matches the serial path exactly.
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
                    if (diff != 0)
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
    ///     Lets a completely ignited voxel start a flame kernel in its face neighbors. Only voxels that are ignited across their
    ///     whole volume (which implies a sustained flame) donate, and a receiver only gets a small kernel
    ///     that then grows, recedes or dies by its own gas's flame speed.
    ///     This is a single pass with no double buffering. Donors are never written here, and a receiver
    ///     is only ever written with a kernel below 1, so no voxel can change from receiver to donor
    ///     while the pass runs.
    /// </summary>
    /// <param name="chunk">The chunk whose voxels receive ignition from their neighbors.</param>
    /// <param name="context">Supplies the neighbor chunk lookup.</param>
    private void TransferIgnition(AtmosChunk chunk, AtmosSolverExecutionContext context)
    {
        if (!chunk.IsAwake || chunk.ActiveAirCount == 0)
            return;

        Scalar seedFraction = MathF.Min(TransferSeedFraction, MaximumSeedFraction);
        if (!(seedFraction > 0f))
            return;

        Scalar[] ignitionPercents = chunk.GetOrCreateField(IgnitionPercentField);
        Kelvin[] ignitedVolumeTemps = chunk.GetOrCreateField(IgnitedVolumeTempField);

        for (int activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            Scalar ownFraction = ignitionPercents[voxelIndex];
            if (ownFraction >= seedFraction)
                continue;

            int donorCount = 0;
            Kelvin donorTemperatureSum = 0f;
            for (int face = 0; face < 6; face++)
            {
                // TODO: This is a bad way to do this, it should do something similar to advection
                if (!TryGetNeighbor(chunk, voxelIndex, context, face, out AtmosChunk neighborChunk, out ushort neighborIndex))
                    continue;

                // Interior faces skip the per-chunk array lookups. Solve created every chunk's ignition
                // arrays before this stage, so the lookup for another chunk never creates anything.
                bool sameChunk = ReferenceEquals(neighborChunk, chunk);
                Scalar[] neighborPercents = sameChunk
                    ? ignitionPercents
                    : neighborChunk.GetOrCreateField(IgnitionPercentField);
                if (!(neighborPercents[neighborIndex] >= 1f))
                    continue;

                Kelvin[] neighborTemps = sameChunk
                    ? ignitedVolumeTemps
                    : neighborChunk.GetOrCreateField(IgnitedVolumeTempField);
                donorCount++;
                donorTemperatureSum += neighborTemps[neighborIndex];
            }

            if (donorCount == 0)
                continue;

            // Volume-weighted flame temperature of what was already burning plus the new kernel.
            Kelvin ownTemperature = ownFraction > 0f
                ? (ignitedVolumeTemps[voxelIndex] > 0f ? ignitedVolumeTemps[voxelIndex] : chunk.Temperature[voxelIndex])
                : 0f;
            Kelvin donorTemperature = donorTemperatureSum / donorCount;
            ignitedVolumeTemps[voxelIndex] = (ownFraction * ownTemperature + (seedFraction - ownFraction) * donorTemperature) / seedFraction;
            ignitionPercents[voxelIndex] = seedFraction;
        }
    }

    // This is kinda bad
    // Finds the chunk each time
    // Should use something similar to advection solver
    private static bool TryGetNeighbor(
        AtmosChunk chunk, ushort voxelIndex, AtmosSolverExecutionContext context, int face, out AtmosChunk neighborChunk, out ushort neighborIndex)
    {
        var position = chunk.GetXyzInt3(voxelIndex);
        var direction = Int3.CardinalOffsets[face];
        var neighborPosition = position + direction;
        neighborChunk = chunk;
        neighborIndex = 0;
        if (!neighborPosition.IsWithin(chunk.Dimensions))
        {
            if (!context.TryGetChunk(chunk.GridPosition + direction, out neighborChunk))
                return false;

            neighborPosition = (neighborPosition + neighborChunk.Dimensions) % neighborChunk.Dimensions;
        }
        neighborIndex = neighborChunk.GetIndexUnsafe(neighborPosition);
        return true;
    }

    // Energy of the mixture at its current temperature: sum of moles * Cv * T over gases.
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

    private static SquareMetrePerSecond ComputeThermalDiffusivity(
        ReadOnlySpan<Mole> mixtureVector, int mixtureLength, CubicMetre volume, Kelvin temperature, IAtmosConfig config)
    {
        SquareMetrePerSecond result = 0f;
        JoulePerKelvin oldHeatCapacity = ComputeHeatCapacity(mixtureVector, mixtureLength, config);
        Mole totalMoles = 0f;
        MoleWattsPerMeterKelvin moleWeightedConductivity = 0f;
        for (int i = 0; i < mixtureLength; i++)
        {
            if (!(mixtureVector[i] > 0f))
                continue;

            totalMoles += mixtureVector[i];
            moleWeightedConductivity += mixtureVector[i] * config.GetThermalConductivity(i, temperature);
        }

        if (!(totalMoles > 0f) || !(oldHeatCapacity > 0f) || !(volume > 0f))
            return result;

        // Mole-fraction weighted conductivity of the mixture, divided by the volumetric heat capacity
        // (oldHeatCapacity / volume).
        WattsPerMeterKelvin conductivity = moleWeightedConductivity / totalMoles;
        // C_V + nR for C_P
        result = conductivity * volume / (oldHeatCapacity + AtmosPhysicalConstants.MolarGasConstant*totalMoles);
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
    /// <param name="ignitionPercent">
    ///     Fraction of the voxel that is burning (0..1), updated in place. While it is above 0, all
    ///     reactions take place inside that sub-volume at <paramref name="ignitedVolumeTemp" />.
    ///     Use <see cref="ApplyIgnitionSource" /> beforehand to add an ignition source.
    /// </param>
    /// <param name="ignitedVolumeTemp">Temperature of the burning fraction, updated in place.</param>
    /// <param name="reactionFeedback">
    ///     Optional accumulator, indexed by reaction ID, incremented by how many times each reaction fired.
    /// </param>
    /// <param name="config">The gas registry and reaction definitions to react against.</param>
    /// <param name="mixtureLength">The number of registered gases, i.e. the valid length of <paramref name="mixtureVector" />.</param>
    internal void ProcessVoxel(
        Second deltaTime, Span<Mole> mixtureVector, ref Kelvin currentTemperature, ref Scalar ignitionPercent, ref Kelvin ignitedVolumeTemp,
        Scalar[]? reactionFeedback, IAtmosConfig config, int mixtureLength)
    {
        ProcessMixtureCore(
            deltaTime,
            mixtureVector,
            ref currentTemperature,
            ref ignitionPercent,
            ref ignitedVolumeTemp,
            config.GetVoxelVolume(),
            reactionFeedback,
            config,
            mixtureLength,
            ReactionTables.Create(config));
    }

    /// <summary>
    ///     Does the actual per-voxel reaction work against prebuilt <paramref name="tables" />.
    /// </summary>
    private void ProcessMixtureCore(
        Second deltaTime, Span<Mole> mixtureVector, ref Kelvin currentTemperature, ref Scalar ignitionPercent, ref Kelvin ignitedVolumeTemp, CubicMetre volume,
        Scalar[]? reactionFeedback, IAtmosConfig config, int mixtureLength, ReactionTables tables)
    {
        var reactions = tables.Reactions;
        GasReactionData[] gasData = tables.GasData;

        // GetVoxelVolume always returns a finite positive volume, so only the timestep needs checking.
        if (!float.IsFinite(deltaTime))
            return;

        // Other solvers only ever change the mean temperature, so make sure the burning sub-volume's
        // state is still valid against it before it feeds the rate calculation.
        SanitizeIgnition(currentTemperature, ref ignitionPercent, ref ignitedVolumeTemp);

        Joule initialEnergy = ExtractHeat(mixtureVector, ref currentTemperature, mixtureLength, config);
        int reactionCount = reactions.Count;
        Mole[] reactionSpeeds = RentReactionSpeeds(reactionCount);
        ConcentrationPerSecond64[] intrinsicRates = RentIntrinsicRates(reactionCount);

        Array.Clear(reactionSpeeds, 0, reactionCount);
        Array.Clear(intrinsicRates, 0, reactionCount);
        //get all reaction speeds. Sequential: reactionCount is typically a handful of items, far too
        //small to justify a parallel dispatch, especially one issued once per voxel from inside an
        //already-parallel voxel worker.
        Kelvin temperature = currentTemperature; //to stop warning about the later change of currentTemperature.
        bool flameActive = ignitionPercent > 0f;
        // Stays true unless an ignition reaction burns faster inside the flame than across the whole voxel.
        bool bulkDominates = flameActive;

        // How fast a flame can move through this gas, from the intrinsic rate at which its limiting
        // reactant is used up at the flame temperature. Needs the mixture from before the reactions are
        // applied. Only worked out when it matters, so voxels without any ignition keep the original cost.
        MeterPerSecond flameSpeed = 0f;
        bool sustained = false;
        // The flame moves while the reactions run, so they take place across the average of the burning
        // fraction at the start and at the end of the tick, not just the fraction the flame started with.
        Scalar burningFraction = ignitionPercent;
        Scalar grownIgnitionPercent = ignitionPercent;
        if (flameActive)
        {
            for (int reactionId = 0; reactionId < reactionCount; reactionId++)
            {
                if (!reactions.GetReactionIsIgnition(reactionId))
                    continue;

                ConcentrationPerSecond64 intrinsicRate = reactions.GetRateConstant(reactionId, ignitedVolumeTemp);
                if (!double.IsNormal(intrinsicRate) || intrinsicRate <= 0f)
                    continue;

                foreach (var gas in tables.RateOrder)
                    intrinsicRate = gas.ApplyRateFactors(reactionId, (Mole64)mixtureVector[gas.GasId] / volume, intrinsicRate);

                intrinsicRates[reactionId] = intrinsicRate;
            }

            PerSecond depletionRate = ComputeDepletionRate(mixtureVector, mixtureLength, volume, reactions, gasData, intrinsicRates);
            Kelvin conductivityTemperature = (currentTemperature + ignitedVolumeTemp) / 2f;
            SquareMetrePerSecond thermalDiffusivity = ComputeThermalDiffusivity(mixtureVector, mixtureLength, volume, conductivityTemperature, config);
            flameSpeed = ComputeFlameSpeed(depletionRate, thermalDiffusivity);
            sustained = flameSpeed >= MinimumFlameSpeed;

            // Signed front advance in voxel edge lengths: the flame grows at the flame speed while it is
            // sustained, and recedes by how far the speed falls short of the minimum otherwise.
            MeterPerSecond frontSpeed = sustained ? flameSpeed : flameSpeed - MinimumFlameSpeed;
            Scalar frontAdvance = frontSpeed * deltaTime / MathF.Cbrt(volume);
            grownIgnitionPercent = GrowIgnition(ignitionPercent, frontAdvance);
            burningFraction = (ignitionPercent + grownIgnitionPercent) / 2f;
        }

        for (int reactionId = 0; reactionId < reactionCount; reactionId++)
        {
            ConcentrationPerSecond rate = reactions.GetRateConstant(reactionId, temperature);
            if (flameActive && reactions.GetReactionIsIgnition(reactionId))
            {
                ConcentrationPerSecond flameRate = reactions.GetRateConstant(reactionId, ignitedVolumeTemp) * burningFraction;
                // Only uses the flame rate is the reaction would be faster then the bulk rate
                if (flameRate > rate)
                    bulkDominates = false;

                rate = MathF.Max(flameRate, rate);
            }
            if (!float.IsNormal(rate) || rate <= 0f)
                continue;

            // Preserve canonical gas-name and factor order independently of registry indices.
            ConcentrationPerSecond64 rate64 = rate;
            foreach (var gas in tables.RateOrder)
                rate64 = gas.ApplyRateFactors(reactionId, (Mole64)mixtureVector[gas.GasId] / volume, rate64);

            // Without a flame the whole voxel is reacting, so this is the rate a flame would have to beat.
            // It has to be the rate with the concentration factors applied, like the flame's intrinsic rates.
            if (!flameActive && reactions.GetReactionIsIgnition(reactionId))
                intrinsicRates[reactionId] = rate64;

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
            // Nothing can burn here, so any ignition (its own or spread in from a neighbor) dies out.
            ignitionPercent = 0f;
            ignitedVolumeTemp = 0f;
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
                {
                    Mole speed = reactionSpeeds[reactionId];
                    // A flame can only burn the reactant inside it: weigh its speed against the burning
                    // share of the gas, so a small flame can't use up the whole voxel's fuel in one tick.
                    if (flameActive && !bulkDominates && reactions.GetReactionIsIgnition(reactionId))
                        speed /= burningFraction;
                    accumulatedConsumption += consumed * speed;
                }

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

            if (initialEnergy + energyConsumption >= 0)
            {
                converged = true;
                break;
            }

            // Scale down just the endothermic reactions, same "clamp to exactly enough" shape as the
            // material-limiter scale above.
            Scalar energyScale = MathF.Min(1f, MathF.Max(0f, initialEnergy / Math.Abs(energyConsumption)));
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

        Joule energyDelta = 0f;
        Joule ignitionEnergyDelta = 0f;
        for (int j = 0; j < reactionCount; j++)
        {
            energyDelta += reactions.GetEnergyBalance(j) * reactionSpeeds[j];
            ignitionEnergyDelta += reactions.GetReactionIsIgnition(j) ? reactions.GetEnergyBalance(j) * reactionSpeeds[j] : 0;
        }

        // Without a flame, check whether the reactions that are running could keep one going. This also
        // needs the mixture from before the reactions are applied.
        if (!flameActive && ignitionEnergyDelta > 0f)
        {
            PerSecond depletionRate = ComputeDepletionRate(mixtureVector, mixtureLength, volume, reactions, gasData, intrinsicRates);
            SquareMetrePerSecond thermalDiffusivity = ComputeThermalDiffusivity(mixtureVector, mixtureLength, volume, currentTemperature, config);
            flameSpeed = ComputeFlameSpeed(depletionRate, thermalDiffusivity);
            sustained = flameSpeed >= MinimumFlameSpeed;
        }

        //apply mixture, this also includes heat, since all change equations contain energy balance.
        //dN/dt = S * reactionSpeeds: walk one reaction (matrix row) at a time and fold its whole gas
        //column into the mixture with one lane-independent vector add. This visits the exact same
        //(gas, reaction) terms in the exact same order as a per-gas accumulation would, so it is
        //bit-exact regardless of hardware SIMD width.
        Span<Mole> mixture = mixtureVector[..mixtureLength];
        for (int j = 0; j < reactionCount; j++)
            TensorPrimitives.MultiplyAdd(tables.Matrix.GetReactionRow(j), reactionSpeeds[j], mixture, mixture);

        if (reactionFeedback != null)
        {
            for (int j = 0; j < reactionCount; j++)
            {
                reactionFeedback[j] += reactionSpeeds[j];
            }
        }

        Joule energy = Math.Max(0, energyDelta + initialEnergy);
        //adjust temperature based on heat value, using the post-reaction mixture's heat capacity
        //(composition changed above, so the capacity used to extract heat no longer applies).
        JoulePerKelvin heatCapacity = ComputeHeatCapacity(mixtureVector, mixtureLength, config);
        if (heatCapacity > 0f)
            currentTemperature = energy / heatCapacity;

        if (ignitionPercent > 0f)
        {
            if (ignitionEnergyDelta <= 0f || !(heatCapacity > 0f) || (bulkDominates && !sustained))
            {
                // The flame stopped releasing energy, or the gas can still react but not fast enough to
                // keep a flame going: it is out. The gas itself keeps reacting normally.
                ignitionPercent = 0f;
                ignitedVolumeTemp = 0f;
            }
            else if (bulkDominates)
            {
                // Burning the whole voxel is at least as fast as the flame kernel, so stop tracking a
                // sub-volume and treat the whole voxel as ignited.
                ignitionPercent = 1f;
                ignitedVolumeTemp = currentTemperature;
            }
            else if (grownIgnitionPercent <= MinimumIgnitionPercent)
            {
                ignitionPercent = 0f;
                ignitedVolumeTemp = 0f;
            }
            else
            {
                // The burning sub-volume gets its share of the voxel's energy plus all the energy the
                // ignition reactions released, which only ever happens inside the flame.
                Joule ignitionEnergy = initialEnergy * grownIgnitionPercent + (energyDelta - ignitionEnergyDelta) * grownIgnitionPercent + ignitionEnergyDelta;
                JoulePerKelvin ignitionHeatCapacity = heatCapacity * grownIgnitionPercent;
                ignitedVolumeTemp = ignitionEnergy / ignitionHeatCapacity;
                ignitionPercent = grownIgnitionPercent;
            }
        }
        else if (ignitionEnergyDelta > 0f && sustained)
        {
            // Usual ignition: the whole voxel was reacting at the gas temperature, fast enough to keep a flame going.
            ignitionPercent = 1f;
            ignitedVolumeTemp = currentTemperature;
        }
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

    private readonly struct TransferIgnitionAction(ReactionSolver solver, AtmosSolverExecutionContext context) : IInAction<AtmosChunk>
    {
        public void Invoke(in AtmosChunk chunk)
        {
            solver.TransferIgnition(chunk, context);
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
                if (diff != 0f)
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
        CubicMetre volume,
        IAtmosConfig config,
        Scalar[]? reactionCount,
        Scalar[][]? reactionFeedbacks,
        Mole[] mixtures,
        Kelvin[] newTemps,
        Scalar[] ignitionPercents,
        Kelvin[] ignitedVolumeTemps,
        CubicMetre[] sourceVolumes,
        Kelvin[] sourceTemps,
        int mixtureLength,
        ReactionTables tables) : IAction
    {
        public void Invoke(int activeIndex)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            Kelvin temp = chunk.Temperature[voxelIndex];
            Scalar[]? reactionFeedback =
                reactionCount == null ? null : ArrayPool<Scalar>.Shared.Rent(tables.Reactions.Count);
            Scalar ignitionPercent = ignitionPercents[voxelIndex];
            Kelvin ignitedVolumeTemp = ignitedVolumeTemps[voxelIndex];

            // A source request lasts one tick: take it and clear the slot.
            CubicMetre sourceVolume = sourceVolumes[voxelIndex];
            Kelvin sourceTemp = sourceTemps[voxelIndex];
            if (sourceVolume > 0f || sourceTemp > 0f)
            {
                sourceVolumes[voxelIndex] = 0f;
                sourceTemps[voxelIndex] = 0f;
            }

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
            {
                // No gas left to burn, so nothing can stay ignited here.
                ignitionPercents[voxelIndex] = 0f;
                ignitedVolumeTemps[voxelIndex] = 0f;
                return;
            }

            // Applied before reacting, so the source's own tiny, very hot volume is what the reaction
            // rate sees this tick, even if the gas around it is far below its usual ignition temperature.
            if (sourceVolume > 0f)
            {
                ApplyIgnitionSource(
                    ref ignitionPercent,
                    ref ignitedVolumeTemp,
                    sourceTemp,
                    sourceVolume,
                    volume);
            }

            solver.ProcessMixtureCore(
                deltaTime,
                mixtureVector,
                ref temp,
                ref ignitionPercent,
                ref ignitedVolumeTemp,
                volume,
                reactionFeedback,
                config,
                mixtureLength,
                tables);

            chunk.Temperature[voxelIndex] = temp;
            newTemps[activeIndex] = temp;
            ignitionPercents[voxelIndex] = ignitionPercent;
            ignitedVolumeTemps[voxelIndex] = ignitedVolumeTemp;

            // Benign race: every writer stores true.
            if (ignitionPercent >= 1f)
                solver._anyFullyIgnited = true;
        }
    }
}