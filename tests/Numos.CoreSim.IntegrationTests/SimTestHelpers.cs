using Numos.API;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.Maths;

namespace Numos.CoreSim.IntegrationTests;

internal static class SimTestHelpers
{
    internal const int FirstGasId = 0;
    internal const int SecondGasId = 1;
    internal const int RoomId = 1;
    internal const float DefaultTemperature = 300f;
    internal const float Tolerance = 0.0001f;

    internal static AtmosConfig CreateDeterministicConfig()
    {
        return new AtmosConfig
        {
            DefaultTemperatureFallback = DefaultTemperature,
            FlowFriction = 0.25f,
            DampingFactor = 0.5f,
            SnapThreshold = 5f,
            MinFlowCutoff = 0f,
            VacuumThreshold = 0f,
            SleepThreshold = int.MaxValue,
            SleepEpsilon = 0f,
            ThermalConductivity = 0.05f,
            CondensationRateFactor = 0.5f,
            CflFlowCap = 0.16f,
            GasRegistry =
            [
                new GasProperties { Name = "First", DiffusionCoefficient = 0f },
                new GasProperties { Name = "Second", DiffusionCoefficient = 0f }
            ]
        };
    }

    internal static AtmosChunkHandle CreateOpenChunk(AtmosSimulation simulation, Int3 position,
        VoxelClassification? classification = null)
    {
        var chunk = simulation.CreateAndRegisterChunk(position);
        simulation.SetChunkClassification(chunk, classification ?? new VoxelClassification(RoomId));
        return chunk;
    }

    internal static void SetAllTemperatures(AtmosSimulation simulation, AtmosChunkHandle chunk,
        int width, int height, int depth, float temperature = DefaultTemperature)
    {
        for (var z = 0; z < depth; z++)
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            simulation.SetVoxelTemperature(chunk, x, y, z, temperature);
    }

    internal static int Index(int x, int y, int z, int width, int height)
    {
        return x + y * width + z * width * height;
    }

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

    internal static float TotalThermalEnergy(AtmosConfig config,
        params AtmosChunkSnapshot[] snapshots)
    {
        var totalEnergy = 0f;
        float fallbackSpecificHeatCapacity = float.IsFinite(config.DefaultSpecificHeatCapacity) &&
                                             config.DefaultSpecificHeatCapacity > 0f
            ? config.DefaultSpecificHeatCapacity
            : 1f;
        foreach (var snapshot in snapshots)
        {
            for (var index = 0; index < snapshot.Temperature.Length; index++)
            {
                var heatCapacity = 0f;
                foreach (var gas in snapshot.Gases)
                {
                    float configuredSpecificHeatCapacity = gas.GasId >= 0 &&
                                                           gas.GasId < config.GasRegistry.Count
                        ? config.GasRegistry[gas.GasId].SpecificHeatCapacity
                        : fallbackSpecificHeatCapacity;
                    float specificHeatCapacity = float.IsFinite(configuredSpecificHeatCapacity) &&
                                                 configuredSpecificHeatCapacity > 0f
                        ? configuredSpecificHeatCapacity
                        : fallbackSpecificHeatCapacity;
                    heatCapacity += gas.Moles[index] * specificHeatCapacity;
                }

                float storedTemperature = snapshot.Temperature[index];
                float effectiveTemperature = float.IsFinite(storedTemperature) && storedTemperature > 0f
                    ? storedTemperature
                    : config.DefaultTemperatureFallback;
                totalEnergy += effectiveTemperature * heatCapacity;
            }
        }

        return totalEnergy;
    }
}
