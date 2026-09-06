using Numos.Maths;

namespace Numos.CoreSim.Solvers;

/// <summary>
///     Applies one gas injection while keeping mixture SHC, temperature, and pressure coherent.
/// </summary>
/// <remarks>
///     Callers validate the target and wake its room before entry. <see cref="AtmosChunk.InjectGasToVoxel" />
///     remains the single invariant guard at the storage boundary.
/// </remarks>
internal class GasInjectionSolver
{
    private readonly Dictionary<Int3, Queue<InjectionEvent>> _injectionBuffer = new();

    // Scratch set reused across RunQueuedInjections batches to avoid re-deriving heat
    // capacity from composition for a voxel that's already been touched this batch.
    private readonly HashSet<ushort> _resyncedVoxels = new();

    /// <summary>
    ///     Public entry point for injecting gas outside the tick/solver flow (e.g. explosions, tools).
    ///     Distinct from the instance <see cref="Inject(AtmosChunk, ushort, int, Mole, Kelvin, AtmosSolverConfigSnapshot, bool)" />
    ///     overload, which is used during ticked solving against a config snapshot.
    /// </summary>
    internal static void Inject(
        AtmosChunk chunk, ushort localVoxelIndex, int gasId, Mole moles,
        Kelvin temperature, IAtmosConfig config)
    {
        JoulePerKelvin currentHeatCapacity = AtmosSolverMath.CalculateHeatCapacityAtVoxel(config, chunk, localVoxelIndex);

        InjectCore(
            chunk,
            localVoxelIndex,
            gasId,
            moles,
            temperature,
            config.GetMolarHeatCapacityAtConstantVolume(gasId),
            currentHeatCapacity,
            config.GetValidatedTemp(chunk.Temperature[localVoxelIndex]),
            AtmosPhysicalConstants.MolarGasConstant / config.GetVoxelVolume());
    }

    internal void Inject(
        AtmosChunk chunk, ushort localVoxelIndex, int gasId, Mole moles,
        Kelvin temperature, AtmosSolverConfigSnapshot config, bool queueInjection = false)
    {
        if (queueInjection)
        {
            var gridPosition = chunk.GridPosition;
            if (!_injectionBuffer.TryGetValue(gridPosition, out var queue))
            {
                queue = new Queue<InjectionEvent>();
                _injectionBuffer.Add(gridPosition, queue);
            }
            queue.Enqueue(new InjectionEvent(localVoxelIndex, gasId, moles, temperature));
            return;
        }

        JoulePerKelvin currentHeatCapacity = AtmosSolverMath.CalculateHeatCapacityAtVoxel(config, chunk, localVoxelIndex);

        InjectCore(
            chunk,
            localVoxelIndex,
            gasId,
            moles,
            temperature,
            config.GetMolarHeatCapacityAtConstantVolume(gasId),
            currentHeatCapacity,
            config.GetValidatedTemp(chunk.Temperature[localVoxelIndex]),
            config.PressurePerMoleKelvin);
    }

    internal void RunQueuedInjections(AtmosSolverExecutionContext context, AtmosSolverConfigSnapshot config)
    {
        foreach (var (chunkPosition, queue) in _injectionBuffer)
        {
            if (!context.World.TryGetChunk(chunkPosition, out var chunk))
                continue;

            // A voxel can appear in this queue multiple times in one batch — once per gas
            // species, once per boundary direction that flowed into/out of it, etc.
            // InjectCore/InjectGasToVoxel keep TotalHeatCapacity updated incrementally after
            // every call, so composition only needs to be re-derived from ActiveGases once
            // per voxel per batch; subsequent events for that voxel can trust the running
            // total instead of re-summing every active gas from scratch.
            _resyncedVoxels.Clear();

            while (queue.Count > 0)
            {
                var ev = queue.Dequeue();

                JoulePerKelvin currentHeatCapacity = _resyncedVoxels.Add(ev.LocalVoxelIndex)
                    ? AtmosSolverMath.CalculateHeatCapacityAtVoxel(config, chunk, ev.LocalVoxelIndex)
                    : chunk.TotalHeatCapacity[ev.LocalVoxelIndex];

                InjectCore(
                    chunk,
                    ev.LocalVoxelIndex,
                    ev.GasId,
                    ev.Moles,
                    ev.Temperature,
                    config.GetMolarHeatCapacityAtConstantVolume(ev.GasId),
                    currentHeatCapacity,
                    config.GetValidatedTemp(chunk.Temperature[ev.LocalVoxelIndex]),
                    config.PressurePerMoleKelvin);
            }
        }
    }

    internal void ClearQueue()
    {
        _injectionBuffer.Clear();
    }

    private static void InjectCore(
        AtmosChunk chunk, ushort localVoxelIndex, int gasId, Mole moles,
        Kelvin temperature, JoulePerMoleKelvin molarHeatCapacity, JoulePerKelvin currentHeatCapacity,
        Kelvin effectiveCurrentTemperature, PascalPerMoleKelvin pressurePerMoleKelvin)
    {
        chunk.TotalHeatCapacity[localVoxelIndex] = currentHeatCapacity;
        if (currentHeatCapacity > 0f && !AtmosSolverMath.IsFinitePositive(chunk.Temperature[localVoxelIndex]))
            chunk.Temperature[localVoxelIndex] = effectiveCurrentTemperature;

        chunk.InjectGasToVoxel(
            localVoxelIndex,
            gasId,
            moles,
            temperature,
            molarHeatCapacity,
            pressurePerMoleKelvin);
    }
}

internal record InjectionEvent(ushort LocalVoxelIndex, int GasId, Mole Moles, Kelvin Temperature);