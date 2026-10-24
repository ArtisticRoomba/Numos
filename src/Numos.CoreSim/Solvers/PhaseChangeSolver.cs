using System.Buffers;
using CommunityToolkit.HighPerformance.Helpers;

namespace Numos.CoreSim.Solvers;

/// <summary>
///     Applies configured gas condensation and its constant-volume internal-energy change.
/// </summary>
internal sealed class PhaseChangeSolver
{
    private const int MaximumEquilibriumIterations = 24;
    private const Mole64 MinimumMoleTolerance = 1e-7d;
    private const Scalar64 RelativeMoleTolerance = 1d / (1 << 23);

    /// <summary>
    ///     Condenses supersaturated vapor in every active voxel of one chunk.
    /// </summary>
    /// <remarks>
    ///     Every voxel is independent, because condensation reads and writes only its own <see cref="AtmosChunk" />
    ///     column. Voxels therefore run in parallel.
    ///     Within one voxel the gases are still walked in ascending channel order, as the fully sequential form does.
    ///     Gas <c>g</c>'s condensation can shift that voxel's temperature before gas <c>g+1</c> reads it.
    ///     Preserving that order keeps the result bit-identical to processing one gas at a time across every voxel.
    /// </remarks>
    /// <param name="chunk">The chunk whose voxels should be checked for condensation.</param>
    /// <param name="config">The solver settings captured for this tick.</param>
    /// <param name="awakeChunkCount">
    ///     How many awake, gas-bearing chunks <see cref="ThermodynamicsSolver" /> is dispatching this tick.
    ///     Below worker count, per-voxel parallelism here helps fill idle workers.
    ///     At or above it, the outer per-chunk dispatch already saturates the pool, so this runs sequentially.
    /// </param>
    internal void Solve(AtmosChunk chunk, AtmosSolverConfigSnapshot config, int awakeChunkCount)
    {
        if (config.CondensationRateFactor <= 0f || chunk.ActiveAirCount == 0)
            return;

        int gasCount = chunk.ActiveGasCount;
        if (gasCount == 0)
            return;

        CondensingGas[] condensingGases = ArrayPool<CondensingGas>.Shared.Rent(gasCount);
        int condensingCount = 0;
        try
        {
            for (int gas = 0; gas < gasCount; gas++)
            {
                int gasId = chunk.ActiveGases[gas].GasId;
                if (!config.TryGetGasProperties(gasId, out var properties) || !properties.CondensationEnabled)
                    continue;

                if (!AtmosSolverMath.IsFinitePositive(properties.BoilingPoint) ||
                    !AtmosSolverMath.IsFinitePositive(properties.MolarEnthalpyOfVaporization))
                    continue;

                condensingGases[condensingCount++] =
                    new CondensingGas(gas, properties, 1d / properties.BoilingPoint);
            }

            if (condensingCount == 0)
                return;

            // Condensing (expensive) voxels cluster spatially, for example in a foggy room or a steam vent.
            // They cluster in ActiveAirIndices too, so a run of expensive items would land in a few adjacent grains.
            // A coprime-stride permutation of the work-item index spreads any contiguous run across the whole range
            // before ParallelPhase hands the items out.
            // It is a bijection over [0, ActiveAirCount), so every voxel is still visited exactly once.
            int stride = ChooseStride(chunk.ActiveAirCount);
            var action = new ProcessVoxelAction(chunk, config, condensingGases, condensingCount, stride);

            // See the awakeChunkCount doc above.
            // Add per-voxel parallelism only when ThermodynamicsSolver's per-chunk dispatch leaves workers idle.
            int workerCount = Math.Max(1, Environment.ProcessorCount);
            if (awakeChunkCount < workerCount)
            {
                ParallelPhase.Run(chunk.ActiveAirCount, action);
            }
            else
            {
                for (int activeIndex = 0; activeIndex < chunk.ActiveAirCount; activeIndex++)
                    action.Invoke(activeIndex);
            }
        }
        finally
        {
            ArrayPool<CondensingGas>.Shared.Return(condensingGases);
        }
    }

    private static void ProcessVoxelForGas(
        AtmosChunk chunk, AtmosSolverConfigSnapshot config, int gasIndex,
        GasProperties properties, PerKelvin64 inverseBoilingPoint, ushort voxelIndex)
    {
        Mole gasMoles = chunk.ActiveGases[gasIndex].Moles[voxelIndex];
        if (gasMoles <= AtmosSolverConstants.MinimumMolesForCondensation)
            return;

        Kelvin temperature = config.GetValidatedTemp(chunk.Temperature[voxelIndex]);
        // Saturation amount from the Clausius-Clapeyron equation. Any vapor above it can condense.
        Mole64 saturationMoles = CalculateSaturationMoles(config, properties, temperature, inverseBoilingPoint);
        // At or below saturationMoles condensation is not possible. Special cases are ignored.
        if (gasMoles <= saturationMoles)
            return;

        // ΔU_vap = ΔH_vap - RT, since the condensed liquid has effectively no volume.
        JoulePerMole molarInternalEnergyOfVaporization = MathF.Max(
            0f,
            properties.MolarEnthalpyOfVaporization -
            AtmosPhysicalConstants.MolarGasConstant * temperature);

        // The moles to condense to reach equilibrium at saturation have no analytical solution.
        // An underestimate is fine, because the rest condenses next tick.
        // An overestimate can release enough energy to heat the gas well above the boiling point.
        // That is not physical and must be avoided.
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
        // The condensation factor gives an exponential decay of equilibriumCondensedMoles.
        Mole64 initialMolesToCondense = equilibriumCondensedMoles * config.CondensationRateFactor;
        // Once the leftover is small, condense all of it now instead of decaying toward it over many ticks.
        if (equilibriumCondensedMoles - initialMolesToCondense <= AtmosSolverConstants.CondensationFactorCutoff)
            initialMolesToCondense = equilibriumCondensedMoles;

        Mole64 targetRemainingMoles = gasMoles - Math.Min(gasMoles, initialMolesToCondense);

        Mole remainingMoles = (float)targetRemainingMoles;
        if (remainingMoles < targetRemainingMoles)
            remainingMoles = MathF.BitIncrement(remainingMoles);

        remainingMoles = Math.Clamp(remainingMoles, 0f, gasMoles);

        Mole molesToCondense = gasMoles - remainingMoles;
        if (molesToCondense <= 0f)
            return;

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

    /// <summary>
    ///     Finds the remaining vapor amount whose warmed partial pressure equals its saturation pressure.
    ///     A safeguarded Newton solve works in log-mole space.
    ///     Large inventories and saturation pressures then avoid an overflow-prone pressure round trip.
    /// </summary>
    private static Mole64 CalculateEquilibriumRemainingMoles(
        AtmosChunk chunk,
        AtmosSolverConfigSnapshot config, int gasIndex, ushort voxelIndex, Mole64 gasMoles,
        Kelvin64 initialTemperature, Mole64 initialSaturationMoles,
        JoulePerMole64 molarInternalEnergyOfVaporization, GasProperties properties,
        PerKelvin64 inverseBoilingPoint)
    {
        // TODO PERF: this uses doubles for precision, so it cannot use previously cached values.
        // Check whether the precision is required.
        JoulePerMoleKelvin64 molarHeatCapacity =
            config.GetMolarHeatCapacityAtConstantVolume(chunk.ActiveGases[gasIndex].GasId);

        JoulePerKelvin64 otherHeatCapacity = CalculateOtherHeatCapacityAtVoxel(chunk, config, gasIndex, voxelIndex);
        JoulePerKelvin64 initialTotalHeatCapacity = otherHeatCapacity + gasMoles * molarHeatCapacity;

        // If condensing takes no energy, all of the excess condenses.
        // This happens only with a very low MolarEnthalpyOfVaporization.
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

            // A valid Newton step normally contracts the bracket faster than bisection.
            // If it hugs one endpoint, also evaluate the retained interval's midpoint.
            // Every iteration then still halves the previous width in the worst case.
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

        // Return the supersaturated side of the bracket. ProcessVoxelForGas also rounds the target toward
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
        // P_1 is SaturationReferencePressure and T_1 is the gas's BoilingPoint at that pressure
        // H is the Molar Enthalpy Of Vaporization
        // R is Molar Gas Constant
        // T_2 is current temperature
        // P_2 is the pressure at the current temperature which being above would lead to condensation
        // The code converts this equation to use moles instead of pressure.
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
            // Algebraically this is (T*C_remaining + n_condensed*U_vap) / C_remaining.
            // Dividing before multiplying avoids both C*T overflow and the cancellation of two large energies.
            chunk.Temperature[voxelIndex] = MathF.Max(
                0f,
                temperature + condensedMoles / newHeatCapacity * molarInternalEnergyOfVaporization);
        }

        chunk.TotalPressure[voxelIndex] =
            AtmosSolverMath.CalculatePressureAtVoxel(config, chunk, voxelIndex);

        // TODO: this temperature is wrong. The liquid should be at the equivalent condensation point.
        // Finding that point needs the Lambert W function to invert the saturation pressure equation.
        // The energy the liquid keeps must also come out of the molar internal energy of vaporization.
        // The error is probably small. It may matter once the liquid simulation starts.
        AddPrecipitationEvent(voxelIndex, gasIndex, condensedMoles, temperature);
    }

    private static void AddPrecipitationEvent(ushort LocalVoxelIndex, int gasIndex, Mole moles, Kelvin temp)
    {
        // TODO
    }

    /// <summary>
    ///     Picks a stride coprime with <paramref name="n" /> so that <c>(i * stride) % n</c> is a bijection
    ///     over <c>[0, n)</c>. A contiguous run of <c>i</c> then lands roughly evenly across the whole range.
    ///     The golden-ratio-derived starting point is a standard Fibonacci-hashing choice for even spread.
    ///     The search adds 2 (staying odd) until the stride is coprime with <paramref name="n" />.
    ///     For the chunk sizes this solver sees, that takes at most a handful of steps.
    /// </summary>
    private static int ChooseStride(int n)
    {
        if (n <= 2)
            return 1;

        int stride = (int)(n * 0.6180339887498949) | 1;
        while (Gcd(stride, n) != 1)
            stride += 2;

        return stride;
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0)
            (a, b) = (b, a % b);

        return a;
    }

    private readonly record struct CondensingGas(int GasIndex, GasProperties Properties, PerKelvin64 InverseBoilingPoint);

    /// <summary>
    ///     One work item per active-air voxel. Each voxel owns its own <see cref="AtmosChunk" /> columns
    ///     (moles, temperature, heat capacity, pressure). Distinct voxels never write the same slot,
    ///     so workers need no synchronization.
    ///     <paramref name="stride" /> permutes which voxel a work-item index maps to (see <see cref="ChooseStride" />).
    ///     This only balances load. Every voxel is still visited exactly once, so results are unaffected.
    /// </summary>
    private readonly struct ProcessVoxelAction(
        AtmosChunk chunk,
        AtmosSolverConfigSnapshot config,
        CondensingGas[] condensingGases,
        int condensingCount,
        int stride) : IAction
    {
        public void Invoke(int activeIndex)
        {
            int permutedIndex = (int)((long)activeIndex * stride % chunk.ActiveAirCount);
            ushort voxelIndex = chunk.ActiveAirIndices[permutedIndex];
            for (int i = 0; i < condensingCount; i++)
            {
                CondensingGas gas = condensingGases[i];
                ProcessVoxelForGas(chunk, config, gas.GasIndex, gas.Properties, gas.InverseBoilingPoint, voxelIndex);
            }
        }
    }
}