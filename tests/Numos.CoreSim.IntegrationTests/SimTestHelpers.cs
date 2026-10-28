using Numos.API;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.CoreSim.EulerFlow;
using Numos.Maths;

namespace Numos.CoreSim.IntegrationTests;

internal static class SimTestHelpers
{
    internal const string FirstGasName = "First";
    internal const string SecondGasName = "Second";
    internal const int FirstGasId = 0;
    internal const int SecondGasId = 1;
    internal const int RoomId = 1;
    internal const float DefaultTemperature = 300f;
    internal const float Tolerance = 0.0001f;
    internal const float EnergyTolerance = 0.001f;
    internal const string EulerAirGasName = "Air";
    internal const string EulerUnitGasName = "Unit";
    internal const int EulerAirGasId = 0;
    internal const int EulerUnitGasId = 1;
    internal const float EulerAirMolarMass = 0.02897f;

    internal static AtmosConfig CreateDeterministicConfig()
    {
        return new AtmosConfig
        {
            DefaultTemperatureFallback = DefaultTemperature,
            DefaultMolarHeatCapacityAtConstantVolume = 1f,
            // Preserve the historical reduced-pressure scale in solver tests while production defaults use SI.
            VoxelVolume = AtmosPhysicalConstants.MolarGasConstant,
            SaturationReferencePressure = 1000f,
            BulkFlowCoefficient = 0.25f,
            VacuumThreshold = 0f,
            SleepThreshold = int.MaxValue,
            SleepEpsilon = 0f,
            ThermalConductance = 0.05f,
            CondensationRateFactor = 0.5f,
            MaxPressureTransferFractionPerNeighbor = 0.16f,
            GasRegistry =
            [
                TestGases.Create(FirstGasName, 0f),
                TestGases.Create(SecondGasName, 0f)
            ]
        };
    }

    /// <summary>
    ///     SI configuration for the Euler transport tests: 1 m³ voxels, real molar masses, <c>Cv = 5R/2</c>
    ///     (γ = 1.4), no sleep, no vacuum cleanup, no diffusion, and no substep cap unless the caller passes one.
    /// </summary>
    internal static AtmosConfig CreateEulerConfig(EulerFlowConfig? eulerFlow = null)
    {
        var config = new AtmosConfig
        {
            DefaultTemperatureFallback = AtmosPhysicalConstants.RoomTemperature,
            DefaultMolarHeatCapacityAtConstantVolume =
                AtmosPhysicalConstants.IdealDiatomicMolarHeatCapacityAtConstantVolume,
            VoxelVolume = 1f,
            DefaultDiffusionCoefficient = 0f,
            VacuumThreshold = 0f,
            SleepThreshold = int.MaxValue,
            SleepEpsilon = 0f,
            GasRegistry =
            [
                EulerGas(EulerAirGasName, EulerAirMolarMass),
                EulerGas(EulerUnitGasName, 1f)
            ]
        };

        config.SolverConfigurations.Add(eulerFlow ?? new EulerFlowConfig { MaxSubstepsPerTick = 0 });
        return config;
    }

    /// <summary>
    ///     A private-world simulation on the <see cref="AtmosTransportModel.Legacy" /> stages. For tests that pin the
    ///     pressure-relaxation law's per-tick numbers or its sleep timing, and for tests that zero
    ///     <see cref="AtmosConfig.BulkFlowCoefficient" /> and
    ///     <see cref="AtmosConfig.MaxPressureTransferFractionPerNeighbor" /> to switch transport off.
    ///     The <c>euler-flow</c> stage ignores both knobs.
    /// </summary>
    internal static AtmosSimulation CreateLegacySimulation(AtmosConfig config, int width, int height, int depth)
    {
        return new AtmosSimulation(config, width, height, depth, AtmosTransportModel.Legacy);
    }

    /// <summary>
    ///     A one-chunk simulation on the Euler pipeline. <paramref name="exactGasDynamics" /> turns off
    ///     <c>thermodynamics</c> and <c>gas-reactions</c> so the result can be compared with the Euler equations alone.
    /// </summary>
    internal static AtmosSimulation CreateEulerSimulation(
        AtmosConfig config, int width, int height, int depth,
        out AtmosChunkHandle chunk, bool exactGasDynamics = true)
    {
        var simulation = new AtmosSimulation(config, width, height, depth);
        if (exactGasDynamics)
        {
            simulation.World.Solvers.SetEnabled(AtmosBuiltInSolvers.Thermodynamics, false);
            simulation.World.Solvers.SetEnabled(AtmosBuiltInSolvers.GasReactions, false);
        }

        chunk = CreateOpenChunk(simulation, default);
        return simulation;
    }

    /// <summary>
    ///     Moles of an ideal gas that put one voxel of the configured volume at <paramref name="pressure" /> and
    ///     <paramref name="temperature" />.
    /// </summary>
    internal static float MolesFor(AtmosConfig config, float pressure, float temperature)
    {
        return pressure * config.VoxelVolume / (AtmosPhysicalConstants.MolarGasConstant * temperature);
    }

    /// <summary>
    ///     Total energy <c>Σ (C·T + ½·m·|u|²)</c> of a snapshot in double, the quantity the Euler stage conserves.
    /// </summary>
    internal static double TotalEulerEnergy(AtmosConfig config, AtmosChunkSnapshot snapshot)
    {
        double total = 0d;
        for (int index = 0; index < snapshot.Temperature.Length; index++)
        {
            double heatCapacity = 0d;
            double mass = 0d;
            foreach (var gas in snapshot.Gases)
            {
                var properties = config.GasRegistry[gas.GasId];
                heatCapacity += (double)gas.Moles[index] * properties.MolarHeatCapacityAtConstantVolume;
                mass += (double)gas.Moles[index] * properties.MolarMass;
            }

            if (heatCapacity <= 0d)
                continue;

            double speedSquared = (double)snapshot.VelocityX[index] * snapshot.VelocityX[index] +
                                  (double)snapshot.VelocityY[index] * snapshot.VelocityY[index] +
                                  (double)snapshot.VelocityZ[index] * snapshot.VelocityZ[index];

            total += heatCapacity * snapshot.Temperature[index] + 0.5d * mass * speedSquared;
        }

        return total;
    }

    private static GasProperties EulerGas(string name, float molarMass)
    {
        return new GasProperties
        {
            Name = name,
            MolarMass = molarMass,
            MolarHeatCapacityAtConstantVolume = AtmosPhysicalConstants.IdealDiatomicMolarHeatCapacityAtConstantVolume,
            DiffusionCoefficient = 0f
        };
    }

    internal static AtmosChunkHandle CreateOpenChunk(
        AtmosSimulation simulation, Int3 position,
        VoxelClassification? classification = null)
    {
        var chunk = simulation.CreateAndRegisterChunk(position);
        simulation.SetChunkClassification(chunk, classification ?? new VoxelClassification(RoomId));
        return chunk;
    }

    internal static void SetAllTemperatures(
        AtmosSimulation simulation, AtmosChunkHandle chunk,
        int width, int height, int depth, float temperature = DefaultTemperature)
    {
        for (int z = 0; z < depth; z++)
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
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