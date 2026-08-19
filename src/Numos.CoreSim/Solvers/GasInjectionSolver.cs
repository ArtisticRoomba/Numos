using Numos.CoreSim.Datatypes.Primitives;

namespace Numos.CoreSim.Solvers;

/// <summary>
///     Applies one gas injection while keeping mixture SHC, temperature, and pressure coherent.
/// </summary>
internal static class GasInjectionSolver
{
    internal static void Inject(AtmosChunk chunk, ushort localVoxelIndex, int gasId, float moles,
        float temperature, AtmosConfig config)
    {
        if (!CanInject(chunk, localVoxelIndex))
            return;

        float currentHeatCapacity = 0f;
        for (var gas = 0; gas < chunk.ActiveGasCount; gas++)
        {
            float existingMoles = chunk.ActiveGases[gas].Moles[localVoxelIndex];
            if (existingMoles <= 0f)
                continue;
            currentHeatCapacity += existingMoles *
                                   AtmosSolverMath.GetMolarHeatCapacity(config, chunk.ActiveGases[gas].GasId);
        }

        InjectCore(chunk, localVoxelIndex, gasId, moles, temperature,
            AtmosSolverMath.GetMolarHeatCapacity(config, gasId), currentHeatCapacity,
            AtmosSolverMath.GetEffectiveTemperature(config, chunk.Temperature[localVoxelIndex]),
            AtmosPhysicalConstants.MolarGasConstant / AtmosSolverMath.GetVoxelVolume(config));
    }

    internal static void InjectDuringTick(AtmosChunk chunk, ushort localVoxelIndex, int gasId, float moles,
        float temperature, AtmosSolverConfigSnapshot config)
    {
        if (!CanInject(chunk, localVoxelIndex))
            return;

        float currentHeatCapacity = 0f;
        for (var gas = 0; gas < chunk.ActiveGasCount; gas++)
        {
            float existingMoles = chunk.ActiveGases[gas].Moles[localVoxelIndex];
            if (existingMoles <= 0f)
                continue;
            currentHeatCapacity += existingMoles *
                                   config.GetMolarHeatCapacityAtConstantVolume(chunk.ActiveGases[gas].GasId);
        }

        InjectCore(chunk, localVoxelIndex, gasId, moles, temperature,
            config.GetMolarHeatCapacityAtConstantVolume(gasId), currentHeatCapacity,
            config.GetEffectiveTemperature(chunk.Temperature[localVoxelIndex]), config.PressurePerMoleKelvin);
    }

    private static bool CanInject(AtmosChunk chunk, ushort localVoxelIndex)
    {
        if (!chunk.IsAwake)
            return false;

        int room = chunk.VoxelRoomMap[localVoxelIndex];
        return room != VoxelClassification.RoomSolid && room != VoxelClassification.RoomVoid;
    }

    private static void InjectCore(AtmosChunk chunk, ushort localVoxelIndex, int gasId, float moles,
        float temperature, float molarHeatCapacity, float currentHeatCapacity,
        float effectiveCurrentTemperature, float pressurePerMoleKelvin)
    {
        chunk.TotalHeatCapacity[localVoxelIndex] = currentHeatCapacity;
        if (currentHeatCapacity > 0f && !AtmosSolverMath.IsFinitePositive(chunk.Temperature[localVoxelIndex]))
            chunk.Temperature[localVoxelIndex] = effectiveCurrentTemperature;

        chunk.InjectGasToVoxel(localVoxelIndex, gasId, moles, temperature, molarHeatCapacity,
            pressurePerMoleKelvin);
    }

}