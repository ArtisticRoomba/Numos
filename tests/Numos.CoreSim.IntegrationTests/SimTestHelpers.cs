using Numos.API;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Datatypes.Snapshots;
using Numos.CoreSim.EulerFlow;
using Numos.Maths;

namespace Numos.CoreSim.IntegrationTests;

internal static class SimTestHelpers
{
    /// <summary>
    ///     The tick rate the helpers use when a test does not pick one. The pinned numbers in these tests were
    ///     taken at 20 ticks per second; tests about behaviour over simulated time pass their own rate.
    /// </summary>
    internal const int PinnedTickRate = 20;

    // These constants, CreateDeterministicConfig, and the wrappers at the end of the class forward to SimConfigs,
    // SimSetup, and SimMetrics in tests/Shared. Tests that still name SimTestHelpers then get the same values.
    internal const string FirstGasName = SimConfigs.FirstGasName;
    internal const string SecondGasName = SimConfigs.SecondGasName;
    internal const int FirstGasId = SimConfigs.FirstGasId;
    internal const int SecondGasId = SimConfigs.SecondGasId;
    internal const int RoomId = SimSetup.RoomId;
    internal const float DefaultTemperature = SimConfigs.DefaultTemperature;
    internal const float Tolerance = 0.0001f;
    internal const float EnergyTolerance = 0.001f;
    internal const string EulerAirGasName = "Air";
    internal const string EulerUnitGasName = "Unit";
    internal const int EulerAirGasId = 0;
    internal const int EulerUnitGasId = 1;
    internal const float EulerAirMolarMass = 0.02897f;

    internal static AtmosConfig CreateDeterministicConfig()
    {
        return SimConfigs.CreateDeterministicConfig();
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
            SleepDelay = float.MaxValue,
            SleepEpsilon = 0f,
            GasRegistry =
            [
                EulerGas(EulerAirGasName, EulerAirMolarMass),
                EulerGas(EulerUnitGasName, 1f)
            ]
        };

        config.SolverConfigurations.Add(eulerFlow ?? new EulerFlowConfig { MaxSubstepsPerSecond = 0 });
        return config;
    }

    /// <summary>
    ///     A private-world simulation on the <see cref="AtmosTransportModel.Legacy" /> stages. For tests that pin the
    ///     pressure-relaxation law's per-tick numbers or its sleep timing, and for tests that zero
    ///     <see cref="AtmosConfig.BulkFlowCoefficient" /> and
    ///     <see cref="AtmosConfig.MaxPressureTransferFractionPerNeighbor" /> to switch transport off.
    ///     The <c>euler-flow</c> stage ignores both knobs.
    /// </summary>
    internal static AtmosSimulation CreateLegacySimulation(
        AtmosConfig config, int width, int height, int depth, int tickRate = PinnedTickRate)
    {
        return new AtmosSimulation(config, width, height, depth, AtmosTransportModel.Legacy, tickRate);
    }

    /// <summary>
    ///     A one-chunk simulation on the Euler pipeline. <paramref name="exactGasDynamics" /> turns off
    ///     <c>thermodynamics</c> and <c>gas-reactions</c> so the result can be compared with the Euler equations alone.
    /// </summary>
    internal static AtmosSimulation CreateEulerSimulation(
        AtmosConfig config, int width, int height, int depth,
        out AtmosChunkHandle chunk, bool exactGasDynamics = true, int tickRate = PinnedTickRate)
    {
        var simulation = new AtmosSimulation(config, width, height, depth, AtmosTransportModel.Euler, tickRate);
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
        return SimSetup.CreateOpenChunk(simulation, position, classification);
    }

    internal static void SetAllTemperatures(
        AtmosSimulation simulation, AtmosChunkHandle chunk,
        int width, int height, int depth, float temperature = DefaultTemperature)
    {
        SimSetup.SetAllTemperatures(simulation, chunk, width, height, depth, temperature);
    }

    internal static int Index(int x, int y, int z, int width, int height)
    {
        return x + y * width + z * width * height;
    }

    internal static float Moles(AtmosChunkSnapshot snapshot, int gasId, int index)
    {
        return SimMetrics.Moles(snapshot, gasId, index);
    }

    internal static float TotalMoles(AtmosChunkSnapshot snapshot)
    {
        return SimMetrics.TotalMoles(snapshot);
    }

    internal static float TotalMoles(params AtmosChunkSnapshot[] snapshots)
    {
        return SimMetrics.TotalMoles(snapshots);
    }

    internal static double TotalThermalEnergyPrecise(
        AtmosConfig config,
        params AtmosChunkSnapshot[] snapshots)
    {
        return SimMetrics.TotalThermalEnergyPrecise(config, snapshots);
    }
}