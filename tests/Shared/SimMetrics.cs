using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Snapshots;

namespace Numos.Tests;

/// <summary>
///     Totals read back from chunk snapshots for conservation and transfer assertions.
/// </summary>
internal static partial class SimMetrics
{
    internal static float Moles(AtmosChunkSnapshot snapshot, int gasId, int index)
    {
        foreach (var gas in snapshot.Gases)
        {
            if (gas.GasId == gasId)
                return gas.Moles[index];
        }

        return 0f;
    }

    internal static float TotalMoles(AtmosChunkSnapshot snapshot)
    {
        return snapshot.Gases.Sum(gas => gas.Moles.Sum());
    }

    internal static float TotalMoles(params AtmosChunkSnapshot[] snapshots)
    {
        return snapshots.Sum(TotalMoles);
    }

    internal static float TotalThermalEnergy(
        AtmosConfig config,
        params AtmosChunkSnapshot[] snapshots)
    {
        return (float)TotalThermalEnergyPrecise(config, snapshots);
    }

    internal static double TotalThermalEnergyPrecise(
        AtmosConfig config,
        params AtmosChunkSnapshot[] snapshots)
    {
        double totalEnergy = 0d;
        // Mirrors AtmosConfig.GetMolarHeatCapacityAtConstantVolume and the solver's DefaultTemperatureFallback rule.
        float fallbackMolarHeatCapacityAtConstantVolume =
            float.IsFinite(config.DefaultMolarHeatCapacityAtConstantVolume) &&
            config.DefaultMolarHeatCapacityAtConstantVolume > 0f
                ? config.DefaultMolarHeatCapacityAtConstantVolume
                : AtmosPhysicalConstants.IdealDiatomicMolarHeatCapacityAtConstantVolume;

        foreach (var snapshot in snapshots)
        {
            for (int index = 0; index < snapshot.Temperature.Length; index++)
            {
                double heatCapacity = 0d;
                foreach (var gas in snapshot.Gases)
                {
                    float configuredMolarHeatCapacityAtConstantVolume = gas.GasId >= 0 &&
                                                                        gas.GasId < config.GasRegistry.Count
                        ? config.GasRegistry[gas.GasId].MolarHeatCapacityAtConstantVolume
                        : fallbackMolarHeatCapacityAtConstantVolume;

                    float molarHeatCapacityAtConstantVolume =
                        float.IsFinite(configuredMolarHeatCapacityAtConstantVolume) &&
                        configuredMolarHeatCapacityAtConstantVolume > 0f
                            ? configuredMolarHeatCapacityAtConstantVolume
                            : fallbackMolarHeatCapacityAtConstantVolume;

                    heatCapacity += (double)gas.Moles[index] * molarHeatCapacityAtConstantVolume;
                }

                float storedTemperature = snapshot.Temperature[index];
                float effectiveTemperature = float.IsFinite(storedTemperature) && storedTemperature > 0f
                    ? storedTemperature
                    : config.DefaultTemperatureFallback;

                totalEnergy += heatCapacity * effectiveTemperature;
            }
        }

        return totalEnergy;
    }
}