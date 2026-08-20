namespace Numos.CoreSim.Solvers;

/// <summary>
///     Applies configured gas condensation and its constant-volume internal-energy change.
/// </summary>
internal sealed class PhaseChangeSolver
{
    private const int MaximumEquilibriumIterations = 24;
    private const double MinimumMoleTolerance = 1e-7d;
    private const double RelativeMoleTolerance = 1d / (1 << 23);

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

        double inverseBoilingPoint = 1d / properties.BoilingPoint;
        for (var activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            float gasMoles = chunk.ActiveGases[gasIndex].Moles[voxelIndex];
            if (gasMoles <= AtmosSolverConstants.MinimumMolesForCondensation)
                continue;

            float temperature = config.GetEffectiveTemperature(chunk.Temperature[voxelIndex]);
            double saturationMoles = CalculateSaturationMoles(
                config, properties, temperature, inverseBoilingPoint);
            if (gasMoles <= saturationMoles)
                continue;

            float molarInternalEnergyOfVaporization = MathF.Max(0f,
                properties.MolarEnthalpyOfVaporization -
                AtmosPhysicalConstants.MolarGasConstant * temperature);

            double equilibriumRemainingMoles = CalculateEquilibriumRemainingMoles(
                chunk, config, gasIndex, voxelIndex, gasMoles, temperature,
                saturationMoles, molarInternalEnergyOfVaporization, properties,
                inverseBoilingPoint);
            double equilibriumCondensedMoles = gasMoles - equilibriumRemainingMoles;
            double targetRemainingMoles = gasMoles - Math.Min(gasMoles,
                equilibriumCondensedMoles * config.CondensationRateFactor);
            float remainingMoles = (float)targetRemainingMoles;
            if (remainingMoles < targetRemainingMoles)
                remainingMoles = MathF.BitIncrement(remainingMoles);
            remainingMoles = Math.Clamp(remainingMoles, 0f, gasMoles);

            float molesToCondense = gasMoles - remainingMoles;
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
    private static double CalculateEquilibriumRemainingMoles(AtmosChunk chunk,
        AtmosSolverConfigSnapshot config, int gasIndex, ushort voxelIndex, double gasMoles,
        double initialTemperature, double initialSaturationMoles,
        double molarInternalEnergyOfVaporization, GasProperties properties,
        double inverseBoilingPoint)
    {
        double molarHeatCapacity =
            config.GetMolarHeatCapacityAtConstantVolume(chunk.ActiveGases[gasIndex].GasId);
        double otherHeatCapacity = CalculateOtherHeatCapacityAtVoxel(
            chunk, config, gasIndex, voxelIndex);
        double initialTotalHeatCapacity = otherHeatCapacity + gasMoles * molarHeatCapacity;

        if (molarInternalEnergyOfVaporization == 0d)
            return Math.Clamp(initialSaturationMoles, 0d, gasMoles);

        double lowerBound = 0d;
        double upperBound = gasMoles;
        double candidate = Math.Clamp(initialSaturationMoles, lowerBound, upperBound);
        double moleTolerance = Math.Max(MinimumMoleTolerance,
            gasMoles * RelativeMoleTolerance);

        for (var iteration = 0; iteration < MaximumEquilibriumIterations; iteration++)
        {
            if (candidate <= lowerBound || candidate >= upperBound)
                candidate = (lowerBound + upperBound) * 0.5d;

            double previousWidth = upperBound - lowerBound;
            double residual = CalculateSaturationLogResidual(config, gasMoles,
                initialTemperature, initialTotalHeatCapacity, otherHeatCapacity,
                molarHeatCapacity, molarInternalEnergyOfVaporization, properties,
                inverseBoilingPoint, candidate, out double residualDerivative);

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

            double nextCandidate = double.NaN;
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

    private static double CalculateSaturationLogResidual(AtmosSolverConfigSnapshot config,
        double gasMoles, double initialTemperature, double initialTotalHeatCapacity,
        double otherHeatCapacity, double molarHeatCapacity,
        double molarInternalEnergyOfVaporization, GasProperties properties,
        double inverseBoilingPoint, double remainingMoles, out double residualDerivative)
    {
        double remainingHeatCapacity = otherHeatCapacity + remainingMoles * molarHeatCapacity;
        if (remainingHeatCapacity <= 0d || !double.IsFinite(remainingHeatCapacity))
        {
            residualDerivative = double.NaN;
            return double.NegativeInfinity;
        }

        double condensedMoles = gasMoles - remainingMoles;
        double temperature = initialTemperature +
                             condensedMoles / remainingHeatCapacity *
                             molarInternalEnergyOfVaporization;
        if (temperature <= 0d || !double.IsFinite(temperature))
        {
            residualDerivative = double.NaN;
            return double.NegativeInfinity;
        }

        double logSaturationMoles = Math.Log(config.SaturationReferencePressure) -
                                    Math.Log(config.PressurePerMoleKelvin) -
                                    Math.Log(temperature) -
                                    properties.MolarEnthalpyOfVaporization /
                                    AtmosPhysicalConstants.MolarGasConstant *
                                    (1d / temperature - inverseBoilingPoint);
        double temperatureDerivative = -molarInternalEnergyOfVaporization *
                                       initialTotalHeatCapacity /
                                       (remainingHeatCapacity * remainingHeatCapacity);
        double saturationLogTemperatureDerivative =
            properties.MolarEnthalpyOfVaporization /
            (AtmosPhysicalConstants.MolarGasConstant * temperature * temperature) -
            1d / temperature;
        residualDerivative = 1d / remainingMoles -
                             saturationLogTemperatureDerivative * temperatureDerivative;
        return Math.Log(remainingMoles) - logSaturationMoles;
    }

    private static double CalculateOtherHeatCapacityAtVoxel(AtmosChunk chunk,
        AtmosSolverConfigSnapshot config, int excludedGasIndex, ushort voxelIndex)
    {
        double totalHeatCapacity = 0d;
        for (var gas = 0; gas < chunk.ActiveGasCount; gas++)
        {
            if (gas == excludedGasIndex)
                continue;

            float moles = chunk.ActiveGases[gas].Moles[voxelIndex];
            if (moles <= 0f)
                continue;

            totalHeatCapacity += (double)moles *
                                 config.GetMolarHeatCapacityAtConstantVolume(
                                     chunk.ActiveGases[gas].GasId);
        }

        return totalHeatCapacity;
    }

    private static double CalculateSaturationMoles(AtmosSolverConfigSnapshot config,
        GasProperties properties, double temperature, double inverseBoilingPoint)
    {
        double logSaturationMoles = Math.Log(config.SaturationReferencePressure) -
                                    Math.Log(config.PressurePerMoleKelvin) -
                                    Math.Log(temperature) -
                                    properties.MolarEnthalpyOfVaporization /
                                    AtmosPhysicalConstants.MolarGasConstant *
                                    (1d / temperature - inverseBoilingPoint);
        return Math.Exp(logSaturationMoles);
    }

    private static void ApplyCondensation(AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        int gasIndex, ushort voxelIndex, float temperature, float remainingMoles,
        float condensedMoles, float molarInternalEnergyOfVaporization)
    {
        if (!TryPrepareCondensation(chunk, config, gasIndex, voxelIndex, temperature,
                remainingMoles, condensedMoles, molarInternalEnergyOfVaporization,
                out float newHeatCapacity, out float newTemperature, out float newPressure))
        {
            // A supersaturated voxel still has actionable phase work. Keep retrying instead of allowing the
            // unchanged materialized state to satisfy the automatic-sleep verification window.
            chunk.SleepTimer = 0;
            chunk.VoxelAggregates.Reset();
            return;
        }

        chunk.ActiveGases[gasIndex].Moles[voxelIndex] = remainingMoles;
        chunk.TotalHeatCapacity[voxelIndex] = newHeatCapacity;
        if (newHeatCapacity > 0f)
            chunk.Temperature[voxelIndex] = newTemperature;
        chunk.TotalPressure[voxelIndex] = newPressure;
    }

    private static bool TryPrepareCondensation(AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        int gasIndex, ushort voxelIndex, float temperature, float remainingMoles,
        float condensedMoles, float molarInternalEnergyOfVaporization,
        out float newHeatCapacity, out float newTemperature, out float newPressure)
    {
        var newTotalMoles = 0f;
        newHeatCapacity = 0f;
        for (var gas = 0; gas < chunk.ActiveGasCount; gas++)
        {
            float moles = gas == gasIndex
                ? remainingMoles
                : chunk.ActiveGases[gas].Moles[voxelIndex];
            if (moles <= 0f)
                continue;

            newTotalMoles += moles;
            float heatCapacityContribution = moles *
                                             config.GetMolarHeatCapacityAtConstantVolume(
                                                 chunk.ActiveGases[gas].GasId);
            newHeatCapacity += heatCapacityContribution;
            if (!float.IsFinite(newTotalMoles) || !float.IsFinite(heatCapacityContribution) ||
                !float.IsFinite(newHeatCapacity))
            {
                newTemperature = 0f;
                newPressure = 0f;
                return false;
            }
        }

        newTemperature = chunk.Temperature[voxelIndex];
        if (newHeatCapacity > 0f)
        {
            // Algebraically this is (T*C_remaining + n_condensed*U_vap) / C_remaining. Perform
            // the quotient in double so a finite projected temperature is not rejected because of
            // an overflowing single-precision intermediate.
            double projectedTemperature = temperature +
                                          (double)condensedMoles / newHeatCapacity *
                                          molarInternalEnergyOfVaporization;
            newTemperature = (float)projectedTemperature;
            if (!float.IsFinite(newTemperature) || newTemperature <= 0f)
            {
                newPressure = 0f;
                return false;
            }
        }

        newPressure = AtmosSolverMath.CalculatePressure(config, newTotalMoles, newTemperature);
        return float.IsFinite(newPressure);
    }
}
