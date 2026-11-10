using System.Buffers;
using System.Numerics.Tensors;
using CommunityToolkit.HighPerformance.Helpers;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.Maths;

namespace Numos.CoreSim.Solvers;

/// <summary>
///     The <c>derived-refresh</c> stage: rebuilds <see cref="AtmosChunk.TotalPressure" />,
///     <see cref="AtmosChunk.TotalHeatCapacity" />, and <see cref="AtmosChunk.IsVacuum" /> from the gas moles and
///     temperatures.
/// </summary>
/// <remarks>
///     <para>
///         The legacy layout runs it right before <c>advection</c>, which reads the pressure cache and starts from
///         the heat capacity cache. The Euler layout leaves it out, because every Euler stage keeps the caches
///         current.
///     </para>
///     <para>
///         A voxel whose moles sum to zero or less is emptied through <see cref="AtmosChunk.SetVoxelToVacuum" />.
///         Every other active-air voxel has its vacuum flag cleared.
///     </para>
///     <para>
///         The stage keeps each voxel's mole total in <see cref="RefreshedMoleTotals" />, so <c>advection</c> does not
///         sum the gas channels again.
///     </para>
/// </remarks>
internal static class DerivedStateRefresh
{
    /// <summary>
    ///     Refreshes every awake chunk that has gas.
    /// </summary>
    /// <param name="context">The chunks and configuration snapshot for this stage.</param>
    /// <remarks>
    ///     An awake chunk without active gas channels keeps its caches. That is the set of chunks
    ///     <c>advection</c> solves, so the two stages see the same chunks. With enough chunks to fill the workers,
    ///     each chunk is one work item. Otherwise one phase clears every chunk's caches and a second phase
    ///     refreshes voxel tiles, so no tile accumulates into a cache another tile has yet to clear.
    /// </remarks>
    internal static void Apply(AtmosSolverExecutionContext context)
    {
        RefreshedMoleTotals handOff = RefreshedMoleTotals.Get(context);
        handOff.Begin(context.TickCount);
        if (context.Chunks.Length == 0)
            return;

        AtmosChunk[] chunks = ArrayPool<AtmosChunk>.Shared.Rent(context.Chunks.Length);
        Mole[][] totals = ArrayPool<Mole[]>.Shared.Rent(context.Chunks.Length);
        int count = 0;
        try
        {
            for (int chunkIndex = 0; chunkIndex < context.Chunks.Length; chunkIndex++)
            {
                var chunk = context.Chunks[chunkIndex];
                if (!chunk.IsAwake || chunk.ActiveGasCount == 0)
                    continue;

                chunks[count] = chunk;
                totals[count] = handOff.Add(chunk);
                count++;
            }

            if (count == 0)
                return;

            VoxelWorkItem[]? tiles = VoxelTiling.Plan(
                new ChunkActiveAirCounts(chunks), count, ParallelPhase.WorkerCount, out int tileCount);

            if (tiles == null)
            {
                ParallelPhase.Run(count, new RefreshChunkAction(chunks, totals, context.TickConfig));
            }
            else
            {
                try
                {
                    ParallelPhase.Run(count, new ClearChunkAction(chunks));
                    ParallelPhase.Run(tileCount, new RefreshTileAction(chunks, totals, tiles, context.TickConfig));
                }
                finally
                {
                    VoxelTiling.Return(tiles);
                }
            }

            handOff.Seal(chunks.AsSpan(0, count));
        }
        finally
        {
            ArrayPool<AtmosChunk>.Shared.Return(chunks, true);
            ArrayPool<Mole[]>.Shared.Return(totals, true);
        }
    }

    /// <summary>
    ///     Clears the pressure and heat capacity caches of one chunk, then recomputes them for every active-air
    ///     voxel.
    /// </summary>
    /// <param name="chunk">The chunk to refresh.</param>
    /// <param name="config">The configuration snapshot that supplies gas properties and the pressure law.</param>
    /// <remarks>
    ///     The clear covers the whole chunk, so a solid or void voxel reads zero pressure and heat capacity.
    /// </remarks>
    internal static void RefreshChunk(AtmosChunk chunk, AtmosSolverConfigSnapshot config)
    {
        if (chunk.ActiveAirCount == 0)
        {
            ClearCaches(chunk);
            return;
        }

        Mole[] totals = ArrayPool<Mole>.Shared.Rent(chunk.VoxelCount);
        try
        {
            RefreshChunkInto(chunk, config, totals);
        }
        finally
        {
            ArrayPool<Mole>.Shared.Return(totals);
        }
    }

    /// <summary>
    ///     Refreshes one chunk like <see cref="RefreshChunk" /> and keeps the voxel mole totals.
    /// </summary>
    /// <param name="chunk">The chunk to refresh.</param>
    /// <param name="config">The configuration snapshot that supplies gas properties and the pressure law.</param>
    /// <param name="totals">
    ///     Voxel-indexed output at least <see cref="AtmosChunk.VoxelCount" /> long. See
    ///     <see cref="RefreshActiveRange" /> for what it holds.
    /// </param>
    private static void RefreshChunkInto(AtmosChunk chunk, AtmosSolverConfigSnapshot config, Mole[] totals)
    {
        ClearCaches(chunk);
        if (chunk.ActiveAirCount == 0)
            return;

        Mole[] scratch = ArrayPool<Mole>.Shared.Rent(chunk.VoxelCount);
        try
        {
            RefreshActiveRange(chunk, config, 0, chunk.ActiveAirCount, totals, scratch);
        }
        finally
        {
            ArrayPool<Mole>.Shared.Return(scratch);
        }
    }

    /// <summary>
    ///     Zeroes the pressure and heat capacity caches of one chunk.
    /// </summary>
    /// <param name="chunk">The chunk to clear.</param>
    private static void ClearCaches(AtmosChunk chunk)
    {
        chunk.TotalPressure.Clear();
        chunk.TotalHeatCapacity.Clear();
    }

    /// <summary>
    ///     Recomputes pressure and heat capacity over a range of active-air voxels.
    /// </summary>
    /// <param name="chunk">The chunk that owns the voxels.</param>
    /// <param name="config">The configuration snapshot that supplies gas properties and the pressure law.</param>
    /// <param name="start">The first index into <see cref="AtmosChunk.ActiveAirIndices" />.</param>
    /// <param name="length">The number of active-air voxels to refresh.</param>
    /// <param name="totals">
    ///     Voxel-indexed output. Each refreshed voxel receives its mole total, or zero when the refresh emptied it.
    ///     Zero is what a new sum over the emptied channels gives, so a reader cannot tell the two apart.
    /// </param>
    /// <param name="scratch">Scratch storage at least as long as the longest contiguous voxel run.</param>
    /// <remarks>
    ///     <para>
    ///         The heat capacity cache must be zero over the range on entry, because the gas loop accumulates into
    ///         it. <see cref="RefreshChunk" /> clears it first.
    ///     </para>
    ///     <para>
    ///         Both sums run over gas channels in channel order, and each voxel adds its channels in that order.
    ///         A horizontal reduction or a different channel order changes the rounding and therefore replay state.
    ///         <see cref="TensorPrimitives.MaxNumber{T}(ReadOnlySpan{T}, T, Span{T})" /> keeps the positive-moles
    ///         guard for negative and NaN values. <see cref="TensorPrimitives.MultiplyAdd{T}(ReadOnlySpan{T}, T, ReadOnlySpan{T}, Span{T})" />
    ///         rounds the multiply and the add separately, as the scalar sum did, instead of fusing them.
    ///     </para>
    ///     <para>
    ///         Pressure comes before heat capacity for each run. An emptied voxel then contributes no heat capacity,
    ///         because <see cref="AtmosChunk.SetVoxelToVacuum" /> has already zeroed its moles.
    ///     </para>
    /// </remarks>
    internal static void RefreshActiveRange(
        AtmosChunk chunk,
        AtmosSolverConfigSnapshot config,
        int start,
        int length,
        Span<Mole> totals,
        Span<Mole> scratch)
    {
        int end = start + length;
        int activeIndex = start;
        while (activeIndex < end)
        {
            int runStart = chunk.ActiveAirIndices[activeIndex++];
            int runLength = 1;
            while (activeIndex < end && chunk.ActiveAirIndices[activeIndex] == runStart + runLength)
            {
                activeIndex++;
                runLength++;
            }

            Span<Mole> totalMoles = totals.Slice(runStart, runLength);
            totalMoles.Clear();
            for (int gas = 0; gas < chunk.ActiveGasCount; gas++)
                TensorPrimitives.Add<float>(totalMoles, chunk.ActiveGases[gas].Moles.AsSpan(runStart, runLength), totalMoles);

            for (int index = 0; index < runLength; index++)
            {
                ushort voxelIndex = (ushort)(runStart + index);
                chunk.TotalPressure[voxelIndex] =
                    AtmosSolverMath.CalculatePressureAtVoxel(config, chunk, voxelIndex, totalMoles[index]);

                // CalculatePressureAtVoxel empties a voxel whose sum is zero or less.
                if (totalMoles[index] <= 0f)
                    totalMoles[index] = 0f;
            }

            Span<JoulePerKelvin> heatCapacity = chunk.TotalHeatCapacity.AsSpan().Slice(runStart, runLength);
            Span<Mole> heatScratch = scratch[..runLength];
            for (int gas = 0; gas < chunk.ActiveGasCount; gas++)
            {
                JoulePerMoleKelvin molarHeatCapacity =
                    config.GetMolarHeatCapacityAtConstantVolume(chunk.ActiveGases[gas].GasId);

                TensorPrimitives.MaxNumber(chunk.ActiveGases[gas].Moles.AsSpan(runStart, runLength), 0f, heatScratch);
                TensorPrimitives.MultiplyAdd<float>(heatScratch, molarHeatCapacity, heatCapacity, heatCapacity);
            }
        }
    }

    private readonly struct ChunkActiveAirCounts(AtmosChunk[] chunks) : IActiveAirCounts
    {
        public int GetActiveAirCount(int index)
        {
            return chunks[index].ActiveAirCount;
        }
    }

    private readonly struct ClearChunkAction(AtmosChunk[] chunks) : IAction
    {
        public void Invoke(int index)
        {
            ClearCaches(chunks[index]);
        }
    }

    private readonly struct RefreshTileAction(
        AtmosChunk[] chunks,
        Mole[][] totals,
        VoxelWorkItem[] tiles,
        AtmosSolverConfigSnapshot config) : IAction
    {
        public void Invoke(int index)
        {
            VoxelWorkItem tile = tiles[index];
            Mole[] scratch = ArrayPool<Mole>.Shared.Rent(tile.Length);
            try
            {
                RefreshActiveRange(
                    chunks[tile.WorkspaceIndex], config, tile.Start, tile.Length, totals[tile.WorkspaceIndex], scratch);
            }
            finally
            {
                ArrayPool<Mole>.Shared.Return(scratch);
            }
        }
    }

    private readonly struct RefreshChunkAction(
        AtmosChunk[] chunks,
        Mole[][] totals,
        AtmosSolverConfigSnapshot config) : IAction
    {
        public void Invoke(int index)
        {
            RefreshChunkInto(chunks[index], config, totals[index]);
        }
    }
}
