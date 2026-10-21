using System.Buffers;

namespace Numos.CoreSim.Solvers;

/// <summary>
///     Holds the pooled per-chunk scratch that <see cref="SpeciesDiffusionKernel" /> reads and writes for one tick.
/// </summary>
/// <remarks>
///     <para>
///         The owning solver fills <see cref="NeighborIndices" />, <see cref="NeighborKinds" />, and
///         <see cref="NeighborCounts" /> through <see cref="NeighborResolution.ResolveNeighbors" /> before
///         the kernel runs. The delta buffers are plain destination accumulators, so a solver's own gather
///         (bulk flow in <see cref="AdvectionSolver" />) can fill them and hand them to
///         <see cref="SpeciesDiffusionKernel.ApplyDeltas" /> the same way the diffusion gather does.
///     </para>
///     <para>
///         Buffers a voxel only ever reads for itself stay active-indexed, while anything a gather reads
///         about a <em>neighboring</em> voxel is voxel-indexed, because a neighbor is known only by voxel
///         index: <see cref="NeighborCounts" /> and <see cref="DiffusionTransfers" />.
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
///     Every method covers one tile of active-air indices and only writes state owned by that tile's
///     destination voxels, so a solver can run tiles in parallel behind barriers or run a whole chunk on
///     one worker and get identical results. The gather reads neighbors' post-bulk moles and temperature,
///     so <see cref="PrepareDiffusion" /> must have finished for every tile before any tile gathers.
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
    ///         Everything a voxel's transfer depends on -- post-bulk moles, pressure, and temperature,
    ///         its neighbor count, and the environment factor computed here -- is local to that voxel and
    ///         final by the time this runs, since the same invocation just applied the bulk deltas. That
    ///         is what lets the amount be computed once instead of once per reader: the diffusion gather
    ///         would otherwise redo it at each of the seven destinations that read a source.
    ///     </para>
    ///     <para>
    ///         An ineligible source stores zero, which the gather treats as "did not diffuse". The
    ///         environment factor scales with temperature, inverse pressure, and the inverse square of the voxel
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
        // Fick's law on a cubic grid: the face area over the neighbor distance is one edge, and the source
        // concentration is its moles over an edge cubed, so the per-tick transfer is D * n * dt / dx^2.
        Metre dx = MathF.Pow(config.VoxelVolume, 1f / 3f);
        int end = start + length;
        for (int activeIndex = start; activeIndex < end; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];

            // Voxel-indexed for the same reason as NeighborCounts: the gather reads a neighboring
            // source's transfers and knows that neighbor only by voxel index.
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
    ///     The environment factor of a source voxel: the kinetic-theory <c>T^(3/2) / P</c> dependence of the
    ///     diffusivity, divided by the squared voxel edge length (face area over neighbor distance, over source volume).
    /// </summary>
    /// <param name="config">The immutable configuration snapshot for the tick.</param>
    /// <param name="pressure">The source voxel's positive total pressure.</param>
    /// <param name="temperature">The source voxel's validated temperature.</param>
    /// <param name="voxelEdge"><c>VoxelVolume^(1/3)</c>, computed once by the caller.</param>
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
    /// <param name="referenceDiffusivity">The gas's configured diffusion coefficient.</param>
    /// <param name="environmentFactor">The source voxel's <see cref="EnvironmentFactor" />.</param>
    /// <param name="sourceMoles">The source voxel's positive moles of the gas.</param>
    /// <remarks>
    ///     A voxel can lose at most one seventh of a gas to each of its six neighbors plus itself, so the
    ///     per-neighbor amount is capped rather than letting the debit exceed inventory. The Euler stage evaluates
    ///     this for both cells of a chunk face, so the expression order here is what both sides of that exchange
    ///     agree on bit for bit.
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
    ///         A destination receives one deposit from each air neighbor and pays its own transfer once
    ///         per non-solid neighbor, void included: void takes gas out of the chunk but never receives
    ///         any. Deposits below <see cref="AtmosSolverConstants.MinimumTrackedMoles" /> into a
    ///         destination that would stay below it are suppressed, which is why the self debit is partly
    ///         refunded here rather than being computed from a smaller neighbor count.
    ///     </para>
    ///     <para>
    ///         The amounts themselves come from <see cref="PrepareDiffusion" />; this phase only sums
    ///         them, because a source is read by up to seven destinations and recomputing its transfer at
    ///         each one was measurably more expensive than the buffer it saves.
    ///     </para>
    ///     <para>
    ///         Sources run in ascending voxel index (<see cref="NeighborResolution.AscendingSourceSlots" />),
    ///         matching the active-index order the earlier per-gas scatter used, so the accumulation
    ///         reproduces its floating-point rounding exactly.
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

                    // Zero stands in for the eligibility tests PrepareDiffusion already ran, and
                    // skipping it is exact even for a source that really did diffuse nothing: every
                    // term here is non-negative and both accumulators start at +0, so neither can
                    // hold -0, and x - 0 and x + 0 are then bit-for-bit x. NaN is not zero and still
                    // propagates.
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

                    // Refunds are the rare case, so the loop-invariant half of the suppression test
                    // guards the loop instead of being re-evaluated inside it.
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
    ///     Each invocation exclusively owns its destination voxels. Gas channels are visited in active
    ///     channel order so mole totals and heat capacity retain deterministic rounding.
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

            // Reconstruct temperature from the voxel's energy before transfer plus the net energy
            // carried by moved moles, divided by the heat capacity of the updated mixture.
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
