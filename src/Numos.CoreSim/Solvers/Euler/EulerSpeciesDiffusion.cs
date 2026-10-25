using System.Buffers;
using CommunityToolkit.HighPerformance.Helpers;
using Numos.Maths;

namespace Numos.CoreSim.Solvers.Euler;

/// <summary>
///     The species diffusion pass of the <c>euler-flow</c> stage: the legacy intra-chunk kernel over every active
///     chunk's written-back fields, then the same exchange across the faces between active chunks.
/// </summary>
/// <remarks>
///     <para>
///         The sweeps move species only with the bulk flow, so two gases at equal pressure on either side of a
///         doorway would never mix without this pass. Inside a chunk it is <see cref="SpeciesDiffusionKernel" />
///         run on the chunk fields after the write-back, so a chunk at rest diffuses bit for bit what the
///         <c>advection</c> stage would. Diffusion swaps species at equal pressure and carries no bulk momentum, so
///         the momentum arrays are left alone. <see cref="SpeciesDiffusionKernel.ApplyDeltas" /> rebuilds
///         temperature, heat capacity, and pressure.
///     </para>
///     <para>
///         Faces between chunks are handled the way the sweeps handle them: for every open face between two
///         active chunks, both sides evaluate the per-source amount of both cells from the same live fields, and
///         each applies the net to its own cell. The two evaluations are the same bits and
///         <c>fl(x − y) = −fl(y − x)</c>, so the exchange is exactly antisymmetric without a reserve-and-merge
///         pass. A face into a chunk that is not active exchanges nothing and does not wake it. A void voxel across
///         a chunk face is a sink, as it is inside a chunk.
///     </para>
///     <para>
///         The face amounts are computed for every chunk before any chunk applies them, because the cells a chunk
///         writes are the cells its neighbour reads. Each phase runs in parallel over the active chunks: compute
///         writes only its own chunk's record buffer and apply only its own chunk's cells, so neither depends on
///         which worker runs what. Boundary work is a surface, so it is not tiled below one item per chunk.
///     </para>
/// </remarks>
internal static class EulerSpeciesDiffusion
{
    /// <summary>
    ///     Runs the whole pass over the active set. Tick thread only, after every chunk has been written back.
    /// </summary>
    /// <param name="active">The active workspaces, in canonical order.</param>
    /// <param name="activeCount">How many of <paramref name="active" /> are bound this tick.</param>
    /// <param name="workerCount">The worker count the schedule arithmetic uses.</param>
    /// <param name="config">The tick's configuration snapshot.</param>
    /// <remarks>
    ///     Two schedules, as in the sweeps. With at least as many active chunks as workers, one item per chunk runs
    ///     the kernel's three phases back to back. Otherwise the active voxels are tiled and each phase runs over
    ///     every tile behind a barrier, because the gather reads neighbouring voxels' transfer amounts. Both
    ///     schedules call the same kernel methods over the same ranges in the same phase order, so they produce
    ///     the same bits.
    /// </remarks>
    internal static void Run(
        EulerChunkWorkspace[] active, int activeCount, int workerCount, AtmosSolverConfigSnapshot config)
    {
        if (activeCount == 0)
            return;

        VoxelWorkItem[]? workItems = null;
        int workItemCount = 0;
        try
        {
            if (activeCount >= workerCount)
            {
                ParallelPhase.Run(activeCount, new DiffuseChunkAction(active, config));
            }
            else
            {
                ParallelPhase.Run(activeCount, new InitializeAction(active));

                int totalActiveAirCount = 0;
                for (int index = 0; index < activeCount; index++)
                    totalActiveAirCount = checked(totalActiveAirCount + active[index].Chunk.ActiveAirCount);

                int tileSize = Math.Max(1, ParallelPhase.DivideRoundUp(totalActiveAirCount, workerCount));
                for (int index = 0; index < activeCount; index++)
                {
                    workItemCount = checked(
                        workItemCount + ParallelPhase.DivideRoundUp(active[index].Chunk.ActiveAirCount, tileSize));
                }

                workItems = ArrayPool<VoxelWorkItem>.Shared.Rent(Math.Max(1, workItemCount));
                int item = 0;
                for (int index = 0; index < activeCount; index++)
                {
                    int activeAirCount = active[index].Chunk.ActiveAirCount;
                    for (int start = 0; start < activeAirCount; start += tileSize)
                        workItems[item++] = new VoxelWorkItem(index, start, Math.Min(tileSize, activeAirCount - start));
                }

                ParallelPhase.Run(workItemCount, new ResolveAndPrepareAction(active, workItems, config));
                ParallelPhase.Run(workItemCount, new GatherAction(active, workItems, config));
                ParallelPhase.Run(workItemCount, new ApplyAction(active, workItems, config));
            }

            ParallelPhase.Run(activeCount, new ComputeBoundaryAction(active, config));
            ParallelPhase.Run(activeCount, new ApplyBoundaryAction(active, config));
        }
        finally
        {
            if (workItems != null)
                ArrayPool<VoxelWorkItem>.Shared.Return(workItems);
        }
    }

    /// <summary>
    ///     Prepares the kernel's retained scratch for one chunk, sized for the channel count after the write-back.
    /// </summary>
    private static void Initialize(EulerChunkWorkspace workspace)
    {
        workspace.Diffusion.InitializeRetained(workspace.Chunk);
    }

    /// <summary>
    ///     Resolves the in-chunk neighbours of one tile of voxels and fixes their transfer amounts.
    /// </summary>
    /// <remarks>
    ///     <see cref="NeighborResolution.ResolveNeighbors" /> classifies by voxel room. An air neighbour missing
    ///     from the open mask, because <see cref="EulerFaceTopology.IsFaceOpen" /> declined the face, is demoted to
    ///     blocked so diffusion follows the same topology as the flow. Faces that leave the chunk are left to the
    ///     boundary exchange.
    /// </remarks>
    private static void ResolveAndPrepare(
        EulerChunkWorkspace workspace, int start, int length, AtmosSolverConfigSnapshot config)
    {
        AtmosChunk chunk = workspace.Chunk;
        ref DiffusionWorkspace diffusion = ref workspace.Diffusion;
        int end = start + length;
        for (int activeIndex = start; activeIndex < end; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            int slotBase = activeIndex * NeighborResolution.NeighborDirections.Length;
            int count = NeighborResolution.ResolveNeighbors(
                chunk, chunk.GetXyzInt3(voxelIndex), diffusion.NeighborIndices, diffusion.NeighborKinds, slotBase);

            byte open = workspace.OpenMask[voxelIndex];
            for (int direction = 0; direction < NeighborResolution.NeighborDirections.Length; direction++)
            {
                if (diffusion.NeighborKinds[slotBase + direction] != NeighborKind.Air ||
                    EulerFaceTopology.IsOpen(open, direction))
                    continue;

                diffusion.NeighborKinds[slotBase + direction] = NeighborKind.Blocked;
                count--;
            }

            diffusion.NeighborCounts[voxelIndex] = count;
        }

        SpeciesDiffusionKernel.PrepareDiffusion(ref diffusion, start, length, config);
    }

    /// <summary>
    ///     Records the net exchange of every boundary voxel of one chunk with its active neighbours.
    /// </summary>
    /// <remarks>
    ///     Reads live fields of this chunk and its neighbours and writes only this chunk's
    ///     <see cref="EulerBoundaryExchange" />. Neighbour fields are stable because no apply phase has started.
    /// </remarks>
    private static void ComputeBoundary(EulerChunkWorkspace workspace, AtmosSolverConfigSnapshot config)
    {
        AtmosChunk chunk = workspace.Chunk;
        EulerBoundaryExchange exchange = workspace.Boundary;
        exchange.Clear();
        Int3 last = chunk.Dimensions - new Int3(1, 1, 1);
        // Must match PrepareDiffusion so both sides of a face scale by the same edge length.
        Metre voxelEdge = MathF.Pow(config.VoxelVolume, 1f / 3f);

        for (int activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            byte open = workspace.OpenMask[voxelIndex];
            byte sink = workspace.VoidMask[voxelIndex];
            if ((open | sink) == 0)
                continue;

            Int3 position = chunk.GetXyzInt3(voxelIndex);
            if (!NeighborResolution.IsBoundary(chunk, position))
                continue;

            bool begun = false;
            Joule64 energyDelta = 0d;
            for (int direction = 0; direction < NeighborResolution.NeighborDirections.Length; direction++)
            {
                if (!EulerFaceTopology.IsOnEdge(position, last, direction))
                    continue;

                AtmosChunk? neighborChunk;
                if (EulerFaceTopology.IsOpen(open, direction))
                {
                    EulerChunkWorkspace? neighbor = workspace.NeighborWorkspaces[direction];
                    if (neighbor == null)
                        continue;

                    neighborChunk = neighbor.Chunk;
                }
                else if (EulerFaceTopology.IsVoid(sink, direction))
                {
                    neighborChunk = null;
                }
                else
                {
                    continue;
                }

                if (!begun)
                {
                    exchange.BeginVoxel(voxelIndex);
                    begun = true;
                }

                ExchangeFace(
                    chunk, voxelIndex, neighborChunk,
                    EulerFaceTopology.NeighborIndexAcrossFace(chunk, voxelIndex, direction),
                    config, voxelEdge, exchange, ref energyDelta);
            }

            if (begun)
                exchange.EndVoxel(energyDelta);
        }
    }

    /// <summary>
    ///     Records one face of one cell: a deposit of each of the neighbour's gases and a debit of each of the
    ///     cell's own, with the energy the moles carry at their source temperature.
    /// </summary>
    /// <param name="chunk">The chunk owning the cell.</param>
    /// <param name="voxelIndex">The cell.</param>
    /// <param name="neighborChunk">The active chunk across the face, or <see langword="null" /> for a void face.</param>
    /// <param name="neighborIndex">The cell across the face, in <paramref name="neighborChunk" />.</param>
    /// <param name="config">The tick's configuration snapshot.</param>
    /// <param name="voxelEdge"><c>VoxelVolume^(1/3)</c>.</param>
    /// <param name="exchange">The record buffer of <paramref name="chunk" />.</param>
    /// <param name="energyDelta">The cell's running energy delta.</param>
    /// <remarks>
    ///     <para>
    ///         Each amount is <see cref="SpeciesDiffusionKernel.TransferAmount" /> of one cell alone. Suppression
    ///         follows the gather: a deposit below <see cref="AtmosSolverConstants.MinimumTrackedMoles" /> into a
    ///         cell that would stay below it is dropped and the payer keeps it. Both sides test the same two cells,
    ///         so the neighbour drops exactly the deposits this cell refunds, and the net for a gas is
    ///         <c>q_neighbour − q_own</c> here and <c>q_own − q_neighbour</c> there, exact negatives.
    ///     </para>
    ///     <para>
    ///         The cell's own channels come first, in channel order, then the neighbour's gases this chunk has no
    ///         channel for. The neighbour walks its channels in a different order, which changes nothing because
    ///         each gas's amount depends on one cell only.
    ///     </para>
    ///     <para>
    ///         A void face pays the cell's amount of every gas into the sink without the suppression test, since
    ///         nothing receives it.
    ///     </para>
    /// </remarks>
    private static void ExchangeFace(
        AtmosChunk chunk, ushort voxelIndex, AtmosChunk? neighborChunk, ushort neighborIndex,
        AtmosSolverConfigSnapshot config, Metre voxelEdge, EulerBoundaryExchange exchange, ref Joule64 energyDelta)
    {
        Kelvin temperature = config.GetValidatedTemp(chunk.Temperature[voxelIndex]);
        Pascal pressure = chunk.TotalPressure[voxelIndex];
        PerSquareMetre factor = pressure > 0f
            ? SpeciesDiffusionKernel.EnvironmentFactor(config, pressure, temperature, voxelEdge)
            : 0f;

        if (neighborChunk == null)
        {
            for (int gas = 0; gas < chunk.ActiveGasCount; gas++)
            {
                GasChannel channel = chunk.ActiveGases[gas];
                Mole moles = channel.Moles[voxelIndex];
                if (moles <= 0f)
                    continue;

                Mole debit = SpeciesDiffusionKernel.TransferAmount(config.GetDiffusionCoefficient(channel.GasId), factor, moles);
                if (debit == 0f)
                    continue;

                exchange.Add(channel.GasId, -debit);
                energyDelta -= (Mole64)debit * config.GetMolarHeatCapacityAtConstantVolume(channel.GasId) * temperature;
            }

            return;
        }

        Kelvin neighborTemperature = config.GetValidatedTemp(neighborChunk.Temperature[neighborIndex]);
        Pascal neighborPressure = neighborChunk.TotalPressure[neighborIndex];
        PerSquareMetre neighborFactor = neighborPressure > 0f
            ? SpeciesDiffusionKernel.EnvironmentFactor(config, neighborPressure, neighborTemperature, voxelEdge)
            : 0f;

        for (int gas = 0; gas < chunk.ActiveGasCount; gas++)
        {
            int gasId = chunk.ActiveGases[gas].GasId;
            Mole moles = chunk.ActiveGases[gas].Moles[voxelIndex];
            int neighborChannel = neighborChunk.FindGasChannel(gasId);
            Mole neighborMoles = neighborChannel >= 0 ? neighborChunk.ActiveGases[neighborChannel].Moles[neighborIndex] : 0f;
            SquareMetrePerSecond diffusivity = config.GetDiffusionCoefficient(gasId);
            Mole debit = Amount(diffusivity, factor, moles, neighborMoles);
            Mole deposit = Amount(diffusivity, neighborFactor, neighborMoles, moles);
            Record(gasId, deposit, debit, neighborTemperature, temperature, config, exchange, ref energyDelta);
        }

        for (int gas = 0; gas < neighborChunk.ActiveGasCount; gas++)
        {
            int gasId = neighborChunk.ActiveGases[gas].GasId;
            if (chunk.FindGasChannel(gasId) >= 0)
                continue;

            Mole deposit = Amount(
                config.GetDiffusionCoefficient(gasId), neighborFactor,
                neighborChunk.ActiveGases[gas].Moles[neighborIndex], 0f);

            Record(gasId, deposit, 0f, neighborTemperature, temperature, config, exchange, ref energyDelta);
        }
    }

    /// <summary>
    ///     The amount a source cell sends across one face, zero when the source has none of the gas or the
    ///     suppression rule drops the deposit.
    /// </summary>
    private static Mole Amount(
        SquareMetrePerSecond diffusivity, PerSquareMetre sourceFactor, Mole sourceMoles, Mole destinationMoles)
    {
        if (sourceMoles <= 0f || sourceFactor == 0f)
            return 0f;

        Mole molesDiffused = SpeciesDiffusionKernel.TransferAmount(diffusivity, sourceFactor, sourceMoles);
        if (molesDiffused < AtmosSolverConstants.MinimumTrackedMoles &&
            destinationMoles + molesDiffused < AtmosSolverConstants.MinimumTrackedMoles)
            return 0f;

        return molesDiffused;
    }

    /// <summary>
    ///     Appends the net of one gas on one face. An exactly zero net is skipped. The neighbour skips it by the
    ///     same test, and adding it would change no bit.
    /// </summary>
    private static void Record(
        int gasId, Mole deposit, Mole debit, Kelvin depositTemperature, Kelvin debitTemperature,
        AtmosSolverConfigSnapshot config, EulerBoundaryExchange exchange, ref Joule64 energyDelta)
    {
        Mole net = deposit - debit;
        if (net == 0f)
            return;

        exchange.Add(gasId, net);
        JoulePerMoleKelvin molarHeatCapacity = config.GetMolarHeatCapacityAtConstantVolume(gasId);
        energyDelta += (Mole64)deposit * molarHeatCapacity * depositTemperature;
        energyDelta -= (Mole64)debit * molarHeatCapacity * debitTemperature;
    }

    /// <summary>
    ///     Applies one chunk's recorded exchange to its own cells and rebuilds their temperature, heat capacity,
    ///     and pressure the way <see cref="SpeciesDiffusionKernel.ApplyDeltas" /> does.
    /// </summary>
    /// <remarks>
    ///     A deposit of a gas the chunk has no channel for creates one. Suppression guarantees such a deposit is a
    ///     tracked amount, so diffusion never grows a channel for a trace.
    /// </remarks>
    private static void ApplyBoundary(EulerChunkWorkspace workspace, AtmosSolverConfigSnapshot config)
    {
        AtmosChunk chunk = workspace.Chunk;
        EulerBoundaryExchange exchange = workspace.Boundary;
        int record = 0;
        for (int entry = 0; entry < exchange.VoxelCount; entry++)
        {
            ushort voxelIndex = exchange.Voxels[entry];
            int end = exchange.RecordEnds[entry];
            Joule64 oldEnergy =
                (Kelvin64)config.GetValidatedTemp(chunk.Temperature[voxelIndex]) * chunk.TotalHeatCapacity[voxelIndex];

            for (; record < end; record++)
            {
                int channel = chunk.GetOrCreateGasChannel(exchange.GasIds[record]);
                chunk.ActiveGases[channel].Moles[voxelIndex] += exchange.MoleDeltas[record];
            }

            Mole totalMoles = 0f;
            JoulePerKelvin totalHeatCapacity = 0f;
            for (int gas = 0; gas < chunk.ActiveGasCount; gas++)
            {
                Mole moles = chunk.ActiveGases[gas].Moles[voxelIndex];
                totalMoles += moles;
                totalHeatCapacity += moles * config.GetMolarHeatCapacityAtConstantVolume(chunk.ActiveGases[gas].GasId);
            }

            chunk.TotalHeatCapacity[voxelIndex] = totalHeatCapacity;
            if (totalHeatCapacity <= 0f)
                continue;

            chunk.Temperature[voxelIndex] = MathF.Max(
                0f,
                (Kelvin)((oldEnergy + exchange.EnergyDeltas[entry]) / totalHeatCapacity));

            chunk.TotalPressure[voxelIndex] =
                AtmosSolverMath.CalculatePressureAtVoxel(config, chunk, voxelIndex, totalMoles);
        }
    }

    /// <summary>
    ///     A contiguous range of one chunk's active-air list for the tiled schedule.
    /// </summary>
    private readonly record struct VoxelWorkItem(int WorkspaceIndex, int Start, int Length);

    private readonly struct DiffuseChunkAction(EulerChunkWorkspace[] active, AtmosSolverConfigSnapshot config) : IAction
    {
        public void Invoke(int index)
        {
            EulerChunkWorkspace workspace = active[index];
            int activeAirCount = workspace.Chunk.ActiveAirCount;
            Initialize(workspace);
            ResolveAndPrepare(workspace, 0, activeAirCount, config);
            SpeciesDiffusionKernel.GatherDiffusionDeltas(ref workspace.Diffusion, 0, activeAirCount, config);
            SpeciesDiffusionKernel.ApplyDeltas(ref workspace.Diffusion, 0, activeAirCount, config);
        }
    }

    private readonly struct InitializeAction(EulerChunkWorkspace[] active) : IAction
    {
        public void Invoke(int index)
        {
            Initialize(active[index]);
        }
    }

    private readonly struct ResolveAndPrepareAction(
        EulerChunkWorkspace[] active, VoxelWorkItem[] items, AtmosSolverConfigSnapshot config) : IAction
    {
        public void Invoke(int index)
        {
            VoxelWorkItem item = items[index];
            ResolveAndPrepare(active[item.WorkspaceIndex], item.Start, item.Length, config);
        }
    }

    private readonly struct GatherAction(
        EulerChunkWorkspace[] active, VoxelWorkItem[] items, AtmosSolverConfigSnapshot config) : IAction
    {
        public void Invoke(int index)
        {
            VoxelWorkItem item = items[index];
            SpeciesDiffusionKernel.GatherDiffusionDeltas(
                ref active[item.WorkspaceIndex].Diffusion, item.Start, item.Length, config);
        }
    }

    private readonly struct ApplyAction(
        EulerChunkWorkspace[] active, VoxelWorkItem[] items, AtmosSolverConfigSnapshot config) : IAction
    {
        public void Invoke(int index)
        {
            VoxelWorkItem item = items[index];
            SpeciesDiffusionKernel.ApplyDeltas(ref active[item.WorkspaceIndex].Diffusion, item.Start, item.Length, config);
        }
    }

    private readonly struct ComputeBoundaryAction(EulerChunkWorkspace[] active, AtmosSolverConfigSnapshot config) : IAction
    {
        public void Invoke(int index)
        {
            ComputeBoundary(active[index], config);
        }
    }

    private readonly struct ApplyBoundaryAction(EulerChunkWorkspace[] active, AtmosSolverConfigSnapshot config) : IAction
    {
        public void Invoke(int index)
        {
            ApplyBoundary(active[index], config);
        }
    }
}

/// <summary>
///     One chunk's recorded cross-chunk diffusion for the tick: per touched voxel, its energy delta and a run of
///     (gas id, mole delta) records, in the order they are applied.
/// </summary>
/// <remarks>
///     Records rather than per-voxel-per-gas arrays because the exchange touches a surface, not a volume, and a
///     deposit can be of a gas the chunk has no channel for yet. The arrays grow on demand and stay with the
///     pooled workspace.
/// </remarks>
internal sealed class EulerBoundaryExchange
{
    public Joule64[] EnergyDeltas = [];
    public int[] GasIds = [];
    public Mole[] MoleDeltas = [];
    public int RecordCount;
    public int[] RecordEnds = [];
    public int VoxelCount;
    public ushort[] Voxels = [];
    private int _recordStart;

    public void Clear()
    {
        VoxelCount = 0;
        RecordCount = 0;
    }

    public void BeginVoxel(ushort voxelIndex)
    {
        if (VoxelCount == Voxels.Length)
        {
            int capacity = Math.Max(16, VoxelCount * 2);
            Array.Resize(ref Voxels, capacity);
            Array.Resize(ref EnergyDeltas, capacity);
            Array.Resize(ref RecordEnds, capacity);
        }

        Voxels[VoxelCount] = voxelIndex;
        _recordStart = RecordCount;
    }

    public void Add(int gasId, Mole moleDelta)
    {
        if (RecordCount == GasIds.Length)
        {
            int capacity = Math.Max(32, RecordCount * 2);
            Array.Resize(ref GasIds, capacity);
            Array.Resize(ref MoleDeltas, capacity);
        }

        GasIds[RecordCount] = gasId;
        MoleDeltas[RecordCount] = moleDelta;
        RecordCount++;
    }

    /// <summary>
    ///     Closes the voxel begun by <see cref="BeginVoxel" />. A voxel with no records is dropped.
    /// </summary>
    public void EndVoxel(Joule64 energyDelta)
    {
        if (RecordCount == _recordStart)
            return;

        EnergyDeltas[VoxelCount] = energyDelta;
        RecordEnds[VoxelCount] = RecordCount;
        VoxelCount++;
    }
}
