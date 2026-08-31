namespace Numos.CoreSim.Solvers;

/// <summary>
///     Applies configured gas condensation and its constant-volume internal-energy change.
/// </summary>
internal sealed class PhaseChangeSolver
{
    private const int MaximumEquilibriumIterations = 24;
    private const Mole64 MinimumMoleTolerance = 1e-7d;
    private const Scalar64 RelativeMoleTolerance = 1d / (1 << 23);

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

        PerKelvin64 inverseBoilingPoint = 1d / properties.BoilingPoint;
        for (var activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            Mole gasMoles = chunk.ActiveGases[gasIndex].Moles[voxelIndex];
            if (gasMoles <= AtmosSolverConstants.MinimumMolesForCondensation)
                continue;

            Kelvin temperature = config.GetValidatedTemp(chunk.Temperature[voxelIndex]);
            Mole64 saturationMoles = CalculateSaturationMoles(
                config, properties, temperature, inverseBoilingPoint);
            if (gasMoles <= saturationMoles)
                continue;

            JoulePerMole molarInternalEnergyOfVaporization = MathF.Max(0f,
                properties.MolarEnthalpyOfVaporization -
                AtmosPhysicalConstants.MolarGasConstant * temperature);

            Mole64 equilibriumRemainingMoles = CalculateEquilibriumRemainingMoles(
                chunk, config, gasIndex, voxelIndex, gasMoles, temperature,
                saturationMoles, molarInternalEnergyOfVaporization, properties,
                inverseBoilingPoint);
            Mole64 equilibriumCondensedMoles = gasMoles - equilibriumRemainingMoles;
            Mole64 targetRemainingMoles = gasMoles - Math.Min(gasMoles,
                equilibriumCondensedMoles * config.CondensationRateFactor);
            Mole remainingMoles = (float)targetRemainingMoles;
            if (remainingMoles < targetRemainingMoles)
                remainingMoles = MathF.BitIncrement(remainingMoles);
            remainingMoles = Math.Clamp(remainingMoles, 0f, gasMoles);

            Mole molesToCondense = gasMoles - remainingMoles;
            if (molesToCondense <= 0f)
                continue;

            ApplyCondensation(chunk, config, gasIndex, voxelIndex, temperature,
                remainingMoles, molesToCondense, molarInternalEnergyOfVaporization);
        }
    }

    /// <summary>
    ///     Finds the remaining vapor amount whose warmed partial pressure equals its saturation pressure.
    ///     A safeguarded Newton solve operates in log-mole space so large inventories and saturation pressures
    ///     do not require an overflow-prone pressure round trip.
    /// </summary>
    private static Mole64 CalculateEquilibriumRemainingMoles(AtmosChunk chunk,
        AtmosSolverConfigSnapshot config, int gasIndex, ushort voxelIndex, Mole64 gasMoles,
        Kelvin64 initialTemperature, Mole64 initialSaturationMoles,
        JoulePerMole64 molarInternalEnergyOfVaporization, GasProperties properties,
        PerKelvin64 inverseBoilingPoint)
    {
        JoulePerMoleKelvin64 molarHeatCapacity =
            config.GetMolarHeatCapacityAtConstantVolume(chunk.ActiveGases[gasIndex].GasId);
        JoulePerKelvin64 otherHeatCapacity = CalculateOtherHeatCapacityAtVoxel(
            chunk, config, gasIndex, voxelIndex);
        JoulePerKelvin64 initialTotalHeatCapacity = otherHeatCapacity + gasMoles * molarHeatCapacity;

        if (molarInternalEnergyOfVaporization == 0d)
            return Math.Clamp(initialSaturationMoles, 0d, gasMoles);

        Mole64 lowerBound = 0d;
        Mole64 upperBound = gasMoles;
        Mole64 candidate = Math.Clamp(initialSaturationMoles, lowerBound, upperBound);
        Mole64 moleTolerance = Math.Max(MinimumMoleTolerance,
            gasMoles * RelativeMoleTolerance);

        for (var iteration = 0; iteration < MaximumEquilibriumIterations; iteration++)
        {
            if (candidate <= lowerBound || candidate >= upperBound)
                candidate = (lowerBound + upperBound) * 0.5d;

            Mole64 previousWidth = upperBound - lowerBound;
            Scalar64 residual = CalculateSaturationLogResidual(config, gasMoles,
                initialTemperature, initialTotalHeatCapacity, otherHeatCapacity,
                molarHeatCapacity, molarInternalEnergyOfVaporization, properties,
                inverseBoilingPoint, candidate, out PerMole64 residualDerivative);

            if (residual > 0d)
                upperBound = candidate;
            else
                lowerBound = candidate;

            // A valid Newton step normally contracts the bracket faster than bisection. If it hugs one
            // endpoint, also evaluate the retained interval's midpoint so every iteration still halves
            // the previous width in the worst case.
            if (upperBound - lowerBound > previousWidth * 0.5d)
            {
                candidate = (lowerBound + upperBound) * 0.5d;
                residual = CalculateSaturationLogResidual(config, gasMoles,
                    initialTemperature, initialTotalHeatCapacity, otherHeatCapacity,
                    molarHeatCapacity, molarInternalEnergyOfVaporization, properties,
                    inverseBoilingPoint, candidate, out residualDerivative);
                if (residual > 0d)
                    upperBound = candidate;
                else
                    lowerBound = candidate;
            }

            if (upperBound - lowerBound <= moleTolerance ||
                (float)lowerBound == (float)upperBound)
                break;

            Mole64 nextCandidate = double.NaN;
            if (double.IsFinite(residual) && double.IsFinite(residualDerivative) &&
                residualDerivative != 0d)
                nextCandidate = candidate - residual / residualDerivative;

            candidate = nextCandidate > lowerBound && nextCandidate < upperBound
                ? nextCandidate
                : (lowerBound + upperBound) * 0.5d;
        }

        // Return the supersaturated side of the bracket. ProcessGas also rounds the target toward
        // more remaining vapor when converting it back to the float-backed simulation state.
        return upperBound;
    }

    private static Scalar64 CalculateSaturationLogResidual(AtmosSolverConfigSnapshot config,
        Mole64 gasMoles, Kelvin64 initialTemperature, JoulePerKelvin64 initialTotalHeatCapacity,
        JoulePerKelvin64 otherHeatCapacity, JoulePerMoleKelvin64 molarHeatCapacity,
        JoulePerMole64 molarInternalEnergyOfVaporization, GasProperties properties,
        PerKelvin64 inverseBoilingPoint, Mole64 remainingMoles, out PerMole64 residualDerivative)
    {
        JoulePerKelvin64 remainingHeatCapacity = otherHeatCapacity + remainingMoles * molarHeatCapacity;
        if (remainingHeatCapacity <= 0d || !double.IsFinite(remainingHeatCapacity))
        {
            residualDerivative = double.NaN;
            return double.NegativeInfinity;
        }

        Mole64 condensedMoles = gasMoles - remainingMoles;
        Kelvin64 temperature = initialTemperature +
                               condensedMoles / remainingHeatCapacity *
                               molarInternalEnergyOfVaporization;
        if (temperature <= 0d || !double.IsFinite(temperature))
        {
            residualDerivative = double.NaN;
            return double.NegativeInfinity;
        }

        Scalar64 logSaturationMoles = Math.Log(config.SaturationReferencePressure) -
                                      Math.Log(config.PressurePerMoleKelvin) -
                                      Math.Log(temperature) -
                                      properties.MolarEnthalpyOfVaporization /
                                      AtmosPhysicalConstants.MolarGasConstant *
                                      (1d / temperature - inverseBoilingPoint);
        KelvinPerMole64 temperatureDerivative = -molarInternalEnergyOfVaporization *
                                                initialTotalHeatCapacity /
                                                (remainingHeatCapacity * remainingHeatCapacity);
        PerKelvin64 saturationLogTemperatureDerivative =
            properties.MolarEnthalpyOfVaporization /
            (AtmosPhysicalConstants.MolarGasConstant * temperature * temperature) -
            1d / temperature;
        residualDerivative = 1d / remainingMoles -
                             saturationLogTemperatureDerivative * temperatureDerivative;
        return Math.Log(remainingMoles) - logSaturationMoles;
    }

    private static JoulePerKelvin64 CalculateOtherHeatCapacityAtVoxel(AtmosChunk chunk,
        AtmosSolverConfigSnapshot config, int excludedGasIndex, ushort voxelIndex)
    {
        JoulePerKelvin64 totalHeatCapacity = 0d;
        for (var gas = 0; gas < chunk.ActiveGasCount; gas++)
        {
            if (gas == excludedGasIndex)
                continue;

            Mole moles = chunk.ActiveGases[gas].Moles[voxelIndex];
            if (moles <= 0f)
                continue;

            totalHeatCapacity += (double)moles *
                                 config.GetMolarHeatCapacityAtConstantVolume(
                                     chunk.ActiveGases[gas].GasId);
        }

        return totalHeatCapacity;
    }

    private static Mole64 CalculateSaturationMoles(AtmosSolverConfigSnapshot config,
        GasProperties properties, Kelvin64 temperature, PerKelvin64 inverseBoilingPoint)
    {
        Scalar64 logSaturationMoles = Math.Log(config.SaturationReferencePressure) -
                                      Math.Log(config.PressurePerMoleKelvin) -
                                      Math.Log(temperature) -
                                      properties.MolarEnthalpyOfVaporization /
                                      AtmosPhysicalConstants.MolarGasConstant *
                                      (1d / temperature - inverseBoilingPoint);
        return Math.Exp(logSaturationMoles);
    }

    private static void ApplyCondensation(AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        int gasIndex, ushort voxelIndex, Kelvin temperature, Mole remainingMoles,
        Mole condensedMoles, JoulePerMole molarInternalEnergyOfVaporization)
    {
        chunk.ActiveGases[gasIndex].Moles[voxelIndex] = remainingMoles;

        JoulePerKelvin newHeatCapacity = AtmosSolverMath.CalculateHeatCapacityAtVoxel(
            config, chunk, voxelIndex);
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