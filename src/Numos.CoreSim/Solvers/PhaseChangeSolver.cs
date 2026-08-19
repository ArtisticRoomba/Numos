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
        float molarHeatCapacity = config.GetMolarHeatCapacityAtConstantVolume(gasId);
        for (var activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            float gasMoles = chunk.ActiveGases[gasIndex].Moles[voxelIndex];
            if (gasMoles <= AtmosSolverConstants.MinimumMolesForCondensation)
                continue;

            float temperature = config.GetEffectiveTemperature(chunk.Temperature[voxelIndex]);
            float saturationPressure = CalculateSaturationPressure(
                config, properties, temperature, inverseBoilingPoint);
            float partialPressure = AtmosSolverMath.CalculatePressure(config, gasMoles, temperature);
            if (partialPressure <= saturationPressure)
                continue;

            float molesToCondense = AtmosSolverMath.PressureToMoles(
                                        config, partialPressure - saturationPressure, temperature) *
                                    config.CondensationRateFactor;
            ApplyCondensation(chunk, config, gasIndex, voxelIndex, temperature,
                MathF.Min(gasMoles, molesToCondense), molarHeatCapacity,
                properties.MolarEnthalpyOfVaporization);
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
        float molarHeatCapacity, float molarEnthalpyOfVaporization)
    {
        chunk.ActiveGases[gasIndex].Moles[voxelIndex] -= condensedMoles;

        float oldHeatCapacity = chunk.TotalHeatCapacity[voxelIndex];
        float condensedHeatCapacity = condensedMoles * molarHeatCapacity;
        float newHeatCapacity = MathF.Max(0f, oldHeatCapacity - condensedHeatCapacity);
        float molarInternalEnergyOfVaporization = MathF.Max(0f,
            molarEnthalpyOfVaporization - AtmosPhysicalConstants.MolarGasConstant * temperature);
        float remainingEnergy = temperature * oldHeatCapacity -
                                temperature * condensedHeatCapacity +
                                condensedMoles * molarInternalEnergyOfVaporization;
        chunk.TotalHeatCapacity[voxelIndex] = newHeatCapacity;
        if (newHeatCapacity > 0f)
            chunk.Temperature[voxelIndex] = MathF.Max(0f, remainingEnergy / newHeatCapacity);
        chunk.TotalPressure[voxelIndex] =
            AtmosSolverMath.CalculatePressureAtVoxel(config, chunk, voxelIndex);
    }
}