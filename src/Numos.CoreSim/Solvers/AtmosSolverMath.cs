using System.Diagnostics;
using Numos.Maths;

namespace Numos.CoreSim.Solvers;

/// <summary>
///     Shared, side-effect-free atmospheric calculations used across solver stages and mixture operations.
/// </summary>
internal static class AtmosSolverMath
{
    internal static float CalculatePressure(IAtmosConfig config, float moles, float temperature)
    {
        Debug.Assert(float.IsFinite(moles) && moles >= 0f);
        return moles * config.GetValidatedTemp(temperature) * config.PressurePerMoleKelvin;
    }

    internal static float PressureToMoles(IAtmosConfig config, float pressure, float temperature)
    {
        if (pressure <= 0f || float.IsNaN(pressure))
            return 0f;

        float denominator = config.PressurePerMoleKelvin * config.GetValidatedTemp(temperature);
        return pressure / denominator;
    }

    /// <summary>Recalculates a voxel pressure using the normalized values in a live public configuration.</summary>
    internal static float CalculatePressureAtVoxel(IAtmosConfig config, AtmosChunk chunk,
        ushort localVoxelIndex)
    {
        var totalMoles = GetTotalMoles(chunk, localVoxelIndex);

        if (totalMoles <= 0f)
        {
            chunk.SetVoxelToVacuum(localVoxelIndex);
            return 0f;
        }

        var pressure = CalculatePressure(config, totalMoles, chunk.Temperature[localVoxelIndex]);
        if (pressure < config.VacuumThreshold)
        {
            chunk.SetVoxelToVacuum(localVoxelIndex);
            return 0f;
        }

        return pressure;
    }

    internal static float CalculateHeatCapacityAtVoxel(IAtmosConfig config, AtmosChunk chunk,
        ushort localVoxelIndex)
    {
        var totalHeatCapacity = 0f;
        for (var gas = 0; gas < chunk.ActiveGasCount; gas++)
        {
            float moles = chunk.ActiveGases[gas].Moles[localVoxelIndex];
            if (moles <= 0f)
                continue;

            totalHeatCapacity += moles *
                                 config.GetMolarHeatCapacityAtConstantVolume(chunk.ActiveGases[gas].GasId);
        }

        return totalHeatCapacity;
    }

    internal static float CalculateBulkPressureTransfer(AtmosSolverConfigSnapshot config,
        float pressureDelta, float currentPressure)
    {
        float maximumFraction = config.MaxPressureTransferFractionPerNeighbor;
        if (maximumFraction <= 0f)
            return 0f;

        float pressureTransfer = pressureDelta * config.BulkFlowCoefficient;
        if (pressureTransfer <= 0f)
            return 0f;

        return MathF.Min(pressureTransfer, currentPressure * maximumFraction);
    }

    /// <summary>
    ///     Returns the source-relative species imbalance used by explicit Fickian diffusion.
    /// </summary>
    internal static float CalculateMoleImbalance(float sourceMoles, float sourceTemperature,
        float targetMoles, float targetTemperature)
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

    internal static float CalculateThermalConductance(float sourceHeatCapacity, float targetHeatCapacity,
        float thermalConductance)
    {
        Debug.Assert(IsFinitePositive(sourceHeatCapacity));
        Debug.Assert(IsFinitePositive(targetHeatCapacity));
        Debug.Assert(IsFinitePositive(thermalConductance));

        float smallerHeatCapacity = MathF.Min(sourceHeatCapacity, targetHeatCapacity);
        float largerHeatCapacity = MathF.Max(sourceHeatCapacity, targetHeatCapacity);
        float equilibriumConductance = smallerHeatCapacity /
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

    internal static float GetTotalMoles(AtmosChunk chunk, ushort voxelIndex)
    {
        var totalMoles = 0f;
        for (var gas = 0; gas < chunk.ActiveGasCount; gas++)
            totalMoles += chunk.ActiveGases[gas].Moles[voxelIndex];
        return totalMoles;
    }

}