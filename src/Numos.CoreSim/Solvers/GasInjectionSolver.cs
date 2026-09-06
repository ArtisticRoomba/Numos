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
    private Dictionary<Int3, Queue<InjectionEvent>> _injectionBuffer = new();
    
    internal static void Inject(
        AtmosChunk chunk, ushort localVoxelIndex, int gasId, Mole moles,
        Kelvin temperature, IAtmosConfig config)
    {
        JoulePerKelvin currentHeatCapacity = 0f;
        for (int gas = 0; gas < chunk.ActiveGasCount; gas++)
        {
            Mole existingMoles = chunk.ActiveGases[gas].Moles[localVoxelIndex];
            if (existingMoles <= 0f)
                continue;

            currentHeatCapacity += existingMoles *
                                   config.GetMolarHeatCapacityAtConstantVolume(chunk.ActiveGases[gas].GasId);
        }

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
        JoulePerKelvin currentHeatCapacity = 0f;
        for (int gas = 0; gas < chunk.ActiveGasCount; gas++)
        {
            Mole existingMoles = chunk.ActiveGases[gas].Moles[localVoxelIndex];
            if (existingMoles <= 0f)
                continue;

            currentHeatCapacity += existingMoles *
                                   config.GetMolarHeatCapacityAtConstantVolume(chunk.ActiveGases[gas].GasId);
        }

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
            while (queue.Count > 0)
            {
                var ev = queue.Dequeue();

                JoulePerKelvin currentHeatCapacity = 0f;
                for (int gas = 0; gas < chunk.ActiveGasCount; gas++)
                {
                    Mole existingMoles = chunk.ActiveGases[gas].Moles[ev.LocalVoxelIndex];
                    if (existingMoles <= 0f)
                        continue;

                    currentHeatCapacity += existingMoles *
                                        config.GetMolarHeatCapacityAtConstantVolume(chunk.ActiveGases[gas].GasId);
                }
                
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
