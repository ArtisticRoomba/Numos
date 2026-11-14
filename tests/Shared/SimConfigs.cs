using Numos.CoreSim;

namespace Numos.Tests;

/// <summary>
///     Builds the configurations the simulation tests share.
/// </summary>
internal static class SimConfigs
{
    internal const string FirstGasName = "First";
    internal const string SecondGasName = "Second";
    internal const int FirstGasId = 0;
    internal const int SecondGasId = 1;
    internal const float DefaultTemperature = 300f;

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
            SleepDelay = float.MaxValue,
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
    ///     One gas named <c>TestGas</c> with Cv = 20 and bulk flow, diffusion, and conduction all off, for tests that
    ///     care about pipeline and replay plumbing rather than transport.
    /// </summary>
    internal static AtmosConfig CreateNoTransportConfig()
    {
        return new AtmosConfig
        {
            BulkFlowCoefficient = 0f,
            DefaultDiffusionCoefficient = 0f,
            ThermalConductance = 0f,
            GasRegistry = [new GasProperties { Name = "TestGas", MolarHeatCapacityAtConstantVolume = 20f }]
        };
    }

    /// <summary>
    ///     Replaces one registered gas with an edited copy, keeping its gas ID.
    /// </summary>
    /// <returns><paramref name="config"/>, so calls can chain.</returns>
    internal static AtmosConfig WithGas(this AtmosConfig config, int gasId, Func<GasProperties, GasProperties> edit)
    {
        config.GasRegistry.Replace(gasId, edit(config.GasRegistry[gasId]));
        return config;
    }
}