using System.Buffers;
using CommunityToolkit.HighPerformance.Helpers;
using Numos.CoreSim.Datatypes.Events;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.Maths;

namespace Numos.CoreSim.Solvers;

internal sealed partial class AdvectionSolver
{
    private readonly struct WorkspaceActiveAirCounts(ChunkWorkspace[] workspaces) : IActiveAirCounts
    {
        public int GetActiveAirCount(int index)
        {
            return workspaces[index].Chunk!.ActiveAirCount;
        }
    }

    private readonly struct SolveChunkWorkspaceAction(
        ChunkWorkspace[] workspaces,
        AtmosSolverConfigSnapshot config,
        Second deltaTime,
        int interval,
        int sleepDelayTicks) : IAction
    {
        public void Invoke(int index)
        {
            var chunk = workspaces[index].Chunk;
            BoundaryEventBatch<BoundaryFlowEvent> boundaryBatch = workspaces[index].BoundaryBatch;
            Mole[]? refreshedTotalMoles = workspaces[index].RefreshedTotalMoles;
            try
            {
                workspaces[index].Initialize(chunk, boundaryBatch, refreshedTotalMoles);
                SolveChunkWorkspace(ref workspaces[index], index, config, deltaTime, interval, sleepDelayTicks);
            }
            finally
            {
                workspaces[index].Release();
            }
        }
    }

    private readonly struct InitializeWorkspaceAction(ChunkWorkspace[] workspaces) : IAction
    {
        public void Invoke(int index)
        {
            var chunk = workspaces[index].Chunk;
            BoundaryEventBatch<BoundaryFlowEvent> boundaryBatch = workspaces[index].BoundaryBatch;
            Mole[]? refreshedTotalMoles = workspaces[index].RefreshedTotalMoles;
            workspaces[index].Initialize(chunk, boundaryBatch, refreshedTotalMoles);
        }
    }

    private readonly struct ReleaseWorkspaceAction(ChunkWorkspace[] workspaces) : IAction
    {
        public void Invoke(int index)
        {
            workspaces[index].Release();
        }
    }

    /// <summary>
    ///     Holds one chunk's pooled scratch buffers for a single tick.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Every phase partitions destination voxels, so no two workers write the same scratch element.
    ///         The workspace stays alive across phase barriers, so later phases reuse resolved geometry and
    ///         derived values.
    ///     </para>
    ///     <para>
    ///         Buffers that a voxel reads only for itself stay active-indexed.
    ///         Anything a gather reads about a <em>neighboring</em> voxel is voxel-indexed, because a neighbor is
    ///         known only by voxel index.
    ///         These buffers are <see cref="BulkMoleFractions" />, <see cref="DiffusionWorkspace.NeighborCounts" />,
    ///         and <see cref="DiffusionWorkspace.DiffusionTransfers" />.
    ///     </para>
    ///     <para>
    ///         Resolved neighbors and the destination delta buffers live in <see cref="Diffusion" />.
    ///         <see cref="SpeciesDiffusionKernel" /> can then run on them directly.
    ///         Bulk flow gathers into the same delta buffers and applies them with
    ///         <see cref="SpeciesDiffusionKernel.ApplyDeltas" />.
    ///     </para>
    /// </remarks>
    private struct ChunkWorkspace
    {
        public BoundaryEventBatch<BoundaryFlowEvent> BoundaryBatch;
        public AtmosChunk Chunk;
        public bool[] BoundaryEligible;
        public Scalar[] BulkMoleFractions;
        public MolePerPascal[] Capacitance;
        public DiffusionWorkspace Diffusion;
        public MolePerPascal[] EdgeConductance;
        public MolePerPascal[] IncidentConductance;
        public Scalar[] MaximumRelativePressureDeltas;
        public Mole[] TotalMoles;

        /// <summary>
        ///     The voxel mole totals <c>derived-refresh</c> kept for this chunk, or <see langword="null" /> when the
        ///     stage sums them itself. <see cref="RefreshedMoleTotals" /> owns the buffer. The workspace only reads it.
        /// </summary>
        public Mole[]? RefreshedTotalMoles;

        /// <summary>
        ///     Rents and clears the scratch storage required to solve one chunk.
        /// </summary>
        /// <param name="chunk">The chunk that will own this workspace for the tick.</param>
        /// <param name="boundaryBatch">The reusable batch exclusively owned by this workspace.</param>
        /// <param name="refreshedTotalMoles">
        ///     The refreshed totals to use as <see cref="TotalMoles" />, or <see langword="null" /> to rent a buffer.
        /// </param>
        public void Initialize(
            AtmosChunk chunk, BoundaryEventBatch<BoundaryFlowEvent> boundaryBatch, Mole[]? refreshedTotalMoles)
        {
            this = default;
            BoundaryBatch = boundaryBatch;
            Chunk = chunk;
            RefreshedTotalMoles = refreshedTotalMoles;
            int voxelCount = chunk.VoxelCount;
            int activeAirCount = chunk.ActiveAirCount;
            int edgeSlotCount = checked(voxelCount * NeighborResolution.NeighborDirections.Length);

            try
            {
                BoundaryEligible = ArrayPool<bool>.Shared.Rent(Math.Max(1, activeAirCount));
                BulkMoleFractions = ArrayPool<Scalar>.Shared.Rent(edgeSlotCount);
                Capacitance = ArrayPool<MolePerPascal>.Shared.Rent(voxelCount);
                Diffusion.Initialize(chunk);
                EdgeConductance = ArrayPool<MolePerPascal>.Shared.Rent(edgeSlotCount);
                IncidentConductance = ArrayPool<MolePerPascal>.Shared.Rent(voxelCount);
                MaximumRelativePressureDeltas = ArrayPool<Scalar>.Shared.Rent(Math.Max(1, activeAirCount));
                TotalMoles = refreshedTotalMoles ?? ArrayPool<Mole>.Shared.Rent(voxelCount);

                Array.Clear(Capacitance, 0, voxelCount);
                Array.Clear(IncidentConductance, 0, voxelCount);
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
            PooledBuffer.Return(BoundaryEligible);
            PooledBuffer.Return(BulkMoleFractions);
            PooledBuffer.Return(Capacitance);
            Diffusion.Release();
            PooledBuffer.Return(EdgeConductance);
            PooledBuffer.Return(IncidentConductance);
            PooledBuffer.Return(MaximumRelativePressureDeltas);
            if (RefreshedTotalMoles == null)
                PooledBuffer.Return(TotalMoles);

            this = default;
        }
    }

    private readonly struct ResolveWorkspaceAction(
        ChunkWorkspace[] workspaces,
        VoxelWorkItem[] workItems) : IAction
    {
        public void Invoke(int index)
        {
            var workItem = workItems[index];
            ResolveWorkspace(ref workspaces[workItem.WorkspaceIndex], workItem);
        }
    }

    private readonly struct ReduceConductanceAction(
        ChunkWorkspace[] workspaces,
        VoxelWorkItem[] workItems,
        AtmosSolverConfigSnapshot config) : IAction
    {
        public void Invoke(int index)
        {
            var workItem = workItems[index];
            ReduceIncidentConductance(ref workspaces[workItem.WorkspaceIndex], workItem, config);
        }
    }

    private readonly struct ComputeConductanceAction(
        ChunkWorkspace[] workspaces,
        VoxelWorkItem[] workItems,
        AtmosSolverConfigSnapshot config) : IAction
    {
        public void Invoke(int index)
        {
            var workItem = workItems[index];
            ComputeConductance(ref workspaces[workItem.WorkspaceIndex], workItem, config);
        }
    }

    private readonly struct ComputeBulkFlowAction(
        ChunkWorkspace[] workspaces,
        VoxelWorkItem[] workItems,
        AtmosSolverConfigSnapshot config) : IAction
    {
        public void Invoke(int index)
        {
            var workItem = workItems[index];
            ComputeBulkFlow(ref workspaces[workItem.WorkspaceIndex], workItem, config);
        }
    }

    private readonly struct GatherBulkDeltasAction(
        ChunkWorkspace[] workspaces,
        VoxelWorkItem[] workItems,
        AtmosSolverConfigSnapshot config) : IAction
    {
        public void Invoke(int index)
        {
            var workItem = workItems[index];
            GatherBulkDeltas(ref workspaces[workItem.WorkspaceIndex], workItem, config);
        }
    }

    private readonly struct ApplyBulkDeltasAction(
        ChunkWorkspace[] workspaces,
        VoxelWorkItem[] workItems,
        AtmosSolverConfigSnapshot config,
        Second deltaTime) : IAction
    {
        public void Invoke(int index)
        {
            var workItem = workItems[index];
            ref DiffusionWorkspace diffusion = ref workspaces[workItem.WorkspaceIndex].Diffusion;
            SpeciesDiffusionKernel.ApplyDeltas(ref diffusion, workItem.Start, workItem.Length, config);
            SpeciesDiffusionKernel.PrepareDiffusion(ref diffusion, workItem.Start, workItem.Length, config, deltaTime);
        }
    }

    private readonly struct ApplyDiffusionDeltasAction(
        ChunkWorkspace[] workspaces,
        VoxelWorkItem[] workItems,
        AtmosSolverConfigSnapshot config) : IAction
    {
        public void Invoke(int index)
        {
            var workItem = workItems[index];
            SpeciesDiffusionKernel.ApplyDeltas(
                ref workspaces[workItem.WorkspaceIndex].Diffusion, workItem.Start, workItem.Length, config);
        }
    }

    private readonly struct PublishBoundaryEventsAction(
        ChunkWorkspace[] workspaces) : IAction
    {
        public void Invoke(int index)
        {
            PublishBoundaryEvents(ref workspaces[index]);
        }
    }

    private readonly struct GatherDiffusionDeltasAction(
        ChunkWorkspace[] workspaces,
        VoxelWorkItem[] workItems,
        AtmosSolverConfigSnapshot config) : IAction
    {
        public void Invoke(int index)
        {
            var workItem = workItems[index];
            SpeciesDiffusionKernel.GatherDiffusionDeltas(
                ref workspaces[workItem.WorkspaceIndex].Diffusion, workItem.Start, workItem.Length, config);
        }
    }
}
