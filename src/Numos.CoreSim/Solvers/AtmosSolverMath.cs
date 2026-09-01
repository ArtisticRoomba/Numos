using System.Diagnostics;
using Numos.Maths;

namespace Numos.CoreSim.Solvers;

/// <summary>
///     Shared, side-effect-free atmospheric calculations used across solver stages and mixture operations.
/// </summary>
internal static class AtmosSolverMath
{
    /// <summary>
    ///     Ideal gas law calculation
    ///     PressurePerMoleKelvin is R/V
    /// </summary>
    internal static Pascal CalculatePressure(IAtmosConfig config, Mole moles, Kelvin temperature)
    {
        Debug.Assert(float.IsFinite(moles) && moles >= 0f);
        return moles * config.GetValidatedTemp(temperature) * config.PressurePerMoleKelvin;
    }

    /// <summary>
    ///     Ideal gas law calculation
    ///     PressurePerMoleKelvin is R/V
    /// </summary>
    internal static Mole PressureToMoles(IAtmosConfig config, Pascal pressure, Kelvin temperature)
    {
        if (pressure <= 0f || float.IsNaN(pressure))
            return 0f;

        PascalPerMole denominator = config.PressurePerMoleKelvin * config.GetValidatedTemp(temperature);
        return pressure / denominator;
    }

    /// <summary>
    ///     Returns a voxel's pressure based on ideal gas law
    ///     If below vacuum threshold sets voxel to a vacuum
    /// </summary>
    internal static Pascal CalculatePressureAtVoxel(
        IAtmosConfig config, AtmosChunk chunk,
        ushort localVoxelIndex)
    {
        Mole totalMoles = GetTotalMoles(chunk, localVoxelIndex);
        if (totalMoles <= 0f)
        {
            chunk.SetVoxelToVacuum(localVoxelIndex);
            return 0f;
        }

        Pascal pressure = CalculatePressure(config, totalMoles, chunk.Temperature[localVoxelIndex]);
        if (pressure < config.VacuumThreshold)
        {
            chunk.SetVoxelToVacuum(localVoxelIndex);
            return 0f;
        }

        return pressure;
    }

    /// <summary>
    ///     Returns heat capacity of all gasses at voxel
    /// </summary>
    internal static JoulePerKelvin CalculateHeatCapacityAtVoxel(
        IAtmosConfig config, AtmosChunk chunk,
        ushort localVoxelIndex)
    {
        JoulePerKelvin totalHeatCapacity = 0f;
        for (int gas = 0; gas < chunk.ActiveGasCount; gas++)
        {
            Mole moles = chunk.ActiveGases[gas].Moles[localVoxelIndex];
            if (moles <= 0f)
                continue;

            totalHeatCapacity += moles *
                                 config.GetMolarHeatCapacityAtConstantVolume(chunk.ActiveGases[gas].GasId);
        }

        return totalHeatCapacity;
    }

    /// <summary>
    ///     Returns pressure transfer between voxels based on the pressure delta
    ///     Max transfer of total pressure * maximumFraction
    ///     maximumFraction default value is 0.16
    ///     In effect, no voxel can give away all of its gas in one tick
    /// </summary>
    internal static Pascal CalculateBulkPressureTransfer(
        AtmosSolverConfigSnapshot config,
        Pascal pressureDelta, Pascal currentPressure)
    {
        Scalar maximumFraction = config.MaxPressureTransferFractionPerNeighbor;
        if (maximumFraction <= 0f)
            return 0f;

        Pascal pressureTransfer = pressureDelta * config.BulkFlowCoefficient;
        if (pressureTransfer <= 0f)
            return 0f;

        return MathF.Min(pressureTransfer, currentPressure * maximumFraction);
    }

    /// <summary>
    ///     Returns the source-relative species imbalance used by explicit Fickian diffusion.
    /// </summary>
    internal static Mole CalculateMoleImbalance(
        Mole sourceMoles, Kelvin sourceTemperature,
        Mole targetMoles, Kelvin targetTemperature)
    {
        Debug.Assert(sourceMoles >= 0f && targetMoles >= 0f);
        Debug.Assert(IsFinitePositive(sourceTemperature));

        // Mathematically an empty target contributes zero regardless of the temperature ratio. Handling it first
        // prevents 0 * infinity from turning a valid outward imbalance into NaN at extreme temperatures.
        if (targetMoles == 0f)
            return sourceMoles;

        Debug.Assert(IsFinitePositive(targetTemperature));
        return sourceMoles - targetMoles * (targetTemperature / sourceTemperature);
    }

    /// <summary>
    ///     Returns the thermal conductance between voxels
    ///     It can never be higher than the amount of energy needed to equalize the voxels
    /// </summary>
    internal static JoulePerKelvin CalculateThermalConductance(
        JoulePerKelvin sourceHeatCapacity,
        JoulePerKelvin targetHeatCapacity, JoulePerKelvin thermalConductance)
    {
        Debug.Assert(IsFinitePositive(sourceHeatCapacity));
        Debug.Assert(IsFinitePositive(targetHeatCapacity));
        Debug.Assert(IsFinitePositive(thermalConductance));

        JoulePerKelvin smallerHeatCapacity = MathF.Min(sourceHeatCapacity, targetHeatCapacity);
        JoulePerKelvin largerHeatCapacity = MathF.Max(sourceHeatCapacity, targetHeatCapacity);
        JoulePerKelvin equilibriumConductance = smallerHeatCapacity /
                                                (1f + smallerHeatCapacity / largerHeatCapacity);

        return MathF.Min(thermalConductance, equilibriumConductance);
    }

    internal static int CompareChunkPositions(Int3 left, Int3 right)
    {
        int comparison = left.X.CompareTo(right.X);
        if (comparison != 0)
            return comparison;

        comparison = left.Y.CompareTo(right.Y);
        return comparison != 0 ? comparison : left.Z.CompareTo(right.Z);
    }

    internal static bool IsFinitePositive(float value)
    {
        return float.IsFinite(value) && value > 0f;
    }

    /// <summary>
    ///     Returns the sum of all gas moles in a voxel
    /// </summary>
    internal static Mole GetTotalMoles(AtmosChunk chunk, ushort voxelIndex)
    {
        Mole totalMoles = 0f;
        for (int gas = 0; gas < chunk.ActiveGasCount; gas++)
            totalMoles += chunk.ActiveGases[gas].Moles[voxelIndex];

        return totalMoles;
    }
}