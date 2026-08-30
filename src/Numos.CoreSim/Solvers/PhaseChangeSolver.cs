namespace Numos.CoreSim.Solvers;

/// <summary>
///     Applies configured gas condensation and its constant-volume internal-energy change.
/// </summary>
internal sealed class PhaseChangeSolver
{
    internal void Solve(AtmosChunk chunk, AtmosSolverConfigSnapshot config)
    {
        if (config.CondensationRateFactor <= 0f)
            return;

        for (var gas = 0; gas < chunk.ActiveGasCount; gas++)
            ProcessGas(chunk, config, gas);
    }

    private static void ProcessGas(AtmosChunk chunk, AtmosSolverConfigSnapshot config, int gasIndex)
    {
        int gasId = chunk.ActiveGases[gasIndex].GasId;
        if (!config.TryGetGasProperties(gasId, out var properties) || !properties.CondensationEnabled)
            return;
        if (!AtmosSolverMath.IsFinitePositive(properties.BoilingPoint) ||
            !AtmosSolverMath.IsFinitePositive(properties.MolarEnthalpyOfVaporization))
            return;

        float inverseBoilingPoint = 1f / properties.BoilingPoint;
        for (var activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            float gasMoles = chunk.ActiveGases[gasIndex].Moles[voxelIndex];
            if (gasMoles <= AtmosSolverConstants.MinimumMolesForCondensation)
                continue;

            float temperature = config.GetValidatedTemp(chunk.Temperature[voxelIndex]);
            float saturationPressure = CalculateSaturationPressure(
                config, properties, temperature, inverseBoilingPoint);
            float saturationMoles = AtmosSolverMath.PressureToMoles(
                config, saturationPressure, temperature);
            if (gasMoles <= saturationMoles)
                continue;

            // Since P = nRT/V at fixed T and V, the pressure excess can be converted directly into a
            // mole excess. This avoids an overflow-prone pressure round trip for large inventories.
            float molesToCondense = (gasMoles - saturationMoles) * config.CondensationRateFactor;
            if (molesToCondense <= 0f)
                continue;

            ApplyCondensation(chunk, config, gasIndex, voxelIndex, temperature,
                molesToCondense, properties.MolarEnthalpyOfVaporization);
        }
    }

    private static float CalculateSaturationPressure(AtmosSolverConfigSnapshot config,
        GasProperties properties, float temperature, float inverseBoilingPoint)
    {
        float exponent = -properties.MolarEnthalpyOfVaporization /
                         AtmosPhysicalConstants.MolarGasConstant *
                         (1f / temperature - inverseBoilingPoint);
        return config.SaturationReferencePressure * MathF.Exp(exponent);
    }

    private static void ApplyCondensation(AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        int gasIndex, ushort voxelIndex, float temperature, float condensedMoles,
        float molarEnthalpyOfVaporization)
    {
        chunk.ActiveGases[gasIndex].Moles[voxelIndex] -= condensedMoles;

        float newHeatCapacity = AtmosSolverMath.CalculateHeatCapacityAtVoxel(config, chunk, voxelIndex);
        float molarInternalEnergyOfVaporization = MathF.Max(0f,
            molarEnthalpyOfVaporization - AtmosPhysicalConstants.MolarGasConstant * temperature);
        chunk.TotalHeatCapacity[voxelIndex] = newHeatCapacity;
        if (newHeatCapacity > 0f)
        {
            // Algebraically this is (T*C_remaining + n_condensed*U_vap) / C_remaining. Dividing
            // before multiplying avoids both C*T overflow and the cancellation of two large energies.
            chunk.Temperature[voxelIndex] = MathF.Max(0f,
                temperature + condensedMoles / newHeatCapacity * molarInternalEnergyOfVaporization);
        }

        chunk.TotalPressure[voxelIndex] =
            AtmosSolverMath.CalculatePressureAtVoxel(config, chunk, voxelIndex);
    }
}