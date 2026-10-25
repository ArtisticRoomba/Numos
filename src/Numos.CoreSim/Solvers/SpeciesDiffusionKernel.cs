using System.Buffers;

namespace Numos.CoreSim.Solvers;

/// <summary>
///     Holds the pooled per-chunk scratch that <see cref="SpeciesDiffusionKernel" /> reads and writes for one tick.
/// </summary>
/// <remarks>
///     <para>
///         The owning solver fills <see cref="NeighborIndices" />, <see cref="NeighborKinds" />, and
///         <see cref="NeighborCounts" /> through <see cref="NeighborResolution.ResolveNeighbors" /> before
///         the kernel runs.
///         The delta buffers are plain destination accumulators.
///         A solver's own gather (bulk flow in <see cref="AdvectionSolver" />) can fill them.
///         It then hands them to <see cref="SpeciesDiffusionKernel.ApplyDeltas" /> as the diffusion gather does.
///     </para>
///     <para>
///         Buffers that a voxel reads only for itself stay active-indexed.
///         Anything a gather reads about a <em>neighboring</em> voxel is voxel-indexed, because a neighbor is known
///         only by voxel index.
///         These buffers are <see cref="NeighborCounts" /> and <see cref="DiffusionTransfers" />.
///     </para>
/// </remarks>
internal struct DiffusionWorkspace
{
    public AtmosChunk Chunk;
    public Mole[] DiffusionTransfers;
    public Joule64[] EnergyDeltas;
    public Mole[] MoleDeltas;
    public int[] NeighborCounts;
    public ushort[] NeighborIndices;
    public NeighborKind[] NeighborKinds;

    /// <summary>
    ///     Rents the scratch storage required to diffuse one chunk.
    /// </summary>
    /// <param name="chunk">The chunk that will own this workspace for the tick.</param>
    public void Initialize(AtmosChunk chunk)
    {
        this = default;
        Chunk = chunk;
        int voxelCount = chunk.VoxelCount;
        int activeAirCount = chunk.ActiveAirCount;
        int slotCount = checked(activeAirCount * NeighborResolution.NeighborDirections.Length);
        int gasDeltaCount = checked(activeAirCount * chunk.ActiveGasCount);
        int transferCount = checked(voxelCount * chunk.ActiveGasCount);

        try
        {
            DiffusionTransfers = ArrayPool<Mole>.Shared.Rent(Math.Max(1, transferCount));
            EnergyDeltas = ArrayPool<Joule64>.Shared.Rent(Math.Max(1, activeAirCount));
            MoleDeltas = ArrayPool<Mole>.Shared.Rent(Math.Max(1, gasDeltaCount));
            NeighborCounts = ArrayPool<int>.Shared.Rent(voxelCount);
            NeighborIndices = ArrayPool<ushort>.Shared.Rent(Math.Max(1, slotCount));
            NeighborKinds = ArrayPool<NeighborKind>.Shared.Rent(Math.Max(1, slotCount));
        }
        catch
        {
            Release();
            throw;
        }
    }

    /// <summary>
    ///     Prepares the workspace for one chunk and keeps its buffers across ticks.
    /// </summary>
    /// <param name="chunk">The chunk that will own this workspace for the tick.</param>
    /// <remarks>
    ///     <para>
    ///         A buffer is replaced only when it is too small, so a workspace whose owner outlives the tick stops
    ///         allocating once it has seen its largest chunk. The kernel never reads a slot it has not written
    ///         this tick and never reads a buffer's length, so reused contents and extra capacity do not change
    ///         the result.
    ///     </para>
    ///     <para>
    ///         Do not call <see cref="Release" /> on a workspace prepared this way, because the buffers did not
    ///         come from the shared pool.
    ///     </para>
    /// </remarks>
    public void InitializeRetained(AtmosChunk chunk)
    {
        Chunk = chunk;
        int voxelCount = chunk.VoxelCount;
        int activeAirCount = chunk.ActiveAirCount;
        int slotCount = checked(activeAirCount * NeighborResolution.NeighborDirections.Length);
        int gasDeltaCount = checked(activeAirCount * chunk.ActiveGasCount);
        int transferCount = checked(voxelCount * chunk.ActiveGasCount);

        Grow(ref DiffusionTransfers, Math.Max(1, transferCount));
        Grow(ref EnergyDeltas, Math.Max(1, activeAirCount));
        Grow(ref MoleDeltas, Math.Max(1, gasDeltaCount));
        Grow(ref NeighborCounts, voxelCount);
        Grow(ref NeighborIndices, Math.Max(1, slotCount));
        Grow(ref NeighborKinds, Math.Max(1, slotCount));
    }

    /// <summary>
    ///     Replaces a retained buffer with a larger one when it cannot hold <paramref name="length" /> elements.
    /// </summary>
    /// <typeparam name="T">The buffer element type.</typeparam>
    /// <param name="buffer">The buffer to check.</param>
    /// <param name="length">The element count the buffer must hold.</param>
    private static void Grow<T>(ref T[] buffer, int length)
    {
        if (buffer == null || buffer.Length < length)
            buffer = GC.AllocateUninitializedArray<T>(length);
    }

    /// <summary>
    ///     Returns every rented buffer and clears references held by the workspace.
    /// </summary>
    public void Release()
    {
        Return(DiffusionTransfers);
        Return(EnergyDeltas);
        Return(MoleDeltas);
        Return(NeighborCounts);
        Return(NeighborIndices);
        Return(NeighborKinds);
        this = default;
    }

    /// <summary>
    ///     Returns an optional workspace buffer to its shared pool.
    /// </summary>
    /// <typeparam name="T">The buffer element type.</typeparam>
    /// <param name="buffer">The buffer to return, or <see langword="null" />.</param>
    private static void Return<T>(T[]? buffer)
    {
        if (buffer != null)
            ArrayPool<T>.Shared.Return(buffer);
    }
}

/// <summary>
///     Per-species diffusion between a chunk's resolved neighbors, plus the delta application both it and
///     bulk flow finish with.
/// </summary>
/// <remarks>
///     Every method covers one tile of active-air indices and writes only state that the tile's destination voxels own.
///     A solver can therefore run tiles in parallel behind barriers or run a whole chunk on one worker.
///     Both schedules give identical results.
///     The gather reads neighbors' post-bulk moles and temperature.
///     <see cref="PrepareDiffusion" /> must finish for every tile before any tile gathers.
/// </remarks>
internal static class SpeciesDiffusionKernel
{
    /// <summary>
    ///     Fixes how many moles of each gas every voxel in a tile will diffuse this tick.
    /// </summary>
    /// <param name="workspace">The workspace receiving the per-voxel, per-gas transfer amounts.</param>
    /// <param name="start">The first active-air index owned by this invocation.</param>
    /// <param name="length">The number of active-air indices owned by this invocation.</param>
    /// <param name="config">The immutable configuration snapshot for the tick.</param>
    /// <remarks>
    ///     <para>
    ///         A voxel's transfer depends on its post-bulk moles, pressure, and temperature, its neighbor count,
    ///         and the environment factor computed here.
    ///         All of these are local to that voxel.
    ///         They are final when this runs, because the same invocation just applied the bulk deltas.
    ///         The amount can therefore be computed once instead of once per reader.
    ///         The diffusion gather would otherwise redo it at each of the seven destinations that read a source.
    ///     </para>
    ///     <para>
    ///         An ineligible source stores zero, which the gather treats as "did not diffuse".
    ///         The environment factor scales with temperature, inverse pressure, and the inverse square of the voxel
    ///         edge length.
    ///     </para>
    /// </remarks>
    internal static void PrepareDiffusion(
        ref DiffusionWorkspace workspace,
        int start,
        int length,
        AtmosSolverConfigSnapshot config)
    {
        var chunk = workspace.Chunk!;
        int gasCount = chunk.ActiveGasCount;
        Metre dx = GetVoxelEdge(config.VoxelVolume);
        int end = start + length;
        for (int activeIndex = start; activeIndex < end; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];

            // Voxel-indexed for the same reason as NeighborCounts.
            // The gather reads a neighboring source's transfers and knows that neighbor only by voxel index.
            int transferBase = voxelIndex * gasCount;
            Pascal pressure = chunk.TotalPressure[voxelIndex];
            if (pressure <= 0f || workspace.NeighborCounts![voxelIndex] == 0)
            {
                Array.Clear(workspace.DiffusionTransfers!, transferBase, gasCount);
                continue;
            }

            Kelvin temperature = config.GetValidatedTemp(chunk.Temperature[voxelIndex]);
            PerSquareMetre environmentFactor = EnvironmentFactor(config, pressure, temperature, dx);

            for (int gas = 0; gas < gasCount; gas++)
            {
                var gasChannel = chunk.ActiveGases[gas];
                Mole sourceMoles = gasChannel.Moles[voxelIndex];
                workspace.DiffusionTransfers![transferBase + gas] = sourceMoles <= 0f
                    ? 0f
                    : TransferAmount(config.GetDiffusionCoefficient(gasChannel.GasId), environmentFactor, sourceMoles);
            }
        }
    }

    /// <summary>
    ///     Returns the edge length of a cubic voxel.
    /// </summary>
    /// <param name="voxelVolume">The configured voxel volume.</param>
    /// <remarks>
    ///     Every diffusion path must take its edge from this method.
    ///     Both sides of an exchange then scale by the same bits.
    ///     <see cref="MathF.Cbrt" /> rounds differently, so it cannot replace the power here.
    /// </remarks>
    internal static Metre GetVoxelEdge(CubicMetre voxelVolume)
    {
        return MathF.Pow(voxelVolume, 1f / 3f);
    }

    /// <summary>
    ///     Returns the environment factor of a source voxel.
    ///     It is the kinetic-theory <c>T^(3/2) / P</c> dependence of the diffusivity over the square of the voxel edge.
    ///     That is Fick's law on a cubic grid: face area over neighbor distance is one edge, and the source
    ///     concentration is its moles over an edge cubed.
    /// </summary>
    /// <param name="config">The immutable configuration snapshot for the tick.</param>
    /// <param name="pressure">The source voxel's positive total pressure.</param>
    /// <param name="temperature">The source voxel's validated temperature.</param>
    /// <param name="voxelEdge">The voxel edge from <see cref="GetVoxelEdge" />, computed once by the caller.</param>
    internal static PerSquareMetre EnvironmentFactor(
        AtmosSolverConfigSnapshot config, Pascal pressure, Kelvin temperature, Metre voxelEdge)
    {
        Scalar temperatureRatio = temperature / config.GlobalTemperature;
        Scalar pressureRatio = config.SaturationReferencePressure / pressure;
        return MathF.Pow(temperatureRatio, 1.5f) * pressureRatio / (voxelEdge * voxelEdge);
    }

    /// <summary>
    ///     Moles of one gas a source voxel sends to each of its neighbors this tick.
    /// </summary>
    /// <param name="referenceDiffusivity">The gas's configured diffusivity, in m²/s.</param>
    /// <param name="environmentFactor">The source voxel's <see cref="EnvironmentFactor" />.</param>
    /// <param name="sourceMoles">The source voxel's positive moles of the gas.</param>
    /// <remarks>
    ///     A voxel can lose at most one seventh of a gas to each of its six neighbors plus itself.
    ///     The cap keeps the debit from exceeding inventory.
    ///     The Euler stage evaluates this for both cells of a chunk face.
    ///     The expression order here is what both sides of that exchange agree on bit for bit.
    /// </remarks>
    internal static Mole TransferAmount(
        SquareMetrePerSecond referenceDiffusivity, PerSquareMetre environmentFactor, Mole sourceMoles)
    {
        PerSecond diffusionConstant = referenceDiffusivity * environmentFactor;
        Mole molesDiffused = diffusionConstant * sourceMoles * AtmosSolverConstants.FixedTimeStep;
        if (molesDiffused * 7 > sourceMoles)
            molesDiffused = sourceMoles / 7;

        return molesDiffused;
    }

    /// <summary>
    ///     Gathers every diffusion mole and thermal-energy delta owed to the destination voxels in one tile.
    /// </summary>
    /// <param name="workspace">The workspace containing resolved neighbors, diffusion transfers, and deltas.</param>
    /// <param name="start">The first destination active-air index owned by this invocation.</param>
    /// <param name="length">The number of destination active-air indices owned by this invocation.</param>
    /// <param name="config">The immutable configuration snapshot for the tick.</param>
    /// <remarks>
    ///     <para>
    ///         A destination receives one deposit from each air neighbor.
    ///         It pays its own transfer once per non-solid neighbor, void included.
    ///         Void takes gas out of the chunk but never receives any.
    ///         Deposits below <see cref="AtmosSolverConstants.MinimumTrackedMoles" /> into a destination that would
    ///         stay below it are suppressed.
    ///         The self debit is therefore partly refunded here, not computed from a smaller neighbor count.
    ///     </para>
    ///     <para>
    ///         The amounts come from <see cref="PrepareDiffusion" />. This phase only sums them.
    ///         Up to seven destinations read a source.
    ///         Recomputing its transfer at each one measurably cost more than the buffer it saves.
    ///     </para>
    ///     <para>
    ///         Sources run in ascending voxel index (<see cref="NeighborResolution.AscendingSourceSlots" />).
    ///         This fixes the floating-point accumulation order.
    ///         Changing it changes rounding and therefore deterministic replay state.
    ///     </para>
    /// </remarks>
    internal static void GatherDiffusionDeltas(
        ref DiffusionWorkspace workspace,
        int start,
        int length,
        AtmosSolverConfigSnapshot config)
    {
        var chunk = workspace.Chunk!;
        int gasCount = chunk.ActiveGasCount;
        int end = start + length;
        Span<DiffusionSource> sources = stackalloc DiffusionSource[NeighborResolution.AscendingSourceSlots.Length];
        Span<ushort> outgoingNeighbors = stackalloc ushort[NeighborResolution.NeighborDirections.Length];

        for (int activeIndex = start; activeIndex < end; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            int slotBase = activeIndex * NeighborResolution.NeighborDirections.Length;
            int validCount = workspace.NeighborCounts![voxelIndex];
            int sourceCount = 0;
            int outgoingCount = 0;

            for (int order = 0; order < NeighborResolution.AscendingSourceSlots.Length; order++)
            {
                int direction = NeighborResolution.AscendingSourceSlots[order];
                bool isSelf = direction == NeighborResolution.SelfSourceSlot;
                ushort sourceIndex;
                if (isSelf)
                {
                    sourceIndex = voxelIndex;
                }
                else
                {
                    if (workspace.NeighborKinds![slotBase + direction] != NeighborKind.Air)
                        continue;

                    sourceIndex = workspace.NeighborIndices![slotBase + direction];
                }

                sources[sourceCount++] = new DiffusionSource(
                    sourceIndex,
                    config.GetValidatedTemp(chunk.Temperature[sourceIndex]),
                    isSelf);
            }

            for (int direction = 0; direction < NeighborResolution.NeighborDirections.Length; direction++)
            {
                if (workspace.NeighborKinds![slotBase + direction] == NeighborKind.Air)
                    outgoingNeighbors[outgoingCount++] = workspace.NeighborIndices![slotBase + direction];
            }

            int deltaBase = activeIndex * gasCount;
            Joule64 energyTotal = 0d;
            for (int gas = 0; gas < gasCount; gas++)
            {
                var gasChannel = chunk.ActiveGases[gas];
                Mole[] gasMoles = gasChannel.Moles;
                JoulePerMoleKelvin molarHeatCapacity =
                    config.GetMolarHeatCapacityAtConstantVolume(gasChannel.GasId);

                Mole destinationMoles = gasMoles[voxelIndex];
                Mole moleDelta = 0f;
                Joule64 energyDelta = 0d;
                for (int index = 0; index < sourceCount; index++)
                {
                    var source = sources[index];
                    Mole molesDiffused = workspace.DiffusionTransfers![source.Voxel * gasCount + gas];

                    // Zero stands in for the eligibility tests PrepareDiffusion already ran.
                    // Skipping it is exact even for a source that really did diffuse nothing.
                    // Every term here is non-negative and both accumulators start at +0, so neither can hold -0.
                    // x - 0 and x + 0 are then bit-for-bit x.
                    // NaN is not zero and still propagates.
                    if (molesDiffused == 0f)
                        continue;

                    Joule64 energyTransferred =
                        (Mole64)molesDiffused * molarHeatCapacity * source.Temperature;

                    if (!source.IsSelf)
                    {
                        if (molesDiffused < AtmosSolverConstants.MinimumTrackedMoles &&
                            destinationMoles + molesDiffused < AtmosSolverConstants.MinimumTrackedMoles)
                        {
                            continue;
                        }

                        moleDelta += molesDiffused;
                        energyDelta += energyTransferred;
                        continue;
                    }

                    moleDelta -= molesDiffused * validCount;
                    energyDelta -= energyTransferred * validCount;

                    // Refunds are rare.
                    // The loop-invariant half of the suppression test guards the loop instead of running inside it.
                    if (molesDiffused < AtmosSolverConstants.MinimumTrackedMoles)
                    {
                        for (int outgoing = 0; outgoing < outgoingCount; outgoing++)
                        {
                            if (gasMoles[outgoingNeighbors[outgoing]] + molesDiffused <
                                AtmosSolverConstants.MinimumTrackedMoles)
                            {
                                moleDelta += molesDiffused;
                                energyDelta += energyTransferred;
                            }
                        }
                    }
                }

                workspace.MoleDeltas![deltaBase + gas] = moleDelta;
                energyTotal = gas == 0 ? energyDelta : energyTotal + energyDelta;
            }

            workspace.EnergyDeltas![activeIndex] = energyTotal;
        }
    }

    /// <summary>
    ///     Applies the gathered mole and energy deltas to the destination voxels in one tile.
    /// </summary>
    /// <param name="workspace">The workspace containing completed deltas for this tile's voxels.</param>
    /// <param name="start">The first destination active-air index owned by this invocation.</param>
    /// <param name="length">The number of destination active-air indices owned by this invocation.</param>
    /// <param name="config">The immutable configuration snapshot for the tick.</param>
    /// <remarks>
    ///     Each invocation exclusively owns its destination voxels.
    ///     Gas channels are visited in active channel order, so mole totals and heat capacity keep deterministic
    ///     rounding.
    /// </remarks>
    internal static void ApplyDeltas(
        ref DiffusionWorkspace workspace,
        int start,
        int length,
        AtmosSolverConfigSnapshot config)
    {
        var chunk = workspace.Chunk!;
        int gasCount = chunk.ActiveGasCount;
        int end = start + length;
        for (int activeIndex = start; activeIndex < end; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            int deltaBase = activeIndex * gasCount;
            Joule64 energyDelta = workspace.EnergyDeltas![activeIndex];
            bool anyMoleChange = false;
            for (int gas = 0; gas < gasCount; gas++)
                anyMoleChange |= workspace.MoleDeltas![deltaBase + gas] != 0f;

            if (energyDelta == 0d && !anyMoleChange)
                continue;

            // Temperature is the voxel's energy before transfer plus the net energy carried by moved moles,
            // divided by the heat capacity of the updated mixture.
            Joule64 oldEnergy =
                (Kelvin64)config.GetValidatedTemp(chunk.Temperature[voxelIndex]) *
                chunk.TotalHeatCapacity[voxelIndex];

            Mole totalMoles = 0f;
            JoulePerKelvin totalHeatCapacity = 0f;
            for (int gas = 0; gas < gasCount; gas++)
            {
                var gasChannel = chunk.ActiveGases[gas];
                Mole moles = gasChannel.Moles[voxelIndex] +
                             workspace.MoleDeltas![deltaBase + gas];

                gasChannel.Moles[voxelIndex] = moles;
                totalMoles += moles;
                totalHeatCapacity += moles *
                                     config.GetMolarHeatCapacityAtConstantVolume(gasChannel.GasId);
            }

            chunk.TotalHeatCapacity[voxelIndex] = totalHeatCapacity;
            if (totalHeatCapacity <= 0f)
                continue;

            chunk.Temperature[voxelIndex] = MathF.Max(
                0f,
                (Joule)((oldEnergy + energyDelta) / totalHeatCapacity));

            chunk.TotalPressure[voxelIndex] =
                AtmosSolverMath.CalculatePressureAtVoxel(config, chunk, voxelIndex, totalMoles);
        }
    }

    /// <summary>
    ///     One gas-independent diffusion source for a destination voxel.
    /// </summary>
    /// <param name="Voxel">The diffusing voxel.</param>
    /// <param name="Temperature">The source's validated temperature, carried with the diffused moles.</param>
    /// <param name="IsSelf">Whether the source is the destination, which pays a debit instead of a deposit.</param>
    private readonly record struct DiffusionSource(
        ushort Voxel,
        Kelvin Temperature,
        bool IsSelf);
}
