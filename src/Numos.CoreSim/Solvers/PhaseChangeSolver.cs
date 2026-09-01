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

        for (int gas = 0; gas < chunk.ActiveGasCount; gas++)
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
        for (int activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
        {
            ushort voxelIndex = chunk.ActiveAirIndices[activeIndex];
            Mole gasMoles = chunk.ActiveGases[gasIndex].Moles[voxelIndex];
            if (gasMoles <= AtmosSolverConstants.MinimumMolesForCondensation)
                continue;


            Kelvin temperature = config.GetValidatedTemp(chunk.Temperature[voxelIndex]);
            // Finds the number of moles which if below would cause condensation
            // This is found from the clausius-clapeyron equation
            Mole64 saturationMoles = CalculateSaturationMoles(config, properties, temperature, inverseBoilingPoint);
            // If below saturationMoles condensation is not possible (We are ignoring any special cases)
            if (gasMoles <= saturationMoles)
                continue;

            // This value includes the energy when condensing a gas as a liquid has effectively no volume
            JoulePerMole molarInternalEnergyOfVaporization = MathF.Max(
                0f,
                properties.MolarEnthalpyOfVaporization -
                AtmosPhysicalConstants.MolarGasConstant * temperature);

            // Finding the amount of moles to condense to reach equilibrium at saturation is not possible analytically
            // It is fine to under estimate the amount of moles which condense as the rest can condense next tick
            // An over estimation however can easily lead to enough energy released to increase the temperature of the gas well above the boiling point
            // This is not possible physically and needs to be avoided
            Mole64 equilibriumRemainingMoles = CalculateEquilibriumRemainingMoles(
                chunk,
                config,
                gasIndex,
                voxelIndex,
                gasMoles,
                temperature,
                saturationMoles,
                molarInternalEnergyOfVaporization,
                properties,
                inverseBoilingPoint);

            Mole64 equilibriumCondensedMoles = gasMoles - equilibriumRemainingMoles;
            // Condensation factor will lead to an exponential decay of equilibriumCondensedMoles
            Mole64 initialMolesToCondense = equilibriumCondensedMoles * config.CondensationRateFactor;
            // Cuts off final condensation to happen all at once to prevent repeating this to many times
            if (equilibriumCondensedMoles - initialMolesToCondense <= AtmosSolverConstants.CondensationFactorCutoff)
                initialMolesToCondense = equilibriumCondensedMoles;

            Mole64 targetRemainingMoles = gasMoles - Math.Min(gasMoles, initialMolesToCondense);

            Mole remainingMoles = (float)targetRemainingMoles;
            if (remainingMoles < targetRemainingMoles)
                remainingMoles = MathF.BitIncrement(remainingMoles);

            remainingMoles = Math.Clamp(remainingMoles, 0f, gasMoles);

            Mole molesToCondense = gasMoles - remainingMoles;
            if (molesToCondense <= 0f)
                continue;

            ApplyCondensation(
                chunk,
                config,
                gasIndex,
                voxelIndex,
                temperature,
                remainingMoles,
                molesToCondense,
                molarInternalEnergyOfVaporization);
        }
    }

    /// <summary>
    ///     Finds the remaining vapor amount whose warmed partial pressure equals its saturation pressure.
    ///     A safeguarded Newton solve operates in log-mole space so large inventories and saturation pressures
    ///     do not require an overflow-prone pressure round trip.
    /// </summary>
    private static Mole64 CalculateEquilibriumRemainingMoles(
        AtmosChunk chunk,
        AtmosSolverConfigSnapshot config, int gasIndex, ushort voxelIndex, Mole64 gasMoles,
        Kelvin64 initialTemperature, Mole64 initialSaturationMoles,
        JoulePerMole64 molarInternalEnergyOfVaporization, GasProperties properties,
        PerKelvin64 inverseBoilingPoint)
    {
        // TODO PERF
        // doubles are used here for higher precision
        // This means it can't use previously cached values
        // Check if this is required
        JoulePerMoleKelvin64 molarHeatCapacity =
            config.GetMolarHeatCapacityAtConstantVolume(chunk.ActiveGases[gasIndex].GasId);

        JoulePerKelvin64 otherHeatCapacity = CalculateOtherHeatCapacityAtVoxel(chunk, config, gasIndex, voxelIndex);
        JoulePerKelvin64 initialTotalHeatCapacity = otherHeatCapacity + gasMoles * molarHeatCapacity;

        // If it takes no energy to condense, they all condense
        // This can only happen in specific cases with a very low MolarEnthalpyOfVaporization
        if (molarInternalEnergyOfVaporization == 0d)
            return Math.Clamp(initialSaturationMoles, 0d, gasMoles);

        Mole64 lowerBound = 0d;
        Mole64 upperBound = gasMoles;
        Mole64 candidate = Math.Clamp(initialSaturationMoles, lowerBound, upperBound);
        Mole64 moleTolerance = Math.Max(
            MinimumMoleTolerance,
            gasMoles * RelativeMoleTolerance);

        for (int iteration = 0; iteration < MaximumEquilibriumIterations; iteration++)
        {
            if (candidate <= lowerBound || candidate >= upperBound)
                candidate = (lowerBound + upperBound) * 0.5d;

            Mole64 previousWidth = upperBound - lowerBound;
            Scalar64 residual = CalculateSaturationLogResidual(
                config,
                gasMoles,
                initialTemperature,
                initialTotalHeatCapacity,
                otherHeatCapacity,
                molarHeatCapacity,
                molarInternalEnergyOfVaporization,
                properties,
                inverseBoilingPoint,
                candidate,
                out PerMole64 residualDerivative);

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
                residual = CalculateSaturationLogResidual(
                    config,
                    gasMoles,
                    initialTemperature,
                    initialTotalHeatCapacity,
                    otherHeatCapacity,
                    molarHeatCapacity,
                    molarInternalEnergyOfVaporization,
                    properties,
                    inverseBoilingPoint,
                    candidate,
                    out residualDerivative);

                if (residual > 0d)
                    upperBound = candidate;
                else
                    lowerBound = candidate;
            }

            if (upperBound - lowerBound <= moleTolerance ||
                (float)lowerBound == (float)upperBound)
                break;

            Mole64 nextCandidate = double.NaN;
            if (double.IsFinite(residual) &&
                double.IsFinite(residualDerivative) &&
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

    private static Scalar64 CalculateSaturationLogResidual(
        AtmosSolverConfigSnapshot config,
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
                               condensedMoles /
                               remainingHeatCapacity *
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

    private static JoulePerKelvin64 CalculateOtherHeatCapacityAtVoxel(
        AtmosChunk chunk,
        AtmosSolverConfigSnapshot config, int excludedGasIndex, ushort voxelIndex)
    {
        JoulePerKelvin64 totalHeatCapacity = 0d;
        for (int gas = 0; gas < chunk.ActiveGasCount; gas++)
        {
            if (gas == excludedGasIndex)
                continue;

            Mole moles = chunk.ActiveGases[gas].Moles[voxelIndex];
            if (moles <= 0f)
                continue;

            totalHeatCapacity += (double)moles *
                                 config.GetMolarHeatCapacityAtConstantVolume(chunk.ActiveGases[gas].GasId);
        }

        return totalHeatCapacity;
    }

    private static Mole64 CalculateSaturationMoles(
        AtmosSolverConfigSnapshot config,
        GasProperties properties, Kelvin64 temperature, PerKelvin64 inverseBoilingPoint)
    {
        // Clausius-Clapeyron equation for saturation vapor pressure:
        // P_2 = P_1 \exp{\frac{\Delta H_{vap}}{R}\left( \frac{1}{T_1} - \frac{1}{T_2} \right)}
        // P_1 and T_1 are the reference pressure and temperature. These should be room pressure and temp
        // H is the Molar Enthalpy Of Vaporization
        // R is Molar Gas Constant
        // T_2 is current temperature
        // P_2 is the pressure at the current temperature which being above would lead to condensation
        // This equation has been converted to use moles instead of pressure
        // TODO PERF
        Scalar64 logSaturationMoles = Math.Log(config.SaturationReferencePressure) -
                                      Math.Log(config.PressurePerMoleKelvin) -
                                      Math.Log(temperature) -
                                      properties.MolarEnthalpyOfVaporization /
                                      AtmosPhysicalConstants.MolarGasConstant *
                                      (1d / temperature - inverseBoilingPoint);

        return Math.Exp(logSaturationMoles);
    }

    private static void ApplyCondensation(
        AtmosChunk chunk, AtmosSolverConfigSnapshot config,
        int gasIndex, ushort voxelIndex, Kelvin temperature, Mole remainingMoles,
        Mole condensedMoles, JoulePerMole molarInternalEnergyOfVaporization)
    {
        chunk.ActiveGases[gasIndex].Moles[voxelIndex] = remainingMoles;

        JoulePerKelvin newHeatCapacity = AtmosSolverMath.CalculateHeatCapacityAtVoxel(config, chunk, voxelIndex);
        chunk.TotalHeatCapacity[voxelIndex] = newHeatCapacity;
        if (newHeatCapacity > 0f)
        {
            // Algebraically this is (T*C_remaining + n_condensed*U_vap) / C_remaining. Dividing
            // before multiplying avoids both C*T overflow and the cancellation of two large energies.
            chunk.Temperature[voxelIndex] = MathF.Max(
                0f,
                temperature + condensedMoles / newHeatCapacity * molarInternalEnergyOfVaporization);
        }

        chunk.TotalPressure[voxelIndex] =
            AtmosSolverMath.CalculatePressureAtVoxel(config, chunk, voxelIndex);

        // TODO
        // This temperature is technically wrong. The temperature of the liquid should be at the equivalent condensation point.
        // The saturation pressure equation requires the lambertW to invert to find.
        // This energy which the liquid holds onto also needs to be taken away from the molar Internal Energy Of Vaporization
        // This could potentially be ignored as it shouldn't make much of a difference. It might come up once the liquid simulation is started.
        AddPrecipitationEvent(voxelIndex, gasIndex, condensedMoles, temperature);
    }

    private static void AddPrecipitationEvent(ushort LocalVoxelIndex, int gasIndex, Mole moles, Kelvin temp)
    {
        // TODO
    }
}