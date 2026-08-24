namespace Numos.CoreSim.Solvers;

/// <summary>
///     Applies one gas injection while keeping mixture SHC, temperature, and pressure coherent.
/// </summary>
/// <remarks>
///     Callers validate the target and wake its room before entry. <see cref="AtmosChunk.InjectGasToVoxel" />
///     remains the single invariant guard at the storage boundary.
/// </remarks>
internal static class GasInjectionSolver
{
    internal static void Inject(AtmosChunk chunk, ushort localVoxelIndex, int gasId, float moles,
        float temperature, AtmosConfig config)
    {
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
            config.GetValidatedTemp(chunk.Temperature[localVoxelIndex]), config.PressurePerMoleKelvin);
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